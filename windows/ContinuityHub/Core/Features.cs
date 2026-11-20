using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ContinuityHub.Core;

/// <summary>
/// A pluggable sync capability (text clipboard, screenshots, and in future: files, notifications, …).
/// To add one: implement this, register it in HubController, and implement the matching Android
/// <c>SyncFeature</c>. The server, loop guard, pairing, and UI shell need no changes.
/// </summary>
public interface IFeature
{
    /// <summary>Stable id, announced in hello/welcome so peers know what the other side supports.</summary>
    string Id { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>Segoe Fluent Icons code point shown in the UI.</summary>
    string Glyph { get; }
    /// <summary>Message types routed to <see cref="OnMessage"/>.</summary>
    IReadOnlyCollection<string> MessageTypes { get; }
    bool EnabledByDefault => true;

    void Start(FeatureContext ctx);
    void Stop();
    /// <summary>Called on the UI thread, only while the feature is enabled and sync is not paused.</summary>
    void OnMessage(IncomingMessage message);
    /// <summary>Optional feature-specific settings UI (a WPF element); null for none.</summary>
    object? CreateSettingsView(FeatureContext ctx) => null;
}

/// <summary>Everything a feature may use. Keeps features decoupled from the app shell.</summary>
public sealed class FeatureContext
{
    public required HubServer Server { get; init; }
    public required LoopGuard Guard { get; init; }
    public required HubConfig Config { get; init; }
    public required ActivityFeed Activity { get; init; }
    public required SynchronizationContext Ui { get; init; }
    /// <summary>Shows a toast/balloon if the user has notifications on.</summary>
    public required Action<string, string> Notify { get; init; }

    public bool IsPaused => Config.Paused;

    public bool GetSetting(string featureId, string key, bool fallback) =>
        Config.Settings.TryGetValue($"{featureId}.{key}", out var v) ? v == "true" : fallback;

    public void SetSetting(string featureId, string key, bool value)
    {
        Config.Settings[$"{featureId}.{key}"] = value ? "true" : "false";
        Config.Save();
    }
}

/// <summary>Owns the registered features, their enabled state, and message routing.</summary>
public sealed class FeatureHost
{
    private readonly List<IFeature> _features = new();
    private readonly HashSet<string> _running = new();
    private FeatureContext? _ctx;

    public IReadOnlyList<IFeature> All => _features;
    public FeatureContext? Context => _ctx;
    public event Action? Changed;

    public void Register(IFeature feature) => _features.Add(feature);

    public void Start(FeatureContext ctx)
    {
        _ctx = ctx;
        ctx.Server.LocalFeatures = () => _features.Where(f => IsEnabled(f.Id)).Select(f => f.Id).ToList();
        ctx.Server.MessageReceived += m => ctx.Ui.Post(_ => Route(m), null);
        foreach (var f in _features.Where(f => IsEnabled(f.Id))) StartFeature(f);
    }

    public bool IsEnabled(string id)
    {
        if (_ctx == null) return false;
        if (_ctx.Config.Features.TryGetValue(id, out var on)) return on;
        return _features.FirstOrDefault(f => f.Id == id)?.EnabledByDefault ?? false;
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (_ctx == null) return;
        var f = _features.First(x => x.Id == id);
        _ctx.Config.Features[id] = enabled;
        _ctx.Config.Save();
        if (enabled) StartFeature(f); else StopFeature(f);
        Log.Info($"Feature {id} {(enabled ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    public void StopAll() { foreach (var f in _features) StopFeature(f); }

    private void StartFeature(IFeature f)
    {
        if (!_running.Add(f.Id)) return;
        try { f.Start(_ctx!); } catch (Exception e) { Log.Error($"feature {f.Id} failed to start", e); _running.Remove(f.Id); }
    }

    private void StopFeature(IFeature f)
    {
        if (!_running.Remove(f.Id)) return;
        try { f.Stop(); } catch (Exception e) { Log.Error($"feature {f.Id} failed to stop", e); }
    }

    private void Route(IncomingMessage m)
    {
        if (_ctx == null || _ctx.IsPaused) return;
        foreach (var f in _features)
        {
            if (!_running.Contains(f.Id) || !f.MessageTypes.Contains(m.Type)) continue;
            try { f.OnMessage(m); } catch (Exception e) { Log.Error($"feature {f.Id} failed on {m.Type}", e); }
        }
    }
}

public enum ActivityDirection { Incoming, Outgoing, Info }

/// <summary>One row in the activity feed. Mutable so a row can update (e.g. a thumbnail appearing after paste).</summary>
public sealed class ActivityItem
{
    public required string FeatureId { get; init; }
    public required string Glyph { get; init; }
    public required string Title { get; set; }
    public string Detail { get; set; } = "";
    public ActivityDirection Direction { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public byte[]? Thumbnail { get; set; }
    /// <summary>Optional action, e.g. "Copy again".</summary>
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    public event Action? Updated;
    public void NotifyUpdated() => Updated?.Invoke();
}

/// <summary>In-memory history of recent sync events (never persisted: it may contain clipboard previews).</summary>
public sealed class ActivityFeed
{
    private const int Max = 50;
    private readonly List<ActivityItem> _items = new();
    public IReadOnlyList<ActivityItem> Items => _items;
    public int TodayCount => _items.Count(i => i.Time.Date == DateTime.Today && i.Direction != ActivityDirection.Info);

    public event Action<ActivityItem>? Added;

    /// <summary>Must be called on the UI thread.</summary>
    public ActivityItem Add(ActivityItem item)
    {
        _items.Insert(0, item);
        if (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
        Added?.Invoke(item);
        return item;
    }

    public static string Preview(string text, int max = 120)
    {
        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
