using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VsAgentic.Services.Permissions;

/// <summary>
/// Short warnings for a step that waits for approval: "Deletes folders with
/// everything in them", "Force-pushes …". They are shown on the permission
/// banner next to the command, so the user sees what is unusual about it
/// without reading the whole line.
///
/// The notes come from a fixed list of patterns, not from a model. Each note
/// names the pattern that matched, so the same command always gets the same
/// notes. The list is not complete: a command without a note can still do
/// damage, and the banner says so.
///
/// Nothing here decides anything. The notes only inform; Allow and Deny stay
/// with the user. A misread therefore costs one missing or one extra line of
/// text, which is why the splitting below is looser than the one in the rule
/// builder: it reads into substitutions and subshells instead of giving up on
/// them.
///
/// The input is the command text and the path of a changed file, which every
/// agent has. Mapping a tool call of a specific agent to these inputs is the
/// caller's job.
/// </summary>
public static class RiskNotes
{
    public const string DeletesRecursively = "Deletes folders with everything in them";
    public const string Deletes = "Deletes files or folders";
    public const string DeletesUntracked = "Deletes files that git does not track";
    public const string DiscardsChanges = "Discards uncommitted changes";
    public const string DeletesStash = "Deletes stashed changes";
    public const string DeletesUnmergedBranch = "Deletes a branch even if it is not merged";
    public const string ForcePush = "Force-pushes, which can overwrite commits on the remote";
    public const string DeletesOnRemote = "Deletes a branch or tag on the remote";
    public const string Pushes = "Sends commits to a remote";
    public const string RunsDownloadedCode = "Runs code downloaded from the internet";
    public const string RunsEncodedCommand = "Runs an encoded command";
    public const string RunsAsAdministrator = "Runs as administrator";
    public const string StopsProcesses = "Stops running processes";
    public const string Publishes = "Publishes outside this computer";
    public const string ChangesSystemSettings = "Changes Windows system settings";
    public const string ChangesFileOutsideFolder = "Changes a file outside the solution folder";

    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    /// <summary>
    /// Notes for a shell command line.
    /// </summary>
    /// <param name="posix">True for a POSIX shell (bash), where the backslash
    /// escapes and the backtick substitutes; false for PowerShell or cmd,
    /// where the backtick escapes.</param>
    public static IReadOnlyList<string> ForCommand(string? command, bool posix)
    {
        if (string.IsNullOrWhiteSpace(command)) return None;

        var notes = new List<string>();
        Collect(command!, posix, notes, depth: 0);
        return Tidy(notes);
    }

