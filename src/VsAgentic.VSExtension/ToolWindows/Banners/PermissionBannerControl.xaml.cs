using System.Windows;
using System.Windows.Controls;

namespace VsAgentic.VSExtension.ToolWindows.Banners;

public partial class PermissionBannerControl : UserControl
{
    public PermissionBannerControl()
    {
        InitializeComponent();
    }

    private void MoreAllowButton_Click(object sender, RoutedEventArgs e)
    {
        MoreAllowPopup.IsOpen = !MoreAllowPopup.IsOpen;
    }

    /// <summary>
    /// A click inside the popup does not close it, so each choice closes it
    /// itself. The bound command still runs: Click fires alongside it.
    /// </summary>
    private void MoreAllowChoice_Click(object sender, RoutedEventArgs e)
    {
        MoreAllowPopup.IsOpen = false;
    }
}
