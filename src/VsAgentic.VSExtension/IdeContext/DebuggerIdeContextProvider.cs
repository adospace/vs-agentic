using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using EnvDTE90a;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsAgentic.Services.IdeContext;
using StackFrame2 = EnvDTE90a.StackFrame2;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The debugger as IDE context: when the program is stopped, where it
/// stopped, why, and the call stack; and, through a second tool, the
/// arguments and locals of a frame and the exception message. Claude reads
/// the state the user is looking at instead of asking for a pasted call stack.
///
/// The two tools differ in what they send. Where the program stopped says
/// little about its data, so <c>get_debug_context</c> runs without a prompt.
/// Values and the exception message can hold anything, a connection string or
/// a token included, and no filter on names or values catches all of it. So
/// <c>get_debug_values</c> goes through the permission banner on every call,
/// and the user sees what is asked before anything is sent. The filters below
/// are a second layer only.
///
/// Nothing here evaluates code. Locals and arguments are read with automatic
/// property evaluation and implicit function calls turned off, so reading them
/// cannot run code in the program or change its state.
/// </summary>
internal sealed class DebuggerIdeContextProvider : IIdeContextProvider
{
    private const int DefaultMaxFrames = 10;
    private const int MaxMaxFrames = 50;
    private const int DefaultMaxValues = 10;
    private const int MaxMaxValues = 30;

    // Each frame is a synchronous COM call on the UI thread. A stack that is
    // mostly external code (deep recursion, a stack overflow) is merged into
    // a few entries, but every frame is still read; this caps the reading.
    private const int MaxFramesRead = 200;

    // Names and types are cheap and say little about the data; values may
    // hold anything, so they have the lower limit.
    private const int MaxVariables = 50;
    private const int MaxValueChars = 200;
    private const int MaxExceptionChars = 1000;

