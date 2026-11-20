using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ContinuityHub.Core;

// QuickDrop (feature id "file_drop"): PROTOCOL.md §6.
// The sender announces files with `file_offer` over WSS; the receiver pulls each one with
// `GET /drop/<offer id>/<index>` (Bearer token, Range for resume) from the sender's HTTPS server,
// and reports back with `file_status`.

public sealed record DropFile(string Path, long Size);

public sealed record FileOfferMessage(
    string Id, string OriginDeviceId, long Timestamp, string Token, string Label, long TotalSize, IReadOnlyList<DropFile> Files)
{
    public const int MaxFiles = 10_000;

    public static FileOfferMessage? From(JsonObject o)
    {
        var id = o.Str("id"); var origin = o.Str("origin_device_id"); var ts = o.Long("timestamp"); var token = o.Str("token");
        if (string.IsNullOrEmpty(id) || id.Length > 128 || string.IsNullOrEmpty(origin) || ts is null
            || string.IsNullOrEmpty(token) || token.Length > 256) return null;
        if (!o.TryGetPropertyValue("files", out var n) || n is not JsonArray arr || arr.Count == 0 || arr.Count > MaxFiles) return null;

        var files = new List<DropFile>();
        foreach (var item in arr)
        {
            if (item is not JsonObject f) return null;
            var path = DropPaths.Sanitize(f.Str("path"));
            var size = f.Long("size");
            if (path == null || size is null or < 0) return null;
            files.Add(new DropFile(path, size.Value));
        }
        var label = o.Str("label");
        if (string.IsNullOrWhiteSpace(label)) label = files.Count == 1 ? Path.GetFileName(files[0].Path) : $"{files.Count} files";
        return new FileOfferMessage(id, origin, ts.Value, token, label, files.Sum(f => f.Size), files);
    }

    public string ToJson() => new JsonObject
    {
        ["type"] = Protocol.FileOffer, ["id"] = Id, ["origin_device_id"] = OriginDeviceId, ["timestamp"] = Timestamp,
        ["token"] = Token, ["label"] = Label, ["total_size"] = TotalSize,
        ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["size"] = f.Size }).ToArray()),
    }.ToJsonString();
}

/// <summary>Receiver → sender progress report. Deliberately has no "id" envelope, so the loop guard never drops repeats.</summary>
public sealed record FileStatusMessage(string OfferId, string State, long DoneBytes, long TotalBytes, int FilesDone, int FileCount, string? Message)
{
    public const string Progress = "progress", Done = "done", Failed = "failed";

    public static FileStatusMessage? From(JsonObject o)
    {
        var id = o.Str("offer_id"); var state = o.Str("state");
        if (string.IsNullOrEmpty(id) || state is not (Progress or Done or Failed)) return null;
        return new FileStatusMessage(id, state, o.Long("done_bytes") ?? 0, o.Long("total_bytes") ?? 0,
            (int)(o.Long("files_done") ?? 0), (int)(o.Long("file_count") ?? 0), o.Str("message"));
    }

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["type"] = Protocol.FileStatus, ["offer_id"] = OfferId, ["state"] = State, ["done_bytes"] = DoneBytes,
            ["total_bytes"] = TotalBytes, ["files_done"] = FilesDone, ["file_count"] = FileCount,
        };
        if (Message != null) o["message"] = Message;
        return o.ToJsonString();
    }
}

public static class DropPaths
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Makes a peer-supplied relative path safe: '/'-separated, no roots, no "." or ".." segments,
    /// Windows-invalid characters replaced. Returns null if nothing usable is left.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1024) return null;
        var parts = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Select(c => Invalid.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.'))
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .Select(p => IsReservedName(p) ? "_" + p : p)
            .ToList();
        if (parts.Count == 0 || parts.Count > 32) return null;
        return string.Join('/', parts);
    }

    private static bool IsReservedName(string p)
    {
        var stem = p.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
    }

    /// <summary>Expands files and folders (recursively) into (absolute path, relative wire path) pairs.</summary>
    public static List<(string Full, string Rel)> Expand(IEnumerable<string> paths)
    {
        var result = new List<(string, string)>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) result.Add((Path.GetFullPath(p), Path.GetFileName(p)));
            else if (Directory.Exists(p))
            {
                var root = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
                var parent = Path.GetDirectoryName(root) ?? root;
                foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    result.Add((f, Path.GetRelativePath(parent, f).Replace('\\', '/')));
            }
        }
        return result;
    }

    /// <summary>"name.ext" → "name (1).ext" … until the path is free.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}

