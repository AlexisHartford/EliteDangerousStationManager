using System;
using System.IO;
using System.Windows;

namespace EliteDangerousStationManager.Helpers
{
    public static class ConfigHelper
    {
        public static string GetSettingsFilePath()
        {
            // Use AppData\Local\EDStationManager
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EDStationManager"
            );

            try
            {
                Directory.CreateDirectory(folder); // Will not throw if it already exists
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to create settings directory:\n{folder}\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                throw;
            }

            return Path.Combine(folder, "settings.config");
        }
    }
}
