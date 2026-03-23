using System.Windows.Controls;
using System.Windows.Input;
using VsAgentic.UI.ViewModels;
using Microsoft.VisualStudio.Shell;

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

        viewModel.ScrollRequested += () =>
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ChatScrollViewer.ScrollToEnd();
            });
        };
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
