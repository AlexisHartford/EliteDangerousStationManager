using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EliteDangerousStationManager.Models;
using Brush = System.Windows.Media.Brush;

namespace EliteDangerousStationManager.Converters
{
    public class ProjectSelectedToBrushConverter : IMultiValueConverter
    {
        private static readonly Brush HighlightBrush = new SolidColorBrush(Colors.LimeGreen); // ⬅️ Choose your color
        private static readonly Brush DefaultBrush = new SolidColorBrush(Colors.White);

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2 &&
                values[0] is ObservableCollection<Project> selected &&
                values[1] is Project project)
            {
                return selected.Contains(project) ? HighlightBrush : DefaultBrush;
            }

            return DefaultBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
