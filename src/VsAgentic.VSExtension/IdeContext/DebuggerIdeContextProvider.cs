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
/// stopped, why, the call stack, and on request the arguments and locals of a
/// frame. Claude reads the state the user is looking at instead of asking for
/// a pasted call stack.
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

    // Names and types are cheap and say little about the data; values may
    // hold anything, so they have the lower limit.
    private const int MaxVariables = 50;
    private const int MaxValueChars = 200;
    private const int MaxExceptionChars = 1000;

    // In the summary only.
    private const int SummaryMaxFrames = 8;
    private const int SummaryMaxNames = 10;

    // A value is withheld when the variable is named like a secret, has a type
    // that holds one, or reads like a connection string or credential.
    private static readonly Regex SecretName = new(
        @"pass(word|wd|phrase)?|pwd|secret|token|api_?key|credential|connection_?string|private_?key|auth",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretType = new(
        @"SecureString|NetworkCredential|X509Certificate",
        RegexOptions.CultureInvariant);
    private static readonly Regex SecretValue = new(
        @"(password|pwd)\s*=|bearer\s+\S|-----BEGIN [A-Z ]*PRIVATE KEY",
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
                "exception, pause), the process and thread, the exception type, message and the debugger's description of the cause when an " +
                "exception stopped it, the call stack of the current thread with file and line (1-based), " +
                "and optionally the arguments and locals of one frame. Use it when the user asks about " +
                "the program they are debugging (\"why is this null?\", \"how did it get here?\") instead " +
                "of asking them to copy the call stack or values. Values are read without running code " +
                "(no property getters, no ToString), so an object may show only its type. The result " +
                "describes only the current break, numbered by breakNumber: after the user continues or " +
                "steps, earlier results no longer hold; call the tool again. Values of variables that look " +
                "like secrets are withheld.",
                """
                {"type":"object","properties":{
                  "includeStack":{"type":"boolean","description":"Return the call stack. Default true."},
                  "maxFrames":{"type":"integer","minimum":1,"maximum":50,"description":"Most stack frames to return. Default 10."},
                  "frame":{"type":"integer","minimum":0,"description":"The frame whose arguments and locals to return, by depth (0 = top of the stack). Default: the frame selected in Visual Studio."},
                  "includeArguments":{"type":"boolean","description":"Return the frame's arguments: names, types, and values up to maxValues. Default false."},
                  "includeLocals":{"type":"boolean","description":"Return the frame's local variables: names, types, and values up to maxValues. Default false."},
                  "maxValues":{"type":"integer","minimum":1,"maximum":30,"description":"Most values to return for arguments and for locals; past it, only names and types. Default 10."}
                }}
                """,
                GetDebugContextAsync),
        };
    }

    public async Task<IdeContextSection?> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var state = _tracker.Read();
        if (state.Mode == DBGMODE.DBGMODE_Design)
            return null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var debugger = GetDebugger();
        if (debugger is null)
            return null;

        var sb = new StringBuilder();
        if (state.Mode == DBGMODE.DBGMODE_Run)
        {
            sb.Append("Debugging ").Append(ProcessName(debugger.CurrentProcess) ?? "a program").Append("; it is running.");
        }
        else
        {
            var reason = BreakReason(debugger.LastBreakReason);
            var top = debugger.CurrentStackFrame as StackFrame2;
            sb.Append("Debugger stopped");
            if (reason is not null) sb.Append(" (").Append(reason).Append(')');
            if (top is not null) sb.Append(" in ").Append(top.FunctionName).Append(Location(top, prefix: " — "));
            sb.Append('.');

            if (state.Exception is { } exception)
                sb.AppendLine().Append("Exception: ").Append(ExceptionLine(exception, ReadExceptionMessage(debugger.CurrentThread)));

            var frames = ReadStack(debugger, SummaryMaxFrames, out _);
            if (frames.Count > 0)
            {
                sb.AppendLine().Append("Call stack:");
                foreach (var frame in frames)
                {
                    sb.AppendLine().Append("- ");
                    if (frame.ExternalCount > 0) sb.Append("[External Code]");
                    else sb.Append(frame.Function).Append(frame.File is null ? "" : $" — {Path.GetFileName(frame.File)}:{frame.Line}");
                }
            }

            if (top is not null)
            {
                try
                {
                    var names = Names(top.get_Arguments2(false), "Arguments").Concat(Names(top.get_Locals2(false), "Locals")).ToList();
                    if (names.Count > 0)
                        sb.AppendLine().Append(string.Join("; ", names));
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // No variables for this frame; the rest of the summary stands.
                }
            }
        }

        return new IdeContextSection(
            "debugger",
            sb.ToString(),
            "Debugger",
            DateTimeOffset.Now,
            detailsAvailable: state.Mode == DBGMODE.DBGMODE_Break,
            version: state.Mode == DBGMODE.DBGMODE_Break ? $"break-{state.BreakNumber}" : "run");
    }

    private async Task<IdeToolResult> GetDebugContextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var includeStack = GetBool(arguments, "includeStack") ?? true;
        var includeArguments = GetBool(arguments, "includeArguments") ?? false;
        var includeLocals = GetBool(arguments, "includeLocals") ?? false;
        var maxFrames = GetInt(arguments, "maxFrames", DefaultMaxFrames, 1, MaxMaxFrames);
        var maxValues = GetInt(arguments, "maxValues", DefaultMaxValues, 1, MaxMaxValues);
        int? frameDepth = arguments.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Number &&
                          f.TryGetInt32(out var d) && d >= 0 ? d : null;

        var state = _tracker.Read();
        var capturedAt = DateTimeOffset.Now.ToString("o");

        if (state.Mode == DBGMODE.DBGMODE_Design)
        {
            return IdeToolResult.Success(JsonSerializer.Serialize(new
            {
                capturedAt,
                mode = "design",
                note = "No program is being debugged in Visual Studio.",
            }));
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var debugger = GetDebugger();
        if (debugger is null)
            return IdeToolResult.Error("The Visual Studio debugger is not available.");

        var processes = ProcessNames(debugger);

        if (state.Mode == DBGMODE.DBGMODE_Run)
        {
            var running = JsonSerializer.Serialize(new
            {
                capturedAt,
                mode = "run",
                processes,
                note = "The program is running, so there is no current frame or stack. It stops at a breakpoint " +
                       "or an exception, or when the user picks Debug > Break All.",
            });
            await TaskScheduler.Default;
            return IdeToolResult.Success(running);
        }

        var thread = debugger.CurrentThread;
        var selected = debugger.CurrentStackFrame as StackFrame2;

        List<FrameInfo>? stack = null;
        var stackTruncated = false;
        if (includeStack)
            stack = ReadStack(debugger, maxFrames, out stackTruncated);

        object? variables = null;
        string? frameNote = null;
        if (includeArguments || includeLocals)
        {
            var target = frameDepth is { } depth ? FindFrame(thread, depth) : selected;
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
            mode = "break",
            breakNumber = state.BreakNumber,
            stoppedAt = state.BreakStartedAt?.ToString("o"),
            reason = BreakReason(debugger.LastBreakReason),
            processes,
            process = ProcessName(debugger.CurrentProcess),
            thread = thread is null ? null : new { id = thread.ID, name = NullIfEmpty(thread.Name) },
            exception = state.Exception is { } exception
                ? new
                {
                    type = exception.Name,
                    message = Truncate(ReadExceptionMessage(thread), MaxExceptionChars),
                    description = Truncate(CleanDescription(exception.Description), MaxExceptionChars),
                }
                : null,
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
            variables,
            note = frameNote,
        });

        await TaskScheduler.Default;
        return IdeToolResult.Success(result);
    }

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

        foreach (StackFrame frame in stackFrames)
        {
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

        // EnvDTE numbers frames from 1.
        foreach (StackFrame frame in stackFrames)
        {
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
        if (value is not null && SecretValue.IsMatch(value))
            return "<withheld: the value looks like a credential>";

        return Truncate(value, MaxValueChars);
    }

    /// <summary>
    /// The message of the exception the program stopped on, read from the
    /// <c>$exception</c> pseudo-variable. Message is a property, which is not
    /// evaluated here, so the value comes from the field behind it. Null when
    /// the language has no <c>$exception</c> or the field cannot be read.
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
            if (member.Name is "_message" or "Message" &&
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

    private static IEnumerable<string> Names(Expressions expressions, string label)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var names = new List<string>();
        var total = 0;
        foreach (Expression expression in expressions)
        {
            total++;
            if (names.Count < SummaryMaxNames)
                names.Add(string.IsNullOrEmpty(expression.Type) ? expression.Name : $"{expression.Name} ({expression.Type})");
        }

        if (total == 0) yield break;
        yield return $"{label}: {string.Join(", ", names)}{(total > names.Count ? $", … ({total} in all)" : "")}";
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

    private static string Location(StackFrame2 frame, string prefix)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return string.IsNullOrEmpty(frame.FileName)
            ? ""
            : $"{prefix}{Path.GetFileName(frame.FileName)}:{frame.LineNumber}";
    }

    private static string ExceptionLine(DebuggerException exception, string? message)
    {
        var parts = new[] { exception.Name ?? "unknown", message, CleanDescription(exception.Description) }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var oneLine = Regex.Replace(string.Join(" — ", parts), @"\s*\r?\n\s*", " ");
        return Truncate(oneLine, 300)!;
    }

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
