using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ContinuityHub.Core;
using ContinuityHub.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ContinuityHub.Tests;

/// <summary>Runs the real HubServer against a fake Android device speaking the wire protocol.</summary>
public sealed class HubIntegrationTests : IAsyncLifetime
{
    private const string HubId = "hub-0001";
    private readonly HubIdentity _identity = HubIdentity.CreateTransient();
    private readonly DeviceRegistry _registry = new(path: null);
    private HubServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new HubServer(_identity, _registry, new LoopGuard(HubId), HubId, "TestPC", 0, IPAddress.Loopback);
        await _server.StartAsync();
    }

    public async Task DisposeAsync() { await _server.DisposeAsync(); _identity.Dispose(); }

    // ------------------------------------------------------------------ fake phone

    private async Task<ClientWebSocket> ConnectAsync(string pinnedFingerprint)
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && HubIdentity.FingerprintOf(cert) == pinnedFingerprint;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{_server.BoundPort}/ws"), CancellationToken.None);
        return ws;
    }

    private static Task SendAsync(ClientWebSocket ws, JsonObject o) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage) return JsonNode.Parse(ms.ToArray()) as JsonObject;
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    private static JsonObject Hello(string deviceId, string fetchFp, int fetchPort, string? token = null, string? pairCode = null)
    {
        var o = new JsonObject
        {
            ["type"] = "hello", ["device_id"] = deviceId, ["device_name"] = "Fake Pixel",
            ["fetch_port"] = fetchPort, ["fetch_fingerprint"] = fetchFp,
        };
        if (token != null) o["device_token"] = token;
        if (pairCode != null) o["pair_code"] = pairCode;
        return o;
    }

    private static readonly string DummyFp = new('a', 64);

    private async Task<(ClientWebSocket ws, string token)> PairAsync(string deviceId, string fetchFp = "", int fetchPort = 1)
    {
        var code = _server.BeginPairing(TimeSpan.FromMinutes(1));
        var ws = await ConnectAsync(_identity.Fingerprint);
        await SendAsync(ws, Hello(deviceId, fetchFp.Length == 64 ? fetchFp : DummyFp, fetchPort, pairCode: code));
        var welcome = await ReceiveAsync(ws);
        Assert.Equal("welcome", welcome?["type"]?.GetValue<string>());
        var token = welcome!["device_token"]!.GetValue<string>();
        return (ws, token);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Pairing_is_single_use_and_token_reconnect_works()
    {
        var code = _server.BeginPairing(TimeSpan.FromMinutes(1));
        using var a = await ConnectAsync(_identity.Fingerprint);
        await SendAsync(a, Hello("phone-1", DummyFp, 1, pairCode: code));
        var welcome = await ReceiveAsync(a);
        var token = welcome!["device_token"]!.GetValue<string>();
        Assert.Equal(HubId, welcome["hub_id"]!.GetValue<string>());

        using var b = await ConnectAsync(_identity.Fingerprint); // replay of the same pair code
        await SendAsync(b, Hello("phone-2", DummyFp, 1, pairCode: code));
        Assert.Equal("pairing_closed", (await ReceiveAsync(b))?["code"]?.GetValue<string>());

        using var c = await ConnectAsync(_identity.Fingerprint);
        await SendAsync(c, Hello("phone-1", DummyFp, 1, token: token));
        var w2 = await ReceiveAsync(c);
        Assert.Equal("welcome", w2?["type"]?.GetValue<string>());
        Assert.Null(w2!["device_token"]); // only issued at pairing time

        using var d = await ConnectAsync(_identity.Fingerprint);
        await SendAsync(d, Hello("phone-1", DummyFp, 1, token: "wrong"));
        Assert.Equal("unauthorized", (await ReceiveAsync(d))?["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task Client_pinning_rejects_a_different_hub_cert()
    {
        var wrongFp = new string('0', 64);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(wrongFp));
    }

    [Fact]
    public async Task Text_flows_both_ways_and_echoes_are_dropped()
    {
        var (ws, _) = await PairAsync("phone-1");
        using var _ws = ws;

        var got = new TaskCompletionSource<ClipTextMessage>();
        _server.MessageReceived += m => { if (m.Type == "clip_text") got.TrySetResult(ClipTextMessage.From(m.Body)!); };

        // Our own hub id as origin -> must be dropped (loop prevention).
        await SendAsync(ws, Clip("x1", HubId, 10, "echo"));
        // Real phone copy.
        await SendAsync(ws, Clip("x2", "phone-1", 11, "hello from phone"));
        var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("hello from phone", received.Text);

        // PC -> phone
        await _server.SendToAllAsync(Protocol.BuildClipText(Protocol.NewId(), HubId, Protocol.NowMs(), "hello from pc"));
        var msg = await ReceiveAsync(ws);
        Assert.Equal("clip_text", msg?["type"]?.GetValue<string>());
        Assert.Equal("hello from pc", msg?["text"]?.GetValue<string>());
        Assert.Equal(HubId, msg?["origin_device_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Text_is_relayed_to_other_phones_with_original_origin()
    {
        _server.EnableRelay("clip_text");
        var (p1, _) = await PairAsync("phone-1");
        var (p2, _) = await PairAsync("phone-2");
        using var _1 = p1; using var _2 = p2;

        await SendAsync(p1, Clip("r1", "phone-1", 5, "relay me"));
        var msg = await ReceiveAsync(p2);
        Assert.Equal("relay me", msg?["text"]?.GetValue<string>());
        Assert.Equal("phone-1", msg?["origin_device_id"]?.GetValue<string>());
        Assert.Null(await ReceiveAsync(p1, 500)); // not echoed back to the sender
    }

    [Fact]
    public async Task Phone_with_pc_to_phone_off_gets_no_clipboard_text()
    {
        _server.EnableRelay("clip_text");
        var (p1, _) = await PairAsync("phone-1");
        var (paired2, token2) = await PairAsync("phone-2");
        paired2.Dispose();

        // phone-2 reconnects with "PC → phone" switched off.
        using var p2 = await ConnectAsync(_identity.Fingerprint);
        var hello = Hello("phone-2", DummyFp, 1, token: token2);
        hello["clip_directions"] = new JsonObject { ["to_pc"] = true, ["from_pc"] = false };
        await SendAsync(p2, hello);
        Assert.Equal("welcome", (await ReceiveAsync(p2))?["type"]?.GetValue<string>());
        using var _1 = p1;

        // (Checked first: a timed-out receive below aborts the fake phone's socket.)
        Assert.False(_server.Sessions.Single(s => s.DeviceId == "phone-2").AcceptsClipboard);
        Assert.True(_server.Sessions.Single(s => s.DeviceId == "phone-1").AcceptsClipboard);

        // Neither another phone's text (relay) nor the PC's own copies (broadcast) reach it.
        await SendAsync(p1, Clip("d1", "phone-1", 5, "not for phone-2"));
        await _server.SendToAllAsync(Protocol.BuildClipText("d2", HubId, 6, "pc copy"), requiresFeature: "clip_text");
        Assert.Null(await ReceiveAsync(p2, 700));
    }

    [Fact]
    public async Task Screenshot_lazy_fetch_uses_pinned_cert_and_bearer_token()
    {
        // The fake phone's data-plane server, with its own self-signed cert.
        using var phoneCert = X509CertificateLoader.LoadPkcs12(HubIdentity.CreateSelfSigned("Fake Phone").Export(X509ContentType.Pfx), null);
        var phoneFp = HubIdentity.FingerprintOf(phoneCert);
        var png = MakePng(64, 32);
        var requests = 0;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, lo => lo.UseHttps(phoneCert)));
        await using var phone = builder.Build();
        phone.MapGet("/shot/{id}", (string id, HttpContext ctx) =>
        {
            Interlocked.Increment(ref requests);
            return ctx.Request.Headers.Authorization == "Bearer tok-123" && id == "shot-1"
                ? Results.Bytes(png, "image/png") : Results.StatusCode(401);
        });
        await phone.StartAsync();
        var phonePort = new Uri(phone.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .GetEnumerator().Let(e => { e.MoveNext(); return e.Current; })).Port;

        var (ws, _) = await PairAsync("phone-1", phoneFp, phonePort);
        using var _ws = ws;

        var offered = new TaskCompletionSource<ScreenshotOffer>();
        _server.MessageReceived += m => { if (ScreenshotOffer.From(m) is { } o) offered.TrySetResult(o); };
        await SendAsync(ws, new JsonObject
        {
            ["type"] = "screenshot_ready", ["id"] = "shot-1", ["origin_device_id"] = "phone-1", ["timestamp"] = 1,
            ["mime"] = "image/png", ["size"] = png.Length, ["width"] = 64, ["height"] = 32, ["token"] = "tok-123",
        });
        var offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, requests); // metadata only: nothing fetched until paste

        // Simulated Ctrl+V -> lazy fetch
        var bytes = await ScreenshotFetcher.FetchAsync(offer, TimeSpan.FromSeconds(5));
        Assert.Equal(png, bytes);

        var dib = ImageConvert.ToDib(bytes);
        Assert.Equal(40 + 64 * 32 * 4, dib.Length);
        Assert.Equal(64, BitConverter.ToInt32(dib, 4));
        Assert.Equal(32, BitConverter.ToInt32(dib, 8));
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));

        // Wrong token -> 401
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            ScreenshotFetcher.FetchAsync(offer with { Message = offer.Message with { Token = "nope" } }, TimeSpan.FromSeconds(5)));

        // Wrong pinned fingerprint -> TLS refused before any request is sent
        var before = requests;
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            ScreenshotFetcher.FetchAsync(offer with { Fingerprint = new string('f', 64) }, TimeSpan.FromSeconds(5)));
        Assert.Equal(before, requests);
    }

    private static JsonObject Clip(string id, string origin, long ts, string text) => new()
    {
        ["type"] = "clip_text", ["id"] = id, ["origin_device_id"] = origin, ["timestamp"] = ts, ["text"] = text,
    };

    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.CornflowerBlue);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

internal static class FluentExt
{
    public static TR Let<T, TR>(this T value, Func<T, TR> f) => f(value);
}

public sealed class FeatureNegotiationTests
{
    [Fact]
    public void Hello_without_features_means_v1_set()
    {
        var o = (JsonObject)JsonNode.Parse("""{"type":"hello"}""")!;
        Assert.True(Protocol.ReadFeatures(o).SetEquals(new[] { "clip_text", "screenshot" }));
    }

    [Fact]
    public void Hello_features_are_read_and_welcome_lists_hub_features()
    {
        var o = (JsonObject)JsonNode.Parse("""{"type":"hello","features":["clip_text","files"]}""")!;
        Assert.True(Protocol.ReadFeatures(o).SetEquals(new[] { "clip_text", "files" }));

        var welcome = (JsonObject)JsonNode.Parse(Protocol.BuildWelcome("h", "PC", null, new[] { "screenshot" }))!;
        Assert.Equal("screenshot", welcome["features"]![0]!.GetValue<string>());
    }
}
