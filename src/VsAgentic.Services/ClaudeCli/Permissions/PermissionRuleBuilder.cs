using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Turns a pending permission request into the rules the CLI needs in order to
/// stop asking for calls of the same shape.
///
/// Rules are written in the CLI's own syntax, where <c>Bash(git log:*)</c>
/// means "any command starting with git log" and <c>Edit(//c/src/a.cs)</c>
/// means "edits to that one file". The CLI does the matching; this class only
/// decides what to key on.
/// </summary>
public static class PermissionRuleBuilder
{
    /// <summary>
    /// How many subcommand words are kept after the executable.
    ///
    /// One, not two. Two looks right on <c>gh issue list</c> but misreads
    /// <c>git ls-tree main</c>, where the second word is a branch name — the
    /// rule then only ever matches that one branch. There is no way to tell a
    /// subcommand from an operand without knowing the tool, so the conservative
    /// depth is the one that cannot be wrong in this direction.
    /// </summary>
    private const int MaxSubcommands = 1;

    /// <summary>
    /// Rules covering calls of this shape. Empty when the request has nothing
    /// stable to key on, in which case only a one-off allow makes sense.
    /// </summary>
    public static IReadOnlyList<PermissionRule> Build(string toolName, JsonElement input)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return Array.Empty<PermissionRule>();

        if (IsShell(toolName))
            return BuildShellRules(toolName, input);

        if (IsFileEdit(toolName))
        {
            // The CLI matches every file-editing tool against Edit rules, so a
            // Write(...) rule would be stored and then never consulted.
            var path = FileRulePath(ReadString(input, "file_path") ?? ReadString(input, "notebook_path"));
            return path is null
                ? Array.Empty<PermissionRule>()
                : new[] { new PermissionRule("Edit", path) };
        }

        if (toolName.Equals("Read", StringComparison.OrdinalIgnoreCase))
        {
            var path = FileRulePath(ReadString(input, "file_path"));
            return path is null
                ? Array.Empty<PermissionRule>()
                : new[] { new PermissionRule("Read", path) };
        }

        if (toolName.Equals("WebFetch", StringComparison.OrdinalIgnoreCase))
        {
            // Without a specifier this would allow fetching any URL.
            return Uri.TryCreate(ReadString(input, "url"), UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? new[] { new PermissionRule(toolName, "domain:" + uri.Host) }
                : Array.Empty<PermissionRule>();
        }

        // Tools without a meaningful argument to key on (MCP tools, WebSearch,
        // ...) are allowed as a whole, which is also what the CLI itself
        // offers for them.
        return new[] { new PermissionRule(toolName) };
    }

    private static IReadOnlyList<PermissionRule> BuildShellRules(string toolName, JsonElement input)
    {
        // A shell line is usually several commands joined by && or |, and the
        // CLI only stops asking once every one of them is covered. Granting the
        // first segment alone produces Bash(cd:*) for "cd x && rm -rf y", which
        // reads as if the prompt was about cd while the next prompt is
        // identical.
        var rules = new List<PermissionRule>();

        foreach (var segment in SplitSegments(ReadString(input, "command")))
        {
            var prefix = CommandPrefix(segment);
            if (prefix is null) continue;

            var rule = new PermissionRule(toolName, prefix + ":*");
            if (!rules.Any(r => r.Display == rule.Display))
                rules.Add(rule);
        }

        return rules;
    }

    private static bool IsShell(string toolName) =>
        toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileEdit(string toolName) =>
        toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> and line breaks,
    /// but not inside quotes: <c>grep "a|b" x</c> is one command.
    /// </summary>
    private static IReadOnlyList<string> SplitSegments(string? command)
    {
        var segments = new List<string>();
        if (string.IsNullOrWhiteSpace(command)) return segments;

        var current = new StringBuilder();
        char quote = '\0';

        void Flush()
        {
            var s = current.ToString().Trim();
            if (s.Length > 0) segments.Add(s);
            current.Clear();
        }

        for (var i = 0; i < command!.Length; i++)
        {
            var c = command[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == ';' || c == '|' || c == '\n' || c == '\r')
            {
                Flush();
                if (c == '|' && i + 1 < command.Length && command[i + 1] == '|') i++;
                continue;
            }

            if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
            {
                Flush();
                i++;
                continue;
            }

            current.Append(c);
        }

        Flush();
        return segments;
    }

