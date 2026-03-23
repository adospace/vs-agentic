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
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
        finally
        {
            semaphore.Release();
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

    // --- AI Conversation History ---

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
