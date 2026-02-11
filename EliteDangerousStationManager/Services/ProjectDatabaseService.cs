using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Common;
using System.IO;

namespace EliteDangerousStationManager.Services
{
    public class ProjectDatabaseService
    {
        private sealed record ColumnSpec(string Name, string Type, bool IsPrimaryKey);
        private sealed record TableSpec(string Name, ColumnSpec[] Columns);

        private static readonly TableSpec[] ExpectedSchema =
        {
    new TableSpec("Projects", new[]
    {
        new ColumnSpec("MarketID",   "INTEGER",  true),   // PRIMARY KEY
        new ColumnSpec("SystemName", "TEXT",     false),
        new ColumnSpec("StationName","TEXT",     false),
        new ColumnSpec("CreatedBy",  "TEXT",     false),
        new ColumnSpec("CreatedAt",  "DATETIME", false)
    }),
    new TableSpec("ProjectResources", new[]
    {
        new ColumnSpec("ResourceID",     "INTEGER",  true),  // PRIMARY KEY AUTOINCREMENT is okay: PK flag is what we check
        new ColumnSpec("MarketID",       "INTEGER",  false),
        new ColumnSpec("ResourceName",   "TEXT",     false),
        new ColumnSpec("RequiredAmount", "INTEGER",  false),
        new ColumnSpec("ProvidedAmount", "INTEGER",  false),
        new ColumnSpec("Payment",        "INTEGER",  false)
    }),
    new TableSpec("ProjectsArchive", new[]
    {
        new ColumnSpec("MarketID",   "INTEGER",  false),
        new ColumnSpec("SystemName", "TEXT",     false),
        new ColumnSpec("StationName","TEXT",     false),
        new ColumnSpec("CreatedBy",  "TEXT",     false),
        new ColumnSpec("CreatedAt",  "DATETIME", false),
        new ColumnSpec("ArchivedAt", "DATETIME", false)
    }),
};

        // Mode: "Server" (MySQL) or "Local" (SQLite)
        private readonly string _dbMode;
        private readonly string _sqlitePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "EDStationManager",
                        "stations.db"
                    );

        // MySQL connection strings
        private readonly string _primaryConnStr;
        private readonly string _fallbackConnStr;
        private string _activeMySqlConnStr; // can switch at runtime on failure

        private volatile bool _onFailover;

        public bool OnFailover => _onFailover;

        // Fires with true when entering failover, false when leaving
        public event Action<bool>? FailoverStateChanged;

        public string ConnectionString => _dbMode == "Server"
            ? _activeMySqlConnStr
            : $"Data Source={_sqlitePath}";

        /// <summary>
        /// Raised when we switch between PrimaryDB & FallbackDB at runtime.
        /// Args: (fromConnString, toConnString, reason)
        /// </summary>
        public event Action<string, string, string> ConnectionSwitched;

        public ProjectDatabaseService(string dbMode = "Server")
        {

            EnsureSQLiteSchema();
            _dbMode = dbMode;
            if (_dbMode == "Local")
            {
                EnsureSQLiteSchema();
                //Logger.Log("Using local SQLite database (stations.db).", "Info");
            }
        }

        public int DeleteArchivedProject(long marketId)
        {
            return DbConnectionManager.Instance.Execute(cmd =>
            {
                cmd.CommandText = "DELETE FROM ProjectsArchive WHERE MarketID = @id;";
                var p = cmd.CreateParameter();
                p.ParameterName = "@id";
                p.Value = marketId;
                cmd.Parameters.Add(p);
                return cmd.ExecuteNonQuery();
            }, sqlPreview: "DELETE FROM ProjectsArchive WHERE MarketID=@id");
        }


        // ---------------------
        // Failover core (MySQL)
        // ---------------------

