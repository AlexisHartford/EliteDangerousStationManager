using System;
using System.Collections.ObjectModel;
using System.IO;
using EliteDangerousStationManager.Models;

namespace EliteDangerousStationManager.Helpers
{
    public static class Logger
    {
        public static ObservableCollection<LogEntry> Entries { get; } = new();

        private static string LogDirectory;
        private static string LogFilePath;

        static Logger()
        {
            try
            {
                LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EDStationManager"
            );


                if (!Directory.Exists(LogDirectory))
                    Directory.CreateDirectory(LogDirectory);
            }
            catch
            {
                LogDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }

            LogFilePath = Path.Combine(LogDirectory, "log.txt");
            ArchiveOldLog();
        }

        private static void ArchiveOldLog()
        {
            try
            {
                // Rotate current log
                if (File.Exists(LogFilePath))
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string archivePath = Path.Combine(LogDirectory, $"log_{timestamp}.txt");
                    File.Move(LogFilePath, archivePath);
                }

                // Create a new empty log file
                File.WriteAllText(LogFilePath, "");

                // Delete old logs, keep only the 5 most recent
                var logFiles = Directory.GetFiles(LogDirectory, "log_*.txt")
                                        .OrderByDescending(f => File.GetCreationTime(f))
                                        .Skip(5)
                                        .ToList();

                foreach (var file in logFiles)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Fail silently if log rotation or cleanup fails
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
                File.AppendAllText(LogFilePath, $"[{timestamp:yyyy-MM-dd HH:mm:ss}] [{type}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Fail silently if logging to file fails
            }
        }
    }
}
