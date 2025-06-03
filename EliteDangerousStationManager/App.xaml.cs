using EliteDangerousStationManager.Helpers;
using System;
using System.Windows;
using System.Windows.Threading;

namespace EliteDangerousStationManager
{
    public partial class App : Application
    {
        public App()
        {
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "Unhandled UI Exception",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true; // Prevent crash
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            string message = (e.ExceptionObject is Exception ex)
                ? $"A critical error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}"
                : "A non-specific fatal error occurred.";

            MessageBox.Show(
                message,
                "Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Optional: log to file
            Logger.Log("Fatal error - application terminated", "Error");
        }
    }
}
