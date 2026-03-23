using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI;

public class OutputListener : IOutputListener
{
    public event Action<OutputItem>? StepStarted;
    public event Action<OutputItem>? StepUpdated;
    public event Action<OutputItem>? StepCompleted;

    public void OnStepStarted(OutputItem item) => StepStarted?.Invoke(item);
    public void OnStepUpdated(OutputItem item) => StepUpdated?.Invoke(item);
    public void OnStepCompleted(OutputItem item) => StepCompleted?.Invoke(item);
}