    /// <summary>
    /// Notes for a change to one file: an edit, a new file, a notebook edit.
    /// </summary>
    /// <param name="workingDirectory">The folder the agent works in. Without
    /// one, nothing is said about where the file is.</param>
    public static IReadOnlyList<string> ForFileChange(string? path, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workingDirectory)) return None;

        try
        {
            var root = Path.GetFullPath(workingDirectory!).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(workingDirectory!, path!));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? None
                : new[] { ChangesFileOutsideFolder };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // A path that cannot be read gets no note rather than a wrong one.
            return None;
        }
    }

    /// <summary>
    /// Drops a note that a stronger one already says, and repeats.
    /// </summary>
    private static IReadOnlyList<string> Tidy(List<string> notes)
    {
        if (notes.Count == 0) return None;

        var result = notes.Distinct().ToList();
        if (result.Contains(DeletesRecursively)) result.Remove(Deletes);
        if (result.Contains(ForcePush) || result.Contains(DeletesOnRemote)) result.Remove(Pushes);
        return result;
    }

    private static void Collect(string command, bool posix, List<string> notes, int depth)
    {
        var segments = Split(command, posix);

        // "$(…)" and, in bash, "`…`" run inside double quotes too:
        // git commit -m "$(rm -rf ~)", iex "& { $(irm https://…) }".
        // The splitter keeps a quoted string whole, so they are read here.
        var substitutions = depth < 3 ? QuotedSubstitutions(command, posix) : new List<string>();

        // A download feeds the commands after it in the same pipeline.
        var pipelineDownloads = false;
        var lineDownloads = segments.Any(s => IsDownload(s)) ||
                            substitutions.Any(s => Split(s, posix).Any(IsDownload));
        var lineEvaluates = false;

        // The files a download on this line writes, by file name: curl -o
        // i.sh, wget -O i.sh, iwr -OutFile i.ps1, curl … > i.sh.
        var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segments.Where(IsDownload))
            foreach (var w in Words(s.Text))
                if (FileName(w.TrimStart('>')) is { } file && file.IndexOf('.') > 0 && !w.Contains("://"))
                    downloaded.Add(file);

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!segment.Piped) pipelineDownloads = false;

            var words = Words(segment.Text);
            var (name, args, programWord) = Program(words, notes, posix);
            if (name is null) continue;

            if (Evaluates.Contains(name)) lineEvaluates = true;

            // curl … | sh, iwr … | iex
            if (segment.Piped && pipelineDownloads && RunsInput.Contains(name))
                notes.Add(RunsDownloadedCode);

            // bash <(curl …), iex (iwr …): the download is in the next segment.
            if (segment.OpensGroup && RunsInput.Contains(name) && i + 1 < segments.Count && IsDownload(segments[i + 1]))
                notes.Add(RunsDownloadedCode);

            // curl -o i.sh … && sh i.sh, … && ./i.sh: the file the download
            // wrote is run, by a shell or by itself.
            if (downloaded.Count > 0 && !IsDownload(segment) &&
                ((RunsInput.Contains(name) && args.Any(a => FileName(a) is { } f && downloaded.Contains(f))) ||
                 (FileName(programWord) is { } run && downloaded.Contains(run))))
                notes.Add(RunsDownloadedCode);

            if (IsDownload(segment)) pipelineDownloads = true;

            Check(name, args, posix, notes);

            // bash -c "rm -rf x", pwsh -Command "Remove-Item …": the command
            // is in a string. Read it as a command line of its own.
            if (depth < 3 && Shells.TryGetValue(name, out var innerPosix))
            {
                var inline = InlineCommand(name, args, notes);
                if (inline is not null) Collect(inline, innerPosix, notes, depth + 1);
            }
        }

        foreach (var inner in substitutions)
            Collect(inner, posix, notes, depth + 1);

        if (lineDownloads && lineEvaluates) notes.Add(RunsDownloadedCode);
    }

    /// <summary>
    /// The commands in <c>$(…)</c> (and, in bash, <c>`…`</c>) inside double
    /// quotes. Single quotes run nothing, in bash and in PowerShell.
    /// </summary>
    private static List<string> QuotedSubstitutions(string command, bool posix)
    {
        var found = new List<string>();
        var escape = posix ? '\\' : '`';
        var quote = '\0';

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == escape && quote != '\'' && i + 1 < command.Length)
            {
                i++;
                continue;
            }

            if (quote == '\0')
            {
                if (c == '"' || c == '\'') quote = c;
                continue;
            }

            if (c == quote)
            {
                quote = '\0';
                continue;
            }

            if (quote != '"') continue;

            if (c == '$' && i + 1 < command.Length && command[i + 1] == '(')
            {
                // To the matching parenthesis; quotes inside belong to the
                // inner command.
                var depth = 0;
                var start = i + 2;
                var j = i + 1;
                var innerQuote = '\0';
                for (; j < command.Length; j++)
                {
                    var d = command[j];
                    if (innerQuote != '\0')
                    {
                        if (d == innerQuote) innerQuote = '\0';
                        continue;
                    }
                    // "$(echo "x")": inside $( ) quotes start afresh.
                    if (d == '\'' || d == '"') { innerQuote = d; continue; }
                    if (d == '(') depth++;
                    else if (d == ')' && --depth == 0) break;
                }
                if (j > start) found.Add(command.Substring(start, Math.Min(j, command.Length) - start));
                i = j;
                continue;
            }

            if (posix && c == '`')
            {
                var end = command.IndexOf('`', i + 1);
                if (end > i + 1) found.Add(command.Substring(i + 1, end - i - 1));
                if (end < 0) break;
                i = end;
            }
        }

        return found;
    }

    /// <summary>The file name of a path-like word, or null for a flag or an empty word.</summary>
    private static string? FileName(string word)
    {
        if (string.IsNullOrEmpty(word) || word.StartsWith("-", StringComparison.Ordinal)) return null;
        var p = word.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        var name = slash >= 0 ? p.Substring(slash + 1) : p;
        return name.Length == 0 ? null : name;
    }

    private static void Check(string name, List<string> args, bool posix, List<string> notes)
    {
        // A registry path makes Remove-Item, Set-ItemProperty and the rest
        // change settings, not files.
        if (RegistryCommands.Contains(name) && args.Any(IsRegistryPath))
        {
            notes.Add(ChangesSystemSettings);
            return;
        }

        if (DeleteCommands.Contains(name))
        {
            notes.Add(args.Any(IsRecursiveFlag) ? DeletesRecursively : Deletes);
            return;
        }

        if (name == "find")
        {
            if (FindDeletes(args)) notes.Add(Deletes);
            return;
        }

        if (name == "git")
        {
            CheckGit(args, notes);
            return;
        }

        if (KillCommands.Contains(name))
        {
            notes.Add(StopsProcesses);
            return;
        }

        if ((name == "start-process" || name == "saps" || name == "start") && HasVerbRunAs(args))
        {
            notes.Add(RunsAsAdministrator);
            return;
        }

        var sub = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal))?.ToLowerInvariant();

        if (IsPublish(name, sub, args))
        {
            notes.Add(Publishes);
            return;
        }

        if (IsSystemChange(name, sub)) notes.Add(ChangesSystemSettings);
    }

    /// <summary>
    /// find with -delete, or with -exec running a delete command. The braces
    /// of -exec rm {} split the line, so the delete command is the last
    /// argument here.
    /// </summary>
    private static bool FindDeletes(List<string> args)
    {
        if (args.Contains("-delete")) return true;
        for (var i = 0; i < args.Count - 1; i++)
            if ((args[i] == "-exec" || args[i] == "-execdir" || args[i] == "-ok") && DeleteCommands.Contains(Leaf(args[i + 1])))
                return true;
        return false;
    }

    private static void CheckGit(List<string> args, List<string> notes)
    {
        // Global options come before the subcommand; -C, -c and the long
        // ones below take the next word as their value (--git-dir x push).
        var i = 0;
        while (i < args.Count && args[i].StartsWith("-", StringComparison.Ordinal))
        {
            if (args[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix" or "--config-env")
                i++;
            i++;
        }

        if (i >= args.Count) return;

        var sub = args[i].ToLowerInvariant();
        var rest = args.Skip(i + 1).ToList();

        bool Has(params string[] flags) => rest.Any(a => flags.Any(f =>
            a == f || a.StartsWith(f + "=", StringComparison.Ordinal)));

        // A cluster of short flags, such as -fd for git clean.
        bool HasShort(char flag) => rest.Any(a =>
            a.Length >= 2 && a[0] == '-' && a[1] != '-' && a.Skip(1).All(char.IsLetter) && a.IndexOf(flag, 1) > 0);

        switch (sub)
        {
            case "push":
                // --prune deletes remote branches that have no local branch.
                if (Has("--delete", "--prune") || HasShort('d') || rest.Any(a => a.StartsWith(":", StringComparison.Ordinal) && a.Length > 1))
                    notes.Add(DeletesOnRemote);
                if (Has("--force", "--force-with-lease", "--force-if-includes", "--mirror") || HasShort('f') ||
                    rest.Any(a => a.StartsWith("+", StringComparison.Ordinal) && a.Length > 1))
                    notes.Add(ForcePush);
                notes.Add(Pushes);
                break;

            case "reset":
                // --merge and --keep refuse to lose a change; --hard does not.
                if (Has("--hard")) notes.Add(DiscardsChanges);
                break;

            case "checkout":
                // git checkout HEAD a.cs: a commit and then paths, without "--",
                // overwrites the files too. With -b, -B or --orphan the words
                // are a new branch and its start.
                var positional = rest.Count(a => !a.StartsWith("-", StringComparison.Ordinal));
                var makesBranch = rest.Any(a => a is "-b" or "-B" or "--orphan");
                if (rest.Contains("--") || rest.Contains(".") || Has("--force") || HasShort('f') ||
                    (positional >= 2 && !makesBranch))
                    notes.Add(DiscardsChanges);
                break;

            case "restore":
                // --staged alone only takes changes out of the index.
                var staged = Has("--staged") || HasShort('S');
                var worktree = Has("--worktree") || HasShort('W');
                if (!staged || worktree) notes.Add(DiscardsChanges);
                break;

            case "switch":
                if (Has("--discard-changes", "--force") || HasShort('f')) notes.Add(DiscardsChanges);
                break;

            case "clean":
                // Without --force git refuses to clean, and --dry-run only lists.
                if ((Has("--force") || HasShort('f')) && !Has("--dry-run") && !HasShort('n'))
                    notes.Add(DeletesUntracked);
                break;

            case "stash":
                var action = rest.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal))?.ToLowerInvariant();
                if (action == "drop" || action == "clear") notes.Add(DeletesStash);
                break;

            case "branch":
                if (rest.Contains("-D") || ((Has("--delete") || rest.Contains("-d")) && (Has("--force") || rest.Contains("-f"))))
                    notes.Add(DeletesUnmergedBranch);
                break;

            case "rm":
                if (!Has("--cached")) notes.Add(rest.Any(IsRecursiveFlag) ? DeletesRecursively : Deletes);
                break;
        }
    }

    private static bool IsPublish(string name, string? sub, List<string> args)
    {
        switch (name)
        {
            case "npm":
            case "pnpm":
            case "yarn":
            case "cargo":
                return sub == "publish";
            case "gem":
            case "nuget":
            case "docker":
            case "podman":
                return sub == "push";
            case "twine":
                return sub == "upload";
            case "dotnet":
                // dotnet publish is a build into a folder, not a release.
                return sub == "nuget" && args.Any(a => a.Equals("push", StringComparison.OrdinalIgnoreCase));
            case "gh":
                return sub == "release" && args.Any(a => a.Equals("create", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private static bool IsSystemChange(string name, string? sub)
    {
        switch (name)
        {
            case "reg":
                return sub is "add" or "delete" or "import" or "copy" or "restore" or "load" or "unload";
            case "schtasks":
                return sub is "/create" or "/delete" or "/change";
            case "setx":
            case "bcdedit":
            case "diskpart":
            case "set-executionpolicy":
            case "new-service":
            case "set-service":
            case "remove-service":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The program a command runs, after assignments in front of it and
    /// wrappers such as sudo or env, with the words that follow it and the
    /// word it was named by. A wrapper that raises rights adds its own note.
    /// </summary>
    private static (string? Name, List<string> Args, string ProgramWord) Program(List<string> words, List<string> notes, bool posix)
    {
        var i = 0;
        while (i < words.Count)
        {
            var word = words[i];
            if (IsAssignment(word))
            {
                i++;
                continue;
            }

            // PowerShell: $null = Remove-Item …, $r = Invoke-Expression ….
            if (!posix && word.StartsWith("$", StringComparison.Ordinal) && i + 1 < words.Count &&
                (words[i + 1] == "=" || words[i + 1] == "+="))
            {
                i += 2;
                continue;
            }

            var name = Leaf(word);
            if (!Wrappers.Contains(name)) break;

            if (Elevators.Contains(name)) notes.Add(RunsAsAdministrator);
            i++;

            // The wrapper's own options and their values: sudo -u bob,
            // timeout 10s, timeout -k 5 10, env -u NAME, nice -n 10.
            WrapperValueOptions.TryGetValue(name, out var takesValue);
            while (i < words.Count)
            {
                var w = words[i];
                if (w.StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                    if (takesValue is not null && takesValue.Contains(w) && i < words.Count) i++;
                    continue;
                }
                if (IsAssignment(w) || IsDuration(w))
                {
                    i++;
                    continue;
                }
                break;
            }
        }

        if (i >= words.Count) return (null, new List<string>(), "");
        return (Leaf(words[i]), words.Skip(i + 1).ToList(), words[i]);
    }

    /// <summary>10, 10s, 1.5m, 2h: the duration timeout and sleep take.</summary>
    private static bool IsDuration(string word)
    {
        var digits = word.TrimEnd('s', 'm', 'h', 'd');
        return digits.Length > 0 && digits.Length >= word.Length - 1 &&
               digits.All(c => char.IsDigit(c) || c == '.') && char.IsDigit(digits[0]);
    }

    /// <summary>Wrapper options that take the next word as their value.</summary>
    private static readonly Dictionary<string, HashSet<string>> WrapperValueOptions = new(StringComparer.Ordinal)
    {
        ["sudo"] = new(StringComparer.Ordinal) { "-u", "-g", "-p", "-C", "-D", "-h", "-r", "-t", "-U", "-T" },
        ["doas"] = new(StringComparer.Ordinal) { "-u", "-C" },
        ["env"] = new(StringComparer.Ordinal) { "-u", "-C", "-S", "--unset", "--chdir" },
        ["timeout"] = new(StringComparer.Ordinal) { "-s", "-k", "--signal", "--kill-after" },
        ["nice"] = new(StringComparer.Ordinal) { "-n", "--adjustment" },
        ["runas"] = new(StringComparer.OrdinalIgnoreCase),
    };

    private static bool IsAssignment(string word)
    {
        var eq = word.IndexOf('=');
        return eq > 0 && word.Take(eq).All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// The command in <c>bash -c "…"</c>, <c>bash -lc "…"</c>,
    /// <c>pwsh -Command "…"</c> or <c>cmd /c …</c>. For
    /// <c>powershell -EncodedCommand …</c> it adds a note and returns the
    /// decoded command, so what it does is read as well.
    /// </summary>
    private static string? InlineCommand(string shell, List<string> args, List<string> notes)
    {
        var powershell = shell is "pwsh" or "powershell";
        for (var i = 0; i < args.Count - 1; i++)
        {
            var a = args[i];
            var lower = a.ToLowerInvariant();

            if (shell == "cmd")
            {
                if (lower is "/c" or "/k") return string.Join(" ", args.Skip(i + 1));
                continue;
            }

            if (powershell)
            {
                if (lower.Length < 2 || lower[0] != '-') continue;

                // -Command, or any prefix of it: -c, -co, -com.
                if ("-command".StartsWith(lower, StringComparison.Ordinal))
                    return string.Join(" ", args.Skip(i + 1));

                // -EncodedCommand, or a prefix of it: -e, -ec, -enc.
                // PowerShell reads -e as -EncodedCommand.
                if ("-encodedcommand".StartsWith(lower, StringComparison.Ordinal) || lower == "-ec")
                {
                    notes.Add(RunsEncodedCommand);
                    return DecodeCommand(args[i + 1]);
                }
                continue;
            }

            // bash: -c alone, or in a cluster of short flags: -lc, -ec, -xc.
            if (a.Length >= 2 && a[0] == '-' && a[1] != '-' && a.Skip(1).All(char.IsLetter) && a.IndexOf('c', 1) > 0)
                return string.Join(" ", args.Skip(i + 1));
        }

        return null;
    }

    /// <summary>The base64 UTF-16 text of -EncodedCommand, or null when it is not one.</summary>
    private static string? DecodeCommand(string encoded)
    {
        try
        {
            return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool HasVerbRunAs(List<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i].Equals("-Verb", StringComparison.OrdinalIgnoreCase) &&
                args[i + 1].Equals("RunAs", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// -r, -rf, -fR, --recursive, PowerShell's -Recurse (or its short -r),
    /// and cmd's /s.
    /// </summary>
    private static bool IsRecursiveFlag(string arg)
    {
        if (arg.Equals("--recursive", StringComparison.OrdinalIgnoreCase)) return true;
        if (arg.Equals("/s", StringComparison.OrdinalIgnoreCase)) return true;
        if (arg.Length < 2 || arg[0] != '-' || arg[1] == '-') return false;

        var letters = arg.Substring(1);
        var colon = letters.IndexOf(':');
        if (colon >= 0) letters = letters.Substring(0, colon);
        if (letters.Length == 0 || !letters.All(char.IsLetter)) return false;

        // A PowerShell parameter can be shortened to any unique prefix, and
        // -Recurse is the only one of Remove-Item that starts with r.
        if (letters.StartsWith("r", StringComparison.OrdinalIgnoreCase)) return true;

        // A cluster of single-letter flags: -fr, -vrf. Longer words are named
        // parameters such as -Force, which has an r in it as well.
        return letters.Length <= 3 && letters.IndexOf("r", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRegistryPath(string arg)
    {
        var a = arg.TrimStart('-');
        return a.StartsWith("HKLM:", StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith("HKCU:", StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDownload(Segment segment)
    {
        var words = Words(segment.Text);
        if (words.Any(w => Downloaders.Contains(Leaf(w)))) return true;

        // (New-Object Net.WebClient).DownloadString(…) names no program.
        return segment.Text.IndexOf("DownloadString", StringComparison.OrdinalIgnoreCase) >= 0 ||
               segment.Text.IndexOf("DownloadFile", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Lower-case file name of a program, without folder and extension.</summary>
    private static string Leaf(string word)
    {
        var p = word.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        if (slash >= 0) p = p.Substring(slash + 1);
        var dot = p.LastIndexOf('.');
        if (dot > 0) p = p.Substring(0, dot);
        return p.ToLowerInvariant();
    }

    private readonly struct Segment
    {
        public Segment(string text, bool piped, bool opensGroup)
        {
            Text = text;
            Piped = piped;
            OpensGroup = opensGroup;
        }

        public string Text { get; }

        /// <summary>A pipe feeds this command.</summary>
        public bool Piped { get; }

        /// <summary>The command ends where a group, a substitution or a
        /// process substitution starts: <c>bash &lt;(</c>, <c>iex (</c>.</summary>
        public bool OpensGroup { get; }
    }

    /// <summary>
    /// The commands of a line. Splits on separators outside quotes, and also
    /// on brackets and substitutions, so the commands inside them are read
    /// as well. A comment is skipped.
    /// </summary>
    private static List<Segment> Split(string command, bool posix)
    {
        var segments = new List<Segment>();
        var current = new StringBuilder();
        var quote = '\0';
        var piped = false;
        var wordStart = true;
        var escape = posix ? '\\' : '`';

        void Flush(bool nextPiped, bool opensGroup)
        {
            var text = current.ToString().Trim();
            current.Clear();
            if (text.Length > 0) segments.Add(new Segment(text, piped, opensGroup));
            piped = nextPiped;
            wordStart = true;
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            var next = i + 1 < command.Length ? command[i + 1] : '\0';

            if (c == escape && next != '\0' && quote != '\'')
            {
                current.Append(c).Append(next);
                i++;
                wordStart = false;
                continue;
            }

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
                wordStart = false;
                continue;
            }

            // ${name}, ${#array[@]}: a variable, not a group. Its braces would
            // otherwise end the command and its # start a comment that hides
            // the rest of the line.
            if (c == '$' && next == '{')
            {
                var close = command.IndexOf('}', i + 2);
                var end = close < 0 ? command.Length - 1 : close;
                current.Append(command, i, end - i + 1);
                i = end;
                wordStart = false;
                continue;
            }

            // PowerShell's block comment <# … #> ends where it says, not at the
            // end of the line.
            if (!posix && c == '<' && next == '#')
            {
                var close = command.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = close < 0 ? command.Length : close + 1;
                wordStart = true;
                continue;
            }

            if (c == '#' && wordStart)
            {
                while (i + 1 < command.Length && command[i + 1] != '\n') i++;
                continue;
            }

            switch (c)
            {
                case '|':
                    if (next == '|')
                    {
                        Flush(false, false);
                        i++;
                    }
                    else
                    {
                        Flush(true, false);
                    }
                    continue;

                case '&':
                    // 2>&1 and &> are redirections.
                    if ((i > 0 && command[i - 1] == '>') || next == '>')
                    {
                        current.Append(c);
                        continue;
                    }
                    Flush(false, false);
                    if (next == '&') i++;
                    continue;

                case ';':
                case '\n':
                case '\r':
                case ')':
                case '}':
                    Flush(false, false);
                    continue;

                case '(':
                case '{':
                    // $( and <( leave a $ or < that is no part of the command.
                    if (current.Length > 0 && (current[current.Length - 1] == '$' || current[current.Length - 1] == '<'))
                        current.Length--;
                    Flush(false, true);
                    continue;

                case '`' when posix:
                    Flush(false, false);
                    continue;
            }

            current.Append(c);
            wordStart = c == ' ' || c == '\t';
        }

        Flush(false, false);
        return segments;
    }

    /// <summary>The words of a command, with quotes taken off.</summary>
    private static List<string> Words(string segment)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var inWord = false;

        foreach (var c in segment)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                inWord = true;
                continue;
            }

            if (c == ' ' || c == '\t')
            {
                if (inWord) words.Add(current.ToString());
                current.Clear();
                inWord = false;
                continue;
            }

            current.Append(c);
            inWord = true;
        }

        if (inWord) words.Add(current.ToString());
        return words;
    }

    // Names are compared after Leaf(), so in lower case and without .exe.

    /// <summary>rm, del, rd and the PowerShell names; in PowerShell rm, del,
    /// erase, rd, rmdir and ri are all aliases of Remove-Item.</summary>
    private static readonly HashSet<string> DeleteCommands = new(StringComparer.Ordinal)
    {
        "rm", "rmdir", "rd", "del", "erase", "unlink", "shred", "ri", "remove-item",
    };

    private static readonly HashSet<string> RegistryCommands = new(StringComparer.Ordinal)
    {
        "remove-item", "ri", "rm", "del", "set-item", "set-itemproperty", "sp", "new-item", "ni",
        "new-itemproperty", "remove-itemproperty", "rp", "rename-itemproperty", "clear-itemproperty",
    };

    private static readonly HashSet<string> KillCommands = new(StringComparer.Ordinal)
    {
        "kill", "pkill", "killall", "taskkill", "stop-process", "spps",
    };

    private static readonly HashSet<string> Downloaders = new(StringComparer.Ordinal)
    {
        "curl", "wget", "iwr", "invoke-webrequest", "irm", "invoke-restmethod",
    };

    /// <summary>Programs that run the code they read on their input.</summary>
    private static readonly HashSet<string> RunsInput = new(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "ksh", "fish", "pwsh", "powershell", "cmd",
        "python", "python3", "py", "node", "perl", "ruby", "php",
        "iex", "invoke-expression", "source",
    };

    /// <summary>Commands that run a string as code.</summary>
    private static readonly HashSet<string> Evaluates = new(StringComparer.Ordinal)
    {
        "iex", "invoke-expression", "eval",
    };

    /// <summary>Shells that take a command line in a string, and whether
    /// that line is POSIX.</summary>
    private static readonly Dictionary<string, bool> Shells = new(StringComparer.Ordinal)
    {
        ["bash"] = true, ["sh"] = true, ["zsh"] = true, ["dash"] = true, ["ksh"] = true,
        ["pwsh"] = false, ["powershell"] = false, ["cmd"] = false,
    };

    /// <summary>Programs that run the command after them.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "gsudo", "runas", "env", "nohup", "time", "nice", "timeout", "command",
        "builtin", "exec", "stdbuf", "xargs", "winpty",
    };

    private static readonly HashSet<string> Elevators = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "gsudo", "runas",
    };
}
