using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// Keeps a live view of the Error List: every entry from every source, as
/// the Error List itself receives them — build output, IntelliSense and
/// analyzers alike.
///
/// The window's filters are not meant to reach us, but one does: with the window set to "Build Only", the
/// editor's source stops producing live entries at all (measured in Visual
/// Studio 2026), so only build entries arrive. The caller reports that
/// rather than changing the user's filter.
///
/// Each source pushes its entries into a sink of ours; the sinks only hold
/// references, and the entries are read when someone asks. Sources call the
/// sinks from any thread.
/// </summary>
internal sealed class ErrorListReader : IDisposable
{
    private readonly ITableManager _tableManager;
    private readonly object _lock = new();
    private readonly Dictionary<ITableDataSource, SourceSink> _sinks = new();
    private bool _disposed;

    public ErrorListReader(ITableManager tableManager)
    {
        _tableManager = tableManager;
        _tableManager.SourcesChanged += OnSourcesChanged;
        OnSourcesChanged(this, EventArgs.Empty);
    }

    /// <summary>All entries the Error List holds now, suppressed ones included.</summary>
    public ErrorListSnapshot Read()
    {
        List<SourceSink> sinks;
        lock (_lock) sinks = _sinks.Values.ToList();

        var entries = new List<ErrorListEntry>();
        var stable = true;
        unchecked
        {
            var version = 17;
            foreach (var sink in sinks)
            {
                stable &= sink.IsStable;
                version = version * 31 + sink.Read(entries);
            }
            return new ErrorListSnapshot(entries, stable, version.ToString("x8"));
        }
    }

    private void OnSourcesChanged(object sender, EventArgs e)
    {
        var current = _tableManager.Sources;
        var toDispose = new List<SourceSink>();

        lock (_lock)
        {
            if (_disposed) return;

            foreach (var source in current)
            {
                if (_sinks.ContainsKey(source)) continue;
                try
                {
                    _sinks[source] = new SourceSink(source);
                }
                catch (Exception ex)
                {
                    // One source that refuses costs only its own entries.
                    Serilog.Log.Warning(ex, "[ErrorList] could not subscribe to {Source}", source.DisplayName);
                }
            }

            foreach (var gone in _sinks.Keys.Except(current).ToList())
            {
                toDispose.Add(_sinks[gone]);
                _sinks.Remove(gone);
            }
        }

        foreach (var sink in toDispose)
            sink.Dispose();
    }

    public void Dispose()
    {
        _tableManager.SourcesChanged -= OnSourcesChanged;

        List<SourceSink> sinks;
        lock (_lock)
        {
            _disposed = true;
            sinks = _sinks.Values.ToList();
            _sinks.Clear();
        }

        foreach (var sink in sinks)
            sink.Dispose();
    }

    /// <summary>
    /// What one source has handed us. A source uses any mix of single
    /// entries, snapshots and snapshot factories; in Visual Studio 2026 the
    /// editor's source hands over one snapshot.
    /// </summary>
    private sealed class SourceSink : ITableDataSink, IDisposable
    {
        private readonly object _lock = new();
        private readonly List<ITableEntry> _entries = new();
        private readonly List<ITableEntriesSnapshot> _snapshots = new();
        private readonly List<ITableEntriesSnapshotFactory> _factories = new();
        private readonly IDisposable? _subscription;
        private int _changes;

        public SourceSink(ITableDataSource source)
        {
            _subscription = source.Subscribe(this);
        }

        public bool IsStable { get; set; } = true;

        /// <summary>Adds this source's entries and returns a number that changes with them.</summary>
        public int Read(List<ErrorListEntry> into)
        {
            ITableEntry[] entries;
            ITableEntriesSnapshot[] snapshots;
            ITableEntriesSnapshotFactory[] factories;
            int changes;
            lock (_lock)
            {
                entries = _entries.ToArray();
                snapshots = _snapshots.ToArray();
                factories = _factories.ToArray();
                changes = _changes;
            }

            var version = changes;
            foreach (var entry in entries)
                Add(into, (string key, out object value) => entry.TryGetValue(key, out value));

            foreach (var factory in factories)
            {
                version = unchecked(version * 31 + factory.CurrentVersionNumber);
                if (factory.GetCurrentSnapshot() is { } snapshot)
                    AddAll(into, snapshot);
            }

            foreach (var snapshot in snapshots)
            {
                version = unchecked(version * 31 + snapshot.VersionNumber);
                AddAll(into, snapshot);
            }

            return version;
        }

        private static void AddAll(List<ErrorListEntry> into, ITableEntriesSnapshot snapshot)
        {
            for (var i = 0; i < snapshot.Count; i++)
            {
                var index = i;
                Add(into, (string key, out object value) => snapshot.TryGetValue(index, key, out value));
            }
        }

        private delegate bool ValueReader(string key, out object value);

