using System;
using System.Collections.Generic;
using System.Windows;

namespace EliteDangerousStationManager
{
    public partial class SelectCommanderWindow : Window
    {
        public string SelectedCommander { get; private set; }

        public SelectCommanderWindow(List<string> commanders)
        {
            InitializeComponent();
            CommanderComboBox.ItemsSource = commanders;
            CommanderComboBox.SelectedIndex = 0;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            SelectedCommander = CommanderComboBox.SelectedItem as string;
            DialogResult = true;
            Close();
        }
    }
}
