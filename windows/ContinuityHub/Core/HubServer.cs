using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContinuityHub.Core;

/// <summary>A connected, authenticated device.</summary>
public sealed class DeviceSession
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    internal DeviceSession(WebSocket ws, string deviceId, string name, IPAddress remoteIp, int fetchPort,
        string fetchFingerprint, IReadOnlySet<string> features, string? appVersion)
    {
        _ws = ws; DeviceId = deviceId; Name = name; RemoteIp = remoteIp; FetchPort = fetchPort;
        FetchFingerprint = fetchFingerprint; Features = features; AppVersion = appVersion;
    }

    public string DeviceId { get; }
    public string Name { get; }
    public IPAddress RemoteIp { get; }
    public int FetchPort { get; }
    public string FetchFingerprint { get; }
    /// <summary>Feature ids the device announced in hello (v1 devices that send none are assumed to support the v1 set).</summary>
    public IReadOnlySet<string> Features { get; }
    public string? AppVersion { get; }
    /// <summary>The phone's Material You colours from hello ({"light":{…},"dark":{…}}), if it sent them.</summary>
    public JsonObject? Palette { get; internal set; }
    /// <summary>False when the phone switched "PC → phone" clipboard off: never send it clipboard text.</summary>
    public bool AcceptsClipboard { get; internal set; } = true;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;

    public bool Supports(string featureId) => Features.Contains(featureId);

    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    internal Task CloseAsync(string reason) =>
        _ws.State == WebSocketState.Open
            ? _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None)
            : Task.CompletedTask;
}

/// <summary>A validated, loop-guarded message from a device, ready for a feature to handle.</summary>
public sealed record IncomingMessage(string Type, JsonObject Body, string Raw, DeviceSession From);

