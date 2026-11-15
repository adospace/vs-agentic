using System;
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
    /// Puts text in a fenced code block, so that it shows exactly as written,
    /// line breaks included. The fence is longer than the longest run of
    /// backticks in the text, so no line in it can close the block.
    /// </summary>
    public static string CodeBlock(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + "\n" + text.Replace("\r\n", "\n") + "\n" + fence;
    }
}
