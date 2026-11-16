using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace VsAgentic.VSExtension.ToolWindows;

[Guid("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e")]
public class ChatSessionToolWindow : ToolWindowPane
{
    public ChatSessionControl ChatControl { get; }

    /// <summary>
    /// Raised when the tool window frame is closed by the user (e.g. clicking X).
    /// </summary>
    public event Action? Closed;

    public ChatSessionToolWindow() : base(null)
    {
        Caption = "VsAgentic Chat";
        ChatControl = new ChatSessionControl();
        Content = ChatControl;
    }

    protected override void Initialize()
    {
        base.Initialize();

        // Ctrl+F runs Edit.Find and F3 / Shift+F3 run Edit.FindNext and
        // Edit.FindPrevious. Visual Studio translates these keys into the
        // commands before WPF sees them, and unhandled, Edit.Find opens the
        // Find in Files dialog, which has nothing to search in a chat. The
        // active window gets a command first, so the pane answers them with
        // the chat's own find bar. While the chat page has the focus, the page
        // sees the keys itself and the commands do not run.
        if (GetService(typeof(IMenuCommandService)) is IMenuCommandService commands)
        {
            AddFindCommand(commands, VSConstants.VSStd97CmdID.Find, () => ChatControl.OpenFind());
            AddFindCommand(commands, VSConstants.VSStd97CmdID.FindNext, () => ChatControl.FindNext(1));
            AddFindCommand(commands, VSConstants.VSStd97CmdID.FindPrev, () => ChatControl.FindNext(-1));
        }
    }

    private static void AddFindCommand(IMenuCommandService commands, VSConstants.VSStd97CmdID id, Action run)
    {
        commands.AddCommand(new MenuCommand(
            (_, _) => run(),
            new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)id)));
    }

    protected override void OnClose()
    {
        ChatControl.Shutdown();
        Closed?.Invoke();
        base.OnClose();
    }
}
