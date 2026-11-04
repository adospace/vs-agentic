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
///
/// While a SessionStart or Setup hook runs, the CLI answers no control
/// request: it reads the next line only when the hook has ended (CLI 2.1.310,
/// hook of 20 s, <c>initialize</c> answered after 21 s). The dispatcher
/// reports these hooks with <see cref="HookStarted"/> and
/// <see cref="HookEnded"/>, and the timeout of a request does not run while
/// one is open. It starts again, in full, when the last one ends. The CLI's
/// own SDK does the same since 0.3.304.
/// </summary>
internal sealed class CliControlChannel
{
    /// <summary>The error text of a request that got no answer.</summary>
    public const string NoResponse = "no response";

    /// <summary>
    /// The error text of a request that got no answer because start-up hooks
    /// ran for longer than <see cref="MaxHookWait"/>.
    /// </summary>
    public const string HookWaitExceeded = "start-up hooks did not finish";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How much hook time a request waits out before its own timeout runs
    /// regardless. The CLI's SDK allows the same 10 minutes. A hook that runs
    /// longer is broken, and so is the process that waits for it.
    /// </summary>
    public static readonly TimeSpan MaxHookWait = TimeSpan.FromMinutes(10);

    // Shared by all instances, so a late answer from an old process can
    // never match a request sent to the new one.
    private static int s_seq;

    private readonly Func<string, ValueTask> _writeLine;
    private readonly TimeSpan _maxHookWait;
    private readonly Dictionary<string, TaskCompletionSource<ControlReply>> _pending =
        new Dictionary<string, TaskCompletionSource<ControlReply>>(StringComparer.Ordinal);

    // The start-up hooks that run, by hook id, and a task that completes on
    // the next change to the set. Guarded by _hooks.
    private readonly HashSet<string> _hooks = new HashSet<string>(StringComparer.Ordinal);
    private TaskCompletionSource<bool> _hooksChanged = NewSignal();

    public CliControlChannel(Func<string, ValueTask> writeLine)
        : this(writeLine, MaxHookWait)
    {
    }

    internal CliControlChannel(Func<string, ValueTask> writeLine, TimeSpan maxHookWait)
    {
        _writeLine = writeLine;
        _maxHookWait = maxHookWait;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether a start-up hook runs, so the CLI answers nothing until it ends.</summary>
    public bool HooksRunning
    {
        get { lock (_hooks) return _hooks.Count > 0; }
    }

    /// <summary>
    /// Whether the process has sent its first <c>system/init</c>. Hooks after
    /// it are not start-up hooks: a SessionStart hook also runs after a
    /// compaction, in the middle of a turn.
    /// </summary>
    public bool StartupEnded { get; private set; }

    public void EndStartup() => StartupEnded = true;

    /// <summary>A SessionStart or Setup hook has started.</summary>
    public void HookStarted(string hookId) => ChangeHooks(() => _hooks.Add(hookId));

    /// <summary>A SessionStart or Setup hook has ended, however it went.</summary>
    public void HookEnded(string hookId) => ChangeHooks(() => _hooks.Remove(hookId));

    private void ChangeHooks(Func<bool> change)
    {
        TaskCompletionSource<bool> changed;
        lock (_hooks)
        {
            if (!change()) return;
            changed = _hooksChanged;
            _hooksChanged = NewSignal();
        }
        changed.TrySetResult(true);
    }

    /// <summary>
    /// Writes one control request and waits for its answer. No answer within
    /// <paramref name="timeout"/> counts as a failure. Time in which a
    /// start-up hook runs does not count, up to <see cref="MaxHookWait"/>.
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
            return await WaitForReplyAsync(pending.Task, timeout ?? DefaultTimeout).ConfigureAwait(false);
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
    /// Waits for the answer. While no hook runs, the timeout runs; a hook that
    /// starts stops it, and the end of the last hook starts it again in full.
    /// Hook time beyond the allowance counts as time without a hook.
    /// </summary>
    private async Task<ControlReply> WaitForReplyAsync(Task<ControlReply> reply, TimeSpan timeout)
    {
        var hookTime = TimeSpan.Zero;
        var hookWaitExceeded = false;
        while (true)
        {
            bool hooksRunning;
            Task changed;
            lock (_hooks)
            {
                hooksRunning = _hooks.Count > 0;
                changed = _hooksChanged.Task;
            }

            if (hooksRunning && hookTime < _maxHookWait)
            {
                var started = DateTime.UtcNow;
                var done = await Task.WhenAny(reply, changed, Task.Delay(_maxHookWait - hookTime)).ConfigureAwait(false);
                if (done == reply) return reply.Result;
                hookTime += DateTime.UtcNow - started;
                continue;
            }

            // From here a hook that ends or starts again changes nothing: the
            // allowance is spent, and the timeout runs to the end.
            if (hooksRunning) hookWaitExceeded = true;
            var wait = hookWaitExceeded
                ? Task.Delay(timeout)
                : Task.WhenAny(changed, Task.Delay(timeout));
            var first = await Task.WhenAny(reply, wait).ConfigureAwait(false);
            if (first == reply) return reply.Result;
            if (hookWaitExceeded || !changed.IsCompleted)
                return ControlReply.Failed(hookWaitExceeded ? HookWaitExceeded : NoResponse);
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
