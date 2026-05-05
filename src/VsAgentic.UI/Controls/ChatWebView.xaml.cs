using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
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

    // ── Permission banner / question card mounting ───────────────────────

    /// <summary>
    /// Show the permission banner for a request from the Claude CLI. The
    /// supplied callback is invoked once when the user clicks Allow or Deny.
    /// Replaces any banner currently shown.
    /// </summary>
    public void ShowPermissionBanner(PermissionRequest request, Action<PermissionDecision> onResolved)
    {
        Dispatcher.Invoke(() =>
        {
            FrameworkElement? element = null;
            element = ChatBannerBuilder.BuildPermissionBanner(request, decision =>
            {
                HideBanner();
                onResolved(decision);
            });
            MountBanner(element);
        });
    }

    /// <summary>
    /// Show the Claude CLI login banner. Invoked when the CLI returned a
    /// documented authentication error (e.g. "Please run /login"). The callback
    /// is fired when the user clicks the Sign in button.
    /// </summary>
    public void ShowLoginBanner(string? errorMessage, Action onLoginClicked)
    {
        Dispatcher.Invoke(() =>
        {
            var element = ChatBannerBuilder.BuildLoginBanner(errorMessage, () =>
            {
                HideBanner();
                onLoginClicked();
            });
            MountBanner(element);
        });
    }

    /// <summary>
    /// Show the AskUserQuestion card. Callback receives the answer dictionary
    /// (question text → selected label or free-text) on Submit.
    /// </summary>
    public void ShowQuestionCard(UserQuestionRequest request, Action<IReadOnlyDictionary<string, string>> onSubmitted)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[ChatWebView] ShowQuestionCard begin (toolUseId={request.ToolUseId}, questions={request.Questions.Count})");
                var element = ChatBannerBuilder.BuildQuestionCard(request, answers =>
                {
                    HideBanner();
                    onSubmitted(answers);
                });
                MountBanner(element);
                System.Diagnostics.Trace.WriteLine(
                    $"[ChatWebView] ShowQuestionCard mounted (toolUseId={request.ToolUseId})");
            }
            catch (Exception ex)
            {
                // Without this guard the throw escapes Dispatcher.Invoke at a
                // point where nothing logs it, leaving the chat hung.
                System.Diagnostics.Trace.WriteLine($"[ChatWebView] ShowQuestionCard failed: {ex}");
                System.Diagnostics.Debug.WriteLine($"[ChatWebView] ShowQuestionCard failed: {ex}");
                // Best-effort fallback: complete the broker with empty answers
                // so the dispatcher loop unblocks instead of hanging forever.
                try { onSubmitted(new Dictionary<string, string>()); } catch { }
            }
        });
    }

    /// <summary>
    /// Pull the BannerHost border + background from the current BannerTheme so
    /// the host chrome matches the IDE/OS theme. Called each time a banner is
    /// shown so theme changes apply on the next prompt.
    /// </summary>
    private void ApplyBannerHostChrome()
    {
        var theme = BannerTheme.Current;
        BannerHost.BorderBrush = theme.Border;
        BannerHost.Background = theme.Background;
    }

    /// <summary>
    /// Mount the supplied element into <c>BannerHost</c> and force a layout
    /// pass + render-priority kick.
    ///
    /// The reason for the manual invalidation: when WebView2 occupies the
    /// chat area and is actively rendering, transitioning the BannerHost row
    /// from <c>Collapsed</c> (0px) to <c>Visible</c> (Auto) does not always
    /// retrigger the parent Grid's measure pass — the banner ends up in the
    /// visual tree at zero height, invisible until the user moves or resizes
    /// the window (which forces an unrelated relayout). Reproducible during
    /// AskUserQuestion / permission prompts on the VS extension's tool window.
    /// Walking up to the parent and calling InvalidateMeasure + UpdateLayout
    /// resolves it; the deferred Render-priority follow-up is a belt-and-
    /// braces measure for slow layout cycles.
    /// </summary>
    private void MountBanner(FrameworkElement banner)
    {
        ApplyBannerHostChrome();
        BannerHost.Child = banner;
        BannerHost.Visibility = Visibility.Visible;

        BannerHost.InvalidateMeasure();
        if (BannerHost.Parent is UIElement bannerParent)
            bannerParent.InvalidateMeasure();
        UpdateLayout();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            BannerHost.UpdateLayout();
            BannerHost.InvalidateVisual();
            if (BannerHost.Parent is UIElement p) p.InvalidateVisual();
        }), DispatcherPriority.Render);
    }

    public void HideBanner()
    {
        Dispatcher.Invoke(() =>
        {
            BannerHost.Child = null;
            BannerHost.Visibility = Visibility.Collapsed;
        });
    }
}
