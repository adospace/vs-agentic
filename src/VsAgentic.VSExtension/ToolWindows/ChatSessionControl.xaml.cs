using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Abstractions;
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

        // The model panel opens above its button with their right edges in
        // line. The button sits near the right of the status bar, so a panel
        // aligned to its left edge would run past the window. Above or, if
        // there is no room, below.
        ModelPickerPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal),
        };

        // The panel works from the keyboard: it opens with the focus on the
        // session's model, the arrow keys and Tab move inside it, and Escape
        // closes it. The content is laid out after Opened, hence the post.
        ModelPickerPopup.Opened += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusSelectedModelRow));
        ModelPickerPopup.Closed += (_, _) =>
        {
            // Focus left in the closed panel would sit in a hidden window. A
            // click elsewhere has already moved it, and keeps it there.
            if (Keyboard.FocusedElement is not DependencyObject focused || IsInModelPickerPanel(focused))
                ModelPickerToggle.Focus();
        };
        ModelPickerToggle.PreviewKeyDown += ModelPickerToggle_PreviewKeyDown;
    }

    // Alt+Down and F4 open the panel, as they open a dropdown list.
    private void ModelPickerToggle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var altDown = e.Key == Key.System && e.SystemKey == Key.Down;
        if ((altDown || e.Key == Key.F4) && ModelPickerToggle.IsChecked != true)
        {
            ModelPickerToggle.IsChecked = true;
            e.Handled = true;
        }
    }

    private void ModelPickerPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var altUp = e.Key == Key.System && e.SystemKey == Key.Up;
        if (e.Key == Key.Escape || altUp || e.Key == Key.F4)
        {
            ModelPickerToggle.IsChecked = false;
            ModelPickerToggle.Focus();
            e.Handled = true;
        }
    }

    private void FocusSelectedModelRow()
    {
        if (!ModelPickerPopup.IsOpen) return;

        var row = FindDescendants<Button>(ModelPickerPanel)
            .FirstOrDefault(b => b.DataContext is ModelOption { IsSelected: true } && b.IsVisible);
        if (row is not null)
            row.Focus();
        else
            ModelPickerPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private bool IsInModelPickerPanel(DependencyObject element)
    {
        for (var node = element; node is not null;
             node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ModelPickerPanel)) return true;
        }
        return false;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
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

        viewModel.MessageBackgroundStateSet += (id, state, summary) =>
            _ = ChatWebView.SetBackgroundStateAsync(id, state, summary);

        viewModel.MessageCanMoveToBackground += (id) =>
            _ = ChatWebView.SetCanMoveToBackgroundAsync(id);

        viewModel.MessageCompleted += (id) =>
            _ = ChatWebView.CompleteMessageAsync(id);

        viewModel.AllCleared += () =>
            _ = ChatWebView.ClearAllAsync();

        viewModel.MessagesRestored += (messages) =>
            _ = ChatWebView.LoadMessagesAsync(messages);

        viewModel.TurnEnded += () =>
            _ = ChatWebView.EndActivityAsync();

        // Banner mounting is now driven by the ActiveBanner property on the VM —
        // the BannerHost ContentControl in XAML binds to it and DataTemplates
        // pick the right UserControl by VM type.

        // Apply VS theme colors to the WebView
        ApplyThemeColors();
        ApplyShellBrushes();

        // Re-apply when VS theme changes
        VSColorTheme.ThemeChanged += OnThemeChanged;

        // Ctrl+wheel and Ctrl+/- over the chat itself are seen by the browser,
        // not by WPF; the page relays them.
        ChatWebView.ZoomChangeRequested += OnZoomChangeRequested;

        // The "Run in background" button on a running step.
        ChatWebView.MoveToBackgroundRequested += OnMoveToBackgroundRequested;

        // Zoom is one setting across every chat window, so follow it rather
        // than own it. Applied without the toast here: this is the window
        // adopting the level it was born into, not the user changing it.
        ChatZoom.Changed += OnZoomChanged;
        ApplyZoom(ChatZoom.Level);

        // Three ways to say "seen it", between them covering everywhere a
        // pending Completed indicator should stop:
        //
        //  - GotFocus bubbles from whichever descendant actually receives it,
        //    so it fires as soon as VS activates the tab and focus lands
        //    inside — the input box, a banner button, anywhere.
        //  - PreviewMouseDown catches a click on the chrome that takes no
        //    focus: the status bar, the usage meters, a header, bare margin.
        //  - Clicked is relayed by the page, because the transcript itself is
        //    a browser child window that WPF sees no mouse events from.
        GotFocus += (_, _) => viewModel.NotifySessionSeen();
        ChatWebView.Clicked += OnChatClicked;

        // Separately from "seen it", the view model wants to know whether the
        // user is in this session at the moment a turn ends, so one that ends
        // under their nose raises no indicator at all. Same two halves: WPF
        // reports its own chrome, the page reports itself.
        IsKeyboardFocusWithinChanged += OnKeyboardFocusWithinChanged;
        ChatWebView.ContentFocusChanged += OnChatContentFocusChanged;
    }

    private void OnChatClicked()
    {
        if (DataContext is ChatSessionViewModel vm) vm.NotifySessionSeen();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ChatSessionViewModel vm) vm.NotifySessionSeen();
    }

    /// <summary>True while the page reports focus; see <see cref="UpdateSessionFocus"/>.</summary>
    private bool _chatContentFocused;

    private void OnChatContentFocusChanged(bool hasFocus)
    {
        _chatContentFocused = hasFocus;
        UpdateSessionFocus();
    }

    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateSessionFocus();

    /// <summary>
    /// Answers "is the user in this session right now?". Keyboard focus rather
    /// than logical focus or visibility: it goes false when another pane takes
    /// over and when VS is sent to the background, both of which mean the user
    /// is no longer watching — whereas a docked window stays visible either
    /// way. The page's own answer is OR'd in because focus inside the browser
    /// child window does not always register as focus within the host.
    /// </summary>
    private void UpdateSessionFocus()
    {
        if (DataContext is ChatSessionViewModel vm)
            vm.IsSessionFocused = IsKeyboardFocusWithin || _chatContentFocused;
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
        ChatWebView.MoveToBackgroundRequested -= OnMoveToBackgroundRequested;
        ChatWebView.Clicked -= OnChatClicked;
        ChatWebView.ContentFocusChanged -= OnChatContentFocusChanged;
        IsKeyboardFocusWithinChanged -= OnKeyboardFocusWithinChanged;

        // A popup is its own window and would outlive the pane it belongs to.
        _zoomToastTimer?.Stop();
        ZoomToast.IsOpen = false;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyThemeColors();
            ApplyShellBrushes();
        }));
    }

    private void ApplyThemeColors()
    {
        var colors = new Dictionary<string, string>();

        void Map(string cssVar, ThemeResourceKey resourceKey)
        {
            var wpfColor = VSColorTheme.GetThemedColor(resourceKey);
            colors[cssVar] = $"#{wpfColor.R:X2}{wpfColor.G:X2}{wpfColor.B:X2}";
        }

        // Where the shell palette has the color, it comes from the theme the
        // user picked; the environment key is what a Visual Studio 2026 theme
        // leaves to its fallback. See ShellPalette.
        void MapShell(string cssVar, string shellColor, ThemeResourceKey fallback)
        {
            if (ShellPalette.TryGetColor(shellColor) is not Color color)
            {
                Map(cssVar, fallback);
                return;
            }

            // Some of these colors are meant to sit over what is behind them,
            // a control edge above all, so the alpha is passed on.
            colors[cssVar] = color.A == byte.MaxValue
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
        }

        Map("--bg-primary", EnvironmentColors.ToolWindowBackgroundColorKey);
        Map("--bg-secondary", EnvironmentColors.CommandBarGradientBeginColorKey);
        MapShell("--bg-input", "ControlFillActiveInput", EnvironmentColors.ComboBoxBackgroundColorKey);
        MapShell("--text-primary", "TextFillPrimary", EnvironmentColors.ToolWindowTextColorKey);
        MapShell("--text-heading", "TextFillPrimary", EnvironmentColors.ToolWindowTextColorKey);
        MapShell("--text-muted", "TextFillSecondary", EnvironmentColors.CommandBarTextInactiveColorKey);
        MapShell("--border", "ControlStrokeDefault", EnvironmentColors.ToolWindowBorderColorKey);
        Map("--code-bg", EnvironmentColors.ToolWindowContentGridColorKey);
        Map("--pre-bg", EnvironmentColors.ToolWindowBackgroundColorKey);
        Map("--thinking-bg", EnvironmentColors.CommandBarGradientBeginColorKey);

        var accent = ThemeAccent();
        colors["--accent"] = ToCssHex(accent);

        var toolWindowBackground = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        var promptAccent = accent;
        var promptBorderBase = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey);
        var promptBgBlend = RelativeLuminance(toolWindowBackground) >= 0.5 ? 0.08 : 0.12;

        colors["--prompt-bg"] = ToCssHex(Blend(toolWindowBackground, promptAccent, promptBgBlend));
        colors["--prompt-border"] = ToCssHex(Blend(promptBorderBase, promptAccent, 0.35));
        colors["--prompt-accent"] = ToCssHex(promptAccent);

        // Scrollbar: derive from gray/muted text for subtle appearance
        var gray = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbBackgroundColorKey);
        colors["--scrollbar-thumb"] = $"rgba({gray.R}, {gray.G}, {gray.B}, 0.5)";
        var grayHover = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbMouseOverBackgroundColorKey);
        colors["--scrollbar-thumb-hover"] = $"rgba({grayHover.R}, {grayHover.G}, {grayHover.B}, 0.8)";

        _ = ChatWebView.SetThemeColorsAsync(colors);
    }

    /// <summary>
    /// The color the chat uses for links, focus borders and the prompt accent.
    /// </summary>
    /// <remarks>
    /// The OS system highlight is deliberately not used: on Windows 11 it is
    /// often violet and clashes with the VS theme.
    /// </remarks>
    private static System.Drawing.Color ThemeAccent() =>
        // A theme written for the Visual Studio 2026 shell leaves PanelHyperlink
        // to its fallback theme, so ask the shell palette first and keep the
        // environment key for Visual Studio 2022. See ShellPalette.
        ShellPalette.TryGetColor("HyperlinkFillPrimary") is Color accent
            ? System.Drawing.Color.FromArgb(accent.A, accent.R, accent.G, accent.B)
            : VSColorTheme.GetThemedColor(EnvironmentColors.PanelHyperlinkColorKey);

    /// <summary>
    /// The environment brushes the chrome of this window binds to, each paired
    /// with the shell color that carries the same meaning in a Visual Studio
    /// 2026 theme.
    /// </summary>
    // VsBrushes exposes its keys as object, which is also what a resource
    // dictionary takes, so the pairs are held that way.
    private static readonly (object Brush, string ShellColor)[] ChromeBrushes =
    {
        (VsBrushes.ComboBoxBackgroundKey, "ControlFillActiveInput"),
        (VsBrushes.ComboBoxBorderKey, "ControlStrokeDefault"),
        (VsBrushes.WindowTextKey, "TextFillPrimary"),
        (VsBrushes.GrayTextKey, "TextFillSecondary"),
        (VsBrushes.PanelHyperlinkKey, "HyperlinkFillPrimary"),
        (VsBrushes.CommandBarMouseOverBackgroundGradientKey, "SubtleFillSecondary"),
        (VsBrushes.CommandBarMouseDownBackgroundGradientKey, "SubtleFillTertiary"),
        (VsBrushes.CommandBarSelectedKey, "SubtleFillTertiary"),
    };

    /// <summary>
    /// Points the prompt box and the status row at the shell palette, so that a
    /// theme which defines only the shell colors still reaches them.
    /// </summary>
    private void ApplyShellBrushes() => ShellPalette.OverrideBrushes(this, ChromeBrushes);

    private static System.Drawing.Color Blend(System.Drawing.Color from, System.Drawing.Color to, double amount)
    {
        amount = Math.Max(0, Math.Min(1, amount));

        byte BlendChannel(byte fromChannel, byte toChannel) =>
            (byte)Math.Round(fromChannel + ((toChannel - fromChannel) * amount));

        return System.Drawing.Color.FromArgb(
            BlendChannel(from.R, to.R),
            BlendChannel(from.G, to.G),
            BlendChannel(from.B, to.B));
    }

    private static double RelativeLuminance(System.Drawing.Color color)
    {
        var r = ToLinear(color.R);
        var g = ToLinear(color.G);
        var b = ToLinear(color.B);

        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double ToLinear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045
            ? c / 12.92
            : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static string ToCssHex(System.Drawing.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatSessionViewModel vm &&
            sender is FrameworkElement { DataContext: IChatAttachment attachment })
        {
            vm.RemoveAttachment(attachment);
        }
    }

    /// <summary>
    /// Attaches a selection sent from the editor's context menu. The input box
    /// takes focus once the window has come forward, so the user can type the
    /// question straight away.
    /// </summary>
    public void AttachFromEditor(EditorContext context)
    {
        if (DataContext is not ChatSessionViewModel vm) return;

        vm.Attach(context);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => InputTextBox.Focus()));
    }

    private void AcceptEditorContext_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatSessionViewModel vm &&
            sender is FrameworkElement { DataContext: EditorContext suggestion })
        {
            vm.AcceptEditorContext(suggestion);

            // The next thing the user does is type the question about it.
            InputTextBox.Focus();
        }
    }

    // The selection only changes while the user is in the editor, so coming
    // back to the input box is when the suggestion has to catch up. The mouse
    // gets there first when the user goes straight for the suggestion, and
    // the button must name what a click attaches.
    private void InputTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        (DataContext as ChatSessionViewModel)?.RefreshEditorContext();
    }

    private void PromptBox_MouseEnter(object sender, MouseEventArgs e)
    {
        (DataContext as ChatSessionViewModel)?.RefreshEditorContext();
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

    /// <summary>
    /// Opens the find bar over the chat. Called by the tool window for Edit.Find.
    /// </summary>
    public void OpenFind() => _ = ChatWebView.OpenFindAsync();

    /// <summary>
    /// Steps through the matches. Called by the tool window for Edit.FindNext
    /// (+1) and Edit.FindPrevious (-1).
    /// </summary>
    public void FindNext(int direction) => _ = ChatWebView.FindNextAsync(direction);

    private void OnZoomChangeRequested(int step) => ChatZoom.Step(step);

    private void OnMoveToBackgroundRequested(string stepId)
    {
        if (DataContext is ChatSessionViewModel vm)
            _ = vm.MoveStepToBackgroundAsync(stepId);
    }

    // Read the output as the tooltip opens rather than on a timer: nobody
    // looks at it most of the time, and each read is a request to the CLI.
    // The tooltip shows the last text it had and changes when the answer
    // comes, in a few milliseconds.
    private void BackgroundTask_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BackgroundTaskItemViewModel task)
            _ = task.RefreshOutputAsync();
    }

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

    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatSessionViewModel vm) return;

        // No type filter: the CLI reads whatever it is handed, and the picker
        // reaching only the kinds we inline would hide every other file the
        // model can be asked about.
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach to message",
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (picker.ShowDialog() != true) return;

        Attach(vm, picker.FileNames);
        InputTextBox.Focus();
    }

    private void PromptBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        // Anything that is not a file is left alone, so text dragged out of an
        // editor still drops into the textbox as text.
        if (!TryGetDroppedPaths(e.Data, out _)) return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DropHighlight.Visibility = Visibility.Visible;
    }

    private void PromptBox_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // Hidden without asking where the cursor went. The textbox takes drops of
        // its own, so crossing from the box's chrome into it also raises a leave,
        // but WPF hands the leave and the next DragOver to us in one pass over
        // the same mouse move, and the DragOver puts the highlight straight back.
        // Leaving for good, and a drag cancelled with Escape, both end here too,
        // which a test on the cursor position would miss: Escape reports the last
        // point inside the box.
        DropHighlight.Visibility = Visibility.Collapsed;
    }

    private void PromptBox_PreviewDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;

        if (DataContext is not ChatSessionViewModel vm) return;
        if (!TryGetDroppedPaths(e.Data, out var paths)) return;

        Attach(vm, paths!);
        // Otherwise the textbox would go on to handle the same drop.
        e.Handled = true;
        InputTextBox.Focus();
    }

    /// <summary>
    /// Reads the paths a drag is carrying, if it is carrying any. The data
    /// object belongs to the source process and can fail on either call, which
    /// is not a reason to lose the drop's other formats.
    /// </summary>
    private static bool TryGetDroppedPaths(IDataObject data, out string[]? paths)
    {
        paths = null;
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
            paths = data.GetData(DataFormats.FileDrop) as string[];
        }
        catch
        {
            return false;
        }
        return paths is { Length: > 0 };
    }

    private static void Attach(ChatSessionViewModel vm, IEnumerable<string?> paths)
    {
        foreach (var attachment in AttachmentReader.FromPaths(paths))
            vm.Attach(attachment);
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
        // Ctrl+V is caught here rather than through DataObject.Pasting. A
        // screenshot sits on the clipboard as a bitmap with no text format, so
        // TextBoxBase.CanPaste is false, the paste command never runs, and the
        // Pasting event never fires — the keystroke would be swallowed silently.
        //
        // Which clipboard formats lose to plain text is decided in TryRead: it
        // is the one place that knows whether what it found is a bitmap, which
        // text beats, or a file the user copied deliberately, which it does not.
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (DataContext is ChatSessionViewModel vm)
            {
                var attachments = ClipboardAttachments.TryRead();
                if (attachments.Count > 0)
                {
                    foreach (var attachment in attachments) vm.Attach(attachment);
                    // Otherwise a file copied in Explorer would also paste its path.
                    e.Handled = true;
                    return;
                }
            }
        }

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
