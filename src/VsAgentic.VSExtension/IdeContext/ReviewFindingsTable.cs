using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The review findings as a source of the Error List. Each chat has its own
/// set: a new report from a chat replaces that chat's set, and closing the
/// chat removes it. The Tool column says "VsAgentic review", so the user can
/// filter on it, and the Error List itself opens the file at the line on a
/// double-click.
///
/// The Error List counts this as an "other" source, like IntelliSense: with
/// the window set to "Build Only" the findings are hidden. The caller tells
/// Claude, rather than changing the user's filter.
///
/// Sinks are called from any thread; the lock covers the sets and the sinks.
/// </summary>
internal sealed class ReviewFindingsTable : ITableDataSource
{
    /// <summary>The Tool column of every finding. <see cref="DiagnosticsIdeContextProvider"/> leaves these out.</summary>
    public const string ToolName = "VsAgentic review";

    private readonly object _lock = new();
    private readonly List<ITableDataSink> _sinks = new();
    private readonly Dictionary<object, ReviewFindingsSnapshot> _snapshots = new();
    private int _version;

    public ReviewFindingsTable(ITableManager errorsTable)
    {
        errorsTable.AddSource(this,
            StandardTableColumnDefinitions.ErrorSeverity,
            StandardTableColumnDefinitions.ErrorCode,
            StandardTableColumnDefinitions.Text,
            StandardTableColumnDefinitions.DocumentName,
            StandardTableColumnDefinitions.Line,
            StandardTableColumnDefinitions.Column,
            StandardTableColumnDefinitions.BuildTool,
            StandardTableColumnDefinitions.ErrorSource);
    }

    public string SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

    public string Identifier => "VsAgentic.ReviewFindings";

    public string DisplayName => ToolName;

    /// <summary>Replaces the findings of one chat. An empty list removes them.</summary>
    public void Replace(object owner, IReadOnlyList<ReviewFinding> findings)
    {
        lock (_lock)
        {
            _snapshots.TryGetValue(owner, out var old);
            if (findings.Count == 0)
            {
                if (old is null) return;
                _snapshots.Remove(owner);
                foreach (var sink in _sinks)
                    sink.RemoveSnapshot(old);
                return;
            }

            var snapshot = new ReviewFindingsSnapshot(findings, ++_version);
            _snapshots[owner] = snapshot;
            foreach (var sink in _sinks)
            {
                if (old is null)
                    sink.AddSnapshot(snapshot);
                else
                    sink.ReplaceSnapshot(old, snapshot);
            }
        }
    }

    public IDisposable Subscribe(ITableDataSink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);
            foreach (var snapshot in _snapshots.Values)
                sink.AddSnapshot(snapshot);
        }
        return new Subscription(this, sink);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ReviewFindingsTable _table;
        private readonly ITableDataSink _sink;

        public Subscription(ReviewFindingsTable table, ITableDataSink sink)
        {
            _table = table;
            _sink = sink;
        }

        public void Dispose()
        {
            lock (_table._lock)
                _table._sinks.Remove(_sink);
        }
    }

    private sealed class ReviewFindingsSnapshot : TableEntriesSnapshotBase
    {
        private readonly IReadOnlyList<ReviewFinding> _findings;

        public ReviewFindingsSnapshot(IReadOnlyList<ReviewFinding> findings, int version)
        {
            _findings = findings;
            VersionNumber = version;
        }

        public override int VersionNumber { get; }

        public override int Count => _findings.Count;

        public override bool TryGetValue(int index, string keyName, out object content)
        {
            content = null!;
            if (index < 0 || index >= _findings.Count) return false;
            var finding = _findings[index];

            switch (keyName)
            {
                case StandardTableKeyNames.ErrorSeverity:
                    content = finding.Severity;
                    return true;
                case StandardTableKeyNames.ErrorCode:
                    if (finding.Category is null) return false;
                    content = finding.Category;
                    return true;
                case StandardTableKeyNames.Text:
                    content = finding.Message;
                    return true;
                case StandardTableKeyNames.DocumentName:
                    content = finding.File;
                    return true;
                // Both 0-based, as the Error List expects.
                case StandardTableKeyNames.Line:
                    content = finding.Line;
                    return true;
                case StandardTableKeyNames.Column:
                    content = finding.Column;
                    return true;
                case StandardTableKeyNames.BuildTool:
                    content = ToolName;
                    return true;
                case StandardTableKeyNames.ErrorSource:
                    content = ErrorSource.Other;
                    return true;
                default:
                    return false;
            }
        }
    }
}

/// <summary>One finding as the Error List shows it.</summary>
internal sealed class ReviewFinding
{
    public ReviewFinding(string file, int line, int column, __VSERRORCATEGORY severity, string message, string? category)
    {
        File = file;
        Line = line;
        Column = column;
        Severity = severity;
        Message = message;
        Category = category;
    }

    /// <summary>The full path.</summary>
    public string File { get; }

    /// <summary>0-based.</summary>
    public int Line { get; }

    /// <summary>0-based.</summary>
    public int Column { get; }

    public __VSERRORCATEGORY Severity { get; }

    public string Message { get; }

    /// <summary>Shown in the Code column, e.g. "bug". Null leaves it empty.</summary>
    public string? Category { get; }
}
