using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VsAgentic.VSExtension.ToolWindows;

[Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")]
public class SessionListToolWindow : ToolWindowPane
{
    public SessionListControl SessionListControl { get; }

    public SessionListToolWindow() : base(null)
    {
        Caption = "VsAgentic Sessions";
        SessionListControl = new SessionListControl();
        Content = SessionListControl;
    }
}
