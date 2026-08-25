using VsAgentic.UI.Controls;

namespace VsAgentic.VSExtension.ToolWindows;

/// <summary>
/// The one zoom level every chat window renders at.
///
/// Deliberately shared rather than per-window: this is an accessibility
/// setting, not a per-tab view state. Someone who turns it up because the text
/// is too small means it for every window, including ones opened later, the
/// same way the editor's font size applies everywhere.
///
/// The level lives here rather than on the options page so a window can react
/// without reaching for the package; <see cref="VsAgenticPackage"/> seeds it at
/// startup and writes changes back to the registry.
/// </summary>
internal static class ChatZoom
{
    private static double _level = ZoomLevels.Default;

    /// <summary>Raised on the UI thread whenever <see cref="Level"/> changes.</summary>
    public static event Action<double>? Changed;

    /// <summary>The current level, always one of the <see cref="ZoomLevels"/> steps.</summary>
    public static double Level => _level;

    /// <summary>
    /// Seeds the level from the persisted setting. Silent by design — it runs
    /// before any window exists, so there is nothing to notify and nothing to
    /// write back.
    /// </summary>
    public static void Initialize(double level) => _level = ZoomLevels.Normalize(level);

    /// <summary>
    /// Moves one step in (+1), one step out (-1), or back to 100% (0).
    /// </summary>
    public static void Step(int direction)
    {
        _level = direction == 0 ? ZoomLevels.Default : ZoomLevels.Step(_level, direction);

        // Notified even when the step was a no-op at either end of the ladder,
        // so the reading still flashes up. A browser does the same, and the
        // alternative — silence — reads as a dead keyboard shortcut.
        Changed?.Invoke(_level);
    }
}
