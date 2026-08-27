using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    private bool _isWebViewReady;
    private readonly ConcurrentQueue<Func<Task>> _pendingOps = new();

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
        var processFolder = $"{baseFolder}.p{Process.GetCurrentProcess().Id}";
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
                if (!int.TryParse(suffix, out var processId)) continue;

                try
                {
                    // Still alive: its owner is using the folder right now.
                    Process.GetProcessById(processId);
                    continue;
                }
                catch (ArgumentException)
                {
                    // No such process — the folder is orphaned.
                }

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

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isWebViewReady = true;

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
