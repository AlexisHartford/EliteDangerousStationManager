using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;      // Orientation (WPF)
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;          // IValueConverter, Binding

namespace EliteDangerousStationManager.Converters
{
    // Horizontal when width >= Threshold, Vertical when < Threshold
    public class WidthToOrientationConverter : IValueConverter
    {
        public double Threshold { get; set; } = 520;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = (value is double d) ? d : 0;
            double th = Threshold;

            if (parameter != null && double.TryParse(parameter.ToString(), out var p))
                th = p;

            return width < th ? Orientation.Vertical : Orientation.Horizontal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    // Show/Collapse based on width vs threshold
    public class WidthToVisibilityConverter : IValueConverter
    {
        public double Threshold { get; set; } = 520;
        public bool CollapseWhenLessThan { get; set; } = true;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = (value is double d) ? d : 0;
            double th = Threshold;

            if (parameter != null && double.TryParse(parameter.ToString(), out var p))
                th = p;

            bool isLess = width < th;
            bool collapse = CollapseWhenLessThan ? isLess : !isLess;
            return collapse ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