        /// <summary>
        /// Open a MySQL connection to the ACTIVE conn string.
        /// On failure, if fallback exists and wasn't active, switch to it, log, and retry once.
        /// If currently on fallback and we still fail, bubble the error.
        /// </summary>
        private MySqlConnection OpenMySqlWithFailover(string reasonIfSwitchNeeded = "MySQL operation failed")
        {
            // try current
            try
            {
                var conn = new MySqlConnection(_activeMySqlConnStr);
                conn.Open();
                return conn;
            }
            catch (MySqlException firstEx)
            {
                // can we switch?
                bool canSwitch = !string.IsNullOrWhiteSpace(_fallbackConnStr) &&
                                 !string.Equals(_activeMySqlConnStr, _fallbackConnStr, StringComparison.Ordinal);

                if (canSwitch)
                {
                    var from = _activeMySqlConnStr;
                    _activeMySqlConnStr = _fallbackConnStr;

                    // mark failover ON and announce
                    SetFailover(true);
                    ConnectionSwitched?.Invoke(from, _activeMySqlConnStr, reasonIfSwitchNeeded);

                    Logger.Log($"PrimaryDB failed after connect: {firstEx.Message}. Switched to FallbackDB **(ON FAILOVER)**.", "Warning");

                    try
                    {
                        var conn = new MySqlConnection(_activeMySqlConnStr);
                        conn.Open();
                        return conn;
                    }
                    catch (MySqlException secondEx)
                    {
                        // revert and mark failover OFF (since even fallback failed)
                        _activeMySqlConnStr = from;
                        SetFailover(false);

                        throw new InvalidOperationException(
                            $"FallbackDB also failed after switch: {secondEx.Message}", secondEx);
                    }
                }

                // No fallback or already on fallback: bubble up
                throw;
            }
        }
        
        // Optional: call this from a ViewModel to reflect UI
        private void SetFailover(bool value)
        {
            if (_onFailover == value) return;
            _onFailover = value;
            FailoverStateChanged?.Invoke(value);
        }
        private DbConnection GetOpenConnection(string reason)
        {
            if (_dbMode == "Server")
            {
                // returns an *open* MySqlConnection with failover
                return OpenMySqlWithFailover(reason);
            }

            // SQLite: open here and return
            var sqlite = new SqliteConnection(ConnectionString);
            sqlite.Open();
            return sqlite;
        }



        // ---------------------
        // Public API
        // ---------------------

