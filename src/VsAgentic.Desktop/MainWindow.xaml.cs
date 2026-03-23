using VsAgentic.UI.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace VsAgentic.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(ChatSessionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.ScrollRequested += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                ChatScrollViewer.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

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
