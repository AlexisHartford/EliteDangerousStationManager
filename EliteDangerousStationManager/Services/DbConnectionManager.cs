using EliteDangerousStationManager.Helpers;
using MySql.Data.MySqlClient;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EliteDangerousStationManager.Services
{
    public sealed class DbConnectionManager : IDisposable
    {
        private static readonly object _gate = new();
        private static DbConnectionManager _instance;
        public static DbConnectionManager Instance => _instance ?? throw new InvalidOperationException("DbConnectionManager not initialized.");
        public static void Initialize(string primaryConnStr, string fallbackConnStr)
        {
            lock (_gate)
            {
                if (_instance != null) return;
                _instance = new DbConnectionManager(primaryConnStr, fallbackConnStr);
            }
        }

        // Primary / Fallback strings
        private readonly string _primary;
        private readonly string _fallback;

        // Active points to either primary or fallback
        private volatile string _active;
        private volatile bool _onFailover;
        private readonly object _switchLock = new();

        // DbConnectionManager.cs (fields)
        private volatile bool _stickToFallbackUntilRestart = false;



        // Probe primary at most every X seconds while on failover
        private DateTime _nextPrimaryProbeUtc = DateTime.MinValue;
        private static readonly TimeSpan PrimaryProbeInterval = TimeSpan.FromSeconds(20);

        public bool OnFailover => _onFailover;
        public event Action<bool> FailoverStateChanged; // true = on failover

        private DbConnectionManager(string primary, string fallback)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _fallback = fallback; // can be null/empty
            _active = _primary;
        }
        public class DbStatusViewModel : INotifyPropertyChanged
        {
            private string _statusText = "Database Connected";
            private string _statusColor = "White";

            public string StatusText
            {
                get => _statusText;
                set { _statusText = value; OnPropertyChanged(); }
            }

            public string StatusColor
            {
                get => _statusColor;
                set { _statusColor = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ----------------------------
        // Public pooled Execute (sync)
        // ----------------------------
        public T Execute<T>(Func<MySqlCommand, T> work, string sqlPreview = null)
        {
            return TryExecuteWithFailover(() =>
            {
                using var conn = new MySqlConnection(_active);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = conn.ConnectionTimeout;
                return work(cmd);
            }, sqlPreview);
        }

        // ----------------------------
        // Public pooled Execute (async)
        // ----------------------------
        public async Task<T> ExecuteAsync<T>(Func<MySqlCommand, Task<T>> work, string sqlPreview = null)
        {
            return await TryExecuteWithFailoverAsync(async () =>
            {
                using var conn = new MySqlConnection(_active);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = conn.ConnectionTimeout;
                return await work(cmd).ConfigureAwait(false);
            }, sqlPreview).ConfigureAwait(false);
        }

        // ----------------------------
        // Static helper for ad-hoc conn (e.g., CanConnect)
        // ----------------------------
        public static T Execute<T>(string connString, Func<MySqlConnection, T> action, string description = null)
        {
            using var conn = new MySqlConnection(connString);
            conn.Open();
            return action(conn);
        }

        // ============================
        // Failover logic (sync/async)
        // ============================
        private T TryExecuteWithFailover<T>(Func<T> attempt, string sqlPreview)
        {
            try
            {
                var result = attempt();
                MaybeProbePrimary();
                return result;
            }
            catch (Exception firstEx)
            {
                // Try fallback if we were on primary and fallback exists
                if (CanSwitchToFallback())
                {
                    SwitchToFallback(firstEx.Message, sqlPreview);
                    // retry once on fallback
                    return attempt();
                }

                // Already on fallback or no fallback configured -> bubble
                LogDbFail(firstEx, sqlPreview);
                throw;
            }
        }

        private async Task<T> TryExecuteWithFailoverAsync<T>(Func<Task<T>> attempt, string sqlPreview)
        {
            try
            {
                var result = await attempt().ConfigureAwait(false);
                MaybeProbePrimary();
                return result;
            }
            catch (Exception firstEx)
            {
                if (CanSwitchToFallback())
                {
                    SwitchToFallback(firstEx.Message, sqlPreview);
                    return await attempt().ConfigureAwait(false);
                }

                LogDbFail(firstEx, sqlPreview);
                throw;
            }
        }

        private bool CanSwitchToFallback()
            => !string.IsNullOrWhiteSpace(_fallback) && string.Equals(_active, _primary, StringComparison.Ordinal);

        private void SwitchToFallback(string reason, string sqlPreview)
        {
            lock (_switchLock)
            {
                if (!CanSwitchToFallback()) return; // race guard

                _active = _fallback;
                if (!_onFailover)
                {
                    _onFailover = true;
                    FailoverStateChanged?.Invoke(true);
                    StickToFallbackUntilRestart();
                }
                Logger.Log($"PrimaryDB failed ({Trim(reason)}). Switched to FallbackDB **(ON FAILOVER)**. SQL: {sqlPreview ?? "(n/a)"}", "Warning");
                // schedule next primary probe
                _nextPrimaryProbeUtc = DateTime.UtcNow + PrimaryProbeInterval;
            }
        }

        // Optional public API so you can toggle from App startup or a settings UI
        public void StickToFallbackUntilRestart(bool enable = true)
        {
            _stickToFallbackUntilRestart = enable;
        }

        private void MaybeProbePrimary()
        {
            if (!_onFailover) return;

            // If we intend to stick to fallback for this run, don't probe at all.
            if (_stickToFallbackUntilRestart) return;

            if (DateTime.UtcNow < _nextPrimaryProbeUtc) return;

            Task.Run(() =>
            {
                try
                {
                    using var test = new MySqlConnection(_primary);
                    test.Open(); // success -> restore primary (since we are NOT sticking)

                    lock (_switchLock)
                    {
                        // Only restore if still on failover and not sticking
                        if (_onFailover && !_stickToFallbackUntilRestart)
                        {
                            _active = _primary;
                            _onFailover = false;
                            FailoverStateChanged?.Invoke(false);
                            Logger.Log("✅ PrimaryDB restored; leaving failover.", "Info");
                        }
                    }
                }
                catch
                {
                    // still down; push next probe
                    _nextPrimaryProbeUtc = DateTime.UtcNow + PrimaryProbeInterval;
                }
            });
        }

        private static void LogDbFail(Exception ex, string sqlPreview)
        {
            Logger.Log($"[DB][FAIL] op={sqlPreview ?? "(n/a)"}: {ex.GetType().Name}: {ex.Message}", "Warning");
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length > 180 ? s.Substring(0, 180) + "…" : s;
        }

        public void Dispose()
        {
            // Nothing to dispose: pooled short-lived connections only.
        }
    }
}
