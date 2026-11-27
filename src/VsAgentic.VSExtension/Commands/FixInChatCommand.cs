using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.IdeContext;
using VsAgentic.VSExtension.IdeContext;

namespace VsAgentic.VSExtension.Commands;

/// <summary>
/// "Fix in VsAgentic Chat" in the Error List's context menu: writes "Fix this
/// error" for the selected entries into the input box of the chat the user was
/// last in, with each place attached. The user sends it, so they can add to
/// it first.
/// </summary>
[VisualStudioContribution]
public class FixInChatCommand : Command
{
    // guidSHLMainMenu / IDG_VS_ERRORLIST (vsshlids.h): the group of Show
    // Columns, Grouping and Show Error Help. IDG_VS_ERRORLIST_CLIENT, the
    // first group in the VSCT, is not shown in the Visual Studio 2026 menu.
    private static readonly Guid ErrorListMenuGroupGuid = new("d309f791-903f-11d0-9efc-00a0c911004f");
    private const uint ErrorListGroupId = 0x01CB;

    public FixInChatCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    public override CommandConfiguration CommandConfiguration => new("Fix in VsAgentic Chat")
    {
        // First in its group: it is what the user right-clicked the entry for.
        Placements = [CommandPlacement.VsctParent(ErrorListMenuGroupGuid, ErrorListGroupId, 0x0000)],
        Icon = new(ImageMoniker.KnownValues.CommentSparkle, IconSettings.IconAndText),
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

        await VsAgenticPackage.FixInChatAsync(ReadSelectedEntries());
    }

    private static List<Problem> ReadSelectedEntries()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var problems = new List<Problem>();
        if (Package.GetGlobalService(typeof(SVsErrorList)) is not IErrorList errorList)
            return problems;

        foreach (var handle in errorList.TableControl.SelectedEntries)
        {
            if (ErrorListReader.ReadEntry((string key, out object value) => handle.TryGetValue(key, out value)) is { } entry)
                problems.Add(entry.ToProblem());
        }

        return problems;
    }
}