        public Dictionary<string, Dictionary<string, int>> GetLinkedCarrierStockPerCarrier(long marketId)
        {
            var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            if (_dbMode != "Server") return result;

            return DbConnectionManager.Instance.Execute(cmd =>
            {
                cmd.CommandText = @"
            SELECT
                pr.ResourceName,
                fc.CarrierCode        AS CarrierKey,
                COALESCE(fcs.Count,0) AS Cnt
            FROM ProjectCarriers pc
            JOIN FleetCarriers      fc ON fc.id       = pc.CarrierId
            JOIN ProjectResources   pr ON pr.MarketID = pc.MarketID
            LEFT JOIN FleetCarrierStorage fcs
                   ON  fcs.ResourceName = pr.ResourceName
                   AND fcs.CarrierCode  = fc.CarrierCode
            WHERE pr.MarketID = @mid;";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@mid", marketId);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var resource = rdr.GetString("ResourceName");
                    var carrier = rdr.GetString("CarrierKey");
                    var count = rdr.GetInt32("Cnt");

                    if (!result.TryGetValue(resource, out var perCarrier))
                    {
                        perCarrier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        result[resource] = perCarrier;
                    }
                    perCarrier[carrier] = count;
                }

                return result;
            });
        }

        public List<Project> LoadProjects()
        {
            if (_dbMode != "Server") { /* ... your SQLite path ... */ }

            return DbConnectionManager.Instance.Execute(cmd =>
            {
                cmd.CommandText = "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt FROM Projects ORDER BY SystemName;";
                using var rdr = cmd.ExecuteReader();
                var list = new List<Project>();
                while (rdr.Read())
                {
                    list.Add(new Project
                    {
                        MarketId = rdr.GetInt64("MarketID"),
                        SystemName = rdr["SystemName"].ToString(),
                        StationName = rdr["StationName"].ToString(),
                        CreatedBy = rdr["CreatedBy"] == DBNull.Value ? "Unknown" : rdr["CreatedBy"].ToString(),
                        CreatedAt = rdr["CreatedAt"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(rdr["CreatedAt"])
                    });
                }
                return list;
            });
        }

        public void SaveProject(Project project)
        {
            using var conn = GetOpenConnection("SaveProject");
            using var cmd = conn.CreateCommand();
            if (_dbMode == "Server")
            {
                cmd.CommandText = @"
            INSERT INTO Projects (MarketID, SystemName, StationName, CreatedBy)
            VALUES (@mid, @system, @station, @creator)
            ON DUPLICATE KEY UPDATE 
                SystemName=@system, StationName=@station, CreatedBy=@creator;";
            }
            else
            {
                cmd.CommandText = @"
            INSERT OR REPLACE INTO Projects (MarketID, SystemName, StationName, CreatedBy)
            VALUES (@mid, @system, @station, @creator);";
            }
            cmd.AddParam("@mid", project.MarketId);
            cmd.AddParam("@system", project.SystemName);
            cmd.AddParam("@station", project.StationName);
            cmd.AddParam("@creator", project.CreatedBy ?? "Unknown");
            cmd.ExecuteNonQuery();

            Logger.Log($"Saved project {project}", "Success");
        }


        public void ArchiveProject(long marketId)
        {
            if (_dbMode != "Server") { /* your SQLite path */ return; }

            DbConnectionManager.Instance.Execute(cmd =>
            {
                using var tx = cmd.Connection.BeginTransaction();
                cmd.Transaction = tx;

                cmd.CommandText = @"
                INSERT INTO ProjectsArchive (MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt)
                SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, CURRENT_TIMESTAMP
                FROM Projects WHERE MarketID = @id;";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", marketId);
                cmd.ExecuteNonQuery();

                cmd.CommandText = "DELETE FROM Projects WHERE MarketID = @id;";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", marketId);
                cmd.ExecuteNonQuery();

                tx.Commit();
                return 0;
            });
        }

        public List<ArchivedProject> LoadArchivedProjects()
        {
            var projects = new List<ArchivedProject>();
            using var conn = GetOpenConnection("LoadArchivedProjects");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt
                FROM ProjectsArchive
                ORDER BY ArchivedAt DESC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(new ArchivedProject
                {
                    MarketId = Convert.ToInt64(reader["MarketID"]),
                    SystemName = reader["SystemName"].ToString(),
                    StationName = reader["StationName"].ToString(),
                    CreatedBy = reader["CreatedBy"].ToString(),
                    CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                    ArchivedAt = Convert.ToDateTime(reader["ArchivedAt"])
                });
            }

            return projects;
        }

        public void DeleteProject(long marketId)
        {
            using var conn = GetOpenConnection("DeleteProject");
            using var tx = conn.BeginTransaction();

            try
            {
                using (var deleteResources = conn.CreateCommand())
                {
                    deleteResources.CommandText = "DELETE FROM ProjectResources WHERE MarketID = @mid;";
                    deleteResources.AddParam("@mid", marketId);
                    deleteResources.ExecuteNonQuery();
                }

                using (var deleteProject = conn.CreateCommand())
                {
                    deleteProject.CommandText = "DELETE FROM Projects WHERE MarketID = @mid;";
                    deleteProject.AddParam("@mid", marketId);
                    deleteProject.ExecuteNonQuery();
                }

                tx.Commit();
                Logger.Log($"Deleted project and its resources for MarketID {marketId}", "Success");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                Logger.Log($"Error during DeleteProject({marketId}): {ex.Message}", "Error");
                throw;
            }
        }

        // ---------------------
        // SQLite schema
        // ---------------------

        private void EnsureSQLiteSchema()
        {
            var dir = Path.GetDirectoryName(_sqlitePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _sqlitePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };

            bool needsRebuild = !File.Exists(_sqlitePath);

            if (!needsRebuild)
            {
                try
                {
                    using var checkConn = new SqliteConnection(csb.ToString());
                    checkConn.Open();

                    ExecPragma(checkConn, "PRAGMA journal_mode=WAL;");
                    ExecPragma(checkConn, "PRAGMA busy_timeout=5000;");

                    needsRebuild = !SchemaMatches(checkConn, ExpectedSchema);
                }
                catch (Exception ex)
                {
                    Logger.Log($"SQLite open/schema check failed; will rebuild. {ex.Message}", "Warning");
                    needsRebuild = true;
                }
            }

            if (needsRebuild)
            {
                // 1) Try delete-on-disk with retries
                if (TryDeleteSqliteFilesWithRetries(_sqlitePath, maxRetries: 10, delayMs: 300))
                {
                    CreateFreshDatabase(csb);
                    Logger.Log($"SQLite schema (re)created at: {_sqlitePath}", "Info");
                    return;
                }

                // 2) Fallback: rebuild in-place (drop & recreate inside the DB)
                Logger.Log("Delete failed; attempting in-place rebuild (drop & recreate tables).", "Warning");
                if (TryRebuildInPlace(csb))
                {
                    Logger.Log($"SQLite schema rebuilt in-place at: {_sqlitePath}", "Info");
                    return;
                }

                // 3) Still locked: surface a clear message
                throw new IOException(
                    "Could not rebuild SQLite DB. It appears to be locked by another process. " +
                    "Close other instances (or tools indexing the file) and try again.");
            }

            // Schema matched: idempotent ensure
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();
            ExecPragma(conn, "PRAGMA journal_mode=WAL;");
            ExecPragma(conn, "PRAGMA busy_timeout=5000;");

            using var tx = conn.BeginTransaction();

            ExecCreate(conn, tx, @"
CREATE TABLE IF NOT EXISTS Projects (
    MarketID   INTEGER PRIMARY KEY,
    SystemName TEXT,
    StationName TEXT,
    CreatedBy  TEXT,
    CreatedAt  DATETIME
);");

            ExecCreate(conn, tx, @"
CREATE TABLE IF NOT EXISTS ProjectResources (
    ResourceID      INTEGER PRIMARY KEY AUTOINCREMENT,
    MarketID        INTEGER,
    ResourceName        TEXT,
    RequiredAmount  INTEGER,
    ProvidedAmount  INTEGER,
    Payment INTEGER
);");

            ExecCreate(conn, tx, @"
CREATE TABLE IF NOT EXISTS ProjectsArchive (
    MarketID   INTEGER,
    SystemName TEXT,
    StationName TEXT,
    CreatedBy  TEXT,
    CreatedAt  DATETIME,
    ArchivedAt DATETIME
);");

            tx.Commit();

            using var verify = conn.CreateCommand();
            verify.CommandText = @"SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var rdr = verify.ExecuteReader();
            var names = new List<string>();
            while (rdr.Read()) names.Add(rdr.GetString(0));
            Logger.Log($"SQLite tables present: {string.Join(", ", names)}", "Info");
        }
        private static void ExecPragma(SqliteConnection conn, string pragmaSql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = pragmaSql;
            cmd.ExecuteNonQuery();
        }

        private static bool TryDeleteSqliteFilesWithRetries(string mainPath, int maxRetries, int delayMs)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    TryDeleteSqliteFiles(mainPath);
                    return true;
                }
                catch (IOException ex)
                {
                    if (i == maxRetries - 1) return false;
                    Thread.Sleep(delayMs * (i + 1)); // linear backoff
                }
            }
            return false;
        }

        private static void TryDeleteSqliteFiles(string mainPath)
        {
            void TryDelete(string p)
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception ex) { throw new IOException($"Failed to delete '{p}': {ex.Message}", ex); }
            }

            var dir = Path.GetDirectoryName(mainPath) ?? "";
            var file = Path.GetFileName(mainPath);

            TryDelete(mainPath);
            TryDelete(Path.Combine(dir, file + "-journal"));
            TryDelete(Path.Combine(dir, file + "-wal"));
            TryDelete(Path.Combine(dir, file + "-shm"));
        }

        private static void CreateFreshDatabase(SqliteConnectionStringBuilder csb)
        {
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();
            ExecPragma(conn, "PRAGMA journal_mode=WAL;");
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
CREATE TABLE Projects (
    MarketID   INTEGER PRIMARY KEY,
    SystemName TEXT,
    StationName TEXT,
    CreatedBy  TEXT,
    CreatedAt  DATETIME
);

CREATE TABLE ProjectResources (
    ResourceID      INTEGER PRIMARY KEY AUTOINCREMENT,
    MarketID        INTEGER,
    ResourceName        TEXT,
    RequiredAmount  INTEGER,
    ProvidedAmount  INTEGER,
    Payment INTEGER
);

CREATE TABLE ProjectsArchive (
    MarketID   INTEGER,
    SystemName TEXT,
    StationName TEXT,
    CreatedBy  TEXT,
    CreatedAt  DATETIME,
    ArchivedAt DATETIME
);";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        /// Try to rebuild schema without deleting the file.
        private bool TryRebuildInPlace(SqliteConnectionStringBuilder csb)
        {
            try
            {
                using var conn = new SqliteConnection(csb.ToString());
                conn.Open();

                // Give ourselves a chance to grab the write lock
                ExecPragma(conn, "PRAGMA busy_timeout=5000;");
                ExecPragma(conn, "PRAGMA wal_checkpoint(TRUNCATE);"); // clear WAL
                ExecPragma(conn, "PRAGMA journal_mode=DELETE;");      // avoid sidecars during rebuild
                ExecPragma(conn, "PRAGMA foreign_keys=OFF;");
                ExecPragma(conn, "PRAGMA locking_mode=EXCLUSIVE;");

                using var tx = conn.BeginTransaction();

                DropAllUserTables(conn, tx);
                CreateExpectedTablesInTx(conn, tx);

                tx.Commit();

                // Return to WAL mode after the rebuild
                ExecPragma(conn, "PRAGMA foreign_keys=ON;");
                ExecPragma(conn, "PRAGMA journal_mode=WAL;");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"In-place rebuild failed: {ex.Message}", "Error");
                return false;
            }
        }

        private static void DropAllUserTables(SqliteConnection conn, SqliteTransaction tx)
        {
            var tables = new List<string>();
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                using var r = q.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }

            foreach (var t in tables)
            {
                using var drop = conn.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = $"DROP TABLE IF EXISTS \"{t.Replace("\"", "\"\"")}\";";
                drop.ExecuteNonQuery();
            }
        }

        private static void CreateExpectedTablesInTx(SqliteConnection conn, SqliteTransaction tx)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
