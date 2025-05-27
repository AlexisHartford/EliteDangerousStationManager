using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using EliteDangerousStationManager.Models;

namespace EliteDangerousStationManager.Converters
{
    public class ProjectSelectTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not ObservableCollection<Project> selected || parameter is not Project current)
                return "SELECT";

            return selected.Contains(current) ? "DESELECT" : "SELECT";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