/// <summary>Files this PC has offered. Served from <c>GET /drop/&lt;id&gt;/&lt;index&gt;</c> until they expire.</summary>
public sealed class OutgoingDrops
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private sealed record Entry(FileOfferMessage Offer, IReadOnlyList<string> FullPaths, DateTimeOffset Created);
    private readonly ConcurrentDictionary<string, Entry> _offers = new();

    public FileOfferMessage Create(string originDeviceId, IReadOnlyList<(string Full, string Rel)> files, string? label = null)
    {
        var token = DeviceRegistry.Base64Url(RandomNumberGenerator.GetBytes(32));
        var dropFiles = files.Select(f => new DropFile(f.Rel, new FileInfo(f.Full).Length)).ToList();
        label ??= dropFiles.Count == 1 ? Path.GetFileName(files[0].Full) : LabelFor(files);
        var offer = new FileOfferMessage(Protocol.NewId(), originDeviceId, Protocol.NowMs(), token, label, dropFiles.Sum(f => f.Size), dropFiles);
        _offers[offer.Id] = new Entry(offer, files.Select(f => f.Full).ToList(), DateTimeOffset.UtcNow);
        foreach (var old in _offers.Where(kv => DateTimeOffset.UtcNow - kv.Value.Created > Ttl).Select(kv => kv.Key).ToList())
            _offers.TryRemove(old, out _);
        return offer;
    }

    private static string LabelFor(IReadOnlyList<(string Full, string Rel)> files)
    {
        var tops = files.Select(f => f.Rel.Split('/')[0]).Distinct().ToList();
        return tops.Count == 1 && files[0].Rel.Contains('/') ? tops[0] : $"{files.Count} files";
    }

    /// <summary>Returns the absolute path for an authorized request, or null.</summary>
    public string? Resolve(string id, int index, string? bearer)
    {
        if (bearer == null || !_offers.TryGetValue(id, out var e)) return null;
        if (DateTimeOffset.UtcNow - e.Created > Ttl) { _offers.TryRemove(id, out _); return null; }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(e.Offer.Token), Encoding.UTF8.GetBytes(bearer))) return null;
        return index >= 0 && index < e.FullPaths.Count ? e.FullPaths[index] : null;
    }
}

/// <summary>Where to download an offer from right now (the sender may have reconnected from a new address).</summary>
public sealed record DropEndpoint(string Host, int Port, string Fingerprint);

public sealed record DropProgress(long DoneBytes, long TotalBytes, int FilesDone, int FileCount, double BytesPerSecond, string? CurrentFile)
{
    public int Percent => TotalBytes <= 0 ? 100 : (int)Math.Min(100, DoneBytes * 100 / TotalBytes);
}

/// <summary>
/// Pulls every file of an offer into <c>destRoot</c>, keeping relative folders. Partial files live in
/// <c>destRoot\.partial</c>; after a network error it waits and continues from the bytes it already has
/// (HTTP Range). Gives up after <see cref="StallLimit"/> without any progress.
/// </summary>
public sealed class DropDownloader
{
    public static TimeSpan StallLimit { get; set; } = TimeSpan.FromMinutes(15);

    private readonly FileOfferMessage _offer;
    private readonly Func<DropEndpoint?> _endpoint;
    private readonly string _destRoot;
    private readonly IProgress<DropProgress>? _progress;
    private long _done;
    private int _filesDone;
    private double _rate;
    private long _rateBytes;
    private DateTime _rateAt = DateTime.UtcNow;

    public DropDownloader(FileOfferMessage offer, Func<DropEndpoint?> endpoint, string destRoot, IProgress<DropProgress>? progress)
    {
        _offer = offer; _endpoint = endpoint; _destRoot = destRoot; _progress = progress;
    }

    public string PartialDir => Path.Combine(_destRoot, ".partial");

    /// <summary>Returns the saved paths (top-level items, for "show in folder").</summary>
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(PartialDir);
        File.SetAttributes(PartialDir, File.GetAttributes(PartialDir) | FileAttributes.Hidden);
        var saved = new List<string>();
        var topLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // first segment → chosen name on disk

