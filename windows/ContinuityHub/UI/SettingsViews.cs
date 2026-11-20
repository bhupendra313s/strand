using System;
using System.Windows;
using System.Windows.Controls;

namespace ContinuityHub.UI;

/// <summary>Small builders so features can contribute settings UI without writing XAML.</summary>
public static class SettingsViews
{
    public static FrameworkElement Stack(params FrameworkElement[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children) { c.Margin = new Thickness(0, 4, 0, 4); panel.Children.Add(c); }
        return panel;
    }

    public static FrameworkElement Toggle(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content = new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("Body") },
            IsChecked = value,
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
        };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    public static FrameworkElement Note(string text) => new TextBlock
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Caption"),
    };
}
