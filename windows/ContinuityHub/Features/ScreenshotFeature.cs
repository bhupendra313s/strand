using System;
using System.Collections.Generic;
using ContinuityHub.Core;
using ContinuityHub.Platform;
using ContinuityHub.UI;

namespace ContinuityHub.Features;

/// <summary>Phone screenshot → PC clipboard, fetched lazily at paste time (Win32 delayed rendering).</summary>
internal sealed class ScreenshotFeature : IFeature
{
    private readonly ClipboardBridge _bridge;
    private readonly Dictionary<string, ActivityItem> _rows = new();
    private FeatureContext? _ctx;

    public ScreenshotFeature(ClipboardBridge bridge) => _bridge = bridge;

    public string Id => FeatureIds.Screenshot;
    public string Title => "Screenshots";
    public string Description => "Take a screenshot on your phone, then press Ctrl+V on your PC. It only downloads when you paste.";
    public string Glyph => ""; // Photo
    public IReadOnlyCollection<string> MessageTypes { get; } = new[] { Protocol.ScreenshotReady };

    public void Start(FeatureContext ctx)
    {
        _ctx = ctx;
        _bridge.LazyFetchCompleted += OnFetched;
    }

    public void Stop()
    {
        _bridge.LazyFetchCompleted -= OnFetched;
        _ctx = null;
    }

    public void OnMessage(IncomingMessage m)
    {
        var ctx = _ctx!;
        var offer = ScreenshotOffer.From(m);
        if (offer == null) return;
        _bridge.OfferLazyImage(offer);

        var shot = offer.Message;
        var size = shot.Width > 0 ? $"{shot.Width}×{shot.Height} · {FormatBytes(shot.Size)}" : FormatBytes(shot.Size);
        _rows[shot.Id] = ctx.Activity.Add(new ActivityItem
        {
            FeatureId = Id, Glyph = Glyph, Direction = ActivityDirection.Incoming,
            Title = $"Screenshot from {offer.DeviceName}",
            Detail = $"Ready, press Ctrl+V to paste · {size}",
            ActionLabel = "Copy again",
            Action = () => _bridge.OfferLazyImage(offer),
        });
        if (_rows.Count > 20) _rows.Clear();

        if (ctx.GetSetting(Id, "notify", true))
            ctx.Notify($"Screenshot from {offer.DeviceName}", "Press Ctrl+V to paste it. It downloads over Wi-Fi only when you paste.");
    }

    private void OnFetched(ScreenshotOffer offer, byte[]? bytes, Exception? error)
    {
        if (!_rows.TryGetValue(offer.Message.Id, out var row)) return;
        if (bytes != null)
        {
            row.Thumbnail = bytes;
            row.Detail = $"Pasted · fetched {FormatBytes(bytes.Length)} on demand";
        }
        else
        {
            row.Detail = $"Couldn't fetch from {offer.DeviceName}: {error?.Message}";
            _ctx?.Notify("Screenshot unavailable", row.Detail);
        }
        row.NotifyUpdated();
    }

    public object? CreateSettingsView(FeatureContext ctx) => SettingsViews.Stack(
        SettingsViews.Toggle("Notify me when a screenshot is ready to paste", ctx.GetSetting(Id, "notify", true),
            v => ctx.SetSetting(Id, "notify", v)),
        SettingsViews.Note("Screenshots stay on your phone until you paste. They're offered as both bitmap and PNG, and kept out of Win+V history and the cloud clipboard."));

    private static string FormatBytes(long b) => b switch
    {
        <= 0 => "size unknown",
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0} KB",
        _ => $"{b / (1024.0 * 1024):0.0} MB",
    };
}
