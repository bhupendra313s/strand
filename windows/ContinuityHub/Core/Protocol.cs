using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace ContinuityHub.Core;

/// <summary>Stable feature ids. A feature's id doubles as the message type it relays, where applicable.</summary>
public static class FeatureIds
{
    public const string ClipText = "clip_text";
    public const string Screenshot = "screenshot";
    public const string FileDrop = "file_drop";
    public const string ScreenShare = "screen_share";
}

/// <summary>A screenshot the hub can fetch on demand from a device.</summary>
public sealed record ScreenshotOffer(ScreenshotReadyMessage Message, string DeviceName, IPAddress Ip, int Port, string Fingerprint)
{
    public static ScreenshotOffer? From(IncomingMessage m) =>
        ScreenshotReadyMessage.From(m.Body) is { } shot
            ? new ScreenshotOffer(shot, m.From.Name, m.From.RemoteIp, m.From.FetchPort, m.From.FetchFingerprint)
            : null;
}

/// <summary>Message builders/parsers for the v1 wire protocol (see PROTOCOL.md).</summary>
public static class Protocol
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxFrameBytes = MaxTextBytes * 2 + 4096; // text may be escaped in JSON

    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Error = "error";
    public const string ClipText = "clip_text";
    public const string ScreenshotReady = "screenshot_ready";
    public const string FileOffer = "file_offer";
    public const string FileStatus = "file_status";
    public const string ScreenOffer = "screen_offer";
    public const string ScreenStop = "screen_stop";

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string BuildClipText(string id, string origin, long ts, string text) => new JsonObject
    {
        ["type"] = ClipText,
        ["id"] = id,
        ["origin_device_id"] = origin,
        ["timestamp"] = ts,
        ["text"] = text,
    }.ToJsonString();

    public static string BuildWelcome(string hubId, string hubName, string? deviceToken, IEnumerable<string> features)
    {
        var o = new JsonObject
        {
            ["type"] = Welcome, ["hub_id"] = hubId, ["hub_name"] = hubName,
            ["features"] = new JsonArray(features.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        };
        if (deviceToken != null) o["device_token"] = deviceToken;
        return o.ToJsonString();
    }

    /// <summary>Feature ids every v1 peer supports. Peers that send no "features" list are assumed to have these.</summary>
    public static readonly IReadOnlyList<string> V1Features = new[] { FeatureIds.ClipText, FeatureIds.Screenshot };

    public static IReadOnlySet<string> ReadFeatures(JsonObject o)
    {
        if (o.TryGetPropertyValue("features", out var n) && n is JsonArray arr)
            return arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                      .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToHashSet();
        return V1Features.ToHashSet();
    }

    public static string BuildError(string code, string message) =>
        new JsonObject { ["type"] = Error, ["code"] = code, ["message"] = message }.ToJsonString();

    public static JsonObject? TryParse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string? Str(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Long(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }
}

public sealed record ClipTextMessage(string Id, string OriginDeviceId, long Timestamp, string Text)
{
    public static ClipTextMessage? From(JsonObject o)
    {
        var id = o.Str("id"); var origin = o.Str("origin_device_id"); var ts = o.Long("timestamp"); var text = o.Str("text");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(origin) || ts is null || text is null) return null;
        if (System.Text.Encoding.UTF8.GetByteCount(text) > Protocol.MaxTextBytes) return null;
        return new ClipTextMessage(id, origin, ts.Value, text);
    }
}

public sealed record ScreenshotReadyMessage(
    string Id, string OriginDeviceId, long Timestamp, string Mime, long Size, int Width, int Height, string Token)
{
    public static ScreenshotReadyMessage? From(JsonObject o)
    {
        var id = o.Str("id"); var origin = o.Str("origin_device_id"); var ts = o.Long("timestamp");
        var token = o.Str("token"); var mime = o.Str("mime") ?? "image/png";
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(origin) || ts is null || string.IsNullOrEmpty(token)) return null;
        if (id.Length > 128 || token.Length > 256) return null;
        return new ScreenshotReadyMessage(id, origin, ts.Value, mime, o.Long("size") ?? 0,
            (int)(o.Long("width") ?? 0), (int)(o.Long("height") ?? 0), token);
    }
}
