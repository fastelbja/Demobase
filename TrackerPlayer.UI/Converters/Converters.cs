using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrackerPlayer.UI.Converters
{
    /// <summary>
    /// Convertit un bool en Visibility.
    /// ConverterParameter="invert" → inverse la logique.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value is bool b && b;
            bool invert = parameter?.ToString()?.ToLower() == "invert";
            return (v ^ invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility vis && vis == Visibility.Visible;
    }

    /// <summary>
    /// Convertit des secondes (double) en chaîne MM:SS.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class SecondsToTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double secs) return "00:00";
            var ts = TimeSpan.FromSeconds(Math.Max(0, secs));
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    /// <summary>
    /// Retourne Visible si la string est non-vide.
    /// </summary>
    [ValueConversion(typeof(string), typeof(Visibility))]
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}
