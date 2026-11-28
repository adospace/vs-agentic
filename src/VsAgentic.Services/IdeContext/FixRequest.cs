using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VsAgentic.Services.IdeContext;

public enum ProblemSeverity
{
    Error,
    Warning,
    Message,
}

/// <summary>
/// A problem the IDE reports that the user asks the agent to fix, such as an
/// Error List entry. Not tied to an agent: the request is plain text and the
/// locations become editor attachments.
/// </summary>
public sealed class Problem
{
    public ProblemSeverity Severity { get; }

    /// <summary>The diagnostic code, e.g. CS0246, or null when the entry has none.</summary>
    public string? Code { get; }

    public string Text { get; }

    /// <summary>Absolute path, or null for a problem with no document (a project or package error).</summary>
    public string? FilePath { get; }

    /// <summary>1-based, or null when the problem has no line.</summary>
    public int? Line { get; }

    public string? Project { get; }

    /// <summary>
    /// From the last build. Its line stays as it was at the build, while live
    /// analysis follows the edits; after an edit above it the line is off.
    /// </summary>
    public bool IsFromBuild { get; }

    public Problem(ProblemSeverity severity, string? code, string text, string? filePath, int? line, string? project,
        bool isFromBuild = false)
    {
        Severity = severity;
        Code = string.IsNullOrWhiteSpace(code) ? null : code!.Trim();
        Text = (text ?? "").Trim();
        FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        Line = FilePath is null || line is null or < 1 ? null : line;
        Project = string.IsNullOrWhiteSpace(project) ? null : project;
        IsFromBuild = isFromBuild;
    }
}

/// <summary>A place to attach for a fix request.</summary>
/// <param name="FilePath">Absolute path.</param>
/// <param name="Line">1-based.</param>
/// <param name="LineIsCurrent">
/// True when live analysis reports a problem on this line, so the line is the
/// one in the editor now. False when only the last build does: the code at
/// that line may be a different line by now, and is better left out.
/// </param>
public sealed record FixLocation(string FilePath, int Line, bool LineIsCurrent);

/// <summary>
/// The text "Fix in VsAgentic Chat" puts in the input box, and the places it
/// attaches. The user reads and sends it, so it says what to fix and nothing
/// about how.
/// </summary>
public static class FixRequest
{
    /// <summary>Beyond this the request names how many more there are; the agent can read the Error List.</summary>
    public const int MaxProblems = 20;

    /// <summary>One attachment per place, up to this many; the request text names the rest.</summary>
    public const int MaxLocations = 10;

    /// <summary>
    /// The same problem once. The Error List can hold a problem twice: from
    /// the last build and from live analysis, or once per target framework.
    /// The copy from live analysis is kept: its line follows the edits.
    /// </summary>
    public static IReadOnlyList<Problem> Distinct(IEnumerable<Problem> problems) =>
        problems
            .Where(p => p.Text.Length > 0)
            .GroupBy(p => (p.Severity, p.Code, p.Text, Path: p.FilePath?.ToUpperInvariant(), p.Line))
            .Select(g => g.FirstOrDefault(p => !p.IsFromBuild) ?? g.First())
            .ToList();

    /// <summary>The request text, or null when there is nothing to fix.</summary>
    public static string? BuildPrompt(IReadOnlyList<Problem> problems)
    {
        if (problems.Count == 0) return null;

        if (problems.Count == 1)
        {
            var only = problems[0];
            return $"Fix this {Noun(only.Severity)}:\n{Describe(only, withLocation: false)}";
        }

        var kinds = problems.Select(p => p.Severity).Distinct().ToList();
        var noun = kinds.Count == 1 ? Plural(kinds[0]) : "problems";

        var text = new StringBuilder($"Fix these {noun}:");
        foreach (var problem in problems.Take(MaxProblems))
            text.Append("\n- ").Append(Describe(problem, withLocation: true));

        if (problems.Count > MaxProblems)
            text.Append($"\n- … and {problems.Count - MaxProblems} more in the Error List.");

        return text.ToString();
    }

    /// <summary>The distinct places to attach, in the order of the request.</summary>
    public static IReadOnlyList<FixLocation> Locations(IReadOnlyList<Problem> problems) =>
        problems
            .Take(MaxProblems)
            .Where(p => p.FilePath is not null && p.Line is not null)
            .GroupBy(p => (Path: p.FilePath!.ToUpperInvariant(), Line: p.Line!.Value))
            .Select(g => new FixLocation(g.First().FilePath!, g.Key.Line, g.Any(p => !p.IsFromBuild)))
            .Take(MaxLocations)
            .ToList();

    private static string Describe(Problem problem, bool withLocation)
    {
        var text = new StringBuilder();
        if (problem.Code is not null)
            text.Append(problem.Code);

        // A single problem's place is in its attachment. In a list each item
        // names its own, so the agent can tell which attachment goes with it.
        string? where = null;
        if (problem.FilePath is not null)
        {
            if (withLocation)
            {
                where = SafeFileName(problem.FilePath);
                if (problem.Line is { } line)
                    where += $", line {line}";
            }
            else if (problem.Line is null)
            {
                // No attachment carries a file without a line.
                where = problem.FilePath;
            }
        }
        else if (problem.Project is not null)
        {
            where = $"project {problem.Project}";
        }

        if (where is not null)
            text.Append(text.Length > 0 ? " in " : "In ").Append(where);

        if (text.Length > 0)
            text.Append(": ");

        return text.Append(ShownAsWritten(problem.Text)).ToString();
    }

    /// <summary>
    /// The chat renders a sent message as Markdown, which passes HTML through:
    /// <c>List&lt;string&gt;</c> showed as "List" with the type argument gone.
    /// A message with '&lt;' goes into a code span, which shows it as it is
    /// and reads the same to the agent. Others stay plain text.
    /// </summary>
    private static string ShownAsWritten(string text)
    {
        if (text.IndexOf('<') < 0) return text;

        // A fence longer than any run of backticks in the text, and a space
        // inside it when the text starts or ends with one.
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        var fence = new string('`', longest + 1);
        var pad = text.StartsWith("`", StringComparison.Ordinal) || text.EndsWith("`", StringComparison.Ordinal) ? " " : "";
        return fence + pad + text + pad + fence;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (System.ArgumentException)
        {
            return path;
        }
    }

    private static string Noun(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Error => "error",
        ProblemSeverity.Warning => "warning",
        _ => "problem",
    };

    private static string Plural(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Error => "errors",
        ProblemSeverity.Warning => "warnings",
        _ => "problems",
    };
}
