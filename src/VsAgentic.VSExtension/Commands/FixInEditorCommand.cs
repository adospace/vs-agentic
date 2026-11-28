using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsAgentic.VSExtension.Commands;

/// <summary>
/// "Fix in VsAgentic Chat" in the editor's context menu, beside "Add to
/// VsAgentic Chat": the same as the light bulb, for the errors and warnings
/// on the caret's line or the selected lines. Enabled only while there are
/// any there (<see cref="Editor.FixInEditorAvailability"/>); a menu of the new
/// extensibility model cannot hide an item when it opens.
/// </summary>
[VisualStudioContribution]
public class FixInEditorCommand : Command
{
    // guidSHLMainMenu / IDG_VS_CODEWIN_TEXTEDIT (vsshlids.h): the group of
    // Cut, Copy, Paste and Add to VsAgentic Chat.
    private static readonly Guid CodeWindowMenuGroupGuid = new("d309f791-903f-11d0-9efc-00a0c911004f");
    private const uint CodeWindowTextEditGroupId = 0x01C0;

    private static FixInEditorCommand? _instance;
    private static bool _available;

    public FixInEditorCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    public override CommandConfiguration CommandConfiguration => new("Fix in VsAgentic Chat")
    {
        // After Add to VsAgentic Chat, which is 0xFFFF.
        Placements = [CommandPlacement.VsctParent(CodeWindowMenuGroupGuid, CodeWindowTextEditGroupId, 0xFFFF)],
        Icon = new(ImageMoniker.KnownValues.CommentSparkle, IconSettings.IconAndText),
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        _instance = this;
        SetEnabledState(_available);
        return base.InitializeAsync(cancellationToken);
    }

    /// <summary>Called whenever the caret's line gains or loses its last error or warning.</summary>
    internal static void SetAvailable(bool available)
    {
        _available = available;
        if (_instance is { } command && command.IsEnabled != available)
            command.SetEnabledState(available);
    }

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

        await VsAgenticPackage.FixAtEditorCaretAsync();
    }
}
