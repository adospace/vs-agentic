using System.IO;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The Error List's errors and warnings by file and line, for the places in
/// the editor that offer "Fix in VsAgentic Chat": the light bulb, the margin
/// icon and the editor's context menu. Those ask per visible line and per
/// caret move, on the UI thread, so they only look the answer up in an index.
/// The index is built on a timer thread once the list has settled after a
/// change, and swapped in whole; until then the previous one answers.
///
/// Messages are left out: analyzers report one on most lines, and the editor
/// would then offer the fix everywhere. The Error List's own menu still
/// takes them.
/// </summary>
internal static class ErrorListProblems
{
    // A build changes the list hundreds of times in a row; the index is built
    // once it has settled.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    // While changes keep coming (a long build, live analysis while typing),
    // the index is built at least this often, so the icons do not lag far.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    private static readonly object Lock = new();
    private static ErrorListReader? _reader;
    private static Dictionary<string, Dictionary<int, List<Problem>>>? _byFile;
    private static Timer? _timer;

    // When the first change not yet in the index came in; null when the index is current.
    private static DateTime? _pendingSince;

    // Counts the changes, so a build that a change overtook knows to run again.
    private static int _generation;

    // One build at a time: the timer can fire again while one runs.
    private static int _building;

    /// <summary>
    /// Raised on a background thread after a new index is in place, so the
    /// editor redraws from it.
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
            _timer ??= new Timer(_ => Build());
            _generation++;
            _pendingSince = DateTime.UtcNow;
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    public static void Detach()
    {
        lock (Lock)
        {
            if (_reader is not null) _reader.Changed -= OnReaderChanged;
            _reader = null;
            _byFile = null;
            _pendingSince = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// The errors and warnings on the given 0-based lines of a file, errors
    /// first, from the last index built. Empty before the first one. Never
    /// reads the Error List itself, so it is cheap on the UI thread.
    /// </summary>
    public static IReadOnlyList<Problem> OnLines(string? filePath, int firstLine, int lastLine)
    {
        if (string.IsNullOrEmpty(filePath)) return Array.Empty<Problem>();

        Dictionary<string, Dictionary<int, List<Problem>>>? byFile;
        lock (Lock) byFile = _byFile;
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

    // Called by the sources on any thread, often while they update, so it
    // only moves the timer.
    private static void OnReaderChanged()
    {
        lock (Lock)
        {
            if (_timer is null) return;
            _generation++;
            var now = DateTime.UtcNow;
            _pendingSince ??= now;
            var waited = now - _pendingSince.Value;
            var due = waited >= MaxDelay ? TimeSpan.Zero : Min(Settle, MaxDelay - waited);
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>On the timer thread: reads the list, swaps the new index in, tells the editor.</summary>
    private static void Build()
    {
        if (Interlocked.Exchange(ref _building, 1) == 1) return;
        try
        {
            ErrorListReader? reader;
            int generation;
            lock (Lock)
            {
                reader = _reader;
                generation = _generation;
            }
            if (reader is null) return;

            Dictionary<string, Dictionary<int, List<Problem>>> byFile;
            try
            {
                byFile = ReadIndex(reader);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The editor keeps the previous index; the next change builds again.
                Serilog.Log.Debug(ex, "[ErrorListProblems] Could not index the Error List");
                return;
            }

            lock (Lock)
            {
                if (_reader != reader) return;
                // Newer than what the editor has, even if a change came in
                // while the list was read; that change has moved the timer
                // and builds again.
                _byFile = byFile;
                if (_generation == generation) _pendingSince = null;
            }

            RaiseChanged();
        }
        finally
        {
            Volatile.Write(ref _building, 0);
        }

        // A change that came in while this build ran found it busy; build for
        // it now. Reached only after a build that succeeded: a failed one
        // returns above and waits for the next change, so it does not retry
        // without end.
        lock (Lock)
        {
            if (_pendingSince is not null && _timer is not null)
                _timer.Change(Settle, Timeout.InfiniteTimeSpan);
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

    private static Dictionary<string, Dictionary<int, List<Problem>>> ReadIndex(ErrorListReader reader)
    {
        var byFile = new Dictionary<string, Dictionary<int, List<Problem>>>(StringComparer.Ordinal);
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
        return byFile;
    }

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
