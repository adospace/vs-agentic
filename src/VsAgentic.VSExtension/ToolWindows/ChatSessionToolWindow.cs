using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VsAgentic.VSExtension.ToolWindows;

[Guid("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e")]
public class ChatSessionToolWindow : ToolWindowPane
{
    public ChatSessionControl ChatControl { get; }

    public ChatSessionToolWindow() : base(null)
    {
        Caption = "VsAgentic Chat";
        ChatControl = new ChatSessionControl();
        Content = ChatControl;
    }
}
