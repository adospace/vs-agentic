using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

public partial class MarkdownWebView : UserControl
{
    private bool _isWebViewReady;
    private string? _pendingMarkdown;
    private ScrollViewer? _clipScrollViewer;
    private DispatcherTimer? _clipTimer;

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownWebView),
            new PropertyMetadata(null, OnMarkdownChanged));

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly DependencyProperty BodyModeProperty =
        DependencyProperty.Register(
            nameof(BodyMode),
            typeof(OutputBodyMode),
            typeof(MarkdownWebView),
            new PropertyMetadata(OutputBodyMode.Markdown));

    public OutputBodyMode BodyMode
    {
        get => (OutputBodyMode)GetValue(BodyModeProperty);
        set => SetValue(BodyModeProperty, value);
    }

    public static readonly DependencyProperty MaxRenderHeightProperty =
        DependencyProperty.Register(
            nameof(MaxRenderHeight),
            typeof(double),
            typeof(MarkdownWebView),
            new PropertyMetadata(0.0));

    public double MaxRenderHeight
    {
        get => (double)GetValue(MaxRenderHeightProperty);
        set => SetValue(MaxRenderHeightProperty, value);
    }

    /// <summary>
    /// Optional: set the specific ScrollViewer to clip against.
    /// If not set, walks up the visual tree to find the nearest one named "ChatScrollViewer",
    /// falling back to the first ScrollViewer found.
    /// </summary>
    public static readonly DependencyProperty ClipScrollViewerProperty =
        DependencyProperty.Register(
            nameof(ClipScrollViewer),
            typeof(ScrollViewer),
            typeof(MarkdownWebView),
            new PropertyMetadata(null));

    public ScrollViewer? ClipScrollViewer
    {
        get => (ScrollViewer?)GetValue(ClipScrollViewerProperty);
        set => SetValue(ClipScrollViewerProperty, value);
    }

    public MarkdownWebView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isWebViewReady)
        {
            await InitializeWebViewAsync();
        }
        SetupClipping();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TeardownClipping();
    }

    private void SetupClipping()
    {
        TeardownClipping();

        _clipScrollViewer = ClipScrollViewer ?? FindBestScrollViewer();
        if (_clipScrollViewer is null) return;

        _clipScrollViewer.ScrollChanged += OnClipTrigger;
        _clipScrollViewer.SizeChanged += OnClipSizeTrigger;

        // Use a low-frequency timer as a safety net for layout changes
        // that don't trigger ScrollChanged (VS docking, tool window resize, etc.)
        _clipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _clipTimer.Tick += (_, _) => UpdateClipRegion();
        _clipTimer.Start();

        // Initial clip
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateClipRegion));
    }

    private void TeardownClipping()
    {
        if (_clipScrollViewer is not null)
        {
            _clipScrollViewer.ScrollChanged -= OnClipTrigger;
            _clipScrollViewer.SizeChanged -= OnClipSizeTrigger;
            _clipScrollViewer = null;
        }
        if (_clipTimer is not null)
        {
            _clipTimer.Stop();
            _clipTimer = null;
        }
    }

    /// <summary>
    /// Find the best ScrollViewer to clip against.
    /// Prefers one named "ChatScrollViewer", falls back to first found.
    /// </summary>
    private ScrollViewer? FindBestScrollViewer()
    {
        ScrollViewer? first = null;
        DependencyObject? current = this;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is ScrollViewer sv)
            {
                if (sv.Name == "ChatScrollViewer")
                    return sv;
                first ??= sv;
            }
        }
        return first;
    }

    private void OnClipTrigger(object sender, ScrollChangedEventArgs e) => UpdateClipRegion();
    private void OnClipSizeTrigger(object sender, SizeChangedEventArgs e) => UpdateClipRegion();

    private void UpdateClipRegion()
    {
        if (_clipScrollViewer is null || !_isWebViewReady)
            return;

        IntPtr hwnd;
        try
        {
            hwnd = ((HwndHost)WebView).Handle;
        }
        catch
        {
            return;
        }
        if (hwnd == IntPtr.Zero)
            return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return;

        try
        {
            var controlOrigin = TransformToAncestor(_clipScrollViewer).Transform(new Point(0, 0));
            var controlRect = new Rect(controlOrigin, new Size(ActualWidth, ActualHeight));
            var viewportRect = new Rect(0, 0, _clipScrollViewer.ViewportWidth, _clipScrollViewer.ViewportHeight);
            var intersection = Rect.Intersect(controlRect, viewportRect);

            var dpiX = source.CompositionTarget.TransformToDevice.M11;
            var dpiY = source.CompositionTarget.TransformToDevice.M22;

            IntPtr rgn;
            if (intersection.IsEmpty)
            {
                rgn = CreateRectRgn(0, 0, 0, 0);
            }
            else
            {
                var localX = intersection.X - controlOrigin.X;
                var localY = intersection.Y - controlOrigin.Y;

                rgn = CreateRectRgn(
                    (int)(localX * dpiX),
                    (int)(localY * dpiY),
                    (int)((localX + intersection.Width) * dpiX),
                    (int)((localY + intersection.Height) * dpiY));
            }

            SetWindowRgn(hwnd, rgn, true);
        }
        catch
        {
            // TransformToAncestor can throw if not in the same visual tree
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Use a writable folder for WebView2 user data (required when hosted in VS)
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
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    private static string LoadHtmlTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var showdownJs = ReadEmbeddedResource(assembly, "VsAgentic.UI.Assets.showdown.min.js");
        var templateHtml = ReadEmbeddedResource(assembly, "VsAgentic.UI.Assets.markdown-template.html");
        return templateHtml.Replace("{{SHOWDOWN_JS}}", showdownJs);
    }

    private static string ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isWebViewReady = true;
        UpdateClipRegion(); // Clip immediately once ready
        if (_pendingMarkdown is not null)
        {
            _ = UpdateContentAsync(_pendingMarkdown);
            _pendingMarkdown = null;
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

            switch (type)
            {
                case "height":
                    var height = root.GetProperty("value").GetDouble();
                    if (height > 0)
                    {
                        height += 2;
                        if (MaxRenderHeight > 0 && height > MaxRenderHeight)
                            height = MaxRenderHeight;
                        Height = height;
                        UpdateClipRegion();
                    }
                    break;

                case "wheel":
                    var deltaY = root.GetProperty("deltaY").GetDouble();
                    var wpfDelta = -(int)(deltaY > 0 ? 120 : deltaY < 0 ? -120 : 0);
                    if (wpfDelta != 0)
                    {
                        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, wpfDelta)
                        {
                            RoutedEvent = MouseWheelEvent
                        };
                        RaiseEvent(args);
                    }
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownWebView control)
        {
            var markdown = e.NewValue as string;
            if (control._isWebViewReady)
                _ = control.UpdateContentAsync(markdown);
            else
                control._pendingMarkdown = markdown;
        }
    }

    private async Task UpdateContentAsync(string? content)
    {
        if (!_isWebViewReady) return;

        try
        {
            var escaped = JsonSerializer.Serialize(content ?? "");
            var function = BodyMode == OutputBodyMode.Html ? "renderHtml" : "renderMarkdown";
            await WebView.ExecuteScriptAsync($"{function}({escaped})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Content render failed: {ex.Message}");
        }
    }
}
