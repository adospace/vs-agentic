using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

public partial class SessionListControl : UserControl
{
    private bool _initialized;

    /// <summary>
    /// Set while a row action button (rename, delete) is being clicked, so the
    /// selection change it causes doesn't also open the session window.
    /// </summary>
    private bool _suppressOpenOnSelection;

    /// <summary>
    /// The environment brushes this window binds, each paired with the palette
    /// color that carries the same meaning in a Visual Studio 2026 theme. See
    /// <see cref="ShellPalette"/> for why the pairing is needed.
    /// </summary>
    // VsBrushes exposes its keys as object, which is also what a resource
    // dictionary takes, so the pairs are held that way.
    private static readonly (object Key, string ShellColor)[] ChromeBrushes =
    {
        (VsBrushes.ButtonFaceKey, "ControlFillDefault"),
        (VsBrushes.ButtonTextKey, "TextFillPrimary"),
        (VsBrushes.ComboBoxBackgroundKey, "ControlFillActiveInput"),
        (VsBrushes.ComboBoxBorderKey, "ControlStrokeDefault"),
        (VsBrushes.WindowTextKey, "TextFillPrimary"),
        (VsBrushes.GrayTextKey, "TextFillSecondary"),
        (VsBrushes.CommandBarMouseOverBackgroundGradientKey, "SubtleFillSecondary"),
        (VsBrushes.CommandBarMouseDownBackgroundGradientKey, "SubtleFillTertiary"),
        (VsBrushes.CommandBarSelectedKey, "SubtleFillTertiary"),
        // A context menu sits on top of the window, so it takes the raised
        // surface and the flyout edge rather than the window's own.
        (VsBrushes.CommandBarMenuBackgroundGradientKey, "CardBackgroundFillTertiary"),
        (VsBrushes.CommandBarMenuBorderKey, "SurfaceStrokeFlyout"),
        (VsBrushes.CommandBarTextActiveKey, "TextFillPrimary"),
        (VsBrushes.CommandBarTextHoverKey, "TextFillPrimary"),
    };

    /// <summary>
    /// The icon gutter of the context menu is themed against this color, so it
    /// has to follow the menu background above.
    /// </summary>
    private static readonly (object Key, string ShellColor)[] ChromeColors =
    {
        (VsColors.CommandBarMenuBackgroundGradientBeginKey, "CardBackgroundFillTertiary"),
    };

    public SessionListControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        ApplyShellBrushes();
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Drops the static-event subscription this control took out in its
    /// constructor. Called when the tool window closes.
    /// </summary>
    public void Shutdown()
    {
        VSColorTheme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(ApplyShellBrushes));
    }

    private void ApplyShellBrushes()
    {
        ShellPalette.OverrideBrushes(this, ChromeBrushes);
        ShellPalette.OverrideColors(this, ChromeColors);
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

    private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOpenOnSelection)
            return;

        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is SessionInfo session
            && DataContext is SessionListViewModel vm)
        {
            vm.OpenSessionCommand.Execute(session);
        }
    }

    private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click inside the rename box selects a word; it must not
        // open the session.
        if (e.OriginalSource is DependencyObject source && FindAncestor<TextBox>(source) is not null)
            return;

        // Handles re-opening a session whose window was closed while it's
        // still the selected item (SelectionChanged won't fire in that case).
        if (DataContext is SessionListViewModel vm && vm.SelectedSession is not null)
        {
            vm.OpenSessionCommand.Execute(vm.SelectedSession);
        }
    }

    private void ListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;

        if (DataContext is SessionListViewModel vm && vm.SelectedSession is not null)
        {
            vm.BeginRenameCommand.Execute(vm.SelectedSession);
            e.Handled = true;
        }
    }

    private void ListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _suppressOpenOnSelection = false;

    private void RowAction_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _suppressOpenOnSelection = true;

    private void RowMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // The row owns the menu; the button only drops it below itself, the
        // way a "..." overflow reads elsewhere.
        var menu = FindAncestorContextMenu(button);
        if (menu is null) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static ContextMenu? FindAncestorContextMenu(DependencyObject start)
    {
        for (DependencyObject? node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element && element.ContextMenu is not null)
                return element.ContextMenu;
        }
        return null;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;

        // The box is revealed by a template trigger, so it isn't focusable yet
        // at this point — focus it once the layout pass is done.
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box
            || box.DataContext is not SessionInfo session
            || DataContext is not SessionListViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(session);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(session);
            e.Handled = true;
        }
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking away keeps what was typed. Enter and Escape have already
        // left rename mode, so the command is a no-op in those cases.
        if (sender is TextBox box
            && box.DataContext is SessionInfo session
            && DataContext is SessionListViewModel vm)
        {
            vm.CommitRenameCommand.Execute(session);
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (DependencyObject? node = start; node is not null;)
        {
            if (node is T match) return match;

            // Inline content (Run, Hyperlink) has no visual parent, so fall
            // back to the logical tree for those nodes.
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
