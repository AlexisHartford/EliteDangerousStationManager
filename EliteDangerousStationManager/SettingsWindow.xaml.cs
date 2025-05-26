using System;
using System.IO;
using System.Windows;

namespace EliteDangerousStationManager
{
    public partial class SettingsWindow : Window
    {
        private const string ConfigFile = "settings.config";

        public string InaraApiKey { get; private set; }

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (File.Exists(ConfigFile))
            {
                var key = File.ReadAllText(ConfigFile);
                ApiKeyTextBox.Text = key;
            }
        }

        private void SaveSettings()
        {
            File.WriteAllText(ConfigFile, ApiKeyTextBox.Text.Trim());
            InaraApiKey = ApiKeyTextBox.Text.Trim();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}