using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EliteDangerousStationManager.Models;

namespace EliteDangerousStationManager.Converters
{
    public class ProjectGlowEffectConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                values[0] is ObservableCollection<Project> selected &&
                values[1] is Project current)
            {
                if (selected.Contains(current))
                {
                    return new DropShadowEffect
                    {
                        Color = Colors.Orange,
                        BlurRadius = 30,
                        ShadowDepth = 0,
                        Direction = 0,
                        Opacity = 1.0,
                        RenderingBias = RenderingBias.Performance
                    };

                }
            }

            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
