namespace VsAgentic.Services.Abstractions;

public interface IFileSessionTracker
{
    void MarkAsRead(string filePath);
    bool HasBeenRead(string filePath);
    void PushCheckpoint(string filePath, string contents);
    string? PopCheckpoint(string filePath);
}
