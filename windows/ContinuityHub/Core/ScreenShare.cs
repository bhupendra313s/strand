using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ContinuityHub.Core;

// Screen share (feature "screen_share"), PROTOCOL.md §7. The phone announces `screen_offer`; the hub pulls
// `GET /screen/<session>` from the phone's fetch server: a stream of [4-byte big-endian length][JPEG] frames,
// where length 0 is a keep-alive. Either side ends it with `screen_stop`.

public sealed record ScreenOfferMessage(string Id, string OriginDeviceId, long Timestamp, string Token, int Width, int Height)
{
    public static ScreenOfferMessage? From(JsonObject o)
    {
        var id = o.Str("id"); var origin = o.Str("origin_device_id"); var ts = o.Long("timestamp"); var token = o.Str("token");
        if (string.IsNullOrEmpty(id) || id.Length > 128 || string.IsNullOrEmpty(origin) || ts is null
            || string.IsNullOrEmpty(token) || token.Length > 256) return null;
        return new ScreenOfferMessage(id, origin, ts.Value, token, (int)(o.Long("width") ?? 0), (int)(o.Long("height") ?? 0));
    }

    public static string BuildStop(string sessionId) =>
        new JsonObject { ["type"] = Protocol.ScreenStop, ["session_id"] = sessionId }.ToJsonString();
}

public static class ScreenStream
{
    /// <summary>Largest frame accepted; a 1920-px JPEG is far smaller.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>Opens the frame stream from the phone, pinned to the cert it announced in hello.</summary>
    public static async Task<(HttpClient Client, Stream Body)> OpenAsync(DropEndpoint ep, string sessionId, string token, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(HubIdentity.FingerprintOf(cert), ep.Fingerprint, StringComparison.Ordinal),
            },
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var host = ep.Host.Contains(':') ? $"[{ep.Host}]" : ep.Host;
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}:{ep.Port}/screen/{Uri.EscapeDataString(sessionId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Phone answered {(int)resp.StatusCode}");
            return (http, await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        }
        catch { http.Dispose(); throw; }
    }

    /// <summary>
    /// Yields JPEG frames until the stream ends. Keep-alives are skipped, but a stream silent for
    /// <paramref name="idleTimeout"/> (the phone sends one every 2 s) is treated as dropped.
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ReadFramesAsync(Stream body, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[4];
        while (true)
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(idleTimeout);
                try { if (!await FillAsync(body, header, idle.Token).ConfigureAwait(false)) yield break; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Screen stream went silent"); }
            }
            var len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (len == 0) continue;
            if (len < 0 || len > MaxFrameBytes) throw new InvalidDataException($"Bad frame length {len}");
            var frame = new byte[len];
            if (!await FillAsync(body, frame, ct).ConfigureAwait(false)) yield break;
            yield return frame;
        }
    }

    private static async Task<bool> FillAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
