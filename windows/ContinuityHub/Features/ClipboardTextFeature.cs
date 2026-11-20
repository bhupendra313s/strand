using System.Collections.Generic;
using System.Linq;
using ContinuityHub.Core;
using ContinuityHub.Platform;
using ContinuityHub.UI;

namespace ContinuityHub.Features;

/// <summary>Universal clipboard for text, both directions.</summary>
internal sealed class ClipboardTextFeature : IFeature
{
    private readonly ClipboardBridge _bridge;
    private FeatureContext? _ctx;

    public ClipboardTextFeature(ClipboardBridge bridge) => _bridge = bridge;

    public string Id => FeatureIds.ClipText;
    public string Title => "Clipboard text";
    public string Description => "Copy on one device and paste on the other. Works both ways, instantly.";
    public string Glyph => ""; // Copy
    public IReadOnlyCollection<string> MessageTypes { get; } = new[] { Protocol.ClipText };

    public void Start(FeatureContext ctx)
    {
        _ctx = ctx;
        ctx.Server.EnableRelay(Protocol.ClipText); // phone A → PC → phone B
        _bridge.LocalTextCopied += OnLocalTextCopied;
    }

    public void Stop()
    {
        _bridge.LocalTextCopied -= OnLocalTextCopied;
        _ctx?.Server.DisableRelay(Protocol.ClipText);
        _ctx = null;
    }

    private void OnLocalTextCopied(string text)
    {
        var ctx = _ctx;
        if (ctx == null || ctx.IsPaused || !ctx.GetSetting(Id, "send", true)) return;
        var targets = ctx.Server.Sessions.Where(s => s.Supports(Id) && s.AcceptsClipboard).ToList();
        if (targets.Count == 0) return;
        if (!ctx.Guard.ShouldBroadcastLocal(LoopGuard.Hash(text))) return;

        _ = ctx.Server.SendToAllAsync(Protocol.BuildClipText(Protocol.NewId(), ctx.Server.HubId, Protocol.NowMs(), text),
            requiresFeature: Id);
        ctx.Activity.Add(new ActivityItem
        {
            FeatureId = Id, Glyph = "", Direction = ActivityDirection.Outgoing,
            Title = $"Sent to {string.Join(", ", targets.Select(t => t.Name))}",
            Detail = ActivityFeed.Preview(text),
        });
    }

    public void OnMessage(IncomingMessage m)
    {
        var ctx = _ctx!;
        if (!ctx.GetSetting(Id, "receive", true)) return; // "Phone → PC" switched off on this PC
        var clip = ClipTextMessage.From(m.Body);
        if (clip == null) return;
        Apply(ctx, clip.Text);
        ctx.Activity.Add(new ActivityItem
        {
            FeatureId = Id, Glyph = Glyph, Direction = ActivityDirection.Incoming,
            Title = $"Text from {m.From.Name}",
            Detail = ActivityFeed.Preview(clip.Text),
            ActionLabel = "Copy again",
            Action = () => Apply(ctx, clip.Text),
        });
        if (ctx.GetSetting(Id, "notify", false))
            ctx.Notify($"Copied from {m.From.Name}", ActivityFeed.Preview(clip.Text, 80));
    }

    private void Apply(FeatureContext ctx, string text)
    {
        ctx.Guard.MarkAppliedRemote(LoopGuard.Hash(text));
        _bridge.SetRemoteText(text);
    }

    public object? CreateSettingsView(FeatureContext ctx) => SettingsViews.Stack(
        SettingsViews.Toggle("PC → phone: send what I copy on this PC", ctx.GetSetting(Id, "send", true),
            v => ctx.SetSetting(Id, "send", v)),
        SettingsViews.Toggle("Phone → PC: put text from my phone on this PC's clipboard", ctx.GetSetting(Id, "receive", true),
            v => ctx.SetSetting(Id, "receive", v)),
        SettingsViews.Note("Your phone has the same two switches (Features → Clipboard text). Either side can turn a direction off."),
        SettingsViews.Toggle("Show a notification when text arrives", ctx.GetSetting(Id, "notify", false),
            v => ctx.SetSetting(Id, "notify", v)),
        SettingsViews.Note("Content that password managers mark as private is never sent. Synced text is kept out of Windows' cloud clipboard."));
}
