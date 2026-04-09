using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.ClaudeCli.Permissions;

public sealed class PermissionBroker : IPermissionBroker
{
    private readonly ILogger<PermissionBroker> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new();

    public PermissionBroker(ILogger<PermissionBroker> logger)
    {
        _logger = logger;
    }

    public event Action<PermissionRequest>? PermissionRequested;

    public Task<PermissionDecision> SubmitAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, tcs))
        {
            _logger.LogWarning("[PermissionBroker] Duplicate request id {Id}", request.Id);
            return Task.FromResult(PermissionDecision.Deny("Duplicate permission request id"));
        }

        // Cancellation: if the CLI/process is torn down before the user replies,
        // synthesize a deny so the MCP child doesn't hang forever.
        var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var pending))
                pending.TrySetResult(PermissionDecision.Deny("Cancelled"));
        });
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        try
        {
            PermissionRequested?.Invoke(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PermissionBroker] PermissionRequested handler threw");
        }

        return tcs.Task;
    }

    public void Resolve(string requestId, PermissionDecision decision)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(decision);
        }
        else
        {
            _logger.LogWarning("[PermissionBroker] Resolve for unknown request id {Id}", requestId);
        }
    }
}