        for (var i = 0; i < _offer.Files.Count; i++)
        {
            var f = _offer.Files[i];
            var part = Path.Combine(PartialDir, $"{_offer.Id}_{i}.part");
            await DownloadWithRetryAsync(i, f, part, ct).ConfigureAwait(false);

            // A folder ("Photos/a.jpg") keeps one unique top-level name for all its files.
            var segs = f.Path.Split('/');
            if (!topLevel.TryGetValue(segs[0], out var top))
            {
                top = Path.GetFileName(DropPaths.Unique(Path.Combine(_destRoot, segs[0])));
                topLevel[segs[0]] = top;
                saved.Add(Path.Combine(_destRoot, top));
            }
            segs[0] = top;
            var dest = Path.Combine(new[] { _destRoot }.Concat(segs).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(part, segs.Length == 1 ? dest : DropPaths.Unique(dest));
            _filesDone++;
            Report(f.Path, force: true);
        }
        return saved;
    }

    private async Task DownloadWithRetryAsync(int index, DropFile file, string part, CancellationToken ct)
    {
        var lastProgress = DateTime.UtcNow;
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            var before = File.Exists(part) ? new FileInfo(part).Length : 0;
            try
            {
                await DownloadOnceAsync(index, file, part, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or TaskCanceledException or EndpointUnavailableException)
            {
                var now = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (now > before) { lastProgress = DateTime.UtcNow; delay = TimeSpan.FromSeconds(1); }
                if (DateTime.UtcNow - lastProgress > StallLimit)
                    throw new IOException($"No progress for {StallLimit.TotalMinutes:0} minutes: {e.Message}", e);
                Log.Warn($"QuickDrop {_offer.Id[..8]} file {index}: {e.Message}; retrying in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private sealed class EndpointUnavailableException() : Exception("Sender is not connected");

    private async Task DownloadOnceAsync(int index, DropFile file, string part, CancellationToken ct)
    {
        var ep = _endpoint() ?? throw new EndpointUnavailableException();
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size) { File.Delete(part); have = 0; }
        _done = _offer.Files.Take(index).Sum(f => f.Size) + have;
        if (have == file.Size && file.Size > 0) return;

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(HubIdentity.FingerprintOf(cert), ep.Fingerprint, StringComparison.Ordinal),
            },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var host = ep.Host.Contains(':') ? $"[{ep.Host}]" : ep.Host;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}:{ep.Port}/drop/{Uri.EscapeDataString(_offer.Id)}/{index}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _offer.Token);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have == file.Size) return;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new InvalidOperationException($"The sender no longer offers this file ({(int)resp.StatusCode})");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Sender answered {(int)resp.StatusCode}");

        var append = resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.From == have;
        if (!append) { have = 0; _done = _offer.Files.Take(index).Sum(f => f.Size); }

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 16];
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(30)); // a silent connection counts as dropped
                var n = await input.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                if (n == 0) break;
                await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                have += n; _done += n;
                if (have > file.Size) throw new InvalidDataException("Sender sent more bytes than announced");
                Report(file.Path);
            }
        }
        if (have != file.Size) throw new IOException($"Connection ended at {have} of {file.Size} bytes");
    }

    private DateTime _lastReport = DateTime.MinValue;

    private void Report(string current, bool force = false)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _rateAt).TotalSeconds;
        if (dt >= 0.5)
        {
            var inst = (_done - _rateBytes) / dt;
            _rate = _rate == 0 ? inst : _rate * 0.6 + inst * 0.4;
            _rateBytes = _done; _rateAt = now;
        }
        if (!force && (now - _lastReport).TotalMilliseconds < 400) return;
        _lastReport = now;
        _progress?.Report(new DropProgress(_done, _offer.TotalSize, _filesDone, _offer.Files.Count, Math.Max(0, _rate), current));
    }

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.0} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.00} GB",
    };

    public static string Describe(DropProgress p) =>
        $"{p.Percent}% · {FormatBytes(p.DoneBytes)} of {FormatBytes(p.TotalBytes)} · {FormatBytes((long)p.BytesPerSecond)}/s" +
        (p.FileCount > 1 ? $" · {p.FilesDone} of {p.FileCount} files" : "");
}