    // A value is withheld when the variable is named like a secret, has a type
    // that holds one, or reads like a credential. A second layer behind the
    // permission banner: it cannot be complete.
    private static readonly Regex SecretName = new(
        @"pass(word|wd|phrase)?|pwd|secret|token|api_?key|access_?key|credential|connection_?string|private_?key|auth|cookie|session|jwt|signature",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretType = new(
        @"SecureString|NetworkCredential|X509Certificate|Cookie|AuthenticationHeaderValue",
        RegexOptions.CultureInvariant);
    private static readonly Regex SecretValue = new(
        @"(password|pwd|accountkey|sharedaccesskey|sig|token|secret)\s*=" +   // connection strings, SAS URLs, query strings
        @"|\b(bearer|basic)\s+[A-Za-z0-9+/=._~-]{8,}" +                       // Authorization headers
        @"|\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}" +                        // JWT
        @"|\b(AKIA|ASIA)[A-Z0-9]{16}\b" +                                      // AWS access key ids
        @"|-----BEGIN [A-Z ]*PRIVATE KEY",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly DebuggerStateTracker _tracker;

    public IReadOnlyList<IdeTool> Tools { get; }

    public DebuggerIdeContextProvider(DebuggerStateTracker tracker)
    {
        _tracker = tracker;
        Tools = new[]
        {
            new IdeTool(
                "get_debug_context",
                "Reading the debugger state",
                "Returns the state of the Visual Studio debugger: whether a program is being debugged and " +
                "whether it is running or stopped. When stopped (break mode): why (breakpoint, step, " +
                "exception, pause), the process and thread, the exception type when an exception stopped " +
                "it, and the call stack of the current thread with file and line (1-based). It returns no " +
                "values: for the arguments and locals of a frame, or the exception message, call " +
                "get_debug_values, which the user approves each time. Use this tool when the user asks about " +
                "the program they are debugging (\"why is this null?\", \"how did it get here?\") instead " +
                "of asking them to copy the call stack. The result describes only the current break, " +
                "numbered by breakNumber: after the user continues or steps, earlier results no longer " +
                "hold; call the tool again.",
                """
                {"type":"object","properties":{
                  "includeStack":{"type":"boolean","description":"Return the call stack. Default true."},
                  "maxFrames":{"type":"integer","minimum":1,"maximum":50,"description":"Most stack frames to return. Default 10."}
                }}
                """,
                GetDebugContextAsync,
                isReadOnly: true),
            new IdeTool(
                "get_debug_values",
                "Reading values from the debugger",
                "Returns values from the program stopped in the Visual Studio debugger: the arguments and " +
                "local variables of one stack frame, and the message and the debugger's description of the " +
                "exception it stopped on. Values can hold personal data or secrets, so the user is asked to " +
                "allow each call; ask only for what you need, and call get_debug_context first to see where " +
                "the program stopped. Values are read without running code (no property getters, no " +
                "ToString), so an object may show only its type. Values of variables that look like " +
                "secrets are withheld. The result holds only for the break numbered by breakNumber.",
                """
                {"type":"object","properties":{
                  "frame":{"type":"integer","minimum":0,"maximum":199,"description":"The frame whose arguments and locals to return, by depth (0 = top of the stack). Default: the frame selected in Visual Studio."},
                  "includeArguments":{"type":"boolean","description":"Return the frame's arguments: names, types, and values up to maxValues. Default true."},
                  "includeLocals":{"type":"boolean","description":"Return the frame's local variables: names, types, and values up to maxValues. Default true."},
                  "includeException":{"type":"boolean","description":"Return the message and description of the exception the program stopped on. Default true."},
                  "maxValues":{"type":"integer","minimum":1,"maximum":30,"description":"Most values to return for arguments and for locals; past it, only names and types. Default 10."}
                }}
                """,
                GetDebugValuesAsync,
                isReadOnly: false),
        };
    }

    private async Task<IdeToolResult> GetDebugContextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var includeStack = GetBool(arguments, "includeStack") ?? true;
        var maxFrames = GetInt(arguments, "maxFrames", DefaultMaxFrames, 1, MaxMaxFrames);

        var stopped = await SwitchToStoppedDebuggerAsync(cancellationToken);
        if (stopped.Answer is { } answer) return answer;
        // Already there; repeated so the analyzer sees the switch.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var (debugger, state, capturedAt) = (stopped.Debugger!, stopped.State!, stopped.CapturedAt);

        var thread = debugger.CurrentThread;
        var selected = debugger.CurrentStackFrame as StackFrame2;

        List<FrameInfo>? stack = null;
        var stackTruncated = false;
        if (includeStack)
            stack = ReadStack(debugger, maxFrames, out stackTruncated);

        var result = JsonSerializer.Serialize(new
        {
            capturedAt,
            mode = "break",
            breakNumber = state.BreakNumber,
            stoppedAt = state.BreakStartedAt?.ToString("o"),
            reason = BreakReason(debugger.LastBreakReason),
            processes = ProcessNames(debugger),
            process = ProcessName(debugger.CurrentProcess),
            thread = thread is null ? null : new { id = thread.ID, name = NullIfEmpty(thread.Name) },
            exception = state.Exception is { } exception ? new { type = exception.Name } : null,
            currentFrame = selected is null ? null : new
            {
                depth = (int)selected.Depth - 1,
                function = selected.FunctionName,
                file = NullIfEmpty(selected.FileName),
                line = selected.LineNumber > 0 ? (int?)selected.LineNumber : null,
                module = NullIfEmpty(selected.Module),
                language = NullIfEmpty(selected.Language),
                userCode = selected.UserCode,
            },
            stack = stack?.Select(s => s.ExternalCount > 0
                ? (object)new { externalCode = s.ExternalCount }
                : new { depth = s.Depth, function = s.Function, file = s.File, line = s.Line }),
            stackTruncated = includeStack ? stackTruncated : (bool?)null,
        });

        await TaskScheduler.Default;
        return IdeToolResult.Success(result);
    }

    private async Task<IdeToolResult> GetDebugValuesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var includeArguments = GetBool(arguments, "includeArguments") ?? true;
        var includeLocals = GetBool(arguments, "includeLocals") ?? true;
        var includeException = GetBool(arguments, "includeException") ?? true;
        var maxValues = GetInt(arguments, "maxValues", DefaultMaxValues, 1, MaxMaxValues);
        int? frameDepth = arguments.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Number &&
                          f.TryGetInt32(out var d) && d >= 0 && d < MaxFramesRead ? d : null;

        var stopped = await SwitchToStoppedDebuggerAsync(cancellationToken);
        if (stopped.Answer is { } answer) return answer;
        // Already there; repeated so the analyzer sees the switch.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var (debugger, state, capturedAt) = (stopped.Debugger!, stopped.State!, stopped.CapturedAt);

        var thread = debugger.CurrentThread;

        object? variables = null;
        string? frameNote = null;
        if (includeArguments || includeLocals)
        {
            var target = frameDepth is { } depth ? FindFrame(thread, depth) : debugger.CurrentStackFrame as StackFrame2;
            if (target is null)
            {
                frameNote = $"The current thread has no frame at depth {frameDepth}.";
            }
            else
            {
                variables = new
                {
                    depth = (int)target.Depth - 1,
                    function = target.FunctionName,
                    arguments = includeArguments ? ReadVariables(target, locals: false, maxValues) : null,
                    locals = includeLocals ? ReadVariables(target, locals: true, maxValues) : null,
                };
            }
        }

        var result = JsonSerializer.Serialize(new
        {
            capturedAt,
            breakNumber = state.BreakNumber,
            exception = includeException && state.Exception is { } exception
                ? new
                {
                    type = exception.Name,
                    message = FilterText(Truncate(ReadExceptionMessage(thread), MaxExceptionChars)),
                    description = FilterText(Truncate(CleanDescription(exception.Description), MaxExceptionChars)),
                }
                : null,
            variables,
            note = frameNote,
        });

        await TaskScheduler.Default;
        return IdeToolResult.Success(result);
    }

