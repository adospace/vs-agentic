using System.IO;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The Error List's errors and warnings by file and line, for the places in
/// the editor that offer "Fix in VsAgentic Chat": the light bulb, the margin
/// icon and the editor's context menu. Built once per change of the list
/// rather than on every question the editor asks, which comes per line and
/// per caret move.
///
/// Messages are left out: analyzers report one on most lines, and the editor
/// would then offer the fix everywhere. The Error List's own menu still
/// takes them.
/// </summary>
internal static class ErrorListProblems
{
    // A build changes the list hundreds of times in a row; the editor is told
    // once it has settled.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private static readonly object Lock = new();
    private static ErrorListReader? _reader;
    private static Dictionary<string, Dictionary<int, List<Problem>>>? _byFile;
    private static Timer? _timer;

    /// <summary>
    /// Raised on a background thread after the list has changed and settled,
    /// and when the list becomes available.
    /// </summary>
    public static event Action? Changed;

    /// <summary>Called by the package once the Error List can be read.</summary>
    public static void Attach(ErrorListReader reader)
    {
        lock (Lock)
        {
            if (_reader is not null) _reader.Changed -= OnReaderChanged;
            _reader = reader;
            _byFile = null;
            _reader.Changed += OnReaderChanged;
            _timer ??= new Timer(_ => RaiseChanged());
        }

        OnReaderChanged();
    }

    public static void Detach()
    {
        lock (Lock)
        {
            if (_reader is not null) _reader.Changed -= OnReaderChanged;
            _reader = null;
            _byFile = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// The errors and warnings on the given 0-based lines of a file, errors
    /// first. Empty before the package has loaded.
    /// </summary>
    public static IReadOnlyList<Problem> OnLines(string? filePath, int firstLine, int lastLine)
    {
        if (string.IsNullOrEmpty(filePath)) return Array.Empty<Problem>();

        var byFile = Index();
        if (byFile is null || !byFile.TryGetValue(Key(filePath!), out var lines))
            return Array.Empty<Problem>();

        var found = new List<Problem>();
        if (lastLine - firstLine < lines.Count)
        {
            for (var line = firstLine; line <= lastLine; line++)
                if (lines.TryGetValue(line, out var problems)) found.AddRange(problems);
        }
        else
        {
            foreach (var pair in lines)
                if (pair.Key >= firstLine && pair.Key <= lastLine) found.AddRange(pair.Value);
        }

        return FixRequest.Distinct(found.OrderBy(p => p.Severity).ThenBy(p => p.Line));
    }

    private static void OnReaderChanged()
    {
        lock (Lock)
        {
            _generation++;
            _byFile = null;
            _timer?.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[ErrorListProblems] A change handler failed");
        }
    }

    private static Dictionary<string, Dictionary<int, List<Problem>>>? Index()
    {
        ErrorListReader? reader;
        int generation;
        lock (Lock)
        {
            if (_byFile is not null) return _byFile;
            reader = _reader;
            generation = _generation;
        }

        if (reader is null) return null;

        var byFile = new Dictionary<string, Dictionary<int, List<Problem>>>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in reader.Read().Entries)
            {
                if (entry.IsSuppressed || !entry.IsActiveContext || entry.Line < 0 ||
                    string.IsNullOrEmpty(entry.Document) ||
                    entry.Severity is not (__VSERRORCATEGORY.EC_ERROR or __VSERRORCATEGORY.EC_WARNING))
                {
                    continue;
                }

                var key = Key(entry.Document!);
                if (!byFile.TryGetValue(key, out var lines))
                    byFile[key] = lines = new Dictionary<int, List<Problem>>();
                if (!lines.TryGetValue(entry.Line, out var problems))
                    lines[entry.Line] = problems = new List<Problem>();
                problems.Add(entry.ToProblem());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The editor goes without the fix for now; the next change rebuilds.
            Serilog.Log.Debug(ex, "[ErrorListProblems] Could not index the Error List");
            return null;
        }

        lock (Lock)
        {
            // A change that came in while the list was read makes this one
            // stale: use it for this answer, but build again for the next.
            if (_reader == reader && _generation == generation)
                _byFile = byFile;
        }

        return byFile;
    }

    // Counts the changes, so an index built while one came in is not kept.
    private static int _generation;

    private static string Key(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return path.ToUpperInvariant();
        }
    }
}
