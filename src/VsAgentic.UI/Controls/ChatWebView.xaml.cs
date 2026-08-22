using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            MapImageHost();
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

    // Images are served to the WebView over a virtual host instead of being
    // inlined as data URIs. A single screenshot runs to a megabyte or more, and
    // ExecuteScriptAsync takes the whole script as one string — pushing that
    // much base64 through it kills the WebView2 browser process outright, which
    // blanks the pane with no exception on our side.
    private const string ImageHost = "vsagentic-images";
    private string? _imageFolder;

    private void MapImageHost()
    {
        try
        {
            // Scoped to the process and cleared on start: this is a render cache,
            // not storage. The session folder keeps the copies that matter.
            _imageFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VsAgentic", "WebView2Images", $"p{Process.GetCurrentProcess().Id}");

            if (Directory.Exists(_imageFolder))
                Directory.Delete(_imageFolder, recursive: true);
            Directory.CreateDirectory(_imageFolder);

            // DenyCors, not Deny: the page comes from NavigateToString, so its
            // origin is not the virtual host and every <img> pointing at it is a
            // cross-origin request. Deny blocks those outright and the thumbnail
            // renders as a broken image. DenyCors lets subresources load while
            // still refusing fetch/XHR against the folder.
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                ImageHost, _imageFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch (Exception ex)
        {
            _imageFolder = null;
            System.Diagnostics.Debug.WriteLine($"ChatWebView image host mapping failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes any data-URI images in the message to the mapped folder and
    /// rewrites them to short virtual-host URLs, so the script that carries the
    /// message stays small. Messages without images are returned unchanged.
    /// </summary>
    private ChatMessageData MaterializeImages(ChatMessageData data)
    {
        if (data.Images is not { Count: > 0 } || _imageFolder is null) return data;

        var urls = new List<string>(data.Images.Count);
        foreach (var image in data.Images)
        {
            urls.Add(WriteImage(image) ?? image);
        }

        return new ChatMessageData
        {
            Id = data.Id,
            Type = data.Type,
            Content = data.Content,
            ToolName = data.ToolName,
            Title = data.Title,
            Status = data.Status,
            ExpanderTitle = data.ExpanderTitle,
            BodyMode = data.BodyMode,
            Body = data.Body,
            IsStreaming = data.IsStreaming,
            Images = urls
        };
    }

    private string? WriteImage(string dataUri)
    {
        try
        {
            // data:<media type>;base64,<payload>
            var comma = dataUri.IndexOf(',');
            if (!dataUri.StartsWith("data:", StringComparison.Ordinal) || comma < 0)
                return null;

            var mediaType = dataUri.Substring(5, dataUri.IndexOf(';') - 5);
            var bytes = Convert.FromBase64String(dataUri.Substring(comma + 1));

            var extension = mediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin",
            };

            // Content-addressed, so the same image reused across messages is
            // written once.
            string name;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                name = BitConverter.ToString(sha.ComputeHash(bytes), 0, 8).Replace("-", "") + extension;
            }

            var path = Path.Combine(_imageFolder!, name);
            if (!File.Exists(path))
                File.WriteAllBytes(path, bytes);

            return $"https://{ImageHost}/{name}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ChatWebView image write failed: {ex.Message}");
            return null;
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
        var dataJson = JsonSerializer.Serialize(MaterializeImages(data));
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
        var json = JsonSerializer.Serialize(messages.Select(MaterializeImages));
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
