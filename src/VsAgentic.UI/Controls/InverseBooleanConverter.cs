using System;
using System.Globalization;
using System.Windows.Data;

namespace VsAgentic.UI.Controls;

/// <summary>
/// True for false and false for true. For a toggle that opens a popup with
/// StaysOpen="False": while the popup is open the toggle must not take the
/// click that closes it, or the same click opens it again.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}
