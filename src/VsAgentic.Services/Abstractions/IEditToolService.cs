namespace VsAgentic.Services.Abstractions;

public record EditResult(int Replacements, IReadOnlyList<int> AffectedLines, string? Error);

public interface IEditToolService
{
    Task<EditResult> EditAsync(string filePath, string oldString, string newString, bool replaceAll = false, CancellationToken cancellationToken = default);
}
