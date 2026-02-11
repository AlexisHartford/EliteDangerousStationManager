using System.Threading;

namespace EliteDangerousStationManager.Helpers
{
    public static class DbStartupLogger
    {
        private static int _didStartup; // 0 = not yet, 1 = logged
        public static void LogStartupPrimaryOnce()
        {
            if (Interlocked.Exchange(ref _didStartup, 1) == 0)
                Logger.Log("Database: Primary", "Info");
        }

        public static void LogStartupFallbackOnce()
        {
            if (Interlocked.Exchange(ref _didStartup, 1) == 0)
                Logger.Log("Database: Fallback (ON FAILOVER)", "Warning");
        }
    }
}
