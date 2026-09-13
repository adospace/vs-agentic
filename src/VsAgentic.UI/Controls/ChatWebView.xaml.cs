using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using VsAgentic.Services.Abstractions;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.UI.Controls;

public partial class ChatWebView : UserControl
{
    /// <summary>
    /// Raised when the user clicks a file path link in rendered content.
    /// The string argument is the raw path (possibly with :line suffix).
    /// </summary>
    public static event Action<string>? FileOpenRequested;

    /// <summary>
    /// Set by the host once its DI container is up. The control is created by
    /// XAML, so there is nothing to inject into — static, like
    /// <see cref="FileOpenRequested"/>. Defaults to a no-op logger.
    /// </summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// How long a user data folder survives once its owner can no longer be
    /// confirmed. See <see cref="IsTooOld"/>.
    /// </summary>
    private const int StaleFolderMaxAgeDays = 7;

    /// <summary>
    /// Raised when the page asks for a zoom change: +1 in, -1 out, 0 back to
    /// 100%. Ctrl+wheel and Ctrl+/- land inside the browser whenever the
    /// pointer or focus is over the chat and never reach WPF, so the page
    /// forwards the intent here instead of zooming itself — the host owns the
    /// single level that the chat and the chrome around it share.
    /// </summary>
    public event Action<int>? ZoomChangeRequested;

    private bool _isWebViewReady;
    private double _zoomFactor = ZoomLevels.Default;
    private readonly ConcurrentQueue<Func<Task>> _pendingOps = new();