CREATE TABLE Projects (
    MarketID   INTEGER PRIMARY KEY,
    SystemName TEXT,
    StationName TEXT,
    CreatedBy  TEXT,
    CreatedAt  DATETIME
);

CREATE TABLE ProjectResources (
    ResourceID      INTEGER PRIMARY KEY AUTOINCREMENT,
    MarketID        INTEGER,
    ResourceName        TEXT,
    RequiredAmount  INTEGER,
    ProvidedAmount  INTEGER,
    Payment INTEGER
);

CREATE TABLE ProjectsArchive (
    MarketID   INTEGER,
    SystemName TEXT,
    StationName TEXT,
    CreatedBy  TEXT,
    CreatedAt  DATETIME,
    ArchivedAt DATETIME
);";
            cmd.ExecuteNonQuery();
        }


        
        /// <summary>
        /// Returns true if every expected table exists and its columns match (order, name, type, PK).
        /// </summary>
        private static bool SchemaMatches(SqliteConnection conn, IEnumerable<TableSpec> expected)
        {
            var existingTables = GetExistingTables(conn);

            foreach (var table in expected)
            {
                if (!existingTables.Contains(table.Name))
                    return false;

                var actualCols = GetColumns(conn, table.Name);
                if (actualCols.Length != table.Columns.Length)
                    return false;

                for (int i = 0; i < actualCols.Length; i++)
                {
                    if (!ColumnEquals(actualCols[i], table.Columns[i]))
                        return false;
                }
            }
            return true;
        }

        private static HashSet<string> GetExistingTables(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var rdr = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (rdr.Read()) set.Add(rdr.GetString(0));
            return set;
        }

        private sealed class TableInfoRow
        {
            public int Cid { get; init; }
            public string Name { get; init; } = "";
            public string Type { get; init; } = "";
            public bool NotNull { get; init; }
            public bool IsPK { get; init; }
        }

        private static TableInfoRow[] GetColumns(SqliteConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}');";
            using var rdr = cmd.ExecuteReader();
            var list = new List<TableInfoRow>();
            while (rdr.Read())
            {
                list.Add(new TableInfoRow
                {
                    Cid = rdr.GetInt32(0),
                    Name = rdr.GetString(1),
                    Type = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                    NotNull = !rdr.IsDBNull(3) && rdr.GetInt32(3) != 0,
                    IsPK = !rdr.IsDBNull(5) && rdr.GetInt32(5) != 0
                });
            }
            return list.OrderBy(r => r.Cid).ToArray();
        }

        private static bool ColumnEquals(TableInfoRow actual, ColumnSpec expected)
        {
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal))
                return false;

            var actType = NormalizeType(actual.Type);
            var expType = NormalizeType(expected.Type);
            if (!string.Equals(actType, expType, StringComparison.Ordinal))
                return false;

            if (actual.IsPK != expected.IsPrimaryKey)
                return false;

            return true;
        }

        private static string NormalizeType(string t)
        {
            t = (t ?? "").Trim().ToUpperInvariant();
            if (t is "INT" or "INTEGER" or "BIGINT" or "SMALLINT") return "INTEGER";
            if (t.Contains("CHAR") || t == "CLOB" || t == "TEXT") return "TEXT";
            if (t is "DOUBLE" or "REAL" or "FLOAT" or "DOUBLE PRECISION") return "REAL";
            if (t is "BLOB") return "BLOB";
            if (t is "NUMERIC" or "DECIMAL" or "BOOLEAN" or "DATE" or "DATETIME") return t;
            return t; // unknown -> require exact
        }

        private static void ExecCreate(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

    }

    // Helper extension to add parameters cleanly
    public static class DbCommandExtensions
    {
        public static void AddParam(this DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
