using System;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Mediates between the in-process MCP permission server (which receives
/// permission asks from the Claude CLI) and the chat UI (which surfaces them
/// to the user). Singleton.
///
/// Lifecycle:
///  1. The pipe server reads a request from the MCP child and calls
///     <see cref="SubmitAsync"/>, which raises <see cref="PermissionRequested"/>.
///  2. The UI handler shows a banner; the user clicks Allow/Deny.
///  3. The UI calls <see cref="Resolve"/> with the decision.
///  4. The original task completes; the pipe server returns the result to MCP.
/// </summary>
public interface IPermissionBroker
{
    /// <summary>Raised on a background thread when a new permission request arrives.</summary>
    event Action<PermissionRequest>? PermissionRequested;

    /// <summary>
    /// Called by the pipe server when a request comes in. Returns a task that
    /// completes when the UI resolves the request.
    /// </summary>
    Task<PermissionDecision> SubmitAsync(PermissionRequest request, CancellationToken cancellationToken);

    /// <summary>Called by the UI to deliver the user's decision.</summary>
    void Resolve(string requestId, PermissionDecision decision);
}
