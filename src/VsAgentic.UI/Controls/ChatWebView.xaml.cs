using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VsAgentic", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
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
            System.Diagnostics.Debug.WriteLine($"ChatWebView init failed: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"ChatWebView queued op failed: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"ChatWebView script failed: {ex.Message}");
        }
    }
}