    /// <summary>
    /// Scale applied to the rendered chat. Safe to set before the WebView has
    /// finished initializing; the value is re-applied once it has.
    /// </summary>
    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            _zoomFactor = value;
            ApplyZoomFactor();
        }
    }

    public ChatWebView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isWebViewReady)
        {
            await InitializeWebViewAsync();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var env = await CreateEnvironmentAsync();
            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            WebView.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            var html = LoadHtmlTemplate();
            WebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            // Nothing renders when this fails, and the symptom (an empty chat
            // pane next to a perfectly healthy CLI session) gives no hint as to
            // why — so this has to reach the log file, not just the debugger.
            Logger.LogError(ex, "[ChatWebView] WebView2 initialization failed; the chat pane will stay empty.");
        }
    }

    /// <summary>
    /// Creates the WebView2 environment on a user data folder scoped to this
    /// process, so no two hosts ever contend for the same one.
    /// </summary>
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var baseFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VsAgentic", "WebView2");

        // A user data folder belongs to one process at a time. Pointing a
        // second host at a folder another process already owns does not fail
        // where you would expect it to: CoreWebView2Environment.CreateAsync
        // still succeeds, and only CreateCoreWebView2Controller then fails with
        // HRESULT_FROM_WIN32(ERROR_INVALID_STATE). The chat pane is left empty
        // next to a CLI session that is working perfectly, which is a hard
        // symptom to place.
        //
        // Scoping the folder to the process removes the contention entirely,
        // and costs nothing: the chat is handed to the control through
        // NavigateToString with its HTML, CSS and JS inlined, so a shared
        // browser cache has nothing to carry between hosts.
        //
        // The folder carries the process start time as well as the id, because
        // Windows reuses ids. Without the start time a folder whose id has been
        // handed to an unrelated process looks owned for as long as that process
        // lives, and is never reclaimed; and a host can inherit the profile of a
        // predecessor that crashed with the same id, stale lock file included.
        // The time is a UTC file time: StartTime is Kind=Local, so ticks taken
        // either side of a DST shift would not compare equal.
        using var process = Process.GetCurrentProcess();
        var processFolder = $"{baseFolder}.p{process.Id}.t{process.StartTime.ToFileTimeUtc():x16}";
        PurgeStaleProcessFolders(baseFolder);
        return await CoreWebView2Environment.CreateAsync(null, processFolder);
    }

    /// <summary>
    /// Deletes folders left behind by hosts that are no longer running. Each one
    /// is a full browser profile, so they are not cheap to keep around.
    /// Best effort — a folder still held by a live process is simply skipped.
    /// </summary>
    private static void PurgeStaleProcessFolders(string baseFolder)
    {
        try
        {
            var parent = Path.GetDirectoryName(baseFolder);
            if (parent is null || !Directory.Exists(parent)) return;

            var prefix = Path.GetFileName(baseFolder) + ".p";

            foreach (var candidate in Directory.GetDirectories(parent, prefix + "*"))
            {
                var suffix = Path.GetFileName(candidate).Substring(prefix.Length);
                if (!TryParseOwner(suffix, out var processId, out var startTicks)) continue;
                if (IsOwnerAlive(processId, startTicks)) continue;

                try
                {
                    Directory.Delete(candidate, recursive: true);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "[ChatWebView] Could not delete stale WebView2 folder '{Folder}'.", candidate);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[ChatWebView] Purge of stale WebView2 folders failed.");
        }
    }

    /// <summary>
    /// Reads the owning process out of a folder name suffix, which is either
    /// "{id}.t{startTicks:x16}" or the bare "{id}" written by earlier builds of
    /// this branch. A bare id leaves <paramref name="startTicks"/> at 0, meaning
    /// the start time is unknown.
    /// </summary>
    private static bool TryParseOwner(string suffix, out int processId, out long startTicks)
    {
        processId = 0;
        startTicks = 0;

        var separator = suffix.IndexOf(".t", StringComparison.Ordinal);
        if (separator < 0) return int.TryParse(suffix, out processId);

        return int.TryParse(suffix.Substring(0, separator), out processId)
            && long.TryParse(
                suffix.Substring(separator + 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out startTicks);
    }

    /// <summary>
    /// Whether the process that created a folder is still running, and so still
    /// owns it. The id alone cannot answer that, since Windows reuses ids — the
    /// start time is what settles it.
    /// </summary>
    private static bool IsOwnerAlive(int processId, long startTicks)
    {
        try
        {
            using var owner = Process.GetProcessById(processId);

            // Written before the start time went into the name: the id is all
            // there is to go on, so treat the folder as owned.
            if (startTicks == 0) return true;

            return owner.StartTime.ToFileTimeUtc() == startTicks;
        }
        catch (ArgumentException)
        {
            // No such process — the folder is orphaned.
            return false;
        }
        catch (Exception ex)
        {
            // StartTime throws Win32Exception when another user owns the process,
            // or when it runs elevated and we do not, and InvalidOperationException
            // when it exits mid-check. Ownership is unknown, so keep the folder
            // until it is old enough that no live owner is credible.
            Logger.LogDebug(
                ex, "[ChatWebView] Could not confirm the owner of the WebView2 folder for process {ProcessId}.", processId);
            return !IsTooOld(startTicks);
        }
    }

    /// <summary>
    /// Whether a folder is older than <see cref="StaleFolderMaxAgeDays"/>. Only
    /// consulted when the owner cannot be confirmed: without this, a folder we
    /// are not allowed to ask about stays on disk for good. Names are now unique
    /// per run rather than drawn from the small set of process ids, so nothing
    /// else puts a ceiling on what accumulates.
    /// </summary>
    private static bool IsTooOld(long startTicks)
    {
        try
        {
            return DateTime.UtcNow - DateTime.FromFileTimeUtc(startTicks)
                > TimeSpan.FromDays(StaleFolderMaxAgeDays);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Not a file time this code wrote. Leave the folder alone.
            return false;
        }
    }

    private static string LoadHtmlTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var showdownJs = ReadEmbeddedResource(assembly, "VsAgentic.UI.Assets.showdown.min.js");
        var templateHtml = ReadEmbeddedResource(assembly, "VsAgentic.UI.Assets.chat-template.html");
        return templateHtml.Replace("{{SHOWDOWN_JS}}", showdownJs);
    }

    private static string ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Pushes the current level at the control. Before CoreWebView2 exists this
    /// is a no-op rather than an error, which is why the level is kept in a
    /// field and re-applied when navigation completes.
    /// </summary>
    private void ApplyZoomFactor()
    {
        try
        {
            WebView.ZoomFactor = _zoomFactor;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ChatWebView zoom failed: {ex.Message}");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isWebViewReady = true;
        ApplyZoomFactor();

        // Replay queued operations
        while (_pendingOps.TryDequeue(out var op))
        {
            try { await op(); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[ChatWebView] Queued script operation failed.");
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        if (json is null) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "openFile")
            {
                var path = root.GetProperty("path").GetString();
                if (!string.IsNullOrEmpty(path))
                    FileOpenRequested?.Invoke(path!);
            }
            else if (type == "zoom")
            {
                ZoomChangeRequested?.Invoke(root.GetProperty("step").GetInt32());
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    // --- Public API ---

    public Task AddMessageAsync(string id, ChatItemType type, ChatMessageData data)
    {
        var dataJson = JsonSerializer.Serialize(data);
        return ExecuteOrQueueAsync(
            $"addMessage({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(type.ToString())}, {dataJson})");
    }

    public Task UpdateContentAsync(string id, string content)
    {
        return ExecuteOrQueueAsync(
            $"updateContent({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(content)})");
    }

    public Task UpdateStatusAsync(string id, OutputItemStatus status, string expanderTitle)
    {
        return ExecuteOrQueueAsync(
            $"updateStatus({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(status.ToString())}, {JsonSerializer.Serialize(expanderTitle)})");
    }

    public Task SetBodyAsync(string id, string body, OutputBodyMode bodyMode)
    {
        return ExecuteOrQueueAsync(
            $"setBody({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(body)}, {JsonSerializer.Serialize(bodyMode.ToString())})");
    }

    public Task CompleteMessageAsync(string id)
    {
        return ExecuteOrQueueAsync(
            $"completeMessage({JsonSerializer.Serialize(id)})");
    }

    public Task ClearAllAsync()
    {
        return ExecuteOrQueueAsync("clearAll()");
    }

    /// <summary>
    /// Closes the folded run of steps the chat is currently collecting into.
    /// Called when the turn ends, so a run the model finished on stops
    /// presenting itself as still in flight.
    /// </summary>
    public Task EndActivityAsync()
    {
        return ExecuteOrQueueAsync("endActivity()");
    }

    public Task LoadMessagesAsync(IEnumerable<ChatMessageData> messages)
    {
        var json = JsonSerializer.Serialize(messages);
        return ExecuteOrQueueAsync($"loadMessages({json})");
    }

    public Task SetThemeColorsAsync(Dictionary<string, string> colors)
    {
        var json = JsonSerializer.Serialize(colors);
        return ExecuteOrQueueAsync($"setThemeColors({json})");
    }

    private Task ExecuteOrQueueAsync(string script)
    {
        if (_isWebViewReady)
        {
            return ExecuteScriptSafeAsync(script);
        }

        var tcs = new TaskCompletionSource<bool>();
        _pendingOps.Enqueue(async () =>
        {
            await ExecuteScriptSafeAsync(script);
            tcs.SetResult(true);
        });
        return tcs.Task;
    }

    private async Task ExecuteScriptSafeAsync(string script)
    {
        try
        {
            await WebView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ChatWebView] Script execution failed: {Script}", script);
        }
    }
}
