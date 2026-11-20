using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ContinuityHub.UI;

/// <summary>
/// The line between the PC and phone on Home (same idea as the phone app's link). Dashed and quiet while
/// waiting, solid accent when linked, and a dot travels along it while something is moving. With Windows
/// animations turned off the dot rests in the middle.
/// </summary>
public sealed class LinkLine : Grid
{
    public static readonly DependencyProperty IsLinkedProperty = DependencyProperty.Register(
        nameof(IsLinked), typeof(bool), typeof(LinkLine), new PropertyMetadata(false, (d, _) => ((LinkLine)d).Refresh()));
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(LinkLine), new PropertyMetadata(false, (d, _) => ((LinkLine)d).Refresh()));
    public static readonly DependencyProperty IncomingProperty = DependencyProperty.Register(
        nameof(Incoming), typeof(bool), typeof(LinkLine), new PropertyMetadata(false, (d, _) => ((LinkLine)d).Refresh()));

    public bool IsLinked { get => (bool)GetValue(IsLinkedProperty); set => SetValue(IsLinkedProperty, value); }
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    /// <summary>Data is coming to this PC: the dot travels phone → PC (right to left).</summary>
    public bool Incoming { get => (bool)GetValue(IncomingProperty); set => SetValue(IncomingProperty, value); }

    private const double Dot = 10;
    private readonly Rectangle _line = new() { Height = 3, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Line _dashed = new() { StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 2.5, 3 }, StrokeDashCap = PenLineCap.Round,
        VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Fill, X1 = 0, X2 = 1 };
    private readonly Ellipse _dot = new() { Width = Dot, Height = Dot, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform _move = new();

    public LinkLine()
    {
        Height = 24;
        _dot.RenderTransform = _move;
        _line.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        _dot.SetResourceReference(Shape.FillProperty, "AccentTertiaryBrush");
        _dashed.SetResourceReference(Shape.StrokeProperty, "TextFillColorTertiaryBrush");
        Children.Add(_dashed); Children.Add(_line); Children.Add(_dot);
        SizeChanged += (_, _) => Refresh();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _line.Visibility = IsLinked ? Visibility.Visible : Visibility.Collapsed;
        _dashed.Visibility = IsLinked ? Visibility.Collapsed : Visibility.Visible;
        var moving = IsLinked && IsBusy;
        _dot.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        _move.BeginAnimation(TranslateTransform.XProperty, null);
        var travel = Math.Max(0, ActualWidth - Dot);
        if (!moving || travel <= 0) return;
        if (!SystemParameters.ClientAreaAnimation) { _move.X = travel / 2; return; }
        _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Incoming ? travel : 0, Incoming ? 0 : travel, TimeSpan.FromMilliseconds(1400))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
