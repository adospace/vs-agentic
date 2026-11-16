namespace VsAgentic.UI.Controls;

/// <summary>
/// What the user asked to do with a file path link in the chat: a plain click
/// opens it, the right-click menu offers the rest.
/// </summary>
public enum FileLinkAction
{
    Open,
    CopyPath,
    ShowInExplorer,
}
