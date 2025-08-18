using EliteDangerousStationManager.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ColonizationPlanner
{
    /// <summary>
    /// Interaction logic for AllStructuresWindow.xaml
    /// </summary>
    /// 

    public partial class AllStructuresWindow : Window
    {
        public AllStructuresWindow(List<ColonyStructure> structures)
        {
            InitializeComponent();
            StructureDataGrid.ItemsSource = structures;
            StructureDataGrid.LoadingRow += StructureDataGrid_LoadingRow;
        }

        private void StructureDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            var item = e.Row.Item as ColonyStructure;
            if (item == null) return;

            ApplyColor(e.Row, 3, Parse(item.T2));  // T2
            ApplyColor(e.Row, 4, Parse(item.T3));  // T3
            ApplyColor(e.Row, 5, item.Security);
            ApplyColor(e.Row, 6, item.TechLevel);
            ApplyColor(e.Row, 7, item.Wealth);
            ApplyColor(e.Row, 8, item.StandardOfLiving);
            ApplyColor(e.Row, 9, item.DevelopmentLevel);
        }

        private int Parse(string s)
        {
            return int.TryParse(s, out int val) ? val : 0;
        }

        private void ApplyColor(DataGridRow row, int columnIndex, int value)
        {
            var brush = GetBrush(value);

            if (StructureDataGrid.Columns[columnIndex].GetCellContent(row) is TextBlock tb)
            {
                tb.Background = brush;
                tb.Foreground = Brushes.White;
            }
        }

        private Brush GetBrush(int v)
        {
            return v switch
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
    }
}
