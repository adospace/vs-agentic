using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Append-only record of "this many tokens went out at this time", used to fill
/// the rolling 5-hour and weekly meters in the chat header.
///
/// It is deliberately machine-wide rather than per-session: the rate limit it
/// approximates is charged against the account, so a meter that reset when a
/// session closed — or when Visual Studio restarted — would flatter the user
/// exactly when they most need the warning. That means the backing file is
/// shared, possibly by two IDEs at once, so every operation here is best-effort
/// and swallows its own IO errors. A missing or corrupt log costs a meter;
/// it must never cost a message.
/// </summary>
public sealed class UsageLog
{
    private const string DirectoryName = "usage";
    private const string FileName = "tokens.log";

    /// <summary>
    /// Entries older than this are dropped. Matches the longest window we
    /// report, so the file stays proportional to what is actually readable
    /// rather than growing for the life of the install.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly Lazy<UsageLog> LazyShared = new(() => new UsageLog(DefaultPath()));

    /// <summary>
    /// Process-wide instance. Sessions each build their own DI container, so a
    /// container singleton would give one log per session — the opposite of
    /// what this type is for.
    /// </summary>
    public static UsageLog Shared => LazyShared.Value;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private bool _loaded;

    /// <summary>
    /// Last-write stamp of the file as of our last read. Comparing it on every
    /// query is what lets a second Visual Studio instance's spending show up in
    /// this one's meter without either polling or a file watcher.
    /// </summary>
    private DateTime _loadedStamp;

    public UsageLog(string path)
    {
        _path = path;
    }

    private readonly struct Entry
    {
        public Entry(DateTime utc, long tokens)
        {
            Utc = utc;
            Tokens = tokens;
        }

        public DateTime Utc { get; }
        public long Tokens { get; }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VsAgentic", DirectoryName, FileName);

    /// <summary>
    /// Notes tokens spent now. Zero and negative counts are ignored so a turn
    /// that reported no usage cannot pad the log with empty rows.
    /// </summary>
    public void Record(long tokens) => Record(tokens, DateTime.UtcNow);

    internal void Record(long tokens, DateTime utcNow)
    {
        if (tokens <= 0) return;

        lock (_gate)
        {
            EnsureLoadedLocked(utcNow);
            _entries.Add(new Entry(utcNow, tokens));
            AppendLocked(utcNow, tokens);
        }
    }

    /// <summary>
    /// Tokens spent within the trailing <paramref name="shortWindow"/> and
    /// <paramref name="longWindow"/>.
    /// </summary>
    public (long Short, long Long) Totals(TimeSpan shortWindow, TimeSpan longWindow) =>
        Totals(shortWindow, longWindow, DateTime.UtcNow);

    internal (long Short, long Long) Totals(TimeSpan shortWindow, TimeSpan longWindow, DateTime utcNow)
    {
        lock (_gate)
        {
            EnsureLoadedLocked(utcNow);

            var shortCutoff = utcNow - shortWindow;
            var longCutoff = utcNow - longWindow;

            long shortTotal = 0, longTotal = 0;
            foreach (var e in _entries)
            {
                if (e.Utc >= longCutoff) longTotal += e.Tokens;
                if (e.Utc >= shortCutoff) shortTotal += e.Tokens;
            }

            return (shortTotal, longTotal);
        }
    }

    private static DateTime WriteStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception) { return DateTime.MinValue; }
    }

    private void EnsureLoadedLocked(DateTime utcNow)
    {
        var stamp = WriteStamp(_path);
        if (_loaded && stamp == _loadedStamp) return;

        _loaded = true;
        _loadedStamp = stamp;
        _entries.Clear();

        try
        {
            if (!File.Exists(_path)) return;

            var cutoff = utcNow - Retention;
            var stale = false;

            // Scoped, not a function-level `using`: the prune below deletes this
            // very path, which cannot happen while we still hold it open.
            using (var stream = new FileStream(
                       _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!TryParse(line, out var entry))
                    {
                        // A torn last line is normal when another process is mid-append.
                        continue;
                    }

                    if (entry.Utc < cutoff) { stale = true; continue; }
                    _entries.Add(entry);
                }
            }

            if (stale) RewriteLocked();
        }
        catch (Exception)
        {
            // An unreadable log means no meter, not a broken chat.
            _entries.Clear();
        }
    }

    private static bool TryParse(string line, out Entry entry)
    {
        entry = default;

        var space = line.IndexOf(' ');
        if (space <= 0) return false;

        if (!long.TryParse(line.Substring(0, space), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var unixSeconds))
            return false;
        if (!long.TryParse(line.Substring(space + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var tokens))
            return false;
        if (tokens <= 0) return false;

        try
        {
            entry = new Entry(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, tokens);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string Format(DateTime utc, long tokens) =>
        new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        + " " + tokens.ToString(CultureInfo.InvariantCulture);

    private void AppendLocked(DateTime utc, long tokens)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            // One short line per append, so a concurrent writer interleaves
            // whole rows rather than splitting one.
            using (var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                writer.WriteLine(Format(utc, tokens));
            }

            // Our own write must not look like someone else's, or the next
            // query would re-read the whole file for nothing.
            _loadedStamp = WriteStamp(_path);
        }
        catch (Exception)
        {
            // In-memory totals still work for this session.
        }
    }

    private void RewriteLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            // Written to a sibling then moved, so a reader never observes a
            // half-pruned file. Losing the race with another pruner is fine —
            // both write the same surviving rows.
            var temp = _path + ".tmp";
            using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                foreach (var e in _entries)
                    writer.WriteLine(Format(e.Utc, e.Tokens));
            }

            File.Delete(_path);
            File.Move(temp, _path);
            _loadedStamp = WriteStamp(_path);
        }
        catch (Exception)
        {
            // Pruning is housekeeping; failing it only means a larger file.
        }
    }
}
