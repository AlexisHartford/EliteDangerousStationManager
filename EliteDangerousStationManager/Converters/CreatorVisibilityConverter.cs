using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EliteDangerousStationManager.Converters
{
    public class CreatorVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] == null || values[1] == null)
                return Visibility.Collapsed;

            string commanderName = values[0].ToString();
            string createdBy = values[1].ToString();

            return string.Equals(commanderName, createdBy, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
