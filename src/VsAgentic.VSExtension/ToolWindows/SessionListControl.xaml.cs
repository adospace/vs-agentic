using System.Windows.Controls;
using System.Windows.Input;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

public partial class SessionListControl : UserControl
{
    private bool _initialized;

    public SessionListControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Binds the ViewModel if not already bound.
    /// Called both from the package and when the control loads (for VS-restored windows).
    /// </summary>
    public void BindIfNeeded()
    {
        if (_initialized) return;

        var vm = VsAgenticPackage.SessionListVM;
        if (vm is null) return;

        DataContext = vm;
        _initialized = true;
    }

    private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SessionListViewModel vm && vm.SelectedSession is not null)
        {
            vm.OpenSessionCommand.Execute(vm.SelectedSession);
        }
    }
}
