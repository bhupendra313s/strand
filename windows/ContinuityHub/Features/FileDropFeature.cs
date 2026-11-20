using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContinuityHub.Core;
using ContinuityHub.UI;

namespace ContinuityHub.Features;

/// <summary>
/// QuickDrop: files and folders between PC and phone over Wi-Fi, with progress, speed and resume.
/// PC → phone: offer over WSS, the phone pulls from the hub's HTTPS port. Phone → PC: the hub pulls from the
/// phone's fetch server. Received files go to Downloads\QuickDrop.
/// </summary>
internal sealed class FileDropFeature : IFeature
{
    private readonly OutgoingDrops _outgoing = new();
    private readonly Dictionary<string, ActivityItem> _sentRows = new();
    private readonly Dictionary<string, CancellationTokenSource> _incoming = new();
    private FeatureContext? _ctx;

    public string Id => FeatureIds.FileDrop;
    public string Title => "QuickDrop";
    public string Description => "Send files and folders between your phone and PC over Wi-Fi. Drag them onto this window. Interrupted transfers resume.";
    public string Glyph => ""; // Send
    public IReadOnlyCollection<string> MessageTypes { get; } = new[] { Protocol.FileOffer, Protocol.FileStatus };

    public static string DownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QuickDrop");

    public bool IsRunning => _ctx != null;

    public void Start(FeatureContext ctx)
    {
        _ctx = ctx;
        ctx.Server.ResolveDropFile = _outgoing.Resolve;
    }

    public void Stop()
    {
        if (_ctx != null) _ctx.Server.ResolveDropFile = null;
        foreach (var cts in _incoming.Values) cts.Cancel();
        _incoming.Clear();
        _ctx = null;
    }

    // ------------------------------------------------------------------ PC → phone

    /// <summary>Offers files/folders to every connected phone with QuickDrop on. Returns a user-facing result.</summary>
    public string Send(IEnumerable<string> paths)
    {
        var ctx = _ctx;
        if (ctx == null) return "QuickDrop is turned off";
        var targets = ctx.Server.Sessions.Where(s => s.Supports(Id)).ToList();
        if (targets.Count == 0) return ctx.Server.Sessions.Count == 0 ? "No phone is connected" : "QuickDrop is off on your phone";

        var files = DropPaths.Expand(paths);
        if (files.Count == 0) return "Nothing to send (empty folder?)";
        if (files.Count > FileOfferMessage.MaxFiles) return $"Too many files (max {FileOfferMessage.MaxFiles})";

        var offer = _outgoing.Create(ctx.Config.HubId, files);
        var json = offer.ToJson();
        foreach (var s in targets) _ = s.SendAsync(json);

        var to = targets.Count == 1 ? targets[0].Name : $"{targets.Count} phones";
        _sentRows[offer.Id] = ctx.Activity.Add(new ActivityItem
        {
            FeatureId = Id, Glyph = Glyph, Direction = ActivityDirection.Outgoing,
            Title = $"Sending {offer.Label} to {to}",
            Detail = $"Waiting for {to} · {DropDownloader.FormatBytes(offer.TotalSize)}" + (offer.Files.Count > 1 ? $" · {offer.Files.Count} files" : ""),
        });
        Log.Info($"QuickDrop offer {offer.Id[..8]}: {offer.Files.Count} files, {offer.TotalSize} B to {to}");
        return $"Sending to {to}";
    }

    // ------------------------------------------------------------------ messages

    public void OnMessage(IncomingMessage m)
    {
        if (m.Type == Protocol.FileStatus) { OnStatus(m); return; }
        if (FileOfferMessage.From(m.Body) is { } offer) Receive(offer, m.From);
    }

    private void OnStatus(IncomingMessage m)
    {
        if (FileStatusMessage.From(m.Body) is not { } st || !_sentRows.TryGetValue(st.OfferId, out var row)) return;
        var p = new DropProgress(st.DoneBytes, st.TotalBytes, st.FilesDone, st.FileCount, 0, null);
        switch (st.State)
        {
            case FileStatusMessage.Progress:
                row.Detail = $"{p.Percent}% · {DropDownloader.FormatBytes(st.DoneBytes)} of {DropDownloader.FormatBytes(st.TotalBytes)}" +
                             (st.Message != null ? $" · {st.Message}" : "") + (st.FileCount > 1 ? $" · {st.FilesDone} of {st.FileCount} files" : "");
                break;
            case FileStatusMessage.Done:
                row.Title = row.Title.Replace("Sending", "Sent");
                row.Detail = $"Saved on {m.From.Name} · {DropDownloader.FormatBytes(st.TotalBytes)}" + (st.FileCount > 1 ? $" · {st.FileCount} files" : "");
                _ctx?.Notify("QuickDrop", $"Sent to {m.From.Name}");
                break;
            default:
                row.Detail = $"Failed on {m.From.Name}: {st.Message}";
                break;
        }
        row.NotifyUpdated();
    }

