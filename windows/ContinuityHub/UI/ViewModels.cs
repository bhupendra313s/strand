using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ContinuityHub.Core;

namespace ContinuityHub.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    public RelayCommand(Action<object?> run) => _run = run;
    public RelayCommand(Action run) => _run = _ => run();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run(parameter);
}

/// <summary>Two-way binding of a RadioButton to an enum value: ConverterParameter is the enum member name.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) =>
        value is true ? Enum.Parse(t, parameter.ToString()!) : Binding.DoNothing;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object parameter, CultureInfo c) =>
        (value == null || value is string { Length: 0 }) ^ (parameter as string == "invert") ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => throw new NotSupportedException();
}

public enum Page { Home, Devices, Features, Settings }

internal static class Time
{
    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalSeconds < 45) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return t.ToString("t", CultureInfo.CurrentCulture);
        return t.ToString("MMM d, t", CultureInfo.CurrentCulture);
    }
}

// =================================================================== activity

public sealed class ActivityVm : Observable
{
    private readonly ActivityItem _item;
    private ImageSource? _thumb;

    internal ActivityVm(ActivityItem item, Dispatcher ui)
    {
        _item = item;
        item.Updated += () => ui.BeginInvoke(() => { _thumb = null; RaiseAll(); });
        ActionCommand = new RelayCommand(() => _item.Action?.Invoke());
    }

    public string Glyph => _item.Glyph;
    public string Title => _item.Title;
    public string Detail => _item.Detail;
    public string TimeText => Time.Ago(_item.Time);
    public bool IsIncoming => _item.Direction == ActivityDirection.Incoming;
    public string DirectionGlyph => _item.Direction switch
    {
        ActivityDirection.Incoming => "",  // download arrow
        ActivityDirection.Outgoing => "",  // upload arrow
        _ => "",
    };
    public string DirectionText => _item.Direction switch
    {
        ActivityDirection.Incoming => "Received",
        ActivityDirection.Outgoing => "Sent",
        _ => "",
    };
    public string? ActionLabel => _item.ActionLabel;
    public ICommand ActionCommand { get; }

    public ImageSource? Thumbnail => _thumb ??= MakeThumb(_item.Thumbnail);

    internal void Tick() => Raise(nameof(TimeText));

    private static ImageSource? MakeThumb(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 160;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}

// =================================================================== devices

public sealed class DeviceVm : Observable
{
    private readonly HubController _hub;
    internal DeviceVm(HubController hub, PairedDevice device) { _hub = hub; Device = device; ForgetCommand = new RelayCommand(Forget); }

    public PairedDevice Device { get; }
    public string Name => Device.Name;
    private DeviceSession? Session => _hub.Server.Sessions.FirstOrDefault(s => s.DeviceId == Device.DeviceId);
    public bool IsOnline => Session != null;
    public string StatusText => Session is { } s
        ? $"Connected from {s.RemoteIp}" + (s.AppVersion is { } v ? $" · app {v}" : "")
        : Device.LastSeen is { } seen ? $"Offline · last seen {Time.Ago(seen.ToLocalTime())}" : "Offline · never connected";
    public string PairedText => $"Paired {Device.PairedAt.ToLocalTime():MMM d, yyyy}";
    public ICommand ForgetCommand { get; }

    private void Forget()
    {
        var r = MessageBox.Show($"Forget {Name}?\n\nIt will disconnect now and must scan a new QR code to pair again.",
            "Strand", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.OK) _hub.Forget(Device.DeviceId);
    }

    internal void Refresh() => RaiseAll();
}

// =================================================================== features

public sealed class FeatureVm : Observable
{
    private readonly HubController _hub;
    private object? _settingsView;

