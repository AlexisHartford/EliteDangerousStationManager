using EliteDangerousStationManager.Helpers;
using System;
using System.IO;
using System.Windows;

namespace EliteDangerousStationManager
{
    public partial class SettingsWindow : Window
    {
        string configPath = ConfigHelper.GetSettingsFilePath();


        public string InaraApiKey { get; private set; }
        public string HighlightColor => ColorInput.Text.Trim();



        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                if (lines.Length > 0) ApiKeyTextBox.Text = lines[0];
                if (lines.Length > 1) ColorInput.Text = lines[1]; // highlight color
            }
        }

        private void SaveSettings()
        {
            File.WriteAllLines(configPath, new[]
            {
                ApiKeyTextBox.Text.Trim(),
                ColorInput.Text.Trim()
            });

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