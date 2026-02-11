using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Services;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

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
            // Unobserved background task exceptions
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { Logger.Log($"[UnobservedTaskException] {e.Exception}", "Error"); }
                catch { /* last-chance */ }
                e.SetObserved(); // prevents process crash
            };

            // UI thread exceptions
            this.DispatcherUnhandledException += (s, e) =>
            {
                Logger.Log($"[DispatcherUnhandledException] {e.Exception}", "Error");
                e.Handled = true;
            };

            // Non-UI / final fallback
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Logger.Log($"[UnhandledException] {e.ExceptionObject}", "Error");
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                // 1) Global handlers FIRST — before any background tasks/timers can start
                TaskScheduler.UnobservedTaskException += (s, evt) =>
                {
                    evt.SetObserved();
                    Logger.Log($"[UnobservedTaskException] {evt.Exception}", "Error");
                };

                AppDomain.CurrentDomain.UnhandledException += (s, evt) =>
                {
                    Logger.Log($"[UnhandledException] {evt.ExceptionObject}", "Error");
                };

                // WPF UI thread exceptions (prevents process-terminating crashes from async void handlers, etc.)
                this.DispatcherUnhandledException += (s, evt) =>
                {
                    Logger.Log($"[DispatcherUnhandledException] {evt.Exception}", "Error");
                    evt.Handled = true; // keep app alive; you can choose otherwise
                };

                // 2) Config & mode selection
                string configPath = ConfigHelper.GetSettingsFilePath();
                if (!File.Exists(configPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath)
                        ?? AppDomain.CurrentDomain.BaseDirectory);

                    File.WriteAllLines(configPath, new[]
                    {
                "",             // Line 1: API Key placeholder
                "#FFFF6B35",    // Line 2: Default highlight color
                "false"         // Line 3: Public/Server mode off
            });

                    Logger.Log("Created default settings.config for first install.", "Info");
                    CurrentDbMode = "Local";
                }
                else
                {
                    var lines = File.ReadAllLines(configPath);
                    CurrentDbMode = (lines.Length > 2 && lines[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                        ? "Server"
                        : "Local";
                }

                    var primary = System.Configuration.ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
                    var fallback = System.Configuration.ConfigurationManager.ConnectionStrings["FallbackDB"]?.ConnectionString;

                    if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(fallback))
                    {
                        Logger.Log("No PrimaryDB/FallbackDB connection strings found; staying in Local mode for this session.", "Warning");
                        CurrentDbMode = "Local";
                    }
                    else
                    {
                        DbConnectionManager.Initialize(primary, fallback);

                        // One-time startup log of DB state
                        var mgr = DbConnectionManager.Instance;
                        Logger.Log(
                            mgr.OnFailover ? "Database: Fallback (ON FAILOVER)" : "Database: Primary",
                            mgr.OnFailover ? "Warning" : "Info"
                        );
                    }
                

                // 4) Let WPF create the main window AFTER handlers + (optional) DB init
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
