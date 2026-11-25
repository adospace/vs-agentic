namespace VsAgentic.Services.Dictation;

/// <summary>
/// A keyboard shortcut written as text, e.g. "Ctrl+Shift+M" or
/// "RightCtrl+RightShift". Parsing is kept apart from WPF so it can be tested;
/// the chat window matches the result against the keys that are down.
/// </summary>
public sealed class DictationShortcut
{
    public const string Default = "Ctrl+Shift+M";

    public bool Ctrl { get; private set; }
    public bool Shift { get; private set; }
    public bool Alt { get; private set; }

    /// <summary>
    /// Modifier keys that must be that physical key, e.g. "RightCtrl" — a plain
    /// "Ctrl" accepts either side and is not listed here.
    /// </summary>
    public IReadOnlyList<string> Sides { get; private set; } = Array.Empty<string>();

    /// <summary>The closing key's name ("M", "F9", "Space"), or null for a shortcut made of modifiers only.</summary>
    public string? Key { get; private set; }

    /// <summary>For the tooltip: "Right Ctrl + Right Shift".</summary>
    public string Display { get; private set; } = "";

    /// <summary>Null when the text is not a usable shortcut. Empty text is not usable either.</summary>
    public static DictationShortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var result = new DictationShortcut();
        var sides = new List<string>();
        var names = new List<string>();
        var modifiers = 0;

        foreach (var raw in text!.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) return null;
            var flat = token.Replace(" ", "").ToLowerInvariant();

            string? side = null;
            string? category = null;
            switch (flat)
            {
                case "ctrl": case "control": category = "Ctrl"; break;
                case "shift": category = "Shift"; break;
                case "alt": category = "Alt"; break;
                case "leftctrl": case "lctrl": case "leftcontrol": category = "Ctrl"; side = "LeftCtrl"; break;
                case "rightctrl": case "rctrl": case "rightcontrol": category = "Ctrl"; side = "RightCtrl"; break;
                case "leftshift": case "lshift": category = "Shift"; side = "LeftShift"; break;
                case "rightshift": case "rshift": category = "Shift"; side = "RightShift"; break;
                case "leftalt": case "lalt": category = "Alt"; side = "LeftAlt"; break;
                case "rightalt": case "ralt": category = "Alt"; side = "RightAlt"; break;
            }

            if (category is null)
            {
                // Only one closing key, and it must come last.
                if (result.Key is not null) return null;
                result.Key = NormalizeKey(token);
                if (result.Key is null) return null;
                names.Add(result.Key);
                continue;
            }

            if (result.Key is not null) return null;
            modifiers++;
            if (category == "Ctrl") result.Ctrl = true;
            else if (category == "Shift") result.Shift = true;
            else result.Alt = true;
            if (side is not null) sides.Add(side);
            names.Add(side is null ? category : Spaced(side));
        }

        // A shortcut that is modifiers only needs two of them, or pressing
        // Ctrl alone would start a recording. A key alone needs a modifier.
        if (result.Key is null && modifiers < 2) return null;
        if (result.Key is not null && modifiers < 1) return null;

        result.Sides = sides;
        result.Display = string.Join(" + ", names);
        return result;
    }

    private static string Spaced(string side) =>
        side.StartsWith("Left", StringComparison.Ordinal) ? "Left " + side.Substring(4) : "Right " + side.Substring(5);

    /// <summary>
    /// A letter, digit, F1–F24 or a key name. A digit becomes D0–D9, the name WPF
    /// gives the number-row key, because "1" would otherwise read as the number 1.
    /// </summary>
    private static string? NormalizeKey(string token)
    {
        if (token.Length == 1)
        {
            var c = token[0];
            if (c >= 'a' && c <= 'z') return char.ToUpperInvariant(c).ToString();
            if (c >= 'A' && c <= 'Z') return c.ToString();
            if (c >= '0' && c <= '9') return "D" + c;
        }

        // Names such as F9 or Space; digits-only longer than one char are numbers, not keys.
        foreach (var c in token)
            if (!char.IsLetterOrDigit(c)) return null;
        if (char.IsDigit(token[0])) return null;
        return char.ToUpperInvariant(token[0]) + token.Substring(1);
    }
}
