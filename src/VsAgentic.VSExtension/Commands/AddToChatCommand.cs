using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsAgentic.VSExtension.Commands;

/// <summary>
/// "Add to VsAgentic Chat" in the editor's context menu: attaches the selected
/// lines, or the whole file when nothing is selected, to the chat the user was
/// last in. For someone reading code who decides to ask about it, without
/// going to the chat first.
/// </summary>
[VisualStudioContribution]
public class AddToChatCommand : Command
{
    // guidSHLMainMenu / IDG_VS_CODEWIN_TEXTEDIT (vsshlids.h): the group of
    // Cut, Copy and Paste in the code window's context menu.
    private static readonly Guid CodeWindowMenuGroupGuid = new("d309f791-903f-11d0-9efc-00a0c911004f");
    private const uint CodeWindowTextEditGroupId = 0x01C0;

    public AddToChatCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    public override CommandConfiguration CommandConfiguration => new("Add to VsAgentic Chat")
    {
        // Last in the group: after Paste, not between Cut and Copy.
        Placements = [CommandPlacement.VsctParent(CodeWindowMenuGroupGuid, CodeWindowTextEditGroupId, 0xFFFF)],
        Icon = new(ImageMoniker.KnownValues.AddAttachment, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // The package loads in the background and may not be there yet.
        if (!VsAgenticPackage.IsLoaded)
        {
            var shell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));
            if (shell != null)
            {
                var guid = new System.Guid("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f");
                shell.LoadPackage(ref guid, out _);
            }
        }

        await VsAgenticPackage.AddEditorContextToChatAsync();
    }
}
