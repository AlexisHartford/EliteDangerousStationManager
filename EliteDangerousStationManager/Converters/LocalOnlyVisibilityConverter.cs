using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EliteDangerousStationManager.Converters
{
    public class LocalOnlyVisibilityConverter : IValueConverter
    {
        // Convert the Source string ("Local" or "Server") into Visibility
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string source && source.Equals("Local", StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        // We don’t need to convert back for this use case
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
