using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.ClaudeCli.Permissions;

public sealed class PermissionBroker : IPermissionBroker
{
    private readonly ILogger<PermissionBroker> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new();

    /// <summary>
    /// <see cref="PermissionRequest.SessionAllowKey"/> values the user chose to
    /// stop being asked about. Used as a set; the value is ignored. Paths are
    /// compared case-insensitively to match Windows filesystem semantics.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _rememberedAllows =
        new(StringComparer.OrdinalIgnoreCase);

    public PermissionBroker(ILogger<PermissionBroker> logger)
    {
        _logger = logger;
    }

    public event Action<PermissionRequest>? PermissionRequested;

    public Task<PermissionDecision> SubmitAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        // Short-circuit before registering anything in _pending: the user
        // already approved this file for the session, so no banner is raised
        // and there is no pending entry for the UI to resolve.
        if (request.SessionAllowKey is { } key && _rememberedAllows.ContainsKey(key))
        {
            _logger.LogInformation(
                "[PermissionBroker] Auto-allowing {Tool} (remembered for this session)",
                request.ToolName);
            return Task.FromResult(PermissionDecision.Allow(RawInputJson(request)));
        }

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

    public void RememberAllow(PermissionRequest request)
    {
        if (request.SessionAllowKey is not { } key) return;
        if (_rememberedAllows.TryAdd(key, 0))
        {
            _logger.LogInformation(
                "[PermissionBroker] Remembering allow for {Tool} for the rest of the session",
                request.ToolName);
        }
    }

    public void ClearRememberedAllows()
    {
        if (_rememberedAllows.IsEmpty) return;
        var count = _rememberedAllows.Count;
        _rememberedAllows.Clear();
        _logger.LogInformation("[PermissionBroker] Cleared {Count} remembered allow(s)", count);
    }

    private static string RawInputJson(PermissionRequest request) =>
        request.Input.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : request.Input.GetRawText();

    public void CancelAllPending()
    {
        var deny = PermissionDecision.Deny("Cancelled by user");
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                _logger.LogInformation("[PermissionBroker] CancelAllPending denying {Id}", key);
                tcs.TrySetResult(deny);
            }
        }
    }
}
