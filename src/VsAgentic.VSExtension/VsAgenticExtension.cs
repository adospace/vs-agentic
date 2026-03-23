using Microsoft.VisualStudio.Extensibility;

namespace VsAgentic.VSExtension;

[VisualStudioContribution]
internal class VsAgenticExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        RequiresInProcessHosting = true,
    };
}
