using System;
using System.Collections.Generic;
using System.Linq;
using ContinuityHub.Core;
using ContinuityHub.UI;

namespace ContinuityHub.Features;

/// <summary>
/// Screen share: when a phone starts sharing, open a live viewer window. Closing the window tells the phone
/// to stop (so its capture and "sharing" indicator end too).
/// </summary>
internal sealed class ScreenShareFeature : IFeature
{
    private readonly Dictionary<string, (ScreenViewerWindow Window, string DeviceId)> _viewers = new();
    private readonly Dictionary<string, ActivityItem> _rows = new();
    private FeatureContext? _ctx;

    public string Id => FeatureIds.ScreenShare;
    public string Title => "Screen share";
    public string Description => "See your phone's screen live in a window on this PC. Start it from the Strand app on the phone.";
    public string Glyph => ""; // TV monitor
    public IReadOnlyCollection<string> MessageTypes { get; } = new[] { Protocol.ScreenOffer, Protocol.ScreenStop };

    public void Start(FeatureContext ctx) => _ctx = ctx;

    public void Stop()
    {
        foreach (var v in _viewers.Values.ToList()) v.Window.Close();
        _viewers.Clear();
        _ctx = null;
    }

    public void OnMessage(IncomingMessage m)
    {
        if (m.Type == Protocol.ScreenStop)
        {
            var sid = m.Body.Str("session_id");
            if (sid != null && _viewers.TryGetValue(sid, out var v)) { _viewers.Remove(sid); v.Window.EndedByPhone(); }
            if (sid != null && _rows.TryGetValue(sid, out var row)) { row.Title = $"{m.From.Name} stopped sharing its screen"; row.NotifyUpdated(); }
            return;
        }
        if (ScreenOfferMessage.From(m.Body) is { } offer) Open(offer, m.From);
    }

    private void Open(ScreenOfferMessage offer, DeviceSession from)
    {
        var ctx = _ctx!;
        var deviceId = from.DeviceId; var name = from.Name;
        // One live view per phone: a new share replaces the old window.
        foreach (var old in _viewers.Where(v => v.Value.DeviceId == deviceId).ToList()) { _viewers.Remove(old.Key); old.Value.Window.Close(); }

        DropEndpoint? Endpoint() => ctx.Server.Sessions.FirstOrDefault(s => s.DeviceId == deviceId) is { } s
            ? new DropEndpoint(s.RemoteIp.ToString(), s.FetchPort, s.FetchFingerprint) : null;

        var window = new ScreenViewerWindow(offer, name, Endpoint) { Icon = System.Windows.Application.Current.MainWindow?.Icon };
        window.Closed += (_, _) =>
        {
            if (!_viewers.Remove(offer.Id)) return; // already ended by the phone
            var s = ctx.Server.Sessions.FirstOrDefault(x => x.DeviceId == deviceId);
            _ = s?.SendAsync(ScreenOfferMessage.BuildStop(offer.Id));
            Log.Info($"Screen share {offer.Id[..8]} closed on the PC");
        };
        _viewers[offer.Id] = (window, deviceId);
        window.Show();
        window.Activate();

        _rows[offer.Id] = ctx.Activity.Add(new ActivityItem
        {
            FeatureId = Id, Glyph = Glyph, Direction = ActivityDirection.Incoming,
            Title = $"{name} is sharing its screen",
            Detail = "Showing in its own window. Close it to stop sharing.",
            ActionLabel = "Show",
            Action = () => { if (_viewers.TryGetValue(offer.Id, out var v)) { v.Window.Show(); v.Window.Activate(); } },
        });
        if (_rows.Count > 20) _rows.Clear();
        Log.Info($"Screen share {offer.Id[..8]} from {name}: {offer.Width}x{offer.Height}");
    }

    public object? CreateSettingsView(FeatureContext ctx) => SettingsViews.Stack(
        SettingsViews.Note("Start sharing from the Strand app on your phone (Features → Screen share). A viewer window opens here automatically. Closing it stops the share on the phone."),
        SettingsViews.Note("The picture is streamed over your Wi-Fi using the paired, pinned connection. Nothing is recorded; Save screenshot in the viewer writes one PNG to Pictures\\Strand."));
}
