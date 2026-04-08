using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

public partial class ChatSessionControl : UserControl
{
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

        // Apply VS theme colors to the WebView
        ApplyThemeColors();

        // Re-apply when VS theme changes
        VSColorTheme.ThemeChanged += OnThemeChanged;
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
        Map("--accent", EnvironmentColors.SystemHighlightColorKey);
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

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            !Keyboard.IsKeyDown(Key.LeftShift) &&
            !Keyboard.IsKeyDown(Key.RightShift))
        {
            if (DataContext is ChatSessionViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
