using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Services;

public class FileSessionTracker : IFileSessionTracker
{
    private readonly HashSet<string> _readFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Stack<string>> _checkpoints = new(StringComparer.OrdinalIgnoreCase);

    public void MarkAsRead(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        _readFiles.Add(normalized);
    }

    public bool HasBeenRead(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        return _readFiles.Contains(normalized);
    }

    public void PushCheckpoint(string filePath, string contents)
    {
        var normalized = Path.GetFullPath(filePath);
        if (!_checkpoints.TryGetValue(normalized, out var stack))
        {
            stack = new Stack<string>();
            _checkpoints[normalized] = stack;
        }
        stack.Push(contents);
    }

    public string? PopCheckpoint(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        if (_checkpoints.TryGetValue(normalized, out var stack) && stack.Count > 0)
            return stack.Pop();
        return null;
    }
}
