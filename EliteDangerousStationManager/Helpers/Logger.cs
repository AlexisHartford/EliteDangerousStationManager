using System;
using System.Collections.ObjectModel;
using System.IO;
using EliteDangerousStationManager.Models;

namespace EliteDangerousStationManager.Helpers
{
    public static class Logger
    {
        public static ObservableCollection<LogEntry> Entries { get; } = new();

        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EDStationManager"
        );

        private static readonly string LogFilePath = Path.Combine(LogDirectory, "log.txt");

        static Logger()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    Directory.CreateDirectory(LogDirectory);
            }
            catch
            {
                // If even directory creation fails, fallback to current folder
                LogDirectory = AppDomain.CurrentDomain.BaseDirectory;
                LogFilePath = Path.Combine(LogDirectory, "log.txt");
            }
        }

        public static void Log(string message, string type = "Info")
        {
            var timestamp = DateTime.Now;
            var entry = new LogEntry
            {
                Timestamp = timestamp,
                Message = message,
                Type = type
            };

            App.Current?.Dispatcher?.Invoke(() => Entries.Add(entry));

            try
            {
                if (!Directory.Exists(LogDirectory))
                    Directory.CreateDirectory(LogDirectory);

                File.AppendAllText(LogFilePath, $"[{timestamp:yyyy-MM-dd HH:mm:ss}] [{type}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Fail silently if logging to file fails
            }
        }
    }
}