/// <summary>WSS control plane. Feature-agnostic: it authenticates, loop-guards and routes by message type.</summary>
public sealed class HubServer : IAsyncDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly HubIdentity _identity;
    private readonly DeviceRegistry _registry;
    private readonly LoopGuard _guard;
    private readonly IPAddress _bindAddress;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _relayTypes = new();
    private readonly object _pairGate = new();
    private string? _pairCode;
    private DateTimeOffset _pairExpires;
    private WebApplication? _app;

    public HubServer(HubIdentity identity, DeviceRegistry registry, LoopGuard guard, string hubId, string hubName,
        int port, IPAddress? bindAddress = null)
    {
        _identity = identity; _registry = registry; _guard = guard; HubId = hubId; HubName = hubName;
        _port = port; _bindAddress = bindAddress ?? IPAddress.Any;
    }

    public string HubId { get; }
    public string HubName { get; set; }

    /// <summary>Feature ids this hub currently has enabled; sent in welcome. Set by the feature host.</summary>
    public Func<IEnumerable<string>> LocalFeatures { get; set; } = () => Protocol.V1Features;

    /// <summary>
    /// Data plane for QuickDrop: maps (offer id, file index, bearer token) to a local file path, or null to refuse.
    /// Set by the file-drop feature; requests are refused while it's null.
    /// </summary>
    public Func<string, int, string?, string?>? ResolveDropFile { get; set; }

    public event Action<IncomingMessage>? MessageReceived;
    public event Action? SessionsChanged;
    public event Action<string>? DevicePaired;

    public int BoundPort { get; private set; }
    public IReadOnlyCollection<DeviceSession> Sessions => _sessions.Values.ToList();

    /// <summary>Messages of this type from one device are forwarded to the other connected devices, origin preserved.</summary>
    public void EnableRelay(string type) => _relayTypes[type] = 0;
    public void DisableRelay(string type) => _relayTypes.TryRemove(type, out _);

    // ---------------------------------------------------------------- pairing window

    /// <summary>Opens a pairing window and returns the one-time code to embed in the QR.</summary>
    public string BeginPairing(TimeSpan validFor)
    {
        lock (_pairGate)
        {
            _pairCode = DeviceRegistry.Base64Url(RandomNumberGenerator.GetBytes(18));
            _pairExpires = DateTimeOffset.UtcNow + validFor;
            return _pairCode;
        }
    }

    public void EndPairing() { lock (_pairGate) _pairCode = null; }

    private bool TryConsumePairCode(string code)
    {
        lock (_pairGate)
        {
            if (_pairCode == null || DateTimeOffset.UtcNow > _pairExpires) return false;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_pairCode), Encoding.UTF8.GetBytes(code)))
                return false;
            _pairCode = null; // single use
            return true;
        }
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(_bindAddress, _port, lo => lo.UseHttps(_identity.Certificate));
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/ws", HandleWebSocketAsync);
        app.MapGet("/", () => "Continuity Hub");
        app.MapGet("/drop/{id}/{index:int}", (HttpContext ctx, string id, int index) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
            var path = ResolveDropFile?.Invoke(id, index, bearer);
            if (path == null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (!File.Exists(path)) return Results.StatusCode(StatusCodes.Status410Gone);
            ctx.Response.Headers.CacheControl = "no-store";
            // Range requests (resume) are handled by Kestrel's static-file machinery.
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        await app.StartAsync().ConfigureAwait(false);
        _app = app;

        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BoundPort = new Uri(addr.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost")).Port;
        Log.Info($"Hub listening on {_bindAddress}:{BoundPort}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values) { try { await s.CloseAsync("hub shutting down"); } catch { } }
        if (_app != null) { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    /// <summary>Drops a device's live connection (e.g. after "Forget").</summary>
    public void Disconnect(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var s)) _ = s.CloseAsync("forgotten");
    }

    // ---------------------------------------------------------------- outbound

    /// <summary>Sends a message to every connected device (optionally only those supporting a feature).</summary>
    public Task SendToAllAsync(string json, string? exceptDeviceId = null, string? requiresFeature = null) =>
        Task.WhenAll(_sessions.Values
            .Where(s => s.DeviceId != exceptDeviceId && (requiresFeature == null || s.Supports(requiresFeature)))
            .Where(s => requiresFeature != FeatureIds.ClipText || s.AcceptsClipboard)
            .Select(async s =>
            {
                try { await s.SendAsync(json).ConfigureAwait(false); }
                catch (Exception e) { Log.Warn($"send to {s.Name} failed: {e.Message}"); }
            }));

    // ---------------------------------------------------------------- inbound

    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var remote = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        DeviceSession? session = null;
        try
        {
            session = await HandshakeAsync(ws, remote, ctx.RequestAborted).ConfigureAwait(false);
            if (session == null) return;

            if (_sessions.TryGetValue(session.DeviceId, out var old)) _ = old.CloseAsync("replaced by newer connection");
            _sessions[session.DeviceId] = session;
            Log.Info($"Device connected: {session.Name} ({Short(session.DeviceId)}) from {remote}, features [{string.Join(",", session.Features)}]" +
                     (session.AcceptsClipboard ? "" : ", PC → phone clipboard off"));
            SessionsChanged?.Invoke();

            while (ws.State == WebSocketState.Open)
            {
                var frame = await ReceiveTextAsync(ws, ctx.RequestAborted).ConfigureAwait(false);
                if (frame == null) break;
                Dispatch(frame, session);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // normal disconnect
        }
        catch (Exception e) { Log.Error("session error", e); }
        finally
        {
            if (session != null && _sessions.TryGetValue(session.DeviceId, out var cur) && ReferenceEquals(cur, session))
            {
                _sessions.TryRemove(session.DeviceId, out _);
                Log.Info($"Device disconnected: {session.Name}");
                SessionsChanged?.Invoke();
            }
        }
    }

    private async Task<DeviceSession?> HandshakeAsync(WebSocket ws, IPAddress remote, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        var first = await ReceiveTextAsync(ws, timeout.Token).ConfigureAwait(false);
        var hello = first == null ? null : Protocol.TryParse(first);

        var deviceId = hello?.Str("device_id");
        var name = hello?.Str("device_name") ?? "Android";
        var fetchPort = (int)(hello?.Long("fetch_port") ?? 0);
        var fetchFp = hello?.Str("fetch_fingerprint")?.ToLowerInvariant();
        if (hello?.Str("type") != Protocol.Hello || string.IsNullOrEmpty(deviceId) || deviceId.Length > 64
            || fetchPort is < 1 or > 65535 || fetchFp is not { Length: 64 })
        {
            await RejectAsync(ws, "bad_hello", "Malformed hello").ConfigureAwait(false);
            return null;
        }
        if (name.Length > 64) name = name[..64];

        string? newToken = null;
        var token = hello.Str("device_token");
        var pairCode = hello.Str("pair_code");
        if (!string.IsNullOrEmpty(token) && _registry.Verify(deviceId, token))
        {
            // authenticated
        }
        else if (!string.IsNullOrEmpty(pairCode))
        {
            if (!TryConsumePairCode(pairCode))
            {
                await RejectAsync(ws, "pairing_closed", "Pairing code expired or already used. Show a new QR code on the PC.").ConfigureAwait(false);
                return null;
            }
            newToken = _registry.Pair(deviceId, name);
            Log.Info($"Paired new device {name} ({Short(deviceId)})");
            DevicePaired?.Invoke(name);
        }
        else
        {
            await RejectAsync(ws, "unauthorized", "Unknown device or revoked token. Pair again.").ConfigureAwait(false);
            return null;
        }

        var features = Protocol.ReadFeatures(hello);
        var session = new DeviceSession(ws, deviceId, name, remote, fetchPort, fetchFp, features, hello.Str("app_version"))
        {
            Palette = hello.TryGetPropertyValue("palette", out var pal) ? pal?.DeepClone() as JsonObject : null,
            AcceptsClipboard = !(hello["clip_directions"] is JsonObject dirs && dirs["from_pc"] is JsonValue fp
                                 && fp.TryGetValue<bool>(out var fromPc) && !fromPc),
        };
        await session.SendAsync(Protocol.BuildWelcome(HubId, HubName, newToken, LocalFeatures()), ct).ConfigureAwait(false);
        return session;
    }

    private static async Task RejectAsync(WebSocket ws, string code, string message)
    {
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(Protocol.BuildError(code, message)), WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, code, CancellationToken.None);
        }
        catch { }
        Log.Warn($"Rejected connection: {code}");
    }

    private void Dispatch(string frame, DeviceSession session)
    {
        var msg = Protocol.TryParse(frame);
        var type = msg?.Str("type");
        if (msg == null || string.IsNullOrEmpty(type) || type is Protocol.Hello) return;

        // Every sync payload with an envelope goes through the loop guard (PROTOCOL.md §5).
        var id = msg.Str("id"); var origin = msg.Str("origin_device_id"); var ts = msg.Long("timestamp");
        if (id != null && origin != null && ts != null && !_guard.AcceptIncoming(id, origin, ts.Value)) return;

        MessageReceived?.Invoke(new IncomingMessage(type, msg, frame, session));

        if (_relayTypes.ContainsKey(type))
            _ = SendToAllAsync(frame, exceptDeviceId: session.DeviceId, requiresFeature: type);
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > Protocol.MaxFrameBytes)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            if (r.EndOfMessage)
                return r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length) : "";
        }
    }
}
