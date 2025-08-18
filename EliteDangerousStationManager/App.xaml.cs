using EliteDangerousStationManager.Helpers;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EliteDangerousStationManager
{
    public partial class App : Application
    {
        // ✅ This will store the current DB mode globally ("Server" or "Local")
        public static string CurrentDbMode { get; set; } = "Local";


        public App()
        {
            // ✅ Hook all global exception handlers
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnTaskSchedulerException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                string configPath = ConfigHelper.GetSettingsFilePath();

                // ✅ Auto-create config file on first install
                if (!File.Exists(configPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath)
                        ?? AppDomain.CurrentDomain.BaseDirectory);

                    File.WriteAllLines(configPath, new[]
                    {
                        "",             // ✅ Line 1: API Key placeholder
                        "#FFFF6B35",    // ✅ Line 2: Default highlight color
                        "false"         // ✅ Line 3: Public mode off
                    });

                    Logger.Log("Created default settings.config for first install.", "Info");
                    CurrentDbMode = "Local";
                }
                else
                {
                    // ✅ Read config file if it exists
                    var lines = File.ReadAllLines(configPath);
                    if (lines.Length > 2 && lines[2].Trim().ToLower() == "true")
                    {
                        CurrentDbMode = "Server";
                    }
                    else
                    {
                        CurrentDbMode = "Local";
                    }
                }

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                LogCrash("Startup Exception", ex);
                MessageBox.Show($"Startup error:\n{ex}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
            }
        }
        // 🔵 UI thread exceptions
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("UI Thread Exception", e.Exception);

            MessageBox.Show(
                $"An unexpected UI error occurred. A crash report was saved.\n\n{e.Exception.Message}",
                "Unhandled Exception",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true; // prevent app from instantly closing
        }

        // 🔵 Non-UI exceptions (background threads, etc.)
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogCrash("Non-UI Thread Exception", ex);
                MessageBox.Show(
                    $"A critical error occurred. A crash report was saved.\n\n{ex.Message}",
                    "Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // 🔵 Task-based async exceptions
        private void OnTaskSchedulerException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("Task Exception", e.Exception);
            e.SetObserved(); // prevent process teardown
        }

        // ✅ Core Crash Logger
        private void LogCrash(string type, Exception ex)
        {
            try
            {
                // Create crash folder
                string crashDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CrashReports");
                Directory.CreateDirectory(crashDir);

                // Create a timestamped file
                string filePath = Path.Combine(crashDir, $"Crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

                using (StreamWriter sw = new StreamWriter(filePath))
                {
                    sw.WriteLine($"==== {type} ====");
                    sw.WriteLine($"Time: {DateTime.Now}");
                    sw.WriteLine($"App Version: {Assembly.GetExecutingAssembly().GetName().Version}");
                    sw.WriteLine($"Message: {ex.Message}");
                    sw.WriteLine($"Source: {ex.Source}");
                    sw.WriteLine($"Stack Trace:\n{ex.StackTrace}");

                    if (ex.InnerException != null)
                    {
                        sw.WriteLine("\n-- Inner Exception --");
                        sw.WriteLine($"Message: {ex.InnerException.Message}");
                        sw.WriteLine($"Stack Trace:\n{ex.InnerException.StackTrace}");
                    }
                }

                Logger.Log($"Crash report saved: {filePath}", "Error");
            }
            catch
            {
                // Fail silently if crash log writing fails (no infinite loop)
            }
        }
    }
}