    /// <summary>
    /// The executable plus at most <see cref="MaxSubcommands"/> subcommand:
    /// "git log" from <c>git log --oneline -5</c>. Flags and operands are never
    /// part of a rule, so <c>dotnet --version</c> yields "dotnet".
    /// </summary>
    private static string? CommandPrefix(string segment)
    {
        // An executable given by path is quoted when the path has spaces, so it
        // has to be taken whole: splitting on spaces first turned
        // "C:/Program Files/gh/gh.exe" --version into a rule for
        // "C:/Program", which matches nothing. The quotes stay in the rule,
        // because the CLI compares against the command as written.
        var quoted = QuotedHead(segment);
        if (quoted is not null) return quoted;

        var parts = segment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (TakesNoSubcommand(parts[0])) return parts[0];

        var words = new List<string> { parts[0] };

        for (var i = 1; i < parts.Length && words.Count <= MaxSubcommands; i++)
        {
            var part = parts[i];

            // Stop at the first flag: everything after it is arguments, and a
            // rule built from those would match nothing next time.
            if (part.StartsWith("-", StringComparison.Ordinal)) break;

            // Anything with a path separator, quote, redirection or variable is
            // an operand, not a subcommand.
            if (part.IndexOfAny(new[] { '/', '\\', '"', '\'', '>', '<', '$', '=', '.', ':' }) >= 0) break;

            words.Add(part);
        }

        return string.Join(" ", words);
    }

    /// <summary>
    /// Commands whose first argument is always an operand. Without this,
    /// <c>cd src</c> yields <c>Bash(cd src:*)</c>, which never matches the next
    /// <c>cd</c>. The list only has to cover common cases: a command missing
    /// from it gets a narrower rule, never a broader one.
    ///
    /// PowerShell cmdlets (<c>Get-Content</c>) never have subcommands either.
    /// </summary>
    private static bool TakesNoSubcommand(string executable) =>
        OperandOnlyCommands.Contains(executable) ||
        (executable.IndexOf('-') > 0 && char.IsUpper(executable[0]));

    private static readonly HashSet<string> OperandOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "dir", "cat", "head", "tail", "less", "more", "grep", "rg", "find",
        "echo", "printf", "rm", "rmdir", "mkdir", "cp", "mv", "touch", "wc", "sort",
        "uniq", "which", "where", "type", "sed", "awk", "xargs", "chmod", "stat",
        "file", "diff", "tree", "pwd", "sleep", "test",
    };

    /// <summary>
    /// The quoted executable at the head of a segment, quotes included, or
    /// null when the segment does not start with one. Subcommands are not
    /// collected in this case: a command invoked by full path is specific
    /// enough on its own.
    /// </summary>
    private static string? QuotedHead(string segment)
    {
        if (segment.Length < 2) return null;

        var quote = segment[0];
        if (quote != '"' && quote != '\'') return null;

        var close = segment.IndexOf(quote, 1);
        if (close <= 1) return null;

        return segment.Substring(0, close + 1);
    }

    /// <summary>
    /// The path in the form the CLI's file rules are documented to take. Those
    /// follow gitignore syntax, where a single leading slash is relative to the
    /// settings file and an absolute path needs two. On Windows the CLI compares
    /// against the POSIX form, so <c>C:\src\a.cs</c> becomes <c>//c/src/a.cs</c>.
    ///
    /// Returns null for a relative path or one with glob characters in it: the
    /// first would be resolved against a directory we cannot see, the second
    /// would match more files than the one the user was shown.
    /// </summary>
    private static string? FileRulePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path!.IndexOfAny(new[] { '*', '?', '[', ']', '!', '#', '{', '}' }) >= 0) return null;

        var p = path.Replace('\\', '/');

        if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '/')
            return "//" + char.ToLowerInvariant(p[0]) + p.Substring(2);

        if (p.StartsWith("/", StringComparison.Ordinal) && !p.StartsWith("//", StringComparison.Ordinal))
            return "/" + p;

        return null;
    }

    private static string? ReadString(JsonElement input, string property)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        if (!input.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
