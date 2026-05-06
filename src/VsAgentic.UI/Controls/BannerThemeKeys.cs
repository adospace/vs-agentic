namespace VsAgentic.UI.Controls;

/// <summary>
/// Optional WPF theme-resource keys for the in-chat banners.
///
/// When a host (e.g. the VS extension) populates one of these with a theme
/// resource key such as <c>VsBrushes.ComboBoxBackgroundKey</c>, the matching
/// banner surface is wired with <c>SetResourceReference</c> so VS theme
/// changes propagate live through WPF's standard DynamicResource pipeline —
/// no rebuild, no manual brush mutation.
///
/// Hosts that aren't running inside VS (e.g. the standalone Desktop) leave
/// the keys null; the builder falls back to <see cref="BannerTheme.Current"/>.
/// </summary>
public static class BannerThemeKeys
{
    /// <summary>Card / banner inner fill.</summary>
    public static object? Background { get; set; }

    /// <summary>Card border, separator lines.</summary>
    public static object? Border { get; set; }

    /// <summary>
    /// Outer ring of an unchecked CheckBox/RadioButton indicator. Kept
    /// distinct from <see cref="Border"/> because tool-window borders are
    /// often nearly invisible in dark VS themes — combo-box-style borders
    /// give the indicator enough contrast to be seen.
    /// </summary>
    public static object? IndicatorBorder { get; set; }

    /// <summary>Primary text (questions, labels, glyphs).</summary>
    public static object? Foreground { get; set; }

    /// <summary>De-emphasised text (counter, helper hints).</summary>
    public static object? Muted { get; set; }

    /// <summary>Editable surface fill (TextBox, login banner inner area).</summary>
    public static object? InputBackground { get; set; }

    /// <summary>Selected/checked fill, primary action button background.</summary>
    public static object? Accent { get; set; }

    /// <summary>Glyph / text colour drawn on top of <see cref="Accent"/>.</summary>
    public static object? AccentForeground { get; set; }
}
