using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ColonizationPlanner
{
    public class StatColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return Brushes.Transparent;

            int statValue = 0;

            if (value is int i)
                statValue = i;
            else if (value is string s && int.TryParse(s, out int parsed))
                statValue = parsed;

            return statValue switch
            {
                <= -4 => new SolidColorBrush(Color.FromRgb(128, 0, 0)),
                -3 => new SolidColorBrush(Color.FromRgb(160, 30, 30)),
                -2 => new SolidColorBrush(Color.FromRgb(190, 60, 60)),
                -1 => new SolidColorBrush(Color.FromRgb(220, 90, 90)),
                0 => new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                1 => new SolidColorBrush(Color.FromRgb(50, 100, 50)),
                2 => new SolidColorBrush(Color.FromRgb(60, 140, 60)),
                3 => new SolidColorBrush(Color.FromRgb(70, 170, 70)),
                4 => new SolidColorBrush(Color.FromRgb(90, 200, 90)),
                _ => new SolidColorBrush(Color.FromRgb(110, 230, 110))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
