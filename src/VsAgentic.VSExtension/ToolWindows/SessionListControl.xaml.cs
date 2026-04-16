using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

public partial class SessionListControl : UserControl
{
    private bool _initialized;

    public SessionListControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;

        if (!BindIfNeeded())
        {
            // Package hasn't initialized yet (VS restored the window before the package loaded).
            // Subscribe to be notified when it's ready.
            VsAgenticPackage.Initialized += OnPackageInitialized;
        }
    }

    private void OnPackageInitialized()
    {
        VsAgenticPackage.Initialized -= OnPackageInitialized;
        BindIfNeeded();
    }

    /// <summary>
    /// Binds the ViewModel if not already bound.
    /// Called both from the package and when the control loads (for VS-restored windows).
    /// </summary>
    /// <returns>True if binding succeeded.</returns>
    public bool BindIfNeeded()
    {
        if (_initialized) return true;

        var vm = VsAgenticPackage.SessionListVM;
        if (vm is null) return false;

        DataContext = vm;
        _initialized = true;
        return true;
    }

    private void ListBoxItem_Click(object sender, MouseButtonEventArgs e)
    {
        // Don't open session when clicking buttons (e.g., delete)
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != sender)
        {
            if (source is Button) return;
            source = VisualTreeHelper.GetParent(source);
        }

        if (sender is ListBoxItem item && item.DataContext is SessionInfo session
            && DataContext is SessionListViewModel vm)
        {
            vm.OpenSessionCommand.Execute(session);
        }
    }
}
