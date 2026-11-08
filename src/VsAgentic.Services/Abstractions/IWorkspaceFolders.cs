namespace VsAgentic.Services.Abstractions;

/// <summary>
/// Folders that belong to the user's workspace but lie outside the chat's
/// working directory, such as a project the solution includes from
/// <c>..\Shared</c>. The agent is given access to them as if they were part
/// of the working directory.
/// </summary>
public interface IWorkspaceFolders
{
    /// <summary>
    /// The folders for a chat that works in <paramref name="workingDirectory"/>.
    /// Empty when the open workspace is not the one in that directory, so a
    /// chat kept open across a solution switch does not get the folders of
    /// the new solution. Called from any thread.
    /// </summary>
    IReadOnlyList<string> FoldersOutside(string workingDirectory);
}
