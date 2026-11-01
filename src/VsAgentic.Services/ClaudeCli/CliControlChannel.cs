using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The CLI's answer to a control request: the error text, or null on success
/// together with the <c>response</c> body, if it sent one.
/// </summary>
internal readonly struct ControlReply
{
    public ControlReply(string? error, JsonElement? response, string? errorCode = null)
    {
        Error = error;
        Response = response;
        ErrorCode = errorCode;
    }

    public string? Error { get; }
    public JsonElement? Response { get; }

    /// <summary>
    /// Why the request failed, for the failures a host can act on
    /// (<c>bypass_not_launched</c>, <c>invalid_mode</c> …). Null on success,
    /// for a failure the CLI gives no code for, and from a CLI that predates
    /// the field. New values appear over time; treat an unknown one as null.
    /// </summary>
    public string? ErrorCode { get; }

    public bool Succeeded => Error is null;

    public static ControlReply Failed(string error) => new ControlReply(error, null);
}

/// <summary>
/// Pairs the control requests we write to one CLI process with the
/// <c>control_response</c> lines it sends back.
///
/// Each request gets its own id. The dispatcher hands every
/// <c>control_response</c> to <see cref="TryComplete"/>, which wakes the
/// matching waiter. A request that gets no answer in time fails with
/// <see cref="NoResponse"/>, and so does every open request when the process
/// ends, so no caller waits for an answer that cannot come.
///
/// One instance per process. Ending one process must not fail the requests
/// already sent to the next, and the old dispatcher can finish after the new
/// process has started.
/// </summary>
internal sealed class CliControlChannel
{
    /// <summary>The error text of a request that got no answer.</summary>
    public const string NoResponse = "no response";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    // Shared by all instances, so a late answer from an old process can
    // never match a request sent to the new one.
    private static int s_seq;

    private readonly Func<string, ValueTask> _writeLine;
    private readonly Dictionary<string, TaskCompletionSource<ControlReply>> _pending =
        new Dictionary<string, TaskCompletionSource<ControlReply>>(StringComparer.Ordinal);

    public CliControlChannel(Func<string, ValueTask> writeLine)
    {
        _writeLine = writeLine;
    }

    /// <summary>
    /// Writes one control request and waits for its answer. No answer within
    /// <paramref name="timeout"/> counts as a failure.
    ///
    /// Never await this from the dispatcher loop: the dispatcher is what
    /// delivers the answer.
    /// </summary>
    public async Task<ControlReply> SendAsync(Func<string, string> buildLine, TimeSpan? timeout = null)
    {
        var requestId = "vsagentic-" + Interlocked.Increment(ref s_seq);
        var pending = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) { _pending[requestId] = pending; }

        try
        {
            await _writeLine(buildLine(requestId)).ConfigureAwait(false);

            var done = await Task.WhenAny(pending.Task, Task.Delay(timeout ?? DefaultTimeout)).ConfigureAwait(false);
            return done == pending.Task ? pending.Task.Result : ControlReply.Failed(NoResponse);
        }
        catch (Exception ex)
        {
            return ControlReply.Failed(ex.Message);
        }
        finally
        {
            lock (_pending) { _pending.Remove(requestId); }
        }
    }

    /// <summary>
    /// Completes the request a <c>control_response</c> event answers. Returns
    /// false when no request of ours is waiting for it.
    /// </summary>
    public bool TryComplete(JsonElement evt)
    {
        if (!evt.TryGetProperty("response", out var response)) return false;
        var requestId = response.TryGetProperty("request_id", out var id) ? id.GetString() : null;
        if (requestId is null) return false;

        TaskCompletionSource<ControlReply>? pending;
        lock (_pending)
        {
            if (!_pending.TryGetValue(requestId, out pending)) return false;
            _pending.Remove(requestId);
        }

        var ok = response.TryGetProperty("subtype", out var st) && st.GetString() == "success";
        if (ok)
        {
            // Cloned: the event's document is released once the dispatcher
            // moves on, and the waiter reads the body after that.
            JsonElement? body = response.TryGetProperty("response", out var b) ? b.Clone() : null;
            pending.TrySetResult(new ControlReply(null, body));
            return true;
        }

        var error = response.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "error"
            : "error";
        var errorCode = response.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        pending.TrySetResult(new ControlReply(error, null, errorCode));
        return true;
    }

    /// <summary>
    /// Fails every open request. Called when the process ends: its answers
    /// will not arrive, and a new process does not know the old ids.
    /// </summary>
    public void FailAll(string reason)
    {
        List<TaskCompletionSource<ControlReply>> open;
        lock (_pending)
        {
            open = new List<TaskCompletionSource<ControlReply>>(_pending.Values);
            _pending.Clear();
        }
        foreach (var pending in open)
            pending.TrySetResult(ControlReply.Failed(reason));
    }
}
