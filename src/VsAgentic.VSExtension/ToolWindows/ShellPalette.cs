using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace VsAgentic.VSExtension.ToolWindows;

/// <summary>
/// The palette a Visual Studio 2026 theme is written against.
/// </summary>
/// <remarks>
/// Such a theme defines the category "Shell" and leaves "Environment" to its
/// fallback theme, so the environment brushes a window binds keep the
/// fallback's colors: a prompt box in gray inside an IDE the theme paints
/// green. Reading the shell color and entering it in the window's own
/// resources, under the key its XAML already binds, puts the theme back in
/// charge of that window and of nothing else.
///
/// Visual Studio 2022 has no such category and the 17.14 SDK the extension
/// builds against does not know these names, so every lookup there comes back
/// empty and the environment brushes stand unchanged.
/// </remarks>
internal static class ShellPalette
{
    /// <summary>
    /// The category, addressed by GUID because the SDK has no constant for it.
    /// </summary>
    private static readonly Guid Category = new("73708DED-2D56-4AAD-B8EB-73B20D3F4BFF");

    /// <summary>
    /// Reads one color of the palette, or null where there is none.
    /// </summary>
    internal static Color? TryGetColor(string name)
    {
        var application = Application.Current;
        if (application is null)
            return null;

        // The palette holds its colors as background values, so that is the key
        // type a theme registers them under.
        var key = new ThemeResourceKey(Category, name, ThemeResourceKeyType.BackgroundColor);

        return application.TryFindResource(key) switch
        {
            Color color => color,
            SolidColorBrush brush => brush.Color,
            _ => null,
        };
    }

    /// <summary>
    /// Points the brushes of <paramref name="window"/> at the palette. Call it
    /// when the window opens and again on every theme change.
    /// </summary>
    /// <param name="pairs">
    /// The environment brush key the XAML binds, and the palette color that
    /// carries the same meaning.
    /// </param>
    internal static void OverrideBrushes(FrameworkElement window, (object Key, string ShellColor)[] pairs)
    {
        foreach (var (key, shellColor) in pairs)
        {
            if (TryGetColor(shellColor) is not Color color)
            {
                window.Resources.Remove(key);
                continue;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            window.Resources[key] = brush;
        }
    }

    /// <summary>
    /// The same for keys that carry a color rather than a brush, as image
    /// theming needs.
    /// </summary>
    internal static void OverrideColors(FrameworkElement window, (object Key, string ShellColor)[] pairs)
    {
        foreach (var (key, shellColor) in pairs)
        {
            if (TryGetColor(shellColor) is not Color color)
            {
                window.Resources.Remove(key);
                continue;
            }

            window.Resources[key] = color;
        }
    }
}
