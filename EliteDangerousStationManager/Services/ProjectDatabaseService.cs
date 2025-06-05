using System;
using System.Collections.Generic;
using System.Configuration;
using MySql.Data.MySqlClient;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    public class ProjectDatabaseService
    {
        // Hold whichever connection string succeeded
        private readonly string _connectionString;

        // Expose it if anyone needs the raw string
        public string ConnectionString => _connectionString;

        /// <summary>
        /// Try opening a MySQL connection to the given connectionString.
        /// Returns true if Open() succeeds.
        /// </summary>
        private static bool CanConnect(string connString)
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

        /// <summary>
        /// Default ctor: read “PrimaryDB” from App.config; if it fails, try “FallbackDB”.
        /// Throws if neither can be opened.
        /// </summary>
        public ProjectDatabaseService()
        {
            // Read from <connectionStrings> in App.config
            string primary = ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
            string secondary = ConfigurationManager.ConnectionStrings["FallbackDB"]?.ConnectionString;

            if (!string.IsNullOrWhiteSpace(primary) && CanConnect(primary))
            {
                _connectionString = primary;
                Logger.Log("Connected to PrimaryDB (108.211.228.206).", "Info");
            }
            else if (!string.IsNullOrWhiteSpace(secondary) && CanConnect(secondary))
            {
                _connectionString = secondary;
                Logger.Log("PrimaryDB unreachable; connected to FallbackDB (192.168.10.68).", "Info");
            }
            else
            {
                throw new InvalidOperationException(
                    "Cannot connect to either PrimaryDB or FallbackDB. Check your network or credentials."
                );
            }
        }

        /// <summary>
        /// If you really want to pass a custom connection‐string at runtime,
        /// you can still use this overload. It will validate that the string works.
        /// </summary>
        public ProjectDatabaseService(string explicitConnectionString)
        {
            if (!CanConnect(explicitConnectionString))
                throw new InvalidOperationException("Cannot connect using the provided connection string.");

            _connectionString = explicitConnectionString;
            Logger.Log("Connected using explicit connection string.", "Info");
        }

        public List<Project> LoadProjects()
        {
            var projects = new List<Project>();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            var cmd = new MySqlCommand(
                "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt " +
                "FROM Projects " +
                "ORDER BY SystemName;",
                conn
            );
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var project = new Project
                {
                    MarketId = reader.GetInt64("MarketID"),
                    SystemName = reader.GetString("SystemName"),
                    StationName = reader.GetString("StationName"),
                    CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy"))
                                  ? "Unknown"
                                  : reader.GetString("CreatedBy"),
                    CreatedAt = reader.IsDBNull(reader.GetOrdinal("CreatedAt"))
                                  ? DateTime.MinValue
                                  : reader.GetDateTime("CreatedAt")
                };
                projects.Add(project);
            }

            Logger.Log($"Loaded {projects.Count} projects from database.", "Info");
            return projects;
        }

        public void SaveProject(Project project)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            var cmd = new MySqlCommand(@"
                INSERT INTO Projects (MarketID, SystemName, StationName, CreatedBy)
                VALUES (@mid, @system, @station, @creator)
                ON DUPLICATE KEY UPDATE 
                    SystemName  = @system,
                    StationName = @station,
                    CreatedBy   = @creator;",
                conn
            );

            cmd.Parameters.AddWithValue("@mid", project.MarketId);
            cmd.Parameters.AddWithValue("@system", project.SystemName);
            cmd.Parameters.AddWithValue("@station", project.StationName);
            cmd.Parameters.AddWithValue("@creator", project.CreatedBy ?? "Unknown");

            cmd.ExecuteNonQuery();
            Logger.Log($"Saved project {project}", "Success");
        }

        public void ArchiveProject(long marketId)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();
            try
            {
                // 1) Copy row into ProjectsArchive
                using (var insertCmd = new MySqlCommand(@"
                    INSERT INTO ProjectsArchive
                        (MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt)
                    SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, NOW()
                    FROM Projects
                    WHERE MarketID = @id;",
                    conn, tx
                ))
                {
                    insertCmd.Parameters.AddWithValue("@id", marketId);
                    insertCmd.ExecuteNonQuery();
                }

                // 2) Delete from Projects
                using (var deleteCmd = new MySqlCommand(@"
                    DELETE FROM Projects
                    WHERE MarketID = @id;",
                    conn, tx
                ))
                {
                    deleteCmd.Parameters.AddWithValue("@id", marketId);
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
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            var cmd = new MySqlCommand(@"
                SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt
                FROM ProjectsArchive
                ORDER BY ArchivedAt DESC;",
                conn
            );
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                projects.Add(new ArchivedProject
                {
                    MarketId = reader.GetInt64("MarketID"),
                    SystemName = reader.GetString("SystemName"),
                    StationName = reader.GetString("StationName"),
                    CreatedBy = reader.GetString("CreatedBy"),
                    CreatedAt = reader.GetDateTime("CreatedAt"),
                    ArchivedAt = reader.GetDateTime("ArchivedAt")
                });
            }

            return projects;
        }

        public void DeleteProject(long marketId)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();
            try
            {
                // Delete resources first
                using (var deleteResources = new MySqlCommand(@"
                    DELETE FROM ProjectResources
                    WHERE MarketID = @mid;",
                    conn, tx
                ))
                {
                    deleteResources.Parameters.AddWithValue("@mid", marketId);
                    deleteResources.ExecuteNonQuery();
                }

                // Then delete the project row
                using (var deleteProject = new MySqlCommand(@"
                    DELETE FROM Projects
                    WHERE MarketID = @mid;",
                    conn, tx
                ))
                {
                    deleteProject.Parameters.AddWithValue("@mid", marketId);
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
}