    internal FeatureVm(HubController hub, IFeature feature)
    {
        _hub = hub; Feature = feature;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    private bool _expanded;
    /// <summary>Extra settings are folded away until the chevron is clicked.</summary>
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(Chevron)); } }
    public string Chevron => _expanded ? "" : ""; // ChevronUp / ChevronDown
    public ICommand ToggleExpandedCommand { get; }

    public IFeature Feature { get; }
    public string Title => Feature.Title;
    public string Description => Feature.Description;
    public string Glyph => Feature.Glyph;

    public bool IsEnabled
    {
        get => _hub.Features.IsEnabled(Feature.Id);
        set { _hub.Features.SetEnabled(Feature.Id, value); Raise(); Raise(nameof(SupportText)); }
    }

    public object? SettingsView => _settingsView ??= _hub.Features.Context is { } ctx ? Feature.CreateSettingsView(ctx) : null;

    public string? SupportText
    {
        get
        {
            // Only speak up when something is wrong; a working feature needs no status line.
            var online = _hub.Server.Sessions.ToList();
            if (!IsEnabled || online.Count == 0) return null;
            return online.Any(s => s.Supports(Feature.Id)) ? null : "Turned off on your phone";
        }
    }

    internal void Refresh() => Raise(nameof(SupportText));
}

// =================================================================== pairing

public sealed class PairingVm : Observable
{
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
    private readonly HubController _hub;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _expires;
    private bool _isActive, _isPaired;
    private string _pairedName = "", _payload = "";
    private ImageSource? _qr;
    private IPAddress? _selectedIp;

    internal PairingVm(HubController hub)
    {
        _hub = hub;
        _timer.Tick += (_, _) => Tick();
        StartCommand = new RelayCommand(Start);
        CancelCommand = new RelayCommand(Cancel);
        CopyCommand = new RelayCommand(() => Clipboard.SetText(_payload));
        DoneCommand = new RelayCommand(() => { IsPaired = false; });
    }

    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool IsPaired { get => _isPaired; private set => Set(ref _isPaired, value); }
    public string PairedName { get => _pairedName; private set => Set(ref _pairedName, value); }
    public ImageSource? QrImage { get => _qr; private set => Set(ref _qr, value); }
    public IReadOnlyList<IPAddress> Addresses { get; private set; } = Array.Empty<IPAddress>();
    public IPAddress? SelectedAddress
    {
        get => _selectedIp;
        set { if (Set(ref _selectedIp, value) && IsActive) Regenerate(); }
    }
    public string ExpiresText => IsActive ? $"Code refreshes in {(_expires - DateTime.UtcNow):m\\:ss}" : "";
    public string Fingerprint => FormatFingerprint(_hub.Identity.Fingerprint);

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand DoneCommand { get; }

    public void Start()
    {
        Addresses = Platform.LocalNet.CandidateIPv4();
        Raise(nameof(Addresses));
        if (Addresses.Count == 0)
        {
            MessageBox.Show("This PC isn't connected to a network. Join the same Wi-Fi as your phone first.", "Strand",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _selectedIp = Addresses[0]; Raise(nameof(SelectedAddress));
        IsPaired = false;
        IsActive = true;
        Regenerate();
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _hub.Server.EndPairing();
        IsActive = false;
        QrImage = null;
    }

    internal void OnPaired(string name)
    {
        if (!IsActive) return;
        _timer.Stop();
        IsActive = false;
        QrImage = null;
        PairedName = name;
        IsPaired = true;
    }

    private void Regenerate()
    {
        _payload = _hub.BuildPairingPayload(_selectedIp!, Validity);
        _expires = DateTime.UtcNow + Validity;
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(_payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(12, new byte[] { 0x1B, 0x1B, 0x1F }, new byte[] { 0xFF, 0xFF, 0xFF });
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(png); bmp.EndInit(); bmp.Freeze();
        QrImage = bmp;
        Raise(nameof(ExpiresText));
    }

    private void Tick()
    {
        if (DateTime.UtcNow >= _expires) Regenerate();
        Raise(nameof(ExpiresText));
    }

    internal static string FormatFingerprint(string hex) =>
        string.Join("  ", Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4))).ToUpperInvariant();
}

// =================================================================== main

public sealed class MainViewModel : Observable
{
    private readonly HubController _hub;
    private readonly Dispatcher _ui;
    private Page _page = Page.Home;
    private string _hubName;

