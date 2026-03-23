using VsAgentic.Services.Models;

namespace VsAgentic.Services.Abstractions;

public interface ISessionStore
{
    // Workspace
    Task<bool> WorkspaceExistsAsync(string folderPath);
    Task EnsureWorkspaceAsync(string folderPath);
    string GetWorkspaceId(string folderPath);

    // Session index
    Task<IReadOnlyList<SessionEntry>> GetSessionIndexAsync(string folderPath);
    Task<SessionEntry> CreateSessionAsync(string folderPath, string title);
    Task UpdateSessionAsync(string folderPath, SessionEntry entry);
    Task DeleteSessionAsync(string folderPath, int sessionId);

    // Messages
    Task<IReadOnlyList<PersistedMessage>> GetMessagesAsync(string folderPath, int sessionId);
    Task SaveMessagesAsync(string folderPath, int sessionId, IReadOnlyList<PersistedMessage> messages);
    Task AppendMessageAsync(string folderPath, int sessionId, PersistedMessage message);

    // AI conversation history
    Task<string?> GetConversationHistoryAsync(string folderPath, int sessionId);
    Task SaveConversationHistoryAsync(string folderPath, int sessionId, string historyJson);
}
