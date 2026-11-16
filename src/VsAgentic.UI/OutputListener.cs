using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI;

public class OutputListener : IOutputListener
{
    public event Action<OutputItem>? StepStarted;
    public event Action<OutputItem>? StepUpdated;
    public event Action<OutputItem>? StepCompleted;

    // Handlers marshal to the UI thread and read the item there, later. The
    // chat service keeps changing the same item in the meantime: a short
    // answer is finalized (Delta cleared, Title "Response complete") before
    // the UI reads its text, which then shows and saves as empty. Hand out a
    // copy of the item as it is at the time of the call.
    public void OnStepStarted(OutputItem item) => StepStarted?.Invoke(item with { });
    public void OnStepUpdated(OutputItem item) => StepUpdated?.Invoke(item with { });
    public void OnStepCompleted(OutputItem item) => StepCompleted?.Invoke(item with { });
}
