using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using ContinuityHub.UI.Pages;

namespace ContinuityHub.UI;

public partial class MainWindow : Window
{
    private readonly Dictionary<Page, FrameworkElement> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVmChanged;
            ShowPage();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ---------------------------------------------------------------- QuickDrop drag and drop

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (HasFiles(e)) DropOverlay.Visibility = Visibility.Visible;
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when the pointer left the window.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) Vm?.SendPaths(paths);
        e.Handled = true;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) ShowPage();
    }

    private void ShowPage()
    {
        if (Vm is not { } vm) return;
        if (!_pages.TryGetValue(vm.CurrentPage, out var page))
        {
            page = vm.CurrentPage switch
            {
                Page.Home => new HomePage(),
                Page.Devices => new DevicesPage(),
                Page.Features => new FeaturesPage(),
                _ => new SettingsPage(),
            };
            _pages[vm.CurrentPage] = page;
        }
        if (ReferenceEquals(PageHost.Content, page)) return;
        PageHost.Content = page;
        Scroller.ScrollToTop();

        // Subtle entrance: fade + slide up, like WinUI page transitions.
        var t = new System.Windows.Media.TranslateTransform(0, 14);
        page.RenderTransform = t;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        t.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