        private static void Add(List<ErrorListEntry> into, ValueReader read)
        {
            try
            {
                into.Add(new ErrorListEntry(
                    Severity: Get(read, StandardTableKeyNames.ErrorSeverity) is { } severity
                        ? (__VSERRORCATEGORY)Convert.ToInt32(severity)
                        : __VSERRORCATEGORY.EC_MESSAGE,
                    Code: Get(read, StandardTableKeyNames.ErrorCode) as string,
                    Text: Get(read, StandardTableKeyNames.Text) as string ?? "",
                    Document: Get(read, StandardTableKeyNames.DocumentName) as string,
                    Line: Get(read, StandardTableKeyNames.Line) is int line ? line : -1,
                    Column: Get(read, StandardTableKeyNames.Column) is int column ? column : -1,
                    Project: Get(read, StandardTableKeyNames.ProjectName) as string,
                    IsBuild: Get(read, StandardTableKeyNames.ErrorSource) is ErrorSource.Build,
                    Tool: Get(read, StandardTableKeyNames.BuildTool) as string,
                    IsSuppressed: Get(read, StandardTableKeyNames.SuppressionState) is SuppressionState.Suppressed,
                    IsActiveContext: Get(read, StandardTableKeyNames.IsActiveContext) is not false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A source that fails on one entry should not cost the others.
                Serilog.Log.Debug(ex, "[ErrorList] Could not read an entry");
            }
        }

        private static object? Get(ValueReader read, string key)
            => read(key, out var value) ? value : null;

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries = false)
        {
            lock (_lock)
            {
                if (removeAllEntries) _entries.Clear();
                _entries.AddRange(newEntries);
                _changes++;
            }
        }

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
        {
            lock (_lock)
            {
                foreach (var entry in oldEntries) _entries.Remove(entry);
                _changes++;
            }
        }

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
        {
            lock (_lock)
            {
                foreach (var entry in oldEntries) _entries.Remove(entry);
                _entries.AddRange(newEntries);
                _changes++;
            }
        }

        public void RemoveAllEntries()
        {
            lock (_lock) { _entries.Clear(); _changes++; }
        }

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots = false)
        {
            lock (_lock)
            {
                if (removeAllSnapshots) _snapshots.Clear();
                _snapshots.Add(newSnapshot);
                _changes++;
            }
        }

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
        {
            lock (_lock) { _snapshots.Remove(oldSnapshot); _changes++; }
        }

        public void RemoveAllSnapshots()
        {
            lock (_lock) { _snapshots.Clear(); _changes++; }
        }

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
        {
            lock (_lock)
            {
                _snapshots.Remove(oldSnapshot);
                _snapshots.Add(newSnapshot);
                _changes++;
            }
        }

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories = false)
        {
            lock (_lock)
            {
                if (removeAllFactories) _factories.Clear();
                _factories.Add(newFactory);
                _changes++;
            }
        }

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
        {
            lock (_lock) { _factories.Remove(oldFactory); _changes++; }
        }

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
        {
            lock (_lock)
            {
                _factories.Remove(oldFactory);
                _factories.Add(newFactory);
                _changes++;
            }
        }

        // The factory itself reports the new version; Read picks it up.
        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory) { }

        public void RemoveAllFactories()
        {
            lock (_lock) { _factories.Clear(); _changes++; }
        }

        public void Dispose()
        {
            try { _subscription?.Dispose(); } catch { }
        }
    }
}

internal sealed class ErrorListEntry
{
    public __VSERRORCATEGORY Severity { get; }
    public string? Code { get; }
    public string Text { get; }
    public string? Document { get; }

    /// <summary>0-based, or -1 when the entry has none.</summary>
    public int Line { get; }

    /// <summary>0-based, or -1 when the entry has none.</summary>
    public int Column { get; }

    public string? Project { get; }

    /// <summary>From an explicit build; otherwise from live analysis (IntelliSense, analyzers).</summary>
    public bool IsBuild { get; }

    public string? Tool { get; }
    public bool IsSuppressed { get; }

    /// <summary>
    /// False for the copy of an entry in a project context other than the one
    /// the editor shows, e.g. a second target framework.
    /// </summary>
    public bool IsActiveContext { get; }

    public ErrorListEntry(
        __VSERRORCATEGORY Severity, string? Code, string Text, string? Document, int Line, int Column,
        string? Project, bool IsBuild, string? Tool, bool IsSuppressed, bool IsActiveContext)
    {
        this.Severity = Severity;
        this.Code = Code;
        this.Text = Text;
        this.Document = Document;
        this.Line = Line;
        this.Column = Column;
        this.Project = Project;
        this.IsBuild = IsBuild;
        this.Tool = Tool;
        this.IsSuppressed = IsSuppressed;
        this.IsActiveContext = IsActiveContext;
    }
}

internal sealed class ErrorListSnapshot
{
    public IReadOnlyList<ErrorListEntry> Entries { get; }

    /// <summary>False while a source is still updating, e.g. during a build.</summary>
    public bool IsStable { get; }

    /// <summary>Changes whenever any source's entries change.</summary>
    public string Version { get; }

    public ErrorListSnapshot(IReadOnlyList<ErrorListEntry> entries, bool isStable, string version)
    {
        Entries = entries;
        IsStable = isStable;
        Version = version;
    }
}
