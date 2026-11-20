using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ContinuityHub.Core;

namespace ContinuityHub.Platform;

/// <summary>
/// Message-only window that (a) listens for local clipboard changes and (b) owns the clipboard
/// with delayed rendering, so screenshot bytes are fetched from the phone only when something pastes.
/// Must be created and used on the UI (STA) thread.
/// </summary>
internal sealed class ClipboardBridge : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_RENDERFORMAT = 0x0305;
    private const int WM_RENDERALLFORMATS = 0x0306;
    private const int WM_DESTROYCLIPBOARD = 0x0307;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    private readonly uint _cfPng;
    private readonly uint _cfExcludeMonitor;
    private readonly uint _cfCanIncludeHistory;
    private readonly uint _cfCanUploadCloud;

    private ScreenshotOffer? _pending;
    private byte[]? _pendingImage;

    /// <summary>Raised (on the UI thread) when the user copies text on this PC.</summary>
    public event Action<string>? LocalTextCopied;

    /// <summary>Raised when a lazy fetch starts/finishes, so the UI can show feedback.</summary>
    public event Action<ScreenshotOffer, byte[]?, Exception?>? LazyFetchCompleted;

    public ClipboardBridge()
    {
        CreateHandle(new CreateParams { Caption = "ContinuityHubClipboard", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        _cfPng = RegisterClipboardFormat("PNG");
        _cfExcludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _cfCanIncludeHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _cfCanUploadCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
        if (!AddClipboardFormatListener(Handle)) throw new Win32Exception();
    }

    // ------------------------------------------------------------------ writes from remote

    public void SetRemoteText(string text)
    {
        WithClipboard(() =>
        {
            EmptyClipboard();
            _pending = null; _pendingImage = null;
            SetGlobal(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            SetDword(_cfCanUploadCloud, 0); // zero-cloud: keep it out of Microsoft's cloud clipboard
        });
    }

    /// <summary>Advertise CF_DIB + PNG without any bytes. Windows asks us via WM_RENDERFORMAT on paste.</summary>
    public void OfferLazyImage(ScreenshotOffer offer)
    {
        WithClipboard(() =>
        {
            EmptyClipboard();
            _pending = offer; _pendingImage = null;
            SetClipboardData(CF_DIB, IntPtr.Zero);
            SetClipboardData(_cfPng, IntPtr.Zero);
            // Without these, Win+V clipboard history renders delayed formats immediately (defeating lazy fetch).
            SetDword(_cfCanIncludeHistory, 0);
            SetDword(_cfCanUploadCloud, 0);
        });
    }

    // ------------------------------------------------------------------ window procedure

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                break;
            case WM_RENDERFORMAT:
                // Clipboard is already open by the requesting app; only SetClipboardData is allowed here.
                RenderFormat((uint)m.WParam.ToInt64());
                return;
            case WM_RENDERALLFORMATS:
                // We're exiting. Don't block shutdown on the network: drop the lazy offer instead.
                if (OpenClipboard(Handle))
                {
                    if (GetClipboardOwner() == Handle) EmptyClipboard();
                    CloseClipboard();
                }
                return;
            case WM_DESTROYCLIPBOARD:
                _pending = null; _pendingImage = null; // someone else took the clipboard
                return;
        }
        base.WndProc(ref m);
    }

    private void RenderFormat(uint format)
    {
        var offer = _pending;
        if (offer == null || (format != CF_DIB && format != _cfPng)) return;
        try
        {
            if (_pendingImage == null)
            {
                Log.Info($"Paste requested: fetching screenshot {offer.Message.Id} from {offer.DeviceName}");
                // Blocking is intentional: WM_RENDERFORMAT must supply the data before returning.
                _pendingImage = System.Threading.Tasks.Task.Run(() =>
                    ScreenshotFetcher.FetchAsync(offer, TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
                LazyFetchCompleted?.Invoke(offer, _pendingImage, null);
            }
            var bytes = format == CF_DIB ? ImageConvert.ToDib(_pendingImage) : ImageConvert.ToPng(_pendingImage);
            SetGlobal(format, bytes);
        }
        catch (Exception e)
        {
            Log.Warn($"Lazy fetch failed: {e.Message}");
            LazyFetchCompleted?.Invoke(offer, null, e);
        }
    }

    private void OnClipboardUpdate()
    {
        if (GetClipboardOwner() == Handle) return; // our own write (remote text / lazy image)

        string? text = null;
        WithClipboard(() =>
        {
            if (IsClipboardFormatAvailable(_cfExcludeMonitor)) return;        // password managers
            if (IsClipboardFormatAvailable(_cfCanIncludeHistory) && ReadDword(_cfCanIncludeHistory) == 0) return;
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return;
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return;
            try { text = Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        });

        if (!string.IsNullOrEmpty(text) && System.Text.Encoding.UTF8.GetByteCount(text) <= Protocol.MaxTextBytes)
            LocalTextCopied?.Invoke(text);
    }

    // ------------------------------------------------------------------ helpers

    private void WithClipboard(Action action)
    {
        // Another process may hold the clipboard briefly; retry a few times.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(Handle))
            {
                try { action(); }
                finally { CloseClipboard(); }
                return;
            }
            Thread.Sleep(15);
        }
        Log.Warn("Could not open clipboard (held by another app)");
    }

    private static void SetGlobal(uint format, byte[] bytes)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (h == IntPtr.Zero) throw new OutOfMemoryException();
        var p = GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero) GlobalFree(h); // on success the system owns h
    }

    private static void SetDword(uint format, int value) => SetGlobal(format, BitConverter.GetBytes(value));

    private static int ReadDword(uint format)
    {
        var h = GetClipboardData(format);
        if (h == IntPtr.Zero) return -1;
        var p = GlobalLock(h);
        try { return p == IntPtr.Zero ? -1 : Marshal.ReadInt32(p); }
        finally { GlobalUnlock(h); }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
}
