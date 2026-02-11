using EliteDangerousStationManager.Helpers; // ConfigHelper, MonitorUtil
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EliteDangerousStationManager
{
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
            // If you don't have Loaded="Window_Loaded" in XAML, this keeps it wired up:
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Make this a tool/no-activate window so it floats without stealing focus
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

            // Position based on settings
            PositionOverlayFromSettings();
        }

        /// <summary>
        /// Positions the overlay according to settings:
        ///   line 4: screen index (0-based)
        ///   line 5: corner ("TopLeft" or "TopRight")
        /// Falls back to TopRight on screen 0 if unset/invalid.
        /// </summary>
        public void PositionOverlayFromSettings()
        {
            int screenIndex = 0;
            string corner = "TopRight";

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
            catch
            {
                // ignore and use defaults
            }

            // Get monitor workareas in DIPs (MonitorUtil should already do DPI conversion)
            var monitors = MonitorUtil.GetMonitorsDIP(this);
            if (monitors.Count == 0) return;
            if (screenIndex < 0 || screenIndex >= monitors.Count) screenIndex = 0;

            var wa = monitors[screenIndex].WorkAreaDip;

            // Ensure layout is measured so ActualWidth/Height are valid
            UpdateLayout();

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

        // ---------------- Win32 interop ----------------

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
