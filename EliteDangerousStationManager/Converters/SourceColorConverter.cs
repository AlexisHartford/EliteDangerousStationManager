using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EliteDangerousStationManager.Converters
{
    public class SourceColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string source)
            {
                return source.Equals("Local", StringComparison.OrdinalIgnoreCase)
                    ? Brushes.LimeGreen
                    : Brushes.DeepSkyBlue;
            }
            return Brushes.Gray; // fallback color
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
