using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EliteDangerousStationManager   // 👈 must match the namespace in your XAML "local"
{
    public sealed class StringEqualsToVisibilityConverter : IMultiValueConverter
    {
        public bool CaseInsensitive { get; set; } = true;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
                return Visibility.Collapsed;

            var left = values[0]?.ToString()?.Trim() ?? string.Empty;
            var right = values[1]?.ToString()?.Trim() ?? string.Empty;

            var comparison = CaseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(left, right, comparison)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
