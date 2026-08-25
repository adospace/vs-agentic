using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAgentic.UI.Controls;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

public partial class ChatSessionControl : UserControl
{
    private const double InputMinHeight = 36;

    private bool _isResizing;
    private double _resizeStartScreenY;
    private double _resizeStartHeight;

    // -1 when no mention is active. Otherwise, the caret position right after
    // the triggering '@' — text from this index up to the caret is the filter.
    private int _mentionStart = -1;
    private List<MentionEntry>? _mentionCache;
    private bool _suppressTextChanged;

    public ChatSessionControl()
    {
        InitializeComponent();
    }

    public void Initialize(ChatSessionViewModel viewModel)
    {
        DataContext = viewModel;

        viewModel.MessageAdded += (id, type, data) =>
            _ = ChatWebView.AddMessageAsync(id, type, data);

        viewModel.MessageContentUpdated += (id, content) =>
            _ = ChatWebView.UpdateContentAsync(id, content);

        viewModel.MessageStatusUpdated += (id, status, expanderTitle) =>
            _ = ChatWebView.UpdateStatusAsync(id, status, expanderTitle);

        viewModel.MessageBodySet += (id, body, mode) =>
            _ = ChatWebView.SetBodyAsync(id, body, mode);

        viewModel.MessageCompleted += (id) =>
            _ = ChatWebView.CompleteMessageAsync(id);

        viewModel.AllCleared += () =>
            _ = ChatWebView.ClearAllAsync();

        viewModel.MessagesRestored += (messages) =>
            _ = ChatWebView.LoadMessagesAsync(messages);

        // Banner mounting is now driven by the ActiveBanner property on the VM —
        // the BannerHost ContentControl in XAML binds to it and DataTemplates
        // pick the right UserControl by VM type.

        // Apply VS theme colors to the WebView
        ApplyThemeColors();

        // Re-apply when VS theme changes
        VSColorTheme.ThemeChanged += OnThemeChanged;

        // Ctrl+wheel and Ctrl+/- over the chat itself are seen by the browser,
        // not by WPF; the page relays them.
        ChatWebView.ZoomChangeRequested += OnZoomChangeRequested;

        // Zoom is one setting across every chat window, so follow it rather
        // than own it. Applied without the toast here: this is the window
        // adopting the level it was born into, not the user changing it.
        ChatZoom.Changed += OnZoomChanged;
        ApplyZoom(ChatZoom.Level);
    }

