using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace ContinuityHub.Core;

/// <summary>Data plane: authenticated HTTPS GET to the phone, pinned to the cert it announced in hello.</summary>
public static class ScreenshotFetcher
{
    public const long MaxBytes = 64L * 1024 * 1024;

    public static async Task<byte[]> FetchAsync(ScreenshotOffer offer, TimeSpan timeout, CancellationToken ct = default)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            UseProxy = false, // never route LAN traffic through a configured proxy
            SslOptions = new SslClientAuthenticationOptions
            {
                // The phone's cert is self-signed; trust it only if it is byte-identical to the pinned one.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(HubIdentity.FingerprintOf(cert), offer.Fingerprint, StringComparison.Ordinal),
            },
        };
        using var http = new HttpClient(handler) { Timeout = timeout };

        var host = offer.Ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{offer.Ip}]" : offer.Ip.ToString();
        var url = $"https://{host}:{offer.Port}/shot/{Uri.EscapeDataString(offer.Message.Id)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", offer.Message.Token);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Phone answered {(int)resp.StatusCode}");
        if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Screenshot too large");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) throw new InvalidDataException("Screenshot too large");
        }
        return ms.ToArray();
    }
}
