using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ContinuityHub.Core;
using ContinuityHub.Features;
using ContinuityHub.Platform;
using ContinuityHub.UI;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ContinuityHub;

/// <summary>Composition root: services, features, tray icon, and the main window.</summary>
public sealed class HubController : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ContinuityHub";

    private readonly Dispatcher _ui;
    private readonly ClipboardBridge _clipboard;
    private readonly DnsSdAdvertiser _mdns = new();
    private readonly WinForms.NotifyIcon _tray;
    private readonly FileDropFeature _fileDrop = new();
    private MainWindow? _window;
    private MainViewModel? _vm;

    public HubController(string dataDir)
    {
        DataDir = dataDir;
        _ui = Dispatcher.CurrentDispatcher;

        Config = HubConfig.LoadOrCreate(dataDir);
        Identity = HubIdentity.LoadOrCreate(Config);
        Registry = new DeviceRegistry(System.IO.Path.Combine(dataDir, "devices.json"));
        Guard = new LoopGuard(Config.HubId);
        Server = new HubServer(Identity, Registry, Guard, Config.HubId, Config.HubName, Config.Port);
        _clipboard = new ClipboardBridge();

        // ---- features: register new capabilities here ----
        Features.Register(new ClipboardTextFeature(_clipboard));
        Features.Register(new ScreenshotFeature(_clipboard));
        Features.Register(_fileDrop);
        Features.Register(new ScreenShareFeature());

        _tray = BuildTray();

        Server.SessionsChanged += () => _ui.BeginInvoke(() => { UpdateTray(); StatusChanged?.Invoke(); });
        Server.DevicePaired += name => _ui.BeginInvoke(() =>
        {
            Notify("Phone paired", $"{name} is now paired with this PC.", force: true);
            DevicePaired?.Invoke(name);
        });
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => _ui.BeginInvoke(Readvertise);

        // A second launch of the exe signals this: bring the window forward on the pairing page.
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowPairingEventName);
        new Thread(() => { while (showSignal.WaitOne()) _ui.BeginInvoke(() => ShowWindow(Page.Devices)); })
            { IsBackground = true, Name = "show-signal" }.Start();

        // `ContinuityHub.exe --send <paths>` (e.g. from Explorer's Send to menu) queues files for QuickDrop.
        var sendSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SendFilesEventName);
        new Thread(() => { while (sendSignal.WaitOne()) _ui.BeginInvoke(DrainSendQueue); })
            { IsBackground = true, Name = "send-signal" }.Start();
    }

    private void DrainSendQueue()
    {
        if (!System.IO.Directory.Exists(Program.SendQueueDir)) return;
        foreach (var f in System.IO.Directory.GetFiles(Program.SendQueueDir, "*.txt"))
        {
            string[] paths;
            try { paths = System.IO.File.ReadAllLines(f); System.IO.File.Delete(f); } catch { continue; }
            var result = SendFiles(paths.Where(p => p.Length > 0));
            Log.Info($"QuickDrop --send: {result}");
            Notify("QuickDrop", result, force: true);
        }
    }

    public string DataDir { get; }
    public HubConfig Config { get; }
    public HubIdentity Identity { get; }
    public DeviceRegistry Registry { get; }
    public LoopGuard Guard { get; }
    public HubServer Server { get; }
    public FeatureHost Features { get; } = new();
    public ActivityFeed Activity { get; } = new();
    public IPAddress? AdvertisedIp { get; private set; }

    /// <summary>Connections, pairings, or pause changed. Raised on the UI thread.</summary>
    public event Action? StatusChanged;
    public event Action<string>? DevicePaired;

    public async Task StartAsync(bool showWindow)
    {
        try
        {
            await Server.StartAsync();
        }
        catch (Exception e)
        {
            Log.Error("Hub failed to start", e);
            System.Windows.MessageBox.Show($"Strand couldn't listen on port {Config.Port}.\n\n{e.Message}\n\n" +
                "Change \"port\" in config.json (in the data folder) and restart.", "Strand",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Quit();
            return;
        }

        Features.Start(new FeatureContext
        {
            Server = Server, Guard = Guard, Config = Config, Activity = Activity,
            Ui = SynchronizationContext.Current!, Notify = (t, b) => Notify(t, b),
        });
        Readvertise();
        UpdateTray();
        ApplyPhoneColors();
        Server.SessionsChanged += () => _ui.BeginInvoke(CapturePhonePalette);

        if (showWindow || Registry.All.Count == 0) ShowWindow(Registry.All.Count == 0 ? Page.Devices : Page.Home);
        // Paths queued by `--send` before the hub was running go out once a phone connects.
        Server.SessionsChanged += () => _ui.BeginInvoke(() => { if (Server.Sessions.Any(s => s.Supports(FeatureIds.FileDrop))) DrainSendQueue(); });
        if (Registry.All.Count == 0) _vm?.Pairing.Start();
    }

    // ------------------------------------------------------------------ actions used by the UI

    public void SetPaused(bool paused)
    {
        Config.Paused = paused; Config.Save();
        UpdateTray();
        StatusChanged?.Invoke();
    }

    public void SetHubName(string name)
    {
        Config.HubName = name; Config.Save();
        Server.HubName = name;
        AdvertisedIp = null; // force re-registration under the new name
        Readvertise();
    }

    // ------------------------------------------------------------------ phone colours (Material You)

    /// <summary>Remembers the palette a connecting phone sent, and re-tints the hub if it changed.</summary>
    private void CapturePhonePalette()
    {
        var palette = Server.Sessions.Select(s => s.Palette).FirstOrDefault(p => p != null);
        if (palette == null) return;
        var json = palette.ToJsonString();
        if (json == Config.PhonePalette) return;
        Config.PhonePalette = json; Config.Save();
        Log.Info("Phone colours updated");
        ApplyPhoneColors();
    }

    public void ApplyPhoneColors()
    {
        JsonObject? palette = null;
        try { if (Config.PhonePalette != null) palette = JsonNode.Parse(Config.PhonePalette) as JsonObject; } catch { }
        AccentTheme.Apply(palette, Config.MatchPhoneColors);
        StatusChanged?.Invoke();
    }

    public void SetMatchPhoneColors(bool on)
    {
        Config.MatchPhoneColors = on; Config.Save();
        ApplyPhoneColors();
    }

    /// <summary>QuickDrop: offers files/folders to the connected phones. Returns a short status line for the UI.</summary>
    public string SendFiles(System.Collections.Generic.IEnumerable<string> paths)
    {
        if (Config.Paused) return "Sync is paused";
        try { return _fileDrop.Send(paths); }
        catch (Exception e) { Log.Error("QuickDrop send failed", e); return $"Couldn't send: {e.Message}"; }
    }

    public void Forget(string deviceId)
    {
        Registry.Forget(deviceId);
        Server.Disconnect(deviceId);
        Log.Info($"Forgot device {deviceId[..Math.Min(8, deviceId.Length)]}");
        StatusChanged?.Invoke();
    }

    /// <summary>Opens a pairing window on the server and returns the QR payload.</summary>
    public string BuildPairingPayload(IPAddress ip, TimeSpan validFor) => new JsonObject
    {
        ["v"] = 1,
        ["ip"] = ip.ToString(),
        ["port"] = Server.BoundPort,
        ["fingerprint"] = Identity.Fingerprint,
        ["hub_id"] = Config.HubId,
        ["name"] = Config.HubName,
        ["pair_code"] = Server.BeginPairing(validFor),
    }.ToJsonString();

    public bool StartWithWindows
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public void ShowWindow(Page? page = null)
    {
        if (_window == null)
        {
            _vm ??= new MainViewModel(this, _ui);
            _window = new MainWindow { DataContext = _vm, Icon = WindowIcon() };
            _window.Closing += (_, e) =>
            {
                if (_quitting) return;
                e.Cancel = true; // keep running in the tray
                _window.Hide();
                if (!Config.TrayHintShown)
                {
                    Config.TrayHintShown = true; Config.Save();
                    Notify("Strand is still running", "Sync continues in the background. Find it in the system tray.", force: true);
                }
            };
        }
        if (page is { } p) _vm!.CurrentPage = p;
        _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    private bool _quitting;

    public void Quit()
    {
        _quitting = true;
        System.Windows.Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ tray

    private WinForms.NotifyIcon BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Strand", null, (_, _) => ShowWindow());
        menu.Items.Add("Pair a new phone…", null, (_, _) => { ShowWindow(Page.Devices); _vm?.PairCommand.Execute(null); });
        var pause = new WinForms.ToolStripMenuItem("Pause sync") { CheckOnClick = true };
        pause.Click += (_, _) => { SetPaused(pause.Checked); };
        menu.Opening += (_, _) => pause.Checked = Config.Paused;
        menu.Items.Add(pause);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        var icon = new WinForms.NotifyIcon { Icon = MakeIcon(), Text = "Strand", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowWindow(); };
        icon.BalloonTipClicked += (_, _) => ShowWindow(Page.Home);
        return icon;
    }

    private void UpdateTray()
    {
        var n = Server.Sessions.Count;
        var text = Config.Paused ? "Paused" : n == 0 ? "Waiting for devices" : $"{n} device{(n == 1 ? "" : "s")} connected";
        _tray.Text = $"Strand · {text}";
    }

    private void Notify(string title, string body, bool force = false)
    {
        if (!force && !Config.Notifications) return;
        _tray.ShowBalloonTip(4000, title, body, WinForms.ToolTipIcon.None);
    }

    private void Readvertise()
    {
        var ip = LocalNet.CandidateIPv4().FirstOrDefault();
        if (ip == null || ip.Equals(AdvertisedIp) || Server.BoundPort == 0) return;
        AdvertisedIp = ip;
        _mdns.Register(Config.HubName, ip, Server.BoundPort, Config.HubId, Identity.Fingerprint);
        StatusChanged?.Invoke();
    }

    private System.Windows.Media.ImageSource WindowIcon() =>
        System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(_tray.Icon!.Handle, System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

    private static Icon MakeIcon()
    {
        using var bmp = StrandBitmap(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>The Strand logo: two points joined by one line, on a soft lavender tile (same as the Android icon).</summary>
    public static Bitmap StrandBitmap(int px)
    {
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (var tile = new SolidBrush(Color.FromArgb(0xEA, 0xDD, 0xFF)))
        using (var path = RoundedRect(new Rectangle(0, 0, px - 1, px - 1), Math.Max(2, px / 4)))
            g.FillPath(tile, path);
        var ink = Color.FromArgb(0x4F, 0x37, 0x8B);
        float r = px * 0.10f, y = px / 2f, x1 = px * 0.27f, x2 = px * 0.73f;
        using (var pen = new Pen(ink, Math.Max(1.5f, px * 0.06f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, x1, y, x2, y);
        using (var dot = new SolidBrush(ink))
        {
            g.FillEllipse(dot, x1 - r, y - r, 2 * r, 2 * r);
            g.FillEllipse(dot, x2 - r, y - r, 2 * r, 2 * r);
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    public void Dispose()
    {
        _tray.Visible = false;
        Features.StopAll();
        _mdns.Dispose();
        _clipboard.Dispose();
        try { Server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _tray.Dispose();
    }
}
