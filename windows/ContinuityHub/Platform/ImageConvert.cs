using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ContinuityHub.Platform;

/// <summary>Turns PNG/JPEG/WebP bytes from the phone into clipboard formats.</summary>
public static class ImageConvert
{
    private const int BitmapInfoHeaderSize = 40;

    /// <summary>Packed CF_DIB: BITMAPINFOHEADER + bottom-up 32bpp BGRA pixels with alpha forced opaque.</summary>
    public static byte[] ToDib(byte[] encoded)
    {
        using var ms = new MemoryStream(encoded);
        using var src = new Bitmap(ms);
        int w = src.Width, h = src.Height;
        int stride = w * 4;
        var dib = new byte[BitmapInfoHeaderSize + stride * h];

        // BITMAPINFOHEADER
        BitConverter.TryWriteBytes(dib.AsSpan(0), BitmapInfoHeaderSize);
        BitConverter.TryWriteBytes(dib.AsSpan(4), w);
        BitConverter.TryWriteBytes(dib.AsSpan(8), h);          // positive => bottom-up
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);  // planes
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32); // bpp
        BitConverter.TryWriteBytes(dib.AsSpan(16), 0);         // BI_RGB
        BitConverter.TryWriteBytes(dib.AsSpan(20), stride * h);
        BitConverter.TryWriteBytes(dib.AsSpan(24), 3780);      // 96 dpi
        BitConverter.TryWriteBytes(dib.AsSpan(28), 3780);

        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                for (int x = 3; x < stride; x += 4) row[x] = 0xFF; // some apps treat BI_RGB alpha as transparency
                Buffer.BlockCopy(row, 0, dib, BitmapInfoHeaderSize + (h - 1 - y) * stride, stride);
            }
        }
        finally { src.UnlockBits(data); }
        return dib;
    }

    /// <summary>Bytes for the registered "PNG" clipboard format. Re-encodes if the source isn't PNG.</summary>
    public static byte[] ToPng(byte[] encoded)
    {
        if (encoded.Length > 8 && encoded[0] == 0x89 && encoded[1] == 0x50 && encoded[2] == 0x4E && encoded[3] == 0x47)
            return encoded;
        using var ms = new MemoryStream(encoded);
        using var src = new Bitmap(ms);
        using var outMs = new MemoryStream();
        src.Save(outMs, ImageFormat.Png);
        return outMs.ToArray();
    }
}