    /// <summary>
    /// Drops the static-event subscriptions this control took out in
    /// <see cref="Initialize"/>. Called when the tool window closes — not on
    /// Unloaded, which VS also raises when the window is merely tabbed away.
    /// </summary>
    public void Shutdown()
    {
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        ChatZoom.Changed -= OnZoomChanged;
        ChatWebView.ZoomChangeRequested -= OnZoomChangeRequested;

        // A popup is its own window and would outlive the pane it belongs to.
        _zoomToastTimer?.Stop();
        ZoomToast.IsOpen = false;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => ApplyThemeColors()));
    }

    private void ApplyThemeColors()
    {
        var colors = new Dictionary<string, string>();

        void Map(string cssVar, ThemeResourceKey resourceKey)
        {
            var wpfColor = VSColorTheme.GetThemedColor(resourceKey);
            colors[cssVar] = $"#{wpfColor.R:X2}{wpfColor.G:X2}{wpfColor.B:X2}";
        }

        Map("--bg-primary", EnvironmentColors.ToolWindowBackgroundColorKey);
        Map("--bg-secondary", EnvironmentColors.CommandBarGradientBeginColorKey);
        Map("--bg-input", EnvironmentColors.ComboBoxBackgroundColorKey);
        Map("--text-primary", EnvironmentColors.ToolWindowTextColorKey);
        Map("--text-heading", EnvironmentColors.ToolWindowTextColorKey);
        Map("--text-muted", EnvironmentColors.CommandBarTextInactiveColorKey);
        Map("--border", EnvironmentColors.ToolWindowBorderColorKey);
        // Use VS hyperlink color (theme-driven) rather than the OS system
        // highlight, which on Win11 is often violet/purple and clashes with
        // the dark/light VS theme.
        Map("--accent", EnvironmentColors.PanelHyperlinkColorKey);
        Map("--user-bg", EnvironmentColors.CommandBarGradientBeginColorKey);
        Map("--code-bg", EnvironmentColors.ToolWindowContentGridColorKey);
        Map("--pre-bg", EnvironmentColors.ToolWindowBackgroundColorKey);

        // Scrollbar: derive from gray/muted text for subtle appearance
        var gray = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbBackgroundColorKey);
        colors["--scrollbar-thumb"] = $"rgba({gray.R}, {gray.G}, {gray.B}, 0.5)";
        var grayHover = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbMouseOverBackgroundColorKey);
        colors["--scrollbar-thumb-hover"] = $"rgba({grayHover.R}, {grayHover.G}, {grayHover.B}, 0.8)";

        _ = ChatWebView.SetThemeColorsAsync(colors);
    }

    // ------------------------------------------------------------------
    // Zoom
    // ------------------------------------------------------------------

    private static readonly TimeSpan ZoomToastHold = TimeSpan.FromMilliseconds(900);

    private DispatcherTimer? _zoomToastTimer;

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || e.Delta == 0)
            return;

        ChatZoom.Step(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        // Numpad and main-row alike, as a browser does. Marking the key handled
        // matters beyond WPF: Ctrl+Minus is Navigate Backward in Visual Studio,
        // and anything left unhandled here goes on to the shell's commands.
        int? step = e.Key switch
        {
            Key.Add or Key.OemPlus => 1,
            Key.Subtract or Key.OemMinus => -1,
            Key.NumPad0 or Key.D0 => 0,
            _ => null,
        };

        if (step is null) return;

        ChatZoom.Step(step.Value);
        e.Handled = true;
    }

    private void OnZoomChangeRequested(int step) => ChatZoom.Step(step);

    private void OnZoomChanged(double level)
    {
        ApplyZoom(level);

        // Every open chat window follows the level, but only the one being
        // looked at flashes the reading. A popup belongs to no pane in
        // particular once it is open, so a background tab would put its pill
        // on top of whatever the user is actually working in.
        if (IsVisible) ShowZoomToast(level);
    }

    /// <summary>
    /// The factor the chrome is currently scaled by. Read off the transform
    /// rather than <see cref="ChatZoom.Level"/> so anything measuring against
    /// it stays right even mid-change.
    /// </summary>
    private double ZoomScale =>
        Resources["ChromeZoom"] is ScaleTransform chrome && chrome.ScaleY > 0
            ? chrome.ScaleY
            : 1.0;

    private void ApplyZoom(double level)
    {
        ChatWebView.ZoomFactor = level;

        if (Resources["ChromeZoom"] is ScaleTransform chrome)
        {
            chrome.ScaleX = level;
            chrome.ScaleY = level;
        }
    }

    private void ShowZoomToast(double level)
    {
        ZoomToastText.Text = ZoomLevels.Format(level);
        ZoomToast.CustomPopupPlacementCallback ??= PlaceZoomToast;

        // Reopening an already-open popup does not re-run placement, and the
        // reading changes width between "90%" and "100%"; closing first keeps
        // it pinned to the corner instead of drifting left as it grows.
        ZoomToast.IsOpen = false;
        ZoomToast.IsOpen = true;

        _zoomToastTimer ??= CreateZoomToastTimer();
        _zoomToastTimer.Stop();
        _zoomToastTimer.Start();
    }

    /// <summary>
    /// Pins the reading to the chat's top-right corner. A callback rather than
    /// a fixed offset because only here are both sizes known, and the popup's
    /// width moves with the number in it.
    /// </summary>
    private static CustomPopupPlacement[] PlaceZoomToast(Size popupSize, Size targetSize, Point offset) =>
        new[]
        {
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width - 12, 8),
                PopupPrimaryAxis.None)
        };

    private DispatcherTimer CreateZoomToastTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = ZoomToastHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // PopupAnimation="Fade" carries the close, so there is no animation
            // to run or unwind here.
            ZoomToast.IsOpen = false;
        };
        return timer;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (DataContext is ChatSessionViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not IInputElement grip) return;

        _isResizing = true;
        // Use screen-space coordinates so we are not affected by the textbox
        // resizing under us during drag (which would change positions in
        // the local coordinate system).
        _resizeStartScreenY = PointToScreen(e.GetPosition(this)).Y;
        _resizeStartHeight = InputTextBox.ActualHeight > 0
            ? InputTextBox.ActualHeight
            : InputTextBox.MinHeight;

        Mouse.Capture(grip);
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;

        var currentScreenY = PointToScreen(e.GetPosition(this)).Y;
        // Dragging upward decreases Y, which should grow the input box.
        // The drag is measured outside the zoom transform and the height is set
        // inside it, so divide: at 200% the box would otherwise grow twice as
        // fast as the pointer, and the same factor applies to the cap.
        var zoom = ZoomScale;
        var delta = (_resizeStartScreenY - currentScreenY) / zoom;
        var requested = _resizeStartHeight + delta;

        var maxHeight = Math.Max(InputMinHeight, RootPanel.ActualHeight / (2 * zoom));
        var newHeight = Math.Max(InputMinHeight, Math.Min(requested, maxHeight));

        // Pin the textbox to the dragged height so it neither grows with
        // content nor shrinks below the user's chosen size.
        InputTextBox.MinHeight = newHeight;
        InputTextBox.MaxHeight = newHeight;
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;

        _isResizing = false;
        if (Mouse.Captured is IInputElement captured && ReferenceEquals(captured, sender))
        {
            Mouse.Capture(null);
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // @-mention file/folder picker
    // ------------------------------------------------------------------

    private void AtMentionButton_Click(object sender, RoutedEventArgs e)
    {
        // Insert '@' at the caret (or after a leading space) and open the popup
        // explicitly. We suppress TextChanged because it fires before we get a
        // chance to update CaretIndex, which would confuse the trigger logic.
        InputTextBox.Focus();
        var text = InputTextBox.Text ?? "";
        var caret = Math.Min(InputTextBox.CaretIndex, text.Length);

        var needsLeadingSpace = caret > 0 && !char.IsWhiteSpace(text[caret - 1]);
        var insert = needsLeadingSpace ? " @" : "@";

        _suppressTextChanged = true;
        try
        {
            InputTextBox.Text = text.Insert(caret, insert);
            InputTextBox.CaretIndex = caret + insert.Length;
        }
        finally
        {
            _suppressTextChanged = false;
        }

        _mentionStart = caret + insert.Length;
        ShowMentionPopup("");
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        var text = InputTextBox.Text ?? "";
        var caret = InputTextBox.CaretIndex;

        if (_mentionStart < 0)
        {
            // Look for a freshly typed '@' immediately before the caret that
            // qualifies as a trigger (start of text or preceded by whitespace).
            if (caret > 0 && caret <= text.Length && text[caret - 1] == '@'
                && (caret == 1 || char.IsWhiteSpace(text[caret - 2])))
            {
                _mentionStart = caret;
                ShowMentionPopup("");
            }
            return;
        }

        // Mention is active — re-validate and refilter.
        if (caret < _mentionStart
            || _mentionStart > text.Length
            || _mentionStart == 0
            || text[_mentionStart - 1] != '@')
        {
            CloseMentionPopup();
            return;
        }

        var filter = text.Substring(_mentionStart, caret - _mentionStart);
        if (filter.Any(char.IsWhiteSpace))
        {
            CloseMentionPopup();
            return;
        }

        ApplyMentionFilter(filter);
    }

    private void InputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_mentionStart < 0) return;

        var caret = InputTextBox.CaretIndex;
        if (caret < _mentionStart) CloseMentionPopup();
    }

    private void InputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Don't close if focus moved into the popup (clicking a list item).
        if (e.NewFocus is DependencyObject d && IsDescendantOf(d, MentionPopup.Child)) return;
        CloseMentionPopup();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject? root)
    {
        if (root == null) return false;
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!MentionPopup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                MoveMentionSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveMentionSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (CommitMentionSelection()) e.Handled = true;
                break;
            case Key.Escape:
                CloseMentionPopup();
                e.Handled = true;
                break;
        }
    }

    private void MentionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!MentionPopup.IsOpen) return;
        // Handle on PreviewMouseDown so the click commits BEFORE the default
        // ListBoxItem mouse-down logic moves keyboard focus away from the
        // input textbox.
        if (e.OriginalSource is DependencyObject src)
        {
            var item = FindAncestor<ListBoxItem>(src);
            if (item?.DataContext is MentionEntry entry)
            {
                CommitMention(entry);
                InputTextBox.Focus();
                e.Handled = true;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        DependencyObject? current = node;
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MoveMentionSelection(int delta)
    {
        var count = MentionList.Items.Count;
        if (count == 0) return;

        var idx = MentionList.SelectedIndex;
        if (idx < 0) idx = delta > 0 ? -1 : 0;
        var next = ((idx + delta) % count + count) % count;
        MentionList.SelectedIndex = next;
        if (MentionList.SelectedItem is { } sel)
            MentionList.ScrollIntoView(sel);
    }

    private bool CommitMentionSelection()
    {
        if (MentionList.SelectedItem is MentionEntry entry)
        {
            CommitMention(entry);
            return true;
        }
        return false;
    }

    private void CommitMention(MentionEntry entry)
    {
        if (_mentionStart < 0) return;

        var text = InputTextBox.Text ?? "";
        var caret = InputTextBox.CaretIndex;
        if (_mentionStart - 1 < 0 || _mentionStart - 1 >= text.Length)
        {
            CloseMentionPopup();
            return;
        }

        var atIndex = _mentionStart - 1;
        var endIndex = Math.Min(Math.Max(caret, _mentionStart), text.Length);

        var path = entry.RelativePath;
        if (path.IndexOf(' ') >= 0) path = "\"" + path + "\"";

        _suppressTextChanged = true;
        try
        {
            InputTextBox.Text = text.Remove(atIndex, endIndex - atIndex).Insert(atIndex, path);
            InputTextBox.CaretIndex = atIndex + path.Length;
        }
        finally
        {
            _suppressTextChanged = false;
        }

        CloseMentionPopup();
    }

    private void ShowMentionPopup(string filter)
    {
        EnsureMentionCache();
        ApplyMentionFilter(filter);
    }

    private void CloseMentionPopup()
    {
        _mentionStart = -1;
        MentionPopup.IsOpen = false;
        MentionList.ItemsSource = null;
    }

    private void ApplyMentionFilter(string filter)
    {
        if (_mentionCache == null)
        {
            MentionList.ItemsSource = Array.Empty<MentionEntry>();
            return;
        }

        IEnumerable<MentionEntry> q = _mentionCache;
        if (!string.IsNullOrEmpty(filter))
        {
            q = q.Where(e =>
                e.RelativePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var results = q.Take(200).ToList();
        MentionList.ItemsSource = results;
        if (results.Count > 0) MentionList.SelectedIndex = 0;

        if (results.Count == 0) MentionPopup.IsOpen = false;
        else MentionPopup.IsOpen = true;
    }

    private void EnsureMentionCache()
    {
        if (_mentionCache != null) return;

        var root = (DataContext as ChatSessionViewModel)?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _mentionCache = new List<MentionEntry>();
            return;
        }

        _mentionCache = EnumerateEntries(root!).ToList();
    }

    private static IEnumerable<MentionEntry> EnumerateEntries(string root)
    {
        const int maxEntries = 5000;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", ".idea", "packages", "TestResults"
        };

        var stack = new Stack<string>();
        stack.Push(root);
        var count = 0;

        while (stack.Count > 0 && count < maxEntries)
        {
            var current = stack.Pop();

            string[] dirs;
            string[] files;
            try
            {
                dirs = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (skip.Contains(name) || name.StartsWith("."))
                    continue;
                yield return new MentionEntry(ToRelative(root, dir), name, isDirectory: true);
                if (++count >= maxEntries) yield break;
                stack.Push(dir);
            }

            foreach (var file in files)
            {
                yield return new MentionEntry(
                    ToRelative(root, file), Path.GetFileName(file), isDirectory: false);
                if (++count >= maxEntries) yield break;
            }
        }
    }

    private static string ToRelative(string root, string fullPath)
    {
        var rooted = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var rel = fullPath.StartsWith(rooted, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(rooted.Length)
            : fullPath;
        return rel.Replace('\\', '/');
    }
}
