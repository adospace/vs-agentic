using System.Text;

namespace VsAgentic.Services.Services;

/// <summary>
/// Puts text the extension does not control (an error from the CLI, a
/// server's reason) into a markdown body, so that it shows as written.
/// The chat renders markdown with showdown, which also passes HTML through.
/// </summary>
public static class MarkdownText
{
    /// <summary>
    /// Escapes markdown punctuation with a backslash and HTML with entities.
    /// Line breaks become spaces, so the text stays inside the line or the
    /// emphasis it is put in.
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\r': break;
                case '\n': sb.Append(' '); break;
                case '\\': case '`': case '*': case '_': case '{': case '}':
                case '[': case ']': case '(': case ')': case '#': case '+':
                case '-': case '.': case '!': case '|': case '~':
                    sb.Append('\\').Append(c);
                    break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wraps the text in inline code that shows it as written, also when it
    /// contains backticks: the fence is one backtick longer than the longest
    /// run in the text, and a space pads text that starts or ends with a
    /// backtick or a space. Line breaks become spaces. Empty text stays empty.
    /// </summary>
    public static string InlineCode(string text)
    {
        if (text.Length == 0) return "";
        text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        var fence = new string('`', longest + 1);
        var pad = NeedsPad(text[0]) || NeedsPad(text[text.Length - 1]) ? " " : "";
        return fence + pad + text + pad + fence;

        static bool NeedsPad(char c) => c == '`' || c == ' ';
    }
}
