using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ContinuityHub.Core;
using Xunit;

namespace ContinuityHub.Tests;

/// <summary>QuickDrop: path safety, the hub's /drop endpoint, and download + resume through the real HubServer.</summary>
public sealed class FileDropTests : IAsyncLifetime
{
    private readonly HubIdentity _identity = HubIdentity.CreateTransient();
    private readonly OutgoingDrops _outgoing = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "quickdrop-tests-" + Guid.NewGuid().ToString("N"));
    private HubServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tmp);
        _server = new HubServer(_identity, new DeviceRegistry(path: null), new LoopGuard("hub"), "hub", "TestPC", 0, IPAddress.Loopback)
        {
            ResolveDropFile = _outgoing.Resolve,
        };
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync(); _identity.Dispose();
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private DropEndpoint Endpoint => new("127.0.0.1", _server.BoundPort, _identity.Fingerprint);

    private string MakeFile(string rel, int size)
    {
        var p = Path.Combine(_tmp, "src", rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, RandomNumberGenerator.GetBytes(size));
        return p;
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData("C:\\Windows\\x.dll", "C_/Windows/x.dll")]
    [InlineData("/etc/passwd", "etc/passwd")]
    [InlineData("dir/./sub/../f.bin", "dir/sub/f.bin")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a<b>.txt", "a_b_.txt")]
    public void Sanitize_strips_traversal_and_bad_names(string raw, string expected) =>
        Assert.Equal(expected, DropPaths.Sanitize(raw));

    [Theory]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("/./")]
    public void Sanitize_rejects_empty_results(string raw) => Assert.Null(DropPaths.Sanitize(raw));

    [Fact]
    public async Task Endpoint_requires_token_and_supports_range()
    {
        var file = MakeFile("x.bin", 5000);
        var offer = _outgoing.Create("hub", new List<(string, string)> { (file, "x.bin") });
        using var http = PinnedClient();
        var url = $"https://127.0.0.1:{_server.BoundPort}/drop/{offer.Id}/0";

        using (var noAuth = await http.GetAsync(url)) Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, url);
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        using (var r = await http.SendAsync(wrong)) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", offer.Token);
        ranged.Headers.Range = new RangeHeaderValue(4000, null);
        using var resp = await http.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal(File.ReadAllBytes(file)[4000..], await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Downloads_folder_keeps_structure_and_reports_progress()
    {
        var a = MakeFile("Photos/a.jpg", 300_000);
        var b = MakeFile("Photos/sub/b.txt", 1234);
        var offer = _outgoing.Create("hub", DropPaths.Expand(new[] { Path.Combine(_tmp, "src", "Photos") }));
        Assert.Equal("Photos", offer.Label);
        var parsed = FileOfferMessage.From(Protocol.TryParse(offer.ToJson())!)!;

        var dest = Path.Combine(_tmp, "dest");
        var reports = new List<DropProgress>();
        var saved = await new DropDownloader(parsed, () => Endpoint, dest, new SyncProgress(reports)).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { Path.Combine(dest, "Photos") }, saved);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(Path.Combine(dest, "Photos", "a.jpg")));
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(Path.Combine(dest, "Photos", "sub", "b.txt")));
        Assert.Equal(offer.TotalSize, reports.Last().DoneBytes);
        Assert.Equal(2, reports.Last().FilesDone);
        Assert.Empty(Directory.GetFiles(Path.Combine(dest, ".partial")));
    }

    [Fact]
    public async Task Resumes_from_partial_file_and_does_not_overwrite_existing()
    {
        var src = MakeFile("big.bin", 1_000_000);
        var offer = _outgoing.Create("hub", new List<(string, string)> { (src, "big.bin") });
        var dest = Path.Combine(_tmp, "dest2");
        Directory.CreateDirectory(Path.Combine(dest, ".partial"));
        File.WriteAllText(Path.Combine(dest, "big.bin"), "an older file with the same name");

        // Simulate an interrupted transfer: the first 600 KB are already on disk.
        var bytes = File.ReadAllBytes(src);
        File.WriteAllBytes(Path.Combine(dest, ".partial", $"{offer.Id}_0.part"), bytes[..600_000]);

        var reports = new List<DropProgress>();
        await new DropDownloader(offer, () => Endpoint, dest, new SyncProgress(reports)).RunAsync(CancellationToken.None);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dest, "big (1).bin")));
        Assert.Equal("an older file with the same name", File.ReadAllText(Path.Combine(dest, "big.bin")));
        Assert.True(reports.First().DoneBytes >= 600_000, "progress should start from the resumed offset");
    }

    [Fact]
    public async Task Waits_for_sender_to_come_back()
    {
        var src = MakeFile("late.bin", 10_000);
        var offer = _outgoing.Create("hub", new List<(string, string)> { (src, "late.bin") });
        var dest = Path.Combine(_tmp, "dest3");
        var calls = 0;
        DropEndpoint? Flaky() => ++calls < 3 ? null : Endpoint; // "phone not connected" twice, then back

        await new DropDownloader(offer, Flaky, dest, null).RunAsync(CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(dest, "late.bin")));
        Assert.True(calls >= 3);
    }

    private HttpClient PinnedClient() => new(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, c, _, _) => c != null && HubIdentity.FingerprintOf(c) == _identity.Fingerprint,
        },
    });

    /// <summary>IProgress that records synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(List<DropProgress> sink) : IProgress<DropProgress>
    {
        public void Report(DropProgress value) { lock (sink) sink.Add(value); }
    }
}
