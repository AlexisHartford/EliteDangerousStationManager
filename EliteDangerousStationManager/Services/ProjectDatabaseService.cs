using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Common;
using MySql.Data.MySqlClient;
using Microsoft.Data.Sqlite;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    public class ProjectDatabaseService
    {
        private readonly string _connectionString;
        private readonly string _dbMode; // "Server" (MySQL) or "Local" (SQLite)
        private readonly string _sqlitePath = "stations.db";

        public string ConnectionString => _connectionString;

        public ProjectDatabaseService(string dbMode = "Server")
        {
            _dbMode = dbMode;

            if (_dbMode == "Server")
            {
                string primary = ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
                string secondary = ConfigurationManager.ConnectionStrings["FallbackDB"]?.ConnectionString;

                if (!string.IsNullOrWhiteSpace(primary) && CanConnectMySQL(primary))
                {
                    _connectionString = primary;
                    Logger.Log("Connected to PrimaryDB.", "Info");
                }
                else if (!string.IsNullOrWhiteSpace(secondary) && CanConnectMySQL(secondary))
                {
                    _connectionString = secondary;
                    Logger.Log("PrimaryDB unreachable; connected to FallbackDB.", "Info");
                }
                else
                {
                    throw new InvalidOperationException("Cannot connect to either PrimaryDB or FallbackDB.");
                }
            }
            else
            {
                // SQLite Mode
                _connectionString = $"Data Source={_sqlitePath}";
                EnsureSQLiteSchema();
                Logger.Log("Using local SQLite database (stations.db).", "Info");
            }
        }

        // Validate MySQL connections
        private static bool CanConnectMySQL(string connString)
        {
            try
            {
                using var conn = new MySqlConnection(connString);
                conn.Open();
                conn.Close();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private DbConnection GetConnection()
        {
            if (_dbMode == "Server")
                return new MySqlConnection(_connectionString);

            return new SqliteConnection(_connectionString);
        }

        /// <summary>
        /// Ensures SQLite tables exist if we're in Local mode.
        /// </summary>
        private void EnsureSQLiteSchema()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Projects (
                    MarketID INTEGER PRIMARY KEY,
                    SystemName TEXT,
                    StationName TEXT,
                    CreatedBy TEXT,
                    CreatedAt DATETIME
                );
                CREATE TABLE IF NOT EXISTS ProjectResources (
                    ResourceID INTEGER PRIMARY KEY AUTOINCREMENT,
                    MarketID INTEGER,
                    Material TEXT,
                    RequiredAmount INTEGER,
                    ProvidedAmount INTEGER
                );
                CREATE TABLE IF NOT EXISTS ProjectsArchive (
                    MarketID INTEGER,
                    SystemName TEXT,
                    StationName TEXT,
                    CreatedBy TEXT,
                    CreatedAt DATETIME,
                    ArchivedAt DATETIME
                );
            ";
            cmd.ExecuteNonQuery();
        }

        // ✅ Load projects
        public List<Project> LoadProjects()
        {
            var projects = new List<Project>();
            using var conn = GetConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt FROM Projects ORDER BY SystemName;";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                projects.Add(new Project
                {
                    MarketId = Convert.ToInt64(reader["MarketID"]),
                    SystemName = reader["SystemName"].ToString(),
                    StationName = reader["StationName"].ToString(),
                    CreatedBy = reader["CreatedBy"] == DBNull.Value ? "Unknown" : reader["CreatedBy"].ToString(),
                    CreatedAt = reader["CreatedAt"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(reader["CreatedAt"])
                });
            }

            return projects;
        }

        // ✅ Save project
        public void SaveProject(Project project)
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            if (_dbMode == "Server")
            {
                cmd.CommandText = @"
                    INSERT INTO Projects (MarketID, SystemName, StationName, CreatedBy)
                    VALUES (@mid, @system, @station, @creator)
                    ON DUPLICATE KEY UPDATE 
                        SystemName = @system,
                        StationName = @station,
                        CreatedBy = @creator;";
            }
            else
            {
                // SQLite doesn't support ON DUPLICATE KEY; use INSERT OR REPLACE
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

        // ✅ Archive project
        public void ArchiveProject(long marketId)
        {
            using var conn = GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                using (var insertCmd = conn.CreateCommand())
                {
                    insertCmd.CommandText = @"
                        INSERT INTO ProjectsArchive (MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt)
                        SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, CURRENT_TIMESTAMP
                        FROM Projects WHERE MarketID = @id;";
                    insertCmd.AddParam("@id", marketId);
                    insertCmd.ExecuteNonQuery();
                }

                using (var deleteCmd = conn.CreateCommand())
                {
                    deleteCmd.CommandText = "DELETE FROM Projects WHERE MarketID = @id;";
                    deleteCmd.AddParam("@id", marketId);
                    deleteCmd.ExecuteNonQuery();
                }

                tx.Commit();
                Logger.Log($"Archived project with MarketID {marketId}", "Success");
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        public List<ArchivedProject> LoadArchivedProjects()
        {
            var projects = new List<ArchivedProject>();
            using var conn = GetConnection();
            conn.Open();

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


        // ✅ Delete project
        public void DeleteProject(long marketId)
        {
            using var conn = GetConnection();
            conn.Open();
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
    }

    // ✅ Helper extension to add parameters cleanly
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
