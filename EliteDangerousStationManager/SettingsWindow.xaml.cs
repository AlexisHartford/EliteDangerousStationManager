using EliteDangerousStationManager.Helpers;
using System;
using System.IO;
using System.Linq; // for OfType<>
using System.Windows;
using Application = System.Windows.Application;

namespace EliteDangerousStationManager
{
    public partial class SettingsWindow : Window
    {
        string configPath = ConfigHelper.GetSettingsFilePath();

        public string InaraApiKey { get; private set; }
        public string HighlightColor => ColorInput.Text.Trim();

        // new
        public int OverlayScreenIndex { get; private set; } = 0;
        public string OverlayCorner { get; private set; } = "TopRight";

        public SettingsWindow()
        {
            InitializeComponent();
            PopulateMonitors();
            LoadSettings();
        }

        private void PopulateMonitors()
        {
            OverlayScreenCombo.Items.Clear();
            var mons = MonitorUtil.GetMonitorsDIP(this);
            if (mons.Count == 0)
            {
                OverlayScreenCombo.Items.Add("Screen 0 (Primary)");
                OverlayScreenCombo.SelectedIndex = 0;
                return;
            }

            for (int i = 0; i < mons.Count; i++)
            {
                var m = mons[i];
                var b = m.BoundsDip;
                var label = m.Primary
                    ? $"Screen {i} (Primary) — {Math.Round(b.Width)}x{Math.Round(b.Height)}"
                    : $"Screen {i} — {Math.Round(b.Width)}x{Math.Round(b.Height)}";
                OverlayScreenCombo.Items.Add(label);
            }

            var primaryIdx = mons.FindIndex(m => m.Primary);
            OverlayScreenCombo.SelectedIndex = primaryIdx >= 0 ? primaryIdx : 0;
        }

        private void LoadSettings()
        {
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                if (lines.Length > 0) ApiKeyTextBox.Text = lines[0];
                if (lines.Length > 1) ColorInput.Text = lines[1];
                if (lines.Length > 2)
                {
                    bool isPublic = lines[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    PublicCheckBox.IsChecked = isPublic;
                }
                else
                {
                    PublicCheckBox.IsChecked = true;
                }

                // new (back-compat safe)
                if (lines.Length > 3 && int.TryParse(lines[3], out var idx)) OverlayScreenIndex = Math.Max(0, idx);
                if (lines.Length > 4 && !string.IsNullOrWhiteSpace(lines[4])) OverlayCorner = lines[4].Trim();

                if (OverlayScreenIndex >= 0 && OverlayScreenIndex < OverlayScreenCombo.Items.Count)
                    OverlayScreenCombo.SelectedIndex = OverlayScreenIndex;
                OverlayCornerCombo.SelectedIndex = OverlayCorner.Equals("TopLeft", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }
            else
            {
                PublicCheckBox.IsChecked = true;
                // screen/corner already defaulted by PopulateMonitors + defaults
            }
        }

        private void SaveSettings()
        {
            // read new choices
            OverlayScreenIndex = Math.Max(0, OverlayScreenCombo.SelectedIndex);
            OverlayCorner = ((OverlayCornerCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                              ?? "TopRight").Trim();

            // write (extend to 5 lines)
            var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new();
            while (lines.Count < 5) lines.Add("");
            lines[0] = ApiKeyTextBox.Text.Trim();
            lines[1] = ColorInput.Text.Trim();
            lines[2] = PublicCheckBox.IsChecked == true ? "true" : "false";
            lines[3] = OverlayScreenIndex.ToString();
            lines[4] = OverlayCorner;
            File.WriteAllLines(configPath, lines);

            InaraApiKey = ApiKeyTextBox.Text.Trim();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();

            App.CurrentDbMode = PublicCheckBox.IsChecked == true ? "Server" : "Local";

            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.LoadProjects();

                // if overlay is open, reposition it now (no OverlayManager access needed)
                var ow = Application.Current.Windows.OfType<OverlayWindow>().FirstOrDefault();
                ow?.PositionOverlayFromSettings(); // make this method public in OverlayWindow.xaml.cs (see next step)
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
        private void PublicCheckBox_Checked(object s, RoutedEventArgs e) { }
        private void PublicCheckBox_Unchecked(object s, RoutedEventArgs e) { }
    }
}
