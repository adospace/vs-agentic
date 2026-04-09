using System.Windows.Media;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Color palette for the in-chat permission banner and question card.
/// Set by the host (VS extension or Desktop app) so the banner picks up the
/// current IDE / OS theme. Falls back to dark defaults if never assigned.
/// </summary>
public sealed class BannerTheme
{
    public Brush Background { get; set; } = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x33));
    public Brush Border     { get; set; } = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66));
    public Brush Foreground { get; set; } = Brushes.WhiteSmoke;
    public Brush Muted      { get; set; } = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xB0));
    public Brush InputBackground { get; set; } = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x26));
    public Brush Accent     { get; set; } = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    public Brush AccentForeground { get; set; } = Brushes.White;
    public Brush Danger     { get; set; } = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    public Brush DangerForeground  { get; set; } = Brushes.White;

    /// <summary>
    /// Globally-shared current theme. The host updates this whenever the IDE
    /// theme changes; the next banner build picks up the new colors.
    /// </summary>
    public static BannerTheme Current { get; set; } = new BannerTheme();

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Convenience factory for callers that want to assign by Color.</summary>
    public static BannerTheme FromColors(
        Color background,
        Color border,
        Color foreground,
        Color muted,
        Color inputBackground,
        Color accent,
        Color accentForeground,
        Color danger,
        Color dangerForeground)
    {
        return new BannerTheme
        {
            Background = Freeze(background),
            Border = Freeze(border),
            Foreground = Freeze(foreground),
            Muted = Freeze(muted),
            InputBackground = Freeze(inputBackground),
            Accent = Freeze(accent),
            AccentForeground = Freeze(accentForeground),
            Danger = Freeze(danger),
            DangerForeground = Freeze(dangerForeground),
        };
    }
}
