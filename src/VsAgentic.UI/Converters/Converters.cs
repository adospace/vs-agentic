using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is OutputItemStatus status ? status switch
        {
            OutputItemStatus.Pending => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            OutputItemStatus.Success => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
            OutputItemStatus.Error => new SolidColorBrush(Color.FromRgb(244, 71, 71)),
            OutputItemStatus.Info => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            _ => new SolidColorBrush(Color.FromRgb(128, 128, 128))
        } : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StatusToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is OutputItemStatus status ? status switch
        {
            OutputItemStatus.Pending => "\u25cb",
            OutputItemStatus.Success => "\u25cf",
            OutputItemStatus.Error => "\u25cf",
            OutputItemStatus.Info => "\u25cb",
            _ => "\u25cb"
        } : "\u25cb";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