    internal MainViewModel(HubController hub, Dispatcher ui)
    {
        _hub = hub; _ui = ui;
        _hubName = hub.Config.HubName;
        Pairing = new PairingVm(hub);
        foreach (var f in hub.Features.All) Features.Add(new FeatureVm(hub, f));
        foreach (var a in hub.Activity.Items.Reverse()) Activity.Insert(0, new ActivityVm(a, ui));
        RebuildDevices();

        hub.Activity.Added += item =>
        {
            Activity.Insert(0, new ActivityVm(item, ui)); while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
            Pulse(item); item.Updated += () => ui.BeginInvoke(() => Pulse(item));
            RaiseStatus();
        };
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _busyTimer.Tick += (_, _) => { _busyTimer.Stop(); IsBusy = false; };
        hub.StatusChanged += () => { RebuildDevices(); foreach (var f in Features) f.Refresh(); RaiseStatus(); };
        hub.Features.Changed += RaiseStatus;
        hub.DevicePaired += name => { Pairing.OnPaired(name); RebuildDevices(); };

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        clock.Tick += (_, _) => { foreach (var a in Activity) a.Tick(); foreach (var d in Devices) d.Refresh(); };
        clock.Start();

        NavigateCommand = new RelayCommand(p => { if (p is Page pg) CurrentPage = pg; else if (Enum.TryParse<Page>(p?.ToString(), out var x)) CurrentPage = x; });
        PairCommand = new RelayCommand(() => { CurrentPage = Page.Devices; if (!Pairing.IsActive) Pairing.Start(); });
        OpenDataFolderCommand = new RelayCommand(() => System.Diagnostics.Process.Start("explorer.exe", hub.DataDir));
        OpenLogCommand = new RelayCommand(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(hub.DataDir, "hub.log")) { UseShellExecute = true }));
        QuitCommand = new RelayCommand(() => hub.Quit());
        SendFilesCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send files to your phone" };
            if (dlg.ShowDialog() == true) SendPaths(dlg.FileNames);
        });
        SendFolderCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Send folders to your phone" };
            if (dlg.ShowDialog() == true) SendPaths(dlg.FolderNames);
        });
        OpenDropFolderCommand = new RelayCommand(() =>
        {
            var dir = ContinuityHub.Features.FileDropFeature.DownloadDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        });
    }

    // ------------------------------------------------ QuickDrop
    public ICommand SendFilesCommand { get; }
    public ICommand SendFolderCommand { get; }
    public ICommand OpenDropFolderCommand { get; }

    private string _dropMessage = "Drag files or folders anywhere onto this window.";
    public string DropMessage { get => _dropMessage; private set => Set(ref _dropMessage, value); }

    /// <summary>Called by the buttons and by drag-and-drop onto the window.</summary>
    public void SendPaths(IEnumerable<string> paths)
    {
        DropMessage = _hub.SendFiles(paths);
        CurrentPage = Page.Home;
    }

    // ------------------------------------------------ navigation
    public Page CurrentPage { get => _page; set => Set(ref _page, value); }
    public ICommand NavigateCommand { get; }
    public ICommand PairCommand { get; }

    // ------------------------------------------------ link (home hero)
    private readonly DispatcherTimer _busyTimer;
    private bool _busy, _incoming;
    /// <summary>Something moved in the last few seconds: the dot runs along the link.</summary>
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    /// <summary>The last thing came from the phone (dot travels phone → PC).</summary>
    public bool Incoming { get => _incoming; private set => Set(ref _incoming, value); }
    public bool IsLinked => StatusKind == "connected";
    public string PhoneName => _hub.Server.Sessions.FirstOrDefault()?.Name.Replace("Google ", "") ?? _hub.Registry.All.FirstOrDefault()?.Name.Replace("Google ", "") ?? "Your phone";
    public string LinkSummary =>
        $"{ConnectedCount} of {PairedCount} {(PairedCount == 1 ? "phone" : "phones")} online, {SyncedToday} synced today, {FeaturesOn} features on";

    private void Pulse(ActivityItem item)
    {
        if (item.Direction == ActivityDirection.Info) return;
        Incoming = item.Direction == ActivityDirection.Incoming;
        IsBusy = true;
        _busyTimer.Stop(); _busyTimer.Start();
    }

    // ------------------------------------------------ status (home hero)
    public int ConnectedCount => _hub.Server.Sessions.Count;
    public int PairedCount => _hub.Registry.All.Count;
    public int SyncedToday => _hub.Activity.TodayCount;
    public int FeaturesOn => _hub.Features.All.Count(f => _hub.Features.IsEnabled(f.Id));

    /// <summary>"connected" | "waiting" | "paused" | "unpaired" (drives colours via DataTriggers).</summary>
    public string StatusKind => IsPaused ? "paused" : ConnectedCount > 0 ? "connected" : PairedCount == 0 ? "unpaired" : "waiting";
    public string StatusGlyph => StatusKind switch { "connected" => "", "paused" => "", "unpaired" => "", _ => "" };
    public string StatusTitle => StatusKind switch
    {
        "paused" => "Sync is paused",
        "connected" => ConnectedCount == 1 ? $"Linked to {_hub.Server.Sessions.First().Name}" : $"{ConnectedCount} devices connected",
        "unpaired" => "Pair your first phone",
        _ => "Waiting for your phone",
    };
    public string StatusDetail => StatusKind switch
    {
        "paused" => "Nothing is sent or received until you resume.",
        "connected" => "Copy on either device and paste on the other. Screenshots are ready for Ctrl+V.",
        "unpaired" => "Scan a QR code with the Strand app on Android. It takes about ten seconds.",
        _ => "Open Strand on your phone. It reconnects automatically on the same Wi-Fi.",
    };
    public string NavStatus => StatusKind switch
    {
        "connected" => ConnectedCount == 1 ? "1 device online" : $"{ConnectedCount} devices online",
        "paused" => "Paused",
        "unpaired" => "No devices",
        _ => "Waiting",
    };

    public bool IsPaused
    {
        get => _hub.Config.Paused;
        set { _hub.SetPaused(value); RaiseStatus(); }
    }

    public ObservableCollection<ActivityVm> Activity { get; } = new();
    public ObservableCollection<DeviceVm> Devices { get; } = new();
    public ObservableCollection<FeatureVm> Features { get; } = new();
    public PairingVm Pairing { get; }

    // ------------------------------------------------ settings
    public string HubName
    {
        get => _hubName;
        set { if (Set(ref _hubName, value) && !string.IsNullOrWhiteSpace(value)) _hub.SetHubName(value.Trim()); }
    }
    public bool StartWithWindows { get => _hub.StartWithWindows; set { _hub.StartWithWindows = value; Raise(); } }
    public bool Notifications { get => _hub.Config.Notifications; set { _hub.Config.Notifications = value; _hub.Config.Save(); Raise(); } }
    public bool MatchPhoneColors { get => _hub.Config.MatchPhoneColors; set { _hub.SetMatchPhoneColors(value); Raise(); Raise(nameof(PhoneColorsNote)); } }
    public string PhoneColorsNote => _hub.Config.PhonePalette == null
        ? "Your phone sends its colours when it connects (Android 12 and later)"
        : AccentTheme.Active ? "Using your phone's Material You colours" : "Using your Windows accent colour";
    public string NetworkAddress => _hub.AdvertisedIp is { } ip ? $"{ip}:{_hub.Server.BoundPort}" : $"(no network):{_hub.Server.BoundPort}";
    public string ServiceName => $"{_hub.Config.HubName}._continuity._tcp.local";
    public string Fingerprint => PairingVm.FormatFingerprint(_hub.Identity.Fingerprint);
    public string Version => $"Version {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand QuitCommand { get; }

    private void RebuildDevices()
    {
        Devices.Clear();
        foreach (var d in _hub.Registry.All.OrderByDescending(d => _hub.Server.Sessions.Any(s => s.DeviceId == d.DeviceId)).ThenBy(d => d.Name))
            Devices.Add(new DeviceVm(_hub, d));
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        foreach (var p in new[] { nameof(ConnectedCount), nameof(PairedCount), nameof(SyncedToday), nameof(FeaturesOn), nameof(StatusKind),
                     nameof(StatusGlyph), nameof(StatusTitle), nameof(StatusDetail), nameof(NavStatus), nameof(IsPaused), nameof(NetworkAddress), nameof(ServiceName),
                     nameof(IsLinked), nameof(PhoneName), nameof(LinkSummary), nameof(PhoneColorsNote) })
            Raise(p);
    }
}
