using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ContinuityHub.UI;

/// <summary>
/// Re-tints the Fluent accent with the phone's Material You palette, so the hub matches the wallpaper on the
/// phone. App-level resources win over the theme dictionaries that WPF's Fluent styles read through
/// DynamicResource, so overriding the accent keys here recolours buttons, toggles, links and our own tiles.
/// Removing the overrides returns to the Windows accent.
/// </summary>
public static class AccentTheme
{
    private static readonly string[] BrushKeys =
    {
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "TextOnAccentFillColorSelectedTextBrush",
        "AccentControlElevationBorderBrush", "AccentContainerBrush", "OnAccentContainerBrush", "AccentTertiaryBrush",
        // Fluent's accent button aliases the accent brushes with StaticResource inside its own dictionary,
        // so it doesn't see the overrides above; override its keys directly.
        "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
        "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
        "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed",
    };
    private static readonly string[] ColorKeys =
    {
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    };

    private static JsonObject? _palette;
    private static bool _enabled;
    private static bool _hooked;

    /// <summary>True when the hub is currently painted with phone colours.</summary>
    public static bool Active { get; private set; }

    public static void Apply(JsonObject? palette, bool enabled)
    {
        _palette = palette; _enabled = enabled;
        if (!_hooked)
        {
            _hooked = true;
            // Light/dark switch in Windows settings: re-pick the matching half of the palette.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(Reapply);
            };
        }
        Reapply();
    }

    private static void Reapply()
    {
        var res = Application.Current.Resources;
        foreach (var k in BrushKeys) res.Remove(k);
        foreach (var k in ColorKeys) res.Remove(k);
        // Our own neutral fallbacks for the two container keys (Theme.xaml-free, so they exist either way).
        res["AccentContainerBrush"] = Frozen(SystemAccent(0.18));
        res["OnAccentContainerBrush"] = Application.Current.TryFindResource("AccentTextFillColorPrimaryBrush") ?? Brushes.Gray;
        Active = false;

        var half = _enabled ? _palette?[IsDark() ? "dark" : "light"] as JsonObject : null;
        if (half == null || !TryColor(half, "primary", out var primary) || !TryColor(half, "on_primary", out var onPrimary)) return;
        TryColor(half, "primary_container", out var container);
        TryColor(half, "on_primary_container", out var onContainer);
        if (!TryColor(half, "tertiary", out var tertiary)) tertiary = primary;

        // Material's primary is already tuned per mode (a light tone in dark mode, a dark tone in light mode),
        // so it works both as a fill (with on_primary text) and as accent-coloured text.
        res["SystemAccentColor"] = primary;
        foreach (var k in ColorKeys[1..]) res[k] = primary;
        res["AccentFillColorDefaultBrush"] = Frozen(primary);
        res["AccentFillColorSecondaryBrush"] = Frozen(primary, 0.9);
        res["AccentFillColorTertiaryBrush"] = Frozen(primary, 0.8);
        res["AccentTextFillColorPrimaryBrush"] = Frozen(primary);
        res["AccentTextFillColorSecondaryBrush"] = Frozen(primary);
        res["AccentTextFillColorTertiaryBrush"] = Frozen(primary, 0.85);
        res["TextOnAccentFillColorPrimaryBrush"] = Frozen(onPrimary);
        res["TextOnAccentFillColorSecondaryBrush"] = Frozen(onPrimary, 0.8);
        res["TextOnAccentFillColorSelectedTextBrush"] = Frozen(onPrimary);
        res["AccentControlElevationBorderBrush"] = Frozen(primary);
        res["AccentContainerBrush"] = Frozen(container == default ? primary : container, container == default ? 0.2 : 1);
        res["OnAccentContainerBrush"] = Frozen(onContainer == default ? primary : onContainer);
        res["AccentTertiaryBrush"] = Frozen(tertiary);
        res["AccentButtonBackground"] = Frozen(primary);
        res["AccentButtonBackgroundPointerOver"] = Frozen(primary, 0.9);
        res["AccentButtonBackgroundPressed"] = Frozen(primary, 0.8);
        res["AccentButtonForeground"] = Frozen(onPrimary);
        res["AccentButtonForegroundPointerOver"] = Frozen(onPrimary);
        res["AccentButtonForegroundPressed"] = Frozen(onPrimary, 0.8);
        res["AccentButtonBorderBrush"] = Frozen(primary);
        res["AccentButtonBorderBrushPointerOver"] = Frozen(primary);
        res["AccentButtonBorderBrushPressed"] = Frozen(primary);
        Active = true;
    }

    /// <summary>Windows "Choose your mode → apps" setting.</summary>
    public static bool IsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    private static Color SystemAccent(double alpha)
    {
        var c = SystemParameters.WindowGlassColor;
        return Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B);
    }

    private static SolidColorBrush Frozen(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static bool TryColor(JsonObject o, string key, out Color c)
    {
        c = default;
        var s = o[key] is JsonValue v && v.TryGetValue<string>(out var str) ? str : null;
        if (s is not { Length: 7 } || s[0] != '#' || !uint.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        c = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
