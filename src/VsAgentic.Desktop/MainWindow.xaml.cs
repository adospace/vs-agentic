using VsAgentic.UI.Controls;
using VsAgentic.UI.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VsAgentic.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(ChatSessionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Pull banner colors from the OS theme via WPF SystemColors so
        // light-themed Windows shows a light banner.
        BannerTheme.Current = BannerTheme.FromColors(
            background:        SystemColors.ControlColor,
            border:            SystemColors.ActiveBorderColor,
            foreground:        SystemColors.ControlTextColor,
            muted:             SystemColors.GrayTextColor,
            inputBackground:   SystemColors.WindowColor,
            accent:            SystemColors.HighlightColor,
            accentForeground:  SystemColors.HighlightTextColor,
            danger:            Color.FromRgb(0xDC, 0x26, 0x26),
            dangerForeground:  Colors.White);

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

        viewModel.PermissionPromptRequested += (request, resolve) =>
            ChatWebView.ShowPermissionBanner(request, resolve);

        viewModel.UserQuestionRequested += (request, submit) =>
            ChatWebView.ShowQuestionCard(request, submit);

        Loaded += (_, _) => InputTextBox.Focus();
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
