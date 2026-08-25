using System.Globalization;

namespace VsAgentic.UI.Controls;

/// <summary>
/// The ladder of zoom steps the chat moves through, and the arithmetic for
/// stepping along it. Modelled on a browser's ladder rather than a fixed
/// increment: coarse at the ends, where a few percent make no visible
/// difference, and fine around 100%, which is where most users sit.
/// </summary>
public static class ZoomLevels
{
    /// <summary>The level a fresh install starts at, and what Ctrl+0 returns to.</summary>
    public const double Default = 1.0;

    private static readonly double[] Steps =
    {
        0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0
    };

    /// <summary>
    /// Snaps an arbitrary value to the nearest step. Anything arriving from
    /// outside — a restored setting, a hand-edited registry value — goes
    /// through here, so the rest of the code only ever sees a real step and
    /// <see cref="Step"/> can compare for exact equality.
    /// </summary>
    public static double Normalize(double level)
    {
        var nearest = Steps[0];
        foreach (var step in Steps)
        {
            if (Math.Abs(step - level) < Math.Abs(nearest - level))
                nearest = step;
        }
        return nearest;
    }

    /// <summary>
    /// The step one place <paramref name="direction"/> from
    /// <paramref name="level"/> (+1 zooms in, -1 out), clamped at both ends so
    /// holding the wheel down never runs off the ladder.
    /// </summary>
    public static double Step(double level, int direction)
    {
        var index = Array.IndexOf(Steps, Normalize(level));
        var next = Math.Max(0, Math.Min(Steps.Length - 1, index + Math.Sign(direction)));
        return Steps[next];
    }

    /// <summary>The level as a caption, e.g. "110%".</summary>
    public static string Format(double level) =>
        ((int)Math.Round(level * 100)).ToString(CultureInfo.InvariantCulture) + "%";
}
