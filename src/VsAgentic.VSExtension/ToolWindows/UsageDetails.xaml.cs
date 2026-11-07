using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace VsAgentic.VSExtension.ToolWindows;

/// <summary>
/// The usage panel that opens from the readings in the chat status bar:
/// plan limits with their reset times, the context window, and what this
/// session has spent. All values come from the session's view model.
/// </summary>
public partial class UsageDetails : UserControl
{
    public UsageDetails()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UsageMeter.ApplyWarningBrush(Resources);
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    // VSColorTheme is a shell-lifetime static; see UsageMeter.
    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        VSColorTheme.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(ThemeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => UsageMeter.ApplyWarningBrush(Resources)));
}
