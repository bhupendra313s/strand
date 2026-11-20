using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContinuityHub.Core;
using Xunit;

namespace ContinuityHub.Tests;

/// <summary>Screen share wire format: offer parsing and the length-prefixed frame stream.</summary>
public sealed class ScreenShareTests
{
    private static byte[] Frame(byte[] payload)
    {
        var n = payload.Length;
        return new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }.Concat(payload).ToArray();
    }

    private static async Task<List<byte[]>> ReadAll(Stream s, TimeSpan? idle = null)
    {
        var list = new List<byte[]>();
        await foreach (var f in ScreenStream.ReadFramesAsync(s, idle ?? TimeSpan.FromSeconds(5), CancellationToken.None)) list.Add(f);
        return list;
    }

    [Fact]
    public async Task Reads_frames_and_skips_keepalives()
    {
        var a = new byte[] { 0xFF, 0xD8, 1, 2, 3 }; var b = new byte[70_000]; new Random(1).NextBytes(b);
        var bytes = Frame(a).Concat(Frame(Array.Empty<byte>())).Concat(Frame(b)).ToArray();
        var frames = await ReadAll(new MemoryStream(bytes));
        Assert.Equal(2, frames.Count);
        Assert.Equal(a, frames[0]);
        Assert.Equal(b, frames[1]);
    }

    [Fact]
    public async Task Truncated_frame_ends_stream_quietly()
    {
        var bytes = Frame(new byte[100]).Take(50).ToArray();
        Assert.Empty(await ReadAll(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Rejects_absurd_frame_length()
    {
        var bytes = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Silent_stream_counts_as_dropped()
    {
        await Assert.ThrowsAsync<IOException>(() => ReadAll(new NeverEndingStream(), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Offer_parses_and_validates()
    {
        var ok = Protocol.TryParse("""{"type":"screen_offer","id":"s1","origin_device_id":"d","timestamp":1,"token":"t","width":576,"height":1280}""")!;
        var o = ScreenOfferMessage.From(ok)!;
        Assert.Equal((576, 1280), (o.Width, o.Height));
        Assert.Null(ScreenOfferMessage.From(Protocol.TryParse("""{"type":"screen_offer","id":"s1","origin_device_id":"d","timestamp":1}""")!));
        Assert.Contains("\"session_id\":\"s1\"", ScreenOfferMessage.BuildStop("s1"));
    }

    /// <summary>A stream that never returns data (like a phone that stopped sending).</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