    private sealed class StoppedDebugger
    {
        /// <summary>Set when the program is not stopped: the answer to return as is.</summary>
        public IdeToolResult? Answer { get; set; }
        public Debugger? Debugger { get; set; }
        public DebuggerState? State { get; set; }
        public string CapturedAt { get; set; } = "";
    }

    /// <summary>
    /// Switches to the UI thread and checks that the program is stopped. The
    /// tracker is read first, off the UI thread, but the program can resume
    /// before the switch completes, and the stack cannot be read while it
    /// runs. So the mode is read again from the debugger on the UI thread, and
    /// a program that runs by then gets the "running" answer, not an error.
    /// On return the caller is on the UI thread unless <c>Answer</c> is set.
    /// </summary>
    private async Task<StoppedDebugger> SwitchToStoppedDebuggerAsync(CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.Now.ToString("o");
        if (_tracker.Read().Mode == DBGMODE.DBGMODE_Design)
            return new StoppedDebugger { Answer = DesignAnswer(capturedAt) };

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var debugger = GetDebugger();
        if (debugger is null)
        {
            await TaskScheduler.Default;
            return new StoppedDebugger { Answer = IdeToolResult.Error("The Visual Studio debugger is not available.") };
        }

        var mode = debugger.CurrentMode;
        if (mode == dbgDebugMode.dbgBreakMode)
        {
            return new StoppedDebugger
            {
                Debugger = debugger,
                // Read after the check, so the break number and exception
                // belong to the break being read.
                State = _tracker.Read(),
                CapturedAt = capturedAt,
            };
        }

        IdeToolResult answer;
        if (mode == dbgDebugMode.dbgRunMode)
        {
            answer = IdeToolResult.Success(JsonSerializer.Serialize(new
            {
                capturedAt,
                mode = "run",
                processes = ProcessNames(debugger),
                note = "The program is running, so there is no current frame or stack. It stops at a breakpoint " +
                       "or an exception, or when the user picks Debug > Break All.",
            }));
        }
        else
        {
            answer = DesignAnswer(capturedAt);
        }
        await TaskScheduler.Default;
        return new StoppedDebugger { Answer = answer };
    }