    // ------------------------------------------------------------------ phone → PC

    private void Receive(FileOfferMessage offer, DeviceSession from)
    {
        var ctx = _ctx!;
        if (_incoming.ContainsKey(offer.Id)) return;
        var deviceId = from.DeviceId; var deviceName = from.Name;
        var row = ctx.Activity.Add(new ActivityItem
        {
            FeatureId = Id, Glyph = Glyph, Direction = ActivityDirection.Incoming,
            Title = $"Receiving {offer.Label} from {deviceName}",
            Detail = $"Starting · {DropDownloader.FormatBytes(offer.TotalSize)}",
            ActionLabel = "Open folder",
            Action = () => OpenFolder(DownloadDir),
        });

        var cts = new CancellationTokenSource();
        _incoming[offer.Id] = cts;
        var ui = ctx.Ui;
        var server = ctx.Server;

        DeviceSession? Session() => server.Sessions.FirstOrDefault(s => s.DeviceId == deviceId);
        DropEndpoint? Endpoint() => Session() is { } s ? new DropEndpoint(s.RemoteIp.ToString(), s.FetchPort, s.FetchFingerprint) : null;

        var lastSent = DateTime.MinValue;
        var progress = new Progress<DropProgress>(p =>
        {
            row.Detail = DropDownloader.Describe(p);
            row.NotifyUpdated();
            if ((DateTime.UtcNow - lastSent).TotalMilliseconds < 700) return;
            lastSent = DateTime.UtcNow;
            _ = Session()?.SendAsync(new FileStatusMessage(offer.Id, FileStatusMessage.Progress, p.DoneBytes, p.TotalBytes,
                p.FilesDone, p.FileCount, $"{DropDownloader.FormatBytes((long)p.BytesPerSecond)}/s").ToJson());
        });

        Log.Info($"QuickDrop receiving {offer.Id[..8]}: {offer.Files.Count} files, {offer.TotalSize} B from {deviceName}");
        _ = Task.Run(async () =>
        {
            string? error = null; IReadOnlyList<string> saved = Array.Empty<string>();
            try { saved = await new DropDownloader(offer, Endpoint, DownloadDir, progress).RunAsync(cts.Token); }
            catch (OperationCanceledException) { error = "Cancelled"; }
            catch (Exception e) { error = e.Message; Log.Error($"QuickDrop {offer.Id[..8]} failed", e); }

            ui.Post(_ =>
            {
                _incoming.Remove(offer.Id);
                var status = error == null
                    ? new FileStatusMessage(offer.Id, FileStatusMessage.Done, offer.TotalSize, offer.TotalSize, offer.Files.Count, offer.Files.Count, null)
                    : new FileStatusMessage(offer.Id, FileStatusMessage.Failed, 0, offer.TotalSize, 0, offer.Files.Count, error);
                _ = Session()?.SendAsync(status.ToJson());
                if (error == null)
                {
                    row.Title = $"Received {offer.Label} from {deviceName}";
                    row.Detail = $"Saved to Downloads\\QuickDrop · {DropDownloader.FormatBytes(offer.TotalSize)}" +
                                 (offer.Files.Count > 1 ? $" · {offer.Files.Count} files" : "");
                    _ctx?.Notify($"QuickDrop from {deviceName}", $"{offer.Label} saved to Downloads\\QuickDrop");
                    Log.Info($"QuickDrop {offer.Id[..8]} done");
                }
                else row.Detail = $"Failed: {error}";
                row.NotifyUpdated();
            }, null);
        });
    }

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public object? CreateSettingsView(FeatureContext ctx) => SettingsViews.Stack(
        SettingsViews.Note($"Received files are saved to {DownloadDir}. Drag files or folders onto the Strand window, or use Send files on the Home page."),
        SettingsViews.Note("Transfers use the same pinned, encrypted connection as clipboard sync. If Wi-Fi drops, they continue where they stopped once the phone reconnects."));
}
