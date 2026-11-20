using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ContinuityHub.Core;

namespace ContinuityHub.UI;

/// <summary>
/// Shows a phone's mirrored screen. Pulls the frame stream, decodes JPEGs off the UI thread, and drops
/// frames rather than queueing them, so the picture stays live. Reconnects if Wi-Fi blips.
/// </summary>
public sealed class ScreenViewerWindow : Window
{
    private readonly ScreenOfferMessage _offer;
    private readonly Func<DropEndpoint?> _endpoint;
    private readonly CancellationTokenSource _cts = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new();
    private readonly TextBlock _overlay = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private BitmapSource? _last;
    private int _rendering;
    private int _frames;
    private long _bytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ScreenViewerWindow(ScreenOfferMessage offer, string deviceName, Func<DropEndpoint?> endpoint)
    {
        _offer = offer; _endpoint = endpoint;
        Title = $"{deviceName} · Screen";
        DeviceName = deviceName;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        if (Application.Current.TryFindResource("ApplicationBackgroundBrush") is Brush bg) Background = bg;
        SizeToOffer(offer.Width, offer.Height);

        var onTop = new CheckBox
        {
            Content = new TextBlock { Text = "Always on top", Style = (Style)Application.Current.FindResource("Body") },
            Style = (Style)Application.Current.FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center,
        };
        onTop.Checked += (_, _) => Topmost = true;
        onTop.Unchecked += (_, _) => Topmost = false;

        var save = new Button { Content = "Save screenshot", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(12, 0, 0, 0) };
        save.Click += (_, _) => SaveFrame();

        _status.Style = (Style)Application.Current.FindResource("Caption");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _overlay.Foreground = Brushes.Gainsboro; // always on the black stage
        _overlay.Text = $"Connecting to {deviceName}…";

        // Toolbar: status on the left, controls on the right.
        var bar = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The Caption style wraps; in a narrow window that stacked the readout one character per line and
        // pushed the picture into a thin strip. Keep it on one line and trim instead.
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(0, 0, 12, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(onTop); right.Children.Add(save);
        Grid.SetColumn(right, 1);
        bar.Children.Add(_status); bar.Children.Add(right);

        var stage = new Grid { Background = Brushes.Black };
        stage.Children.Add(_image); stage.Children.Add(_overlay);

        // Picture fills everything above the toolbar (explicit rows: a DockPanel didn't stretch under the Fluent window template).
        var root = new Grid { VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(bar, 1);
        root.Children.Add(stage); root.Children.Add(bar);
        Content = root;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;

        Loaded += (_, _) => _ = Task.Run(RunAsync);
        Closed += (_, _) => _cts.Cancel();
    }

    public string DeviceName { get; }
    public string SessionId => _offer.Id;

    /// <summary>The phone stopped sharing: show it briefly, then close.</summary>
    public void EndedByPhone()
    {
        _cts.Cancel();
        _overlay.Text = $"{DeviceName} stopped sharing";
        _image.Opacity = 0.3;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }

    private void SizeToOffer(int w, int h)
    {
        if (w <= 0 || h <= 0) { w = 9; h = 19; }
        var area = SystemParameters.WorkArea;
        var maxH = area.Height * 0.85; var maxW = area.Width * 0.85;
        var height = Math.Min(maxH, maxW * h / w);
        MinWidth = 440; MinHeight = 320;              // room for the toolbar
        Width = Math.Max(MinWidth, height * w / h); Height = height + 52; // + toolbar
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            HttpClientHolder? holder = null;
            try
            {
                var ep = _endpoint() ?? throw new IOException("Phone not connected");
                var (client, body) = await ScreenStream.OpenAsync(ep, _offer.Id, _offer.Token, ct);
                holder = new HttpClientHolder(client, body);
                delay = TimeSpan.FromSeconds(1);
                await foreach (var jpeg in ScreenStream.ReadFramesAsync(body, TimeSpan.FromSeconds(8), ct))
                {
                    _bytes += jpeg.Length;
                    if (Interlocked.CompareExchange(ref _rendering, 1, 0) != 0) continue; // UI still busy: drop this frame
                    BitmapSource bmp;
                    try { bmp = Decode(jpeg); } catch { Interlocked.Exchange(ref _rendering, 0); continue; }
                    _ = Dispatcher.BeginInvoke(() => Show(bmp));
                }
                if (!ct.IsCancellationRequested) Ui(() => _overlay.Text = "Sharing ended");
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e.Message.Contains("401"))
            {
                Ui(() => _overlay.Text = "This sharing session has ended");
                return;
            }
            catch (Exception e)
            {
                Log.Warn($"screen viewer: {e.Message}; reconnecting in {delay.TotalSeconds:0}s");
                Ui(() => { _overlay.Text = $"Reconnecting to {DeviceName}…"; _image.Opacity = 0.4; });
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 2));
            }
            finally { holder?.Dispose(); }
        }
    }

    private static BitmapSource Decode(byte[] jpeg)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(jpeg);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void Show(BitmapSource bmp)
    {
        _image.Source = bmp; _image.Opacity = 1; _overlay.Text = "";
        if (_last == null || _last.PixelWidth != bmp.PixelWidth) SizeToOffer(bmp.PixelWidth, bmp.PixelHeight); // rotation
        _last = bmp;
        _frames++;
        if (_clock.ElapsedMilliseconds >= 1000)
        {
            var secs = _clock.Elapsed.TotalSeconds;
            _status.Text = $"{bmp.PixelWidth}×{bmp.PixelHeight} · {_frames / secs:0} fps · {_bytes / secs / 1024:0} KB/s · local Wi-Fi only";
            _frames = 0; _bytes = 0; _clock.Restart();
        }
        Interlocked.Exchange(ref _rendering, 0);
    }

    private void SaveFrame()
    {
        if (_last == null) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Strand");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DeviceName} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(_last));
        using (var f = File.Create(path)) enc.Save(f);
        _status.Text = $"Saved to Pictures\\Strand";
    }

    private void Ui(Action a) => Dispatcher.BeginInvoke(a);

    private sealed class HttpClientHolder(System.Net.Http.HttpClient client, Stream body) : IDisposable
    {
        public void Dispose() { body.Dispose(); client.Dispose(); }
    }
}
