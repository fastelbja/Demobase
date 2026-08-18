using System.Globalization;
using System.Windows.Data;

namespace DemoBase.App.ViewModels;

/// <summary>Inverse un booléen (true → false). Utilisé pour IsEnabled="{Binding IsBusy, Converter=...}".</summary>
public class InverseBooleanConverter : IValueConverter
{
    public static readonly InverseBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