    private static IdeToolResult DesignAnswer(string capturedAt) =>
        IdeToolResult.Success(JsonSerializer.Serialize(new
        {
            capturedAt,
            mode = "design",
            note = "No program is being debugged in Visual Studio.",
        }));

    /// <summary>
    /// The current thread's stack as the Call Stack window shows it with Just
    /// My Code: a run of frames outside user code is one [External Code] entry.
    /// </summary>
    private static List<FrameInfo> ReadStack(Debugger debugger, int maxFrames, out bool truncated)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var frames = new List<FrameInfo>();
        truncated = false;
        if (debugger.CurrentThread?.StackFrames is not { } stackFrames)
            return frames;

        var read = 0;
        foreach (StackFrame frame in stackFrames)
        {
            // Counted before anything else: merged external frames cost a COM
            // call each, the same as the frames that are returned.
            if (++read > MaxFramesRead)
            {
                truncated = true;
                break;
            }

            if (frame is not StackFrame2 frame2) continue;

            if (!frame2.UserCode)
            {
                if (frames.Count > 0 && frames[frames.Count - 1].ExternalCount > 0)
                {
                    frames[frames.Count - 1].ExternalCount++;
                    continue;
                }
            }

            if (frames.Count == maxFrames)
            {
                truncated = true;
                break;
            }

            frames.Add(frame2.UserCode
                ? new FrameInfo
                {
                    Depth = (int)frame2.Depth - 1,
                    Function = frame2.FunctionName,
                    File = NullIfEmpty(frame2.FileName),
                    Line = frame2.LineNumber > 0 ? (int?)frame2.LineNumber : null,
                }
                : new FrameInfo { ExternalCount = 1 });
        }

