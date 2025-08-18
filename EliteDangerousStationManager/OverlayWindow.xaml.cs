using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EliteDangerousStationManager.Helpers; // <-- needed for MonitorUtil + ConfigHelper

namespace EliteDangerousStationManager
{
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();

            // If you don't have Loaded="Window_Loaded" in XAML, keep this:
            Loaded += Window_Loaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

            // NEW: position according to Settings (screen + corner)
            PositionOverlayFromSettings();
        }

        // Make this public so SettingsWindow can call it right after Save (optional)
        public void PositionOverlayFromSettings()
        {
            // Defaults
            int screenIndex = 0;
            string corner = "TopRight";

            // Read config
            try
            {
                var path = ConfigHelper.GetSettingsFilePath();
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > 3 && int.TryParse(lines[3], out var idx))
                        screenIndex = Math.Max(0, idx);
                    if (lines.Length > 4 && !string.IsNullOrWhiteSpace(lines[4]))
                        corner = lines[4].Trim();
                }
            }
            catch { /* ignore; fall back to defaults */ }

            // Get monitor workarea in **DIPs**
            var monitors = MonitorUtil.GetMonitorsDIP(this);
            if (monitors.Count == 0) return;
            if (screenIndex < 0 || screenIndex >= monitors.Count) screenIndex = 0;

            var wa = monitors[screenIndex].WorkAreaDip; // already DPI-correct
            UpdateLayout(); // ensure ActualWidth is valid

            // Only TopLeft / TopRight per your request
            if (corner.Equals("TopLeft", StringComparison.OrdinalIgnoreCase))
            {
                Left = wa.Left;
                Top = wa.Top;
            }
            else // TopRight (default)
            {
                Left = wa.Right - ActualWidth;
                Top = wa.Top;
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
