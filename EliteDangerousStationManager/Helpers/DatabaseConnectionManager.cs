using System;
using System.Configuration;
using MySql.Data.MySqlClient;

namespace EliteDangerousStationManager.Helpers
{
    public static class DatabaseConnectionManager
    {
        private static bool PrimaryDbDisabledForSession = false;
        private static string _activeConnStr = null;

        /// <summary>
        /// Always returns a **NEW** MySqlConnection (caller must dispose with using).
        /// Handles Primary → Fallback once per session.
        /// </summary>
        public static MySqlConnection GetOpenConnection()
        {
            string primary = ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
            string fallback = ConfigurationManager.ConnectionStrings["FallbackDB"]?.ConnectionString;

            // ✅ Already decided fallback
            if (PrimaryDbDisabledForSession && !string.IsNullOrEmpty(_activeConnStr))
            {
                var conn = new MySqlConnection(_activeConnStr);
                conn.Open();
                return conn;
            }

            // ✅ First try Primary
            if (!PrimaryDbDisabledForSession && !string.IsNullOrWhiteSpace(primary))
            {
                try
                {
                    var conn = new MySqlConnection(primary);
                    conn.Open();
                    _activeConnStr = primary;
                    Logger.Log("✅ Connected to PrimaryDB (Public IP).", "Info");
                    return conn;
                }
                catch
                {
                    Logger.Log("📡 JournalProcessor: PrimaryDB unreachable → using FallbackDB.", "Warning");
                    PrimaryDbDisabledForSession = true;
                }
            }

            // ✅ Use Fallback
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var conn = new MySqlConnection(fallback);
                conn.Open();
                _activeConnStr = fallback;
                Logger.Log("✅ Connected to FallbackDB (LAN IP).", "Info");
                return conn;
            }

            throw new Exception("❌ No DB connection available (Primary/Fallback failed).");
        }
    }
}