        return frames;
    }

    private static StackFrame2? FindFrame(EnvDTE.Thread? thread, int depth)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (thread?.StackFrames is not { } stackFrames) return null;

        // EnvDTE numbers frames from 1, top first, so the walk stops at the
        // frame asked for and never reads more than that many.
        var read = 0;
        foreach (StackFrame frame in stackFrames)
        {
            if (++read > depth + 1) break;
            if (frame is StackFrame2 frame2 && frame2.Depth == depth + 1)
                return frame2;
        }
        return null;
    }

    private static object ReadVariables(StackFrame2 frame, bool locals, int maxValues)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Expressions expressions;
        try
        {
            // false: no property getters or implicit function calls.
            expressions = locals ? frame.get_Locals2(false) : frame.get_Arguments2(false);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // A frame without symbols, or a language the debugger cannot list variables for.
            return new { total = 0, returned = 0, valuesReturned = 0, items = Array.Empty<object>(), note = $"Not available: {ex.Message}" };
        }

        var items = new List<object>();
        var total = 0;
        foreach (Expression expression in expressions)
        {
            total++;
            if (items.Count == MaxVariables) continue;

            var withValue = items.Count < maxValues;
            items.Add(new
            {
                name = expression.Name,
                type = NullIfEmpty(expression.Type),
                value = withValue ? SafeValue(expression) : null,
            });
        }

        return new
        {
            total,
            returned = items.Count,
            valuesReturned = Math.Min(items.Count, maxValues),
            items,
        };
    }

    private static string? SafeValue(Expression expression)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (SecretName.IsMatch(expression.Name ?? "") || SecretType.IsMatch(expression.Type ?? ""))
            return "<withheld: the name or type suggests a secret>";

        var value = expression.IsValidValue ? expression.Value : $"<not available: {expression.Value}>";
        return Truncate(FilterText(value), MaxValueChars);
    }

    /// <summary>The text, or a note in its place when it reads like a credential.</summary>
    private static string? FilterText(string? text)
        => text is not null && SecretValue.IsMatch(text) ? "<withheld: the text looks like a credential>" : text;

    /// <summary>
    /// The message of the exception the program stopped on, read from the
    /// <c>$exception</c> pseudo-variable. Only the <c>_message</c> field is
    /// read. <c>Message</c> is a property: reading its value could run its
    /// getter when the user has property evaluation on, and an exception type
    /// can override it with any code. Null when the language has no
    /// <c>$exception</c> or the field cannot be read.
    /// </summary>
    private static string? ReadExceptionMessage(EnvDTE.Thread? thread)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (FindFrame(thread, 0) is not { } top) return null;

            foreach (Expression local in top.get_Locals2(false))
            {
                if (local.Name != "$exception") continue;
                return FindMessage(local.DataMembers, depth: 0);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    private static string? FindMessage(Expressions? members, int depth)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (members is null) return null;

        var groups = new List<Expression>();
        foreach (Expression member in members)
        {
            if (member.Name == "_message" &&
                member.IsValidValue && member.Value is { } value && value.StartsWith("\"", StringComparison.Ordinal))
            {
                return value.Trim('"');
            }

            // The field sits under the Non-Public members node, or under the
            // base class where a language does not flatten inherited members.
            if (member.Name is { } name &&
                (name.IndexOf("Non-Public", StringComparison.OrdinalIgnoreCase) >= 0 || name == "base"))
            {
                groups.Add(member);
            }
        }

        if (depth >= 3) return null;
        foreach (var group in groups)
        {
            if (FindMessage(group.DataMembers, depth + 1) is { } message)
                return message;
        }
        return null;
    }

    private static Debugger? GetDebugger()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (Package.GetGlobalService(typeof(SDTE)) as DTE2)?.Debugger;
    }

    private static List<string> ProcessNames(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var names = new List<string>();
        if (debugger.DebuggedProcesses is not { } processes) return names;
        foreach (EnvDTE.Process process in processes)
        {
            if (ProcessName(process) is { } name)
                names.Add(name);
        }
        return names;
    }

    private static string? ProcessName(EnvDTE.Process? process)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (process is null) return null;
        try
        {
            return $"{Path.GetFileName(process.Name)} ({process.ProcessID})";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The process ended between listing and reading.
            return null;
        }
    }

    private static string? BreakReason(dbgEventReason reason) => reason switch
    {
        dbgEventReason.dbgEventReasonBreakpoint => "breakpoint",
        dbgEventReason.dbgEventReasonStep => "step",
        dbgEventReason.dbgEventReasonUserBreak => "paused by the user",
        dbgEventReason.dbgEventReasonExceptionThrown => "exception thrown",
        dbgEventReason.dbgEventReasonExceptionNotHandled => "exception not handled",
        dbgEventReason.dbgEventReasonStopDebugging => "stopped",
        dbgEventReason.dbgEventReasonAttachProgram or dbgEventReason.dbgEventReasonLaunchProgram => "program started",
        dbgEventReason.dbgEventReasonEndProgram or dbgEventReason.dbgEventReasonDetachProgram => "program ended",
        dbgEventReason.dbgEventReasonNone => null,
        _ => reason.ToString().Replace("dbgEventReason", ""),
    };

    // The engine marks words in bold for the Exception Helper, e.g. "Address.**get** returned null."
    private static string? CleanDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description!.Replace("**", "").Trim();

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text.Substring(0, max) + "…";

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static bool? GetBool(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int GetInt(JsonElement arguments, string name, int fallback, int min, int max)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var n)
            ? Math.Max(min, Math.Min(max, n))
            : fallback;

    private sealed class FrameInfo
    {
        public int Depth { get; set; }
        public string? Function { get; set; }
        public string? File { get; set; }
        public int? Line { get; set; }

        /// <summary>More than zero for a run of frames outside user code.</summary>
        public int ExternalCount { get; set; }
    }
}
