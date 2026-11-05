using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Models;

namespace VsAgentic.Services.Services;

public class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public JsonSessionStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _basePath = Path.Combine(appData, "VsAgentic", "workspaces");
    }

    public JsonSessionStore(string basePath)
    {
        _basePath = basePath;
    }

    // --- Workspace ---

    public string GetWorkspaceId(string folderPath)
    {
        var normalized = NormalizePath(folderPath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
    }

    public async Task<bool> WorkspaceExistsAsync(string folderPath)
    {
        var wsPath = GetWorkspacePath(folderPath);
        var infoPath = Path.Combine(wsPath, "workspace.json");
        return File.Exists(infoPath);
    }

    public async Task EnsureWorkspaceAsync(string folderPath)
    {
        var wsPath = GetWorkspacePath(folderPath);
        var infoPath = Path.Combine(wsPath, "workspace.json");

        if (File.Exists(infoPath))
            return;

        Directory.CreateDirectory(wsPath);
        Directory.CreateDirectory(Path.Combine(wsPath, "sessions"));

        var info = new WorkspaceInfo
        {
            FolderPath = folderPath,
            CreatedUtc = DateTime.UtcNow
        };
        await WriteJsonAsync(infoPath, info);

        var indexPath = Path.Combine(wsPath, "sessions-index.json");
        if (!File.Exists(indexPath))
            await WriteJsonAsync(indexPath, new List<SessionEntry>());
    }

    // --- Session Index ---

    public async Task<IReadOnlyList<SessionEntry>> GetSessionIndexAsync(string folderPath)
    {
        var indexPath = GetSessionIndexPath(folderPath);
        if (!File.Exists(indexPath))
            return Array.Empty<SessionEntry>();

        return await ReadJsonAsync<List<SessionEntry>>(indexPath) ?? new List<SessionEntry>();
    }

    public async Task<SessionEntry> CreateSessionAsync(string folderPath, string title)
    {
        var semaphore = GetLock(folderPath);
        await semaphore.WaitAsync();
        try
        {
            await EnsureWorkspaceAsync(folderPath);

            var indexPath = GetSessionIndexPath(folderPath);
            var index = await ReadJsonAsync<List<SessionEntry>>(indexPath) ?? new List<SessionEntry>();

            var nextId = index.Count > 0 ? index.Max(s => s.Id) + 1 : 1;

            var entry = new SessionEntry
            {
                Id = nextId,
                Title = title,
                Ordinal = index.Count,
                CreatedUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow
            };

            index.Add(entry);
            await WriteJsonAsync(indexPath, index);

            // Create session folder and files
            var sessionDir = GetSessionDir(folderPath, nextId);
            Directory.CreateDirectory(sessionDir);

            await WriteJsonAsync(Path.Combine(sessionDir, "session.json"), entry);
            await WriteJsonAsync(Path.Combine(sessionDir, "messages.json"), new List<PersistedMessage>());

            return entry;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task UpdateSessionAsync(string folderPath, SessionEntry entry)
    {
        var semaphore = GetLock(folderPath);
        await semaphore.WaitAsync();
        try
        {
            var indexPath = GetSessionIndexPath(folderPath);
            var index = await ReadJsonAsync<List<SessionEntry>>(indexPath) ?? new List<SessionEntry>();

            var existing = index.FindIndex(s => s.Id == entry.Id);
            if (existing >= 0)
            {
                index[existing] = entry;
                await WriteJsonAsync(indexPath, index);
            }

            var sessionPath = Path.Combine(GetSessionDir(folderPath, entry.Id), "session.json");
            if (File.Exists(sessionPath))
                await WriteJsonAsync(sessionPath, entry);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task DeleteSessionAsync(string folderPath, int sessionId)
    {
        var semaphore = GetLock(folderPath);
        await semaphore.WaitAsync();
        try
        {
            var indexPath = GetSessionIndexPath(folderPath);
            var index = await ReadJsonAsync<List<SessionEntry>>(indexPath) ?? new List<SessionEntry>();

            index.RemoveAll(s => s.Id == sessionId);
            await WriteJsonAsync(indexPath, index);

            var sessionDir = GetSessionDir(folderPath, sessionId);
            _searchCache.TryRemove(Path.Combine(sessionDir, "messages.json"), out _);
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task SetSessionArchivedAsync(string folderPath, int sessionId, bool archived)
    {
        var semaphore = GetLock(folderPath);
        await semaphore.WaitAsync();
        try
        {
            var indexPath = GetSessionIndexPath(folderPath);
            var index = await ReadJsonAsync<List<SessionEntry>>(indexPath) ?? new List<SessionEntry>();

            var entry = index.Find(s => s.Id == sessionId);
            if (entry is null) return;

            entry.ArchivedUtc = archived ? DateTime.UtcNow : null;

            // A restore counts as activity. Without it a session the clean-up
            // archived is still past the cutoff, and the next startup archives
            // it again.
            if (!archived)
                entry.LastActivityUtc = DateTime.UtcNow;

            await WriteJsonAsync(indexPath, index);

            // session.json is the copy a session folder carries for itself; it
            // is rewritten so the two do not disagree after a restore.
            var sessionPath = Path.Combine(GetSessionDir(folderPath, sessionId), "session.json");
            if (File.Exists(sessionPath))
                await WriteJsonAsync(sessionPath, entry);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task PurgeOldSessionsAsync(string folderPath, int days)
    {
        if (days <= 0) return;

        var cutoffUtc = DateTime.UtcNow.AddDays(-days);

        // The index is read once and iterated over a copy, because both calls
        // below rewrite it.
        var index = await GetSessionIndexAsync(folderPath);

        foreach (var entry in index)
        {
            if (entry.ArchivedUtc is DateTime archivedUtc)
            {
                // Stage two: archived long enough, so now it really goes.
                if (archivedUtc < cutoffUtc)
                    await DeleteSessionAsync(folderPath, entry.Id);
            }
            else if (entry.LastActivityUtc < cutoffUtc)
            {
                // Stage one: idle long enough. This used to delete the session
                // outright; it now archives it, so the next startup is what
                // removes it and the user has until then to restore it.
                await SetSessionArchivedAsync(folderPath, entry.Id, archived: true);
            }
        }
    }

    // --- Messages ---

    public async Task<IReadOnlyList<PersistedMessage>> GetMessagesAsync(string folderPath, int sessionId)
    {
        var messagesPath = Path.Combine(GetSessionDir(folderPath, sessionId), "messages.json");
        if (!File.Exists(messagesPath))
            return Array.Empty<PersistedMessage>();

        return await ReadJsonAsync<List<PersistedMessage>>(messagesPath) ?? new List<PersistedMessage>();
    }

    public async Task SaveMessagesAsync(string folderPath, int sessionId, IReadOnlyList<PersistedMessage> messages)
    {
        var sessionDir = GetSessionDir(folderPath, sessionId);
        Directory.CreateDirectory(sessionDir);

        var messagesPath = Path.Combine(sessionDir, "messages.json");
        await WriteJsonAsync(messagesPath, messages);
    }

    public async Task AppendMessageAsync(string folderPath, int sessionId, PersistedMessage message)
    {
        var semaphore = GetLock(folderPath);
        await semaphore.WaitAsync();
        try
        {
            var sessionDir = GetSessionDir(folderPath, sessionId);
            Directory.CreateDirectory(sessionDir);

            var messagesPath = Path.Combine(sessionDir, "messages.json");
            var messages = File.Exists(messagesPath)
                ? await ReadJsonAsync<List<PersistedMessage>>(messagesPath) ?? new List<PersistedMessage>()
                : new List<PersistedMessage>();

            message.Ordinal = messages.Count;
            messages.Add(message);
            await WriteJsonAsync(messagesPath, messages);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task SetStepFileChangeAsync(string folderPath, int sessionId, string stepId, FileChange change)
    {
        var semaphore = GetLock(folderPath);
        await semaphore.WaitAsync();
        try
        {
            var messagesPath = Path.Combine(GetSessionDir(folderPath, sessionId), "messages.json");
            if (!File.Exists(messagesPath)) return;

            var messages = await ReadJsonAsync<List<PersistedMessage>>(messagesPath);
            var step = messages?.FindLast(m => m.StepId == stepId);
            if (step is null) return;

            step.FileChange = change;
            await WriteJsonAsync(messagesPath, messages!);
        }
        finally
        {
            semaphore.Release();
        }
    }

    // --- Search ---

    // Characters of context kept on each side of a match in a search hit.
    private const int HitContextBefore = 30;
    private const int HitContextAfter = 80;

    private static readonly string[] SearchedItemTypes = { "User", "Assistant" };

    /// <summary>
    /// The searchable text of each messages.json, keyed by path and tagged with
    /// the file's size and write time. A search runs on every pause in typing
    /// and reads every session, and a messages.json also carries full tool
    /// output, so parsing them all again each time would cost far more than
    /// the text it yields.
    /// </summary>
    private readonly ConcurrentDictionary<string, SearchableMessages> _searchCache = new();

    private sealed class SearchableMessages
    {
        public DateTime LastWriteUtc { get; init; }
        public long Length { get; init; }
        public IReadOnlyList<string> Texts { get; init; } = Array.Empty<string>();
    }

    public Task<IReadOnlyList<SessionSearchHit>> SearchMessagesAsync(
        string folderPath, string query, CancellationToken cancellationToken = default)
    {
        // Parsing is synchronous and can run to megabytes, so keep it off the
        // caller's thread, which is the UI thread.
        return Task.Run(async () =>
        {
            var hits = new List<SessionSearchHit>();
            if (string.IsNullOrWhiteSpace(query)) return (IReadOnlyList<SessionSearchHit>)hits;

            var index = await GetSessionIndexAsync(folderPath).ConfigureAwait(false);
            foreach (var entry in index)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = Path.Combine(GetSessionDir(folderPath, entry.Id), "messages.json");
                var texts = ReadSearchableTexts(path);
                foreach (var text in texts)
                {
                    var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;

                    hits.Add(CreateHit(entry.Id, text, at, query.Length));
                    break;
                }
            }

            return (IReadOnlyList<SessionSearchHit>)hits;
        }, cancellationToken);
    }

    private IReadOnlyList<string> ReadSearchableTexts(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                _searchCache.TryRemove(path, out _);
                return Array.Empty<string>();
            }

            if (_searchCache.TryGetValue(path, out var cached)
                && cached.LastWriteUtc == file.LastWriteTimeUtc
                && cached.Length == file.Length)
            {
                return cached.Texts;
            }

            var messages = JsonSerializer.Deserialize<List<PersistedMessage>>(
                File.ReadAllText(path, Encoding.UTF8), JsonOptions) ?? new List<PersistedMessage>();

            var texts = messages
                .Where(m => SearchedItemTypes.Contains(m.ItemType) && !string.IsNullOrEmpty(m.Content))
                .Select(m => m.Content)
                .ToList();

            _searchCache[path] = new SearchableMessages
            {
                LastWriteUtc = file.LastWriteTimeUtc,
                Length = file.Length,
                Texts = texts
            };
            return texts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A session that is being written to right now. Search what was
            // read last time; the next search reads it again.
            return _searchCache.TryGetValue(path, out var cached) ? cached.Texts : Array.Empty<string>();
        }
    }

    private static SessionSearchHit CreateHit(int sessionId, string text, int at, int length)
    {
        var start = Math.Max(0, at - HitContextBefore);
        var end = Math.Min(text.Length, at + length + HitContextAfter);

        // Start and end on a word, so the line does not open on half of one.
        if (start > 0)
        {
            var space = text.IndexOf(' ', start, at - start);
            if (space >= 0) start = space + 1;
        }
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1, end - (at + length));
            if (space >= 0) end = space;
        }

        return new SessionSearchHit
        {
            SessionId = sessionId,
            Before = (start > 0 ? "…" : "") + CollapseWhitespace(text.Substring(start, at - start)),
            Match = CollapseWhitespace(text.Substring(at, length)),
            After = CollapseWhitespace(text.Substring(at + length, end - at - length)) + (end < text.Length ? "…" : "")
        };
    }

    // The hit shows on a single line, so line breaks and runs of spaces in the
    // message would only push the match out of view.
    private static string CollapseWhitespace(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    // --- AI Conversation History ---

    public async Task<string> SaveImageAsync(string folderPath, int sessionId, ChatImageAttachment image)
    {
        var imagesDir = Path.Combine(GetSessionDir(folderPath, sessionId), "images");
        Directory.CreateDirectory(imagesDir);

        var fileName = $"{Guid.NewGuid():N}{image.Extension}";
        using (var stream = new FileStream(
                   Path.Combine(imagesDir, fileName),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   bufferSize: 64 * 1024, useAsync: true))
        {
            await stream.WriteAsync(image.Data, 0, image.Data.Length);
        }

        image.FileName = fileName;
        return fileName;
    }

    public async Task<ChatImageAttachment?> GetImageAsync(string folderPath, int sessionId, string fileName)
    {
        // Guard against a crafted or corrupted messages.json walking out of the
        // session folder.
        if (fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || fileName.Contains(".."))
            return null;

        var path = Path.Combine(GetSessionDir(folderPath, sessionId), "images", fileName);
        if (!File.Exists(path))
            return null;

        byte[] data;
        using (var stream = new FileStream(
                   path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   bufferSize: 64 * 1024, useAsync: true))
        {
            data = new byte[stream.Length];
            var read = 0;
            while (read < data.Length)
            {
                var chunk = await stream.ReadAsync(data, read, data.Length - read);
                if (chunk == 0) break;
                read += chunk;
            }
        }

        return new ChatImageAttachment(data, MediaTypeFromExtension(Path.GetExtension(fileName)))
        {
            FileName = fileName
        };
    }

    private static string MediaTypeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    public async Task<string?> GetConversationHistoryAsync(string folderPath, int sessionId)
    {
        var historyPath = Path.Combine(GetSessionDir(folderPath, sessionId), "history.json");
        if (!File.Exists(historyPath))
            return null;

        return await ReadTextAsync(historyPath);
    }

    public async Task SaveConversationHistoryAsync(string folderPath, int sessionId, string historyJson)
    {
        var sessionDir = GetSessionDir(folderPath, sessionId);
        Directory.CreateDirectory(sessionDir);

        var historyPath = Path.Combine(sessionDir, "history.json");
        await WriteTextAsync(historyPath, historyJson);
    }

    // --- Helpers ---

    private string GetWorkspacePath(string folderPath)
        => Path.Combine(_basePath, GetWorkspaceId(folderPath));

    private string GetSessionIndexPath(string folderPath)
        => Path.Combine(GetWorkspacePath(folderPath), "sessions-index.json");

    private string GetSessionDir(string folderPath, int sessionId)
        => Path.Combine(GetWorkspacePath(folderPath), "sessions", sessionId.ToString());

    private SemaphoreSlim GetLock(string folderPath)
        => _locks.GetOrAdd(GetWorkspaceId(folderPath), _ => new SemaphoreSlim(1, 1));

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        var json = await ReadTextAsync(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteTextAsync(path, json);
    }

    private static async Task<string> ReadTextAsync(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteTextAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteAsync(content);
    }
}
