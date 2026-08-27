using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Shows an element only once its container is at least
/// <c>ConverterParameter</c> pixels wide.
///
/// A chat tool window is routinely docked at 300px and just as routinely
/// floated at 1200px, so the header cannot assume room for everything. Rather
/// than let the gauges squeeze the title into an ellipsis, each one names the
/// width it needs and drops out below it — widest-to-narrowest, so what
/// survives at the smallest size is what matters most.
/// </summary>
public sealed class MinWidthVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width))
            return Visibility.Visible;

        // During the first layout pass ActualWidth is 0; treating that as "too
        // narrow" would collapse everything and, because collapsed children
        // have no desired size, keep it collapsed.
        if (width <= 0)
            return Visibility.Visible;

        return width >= Threshold(parameter) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double Threshold(object parameter) => parameter switch
    {
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0d,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
