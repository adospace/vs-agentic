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
    Task DeleteSessionsOlderThanAsync(string folderPath, int days);

    // Messages
    Task<IReadOnlyList<PersistedMessage>> GetMessagesAsync(string folderPath, int sessionId);
    Task SaveMessagesAsync(string folderPath, int sessionId, IReadOnlyList<PersistedMessage> messages);
    Task AppendMessageAsync(string folderPath, int sessionId, PersistedMessage message);

    // AI conversation history
    /// <summary>
    /// Writes an image into the session's <c>images</c> folder and returns the
    /// generated file name, which the caller stores on the message.
    /// </summary>
    Task<string> SaveImageAsync(string folderPath, int sessionId, ChatImageAttachment image);

    /// <summary>
    /// Loads a previously stored image. Returns null when the file is missing,
    /// so a deleted or half-copied session still opens.
    /// </summary>
    Task<ChatImageAttachment?> GetImageAsync(string folderPath, int sessionId, string fileName);

    Task<string?> GetConversationHistoryAsync(string folderPath, int sessionId);
    Task SaveConversationHistoryAsync(string folderPath, int sessionId, string historyJson);
}
