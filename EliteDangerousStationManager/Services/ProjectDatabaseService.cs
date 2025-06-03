using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    public class ProjectDatabaseService
    {
        private readonly string _connectionString;

        public ProjectDatabaseService(string connectionString)
        {
            _connectionString = connectionString;
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
                // 1) Copy to ProjectsArchive
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

        /// <summary>
        /// Deletes both the project row from 'Projects' and any associated
        /// rows in 'ProjectResources' for that MarketID. Wrapped in a transaction
        /// to ensure both tables are updated atomically.
        /// </summary>
        public void DeleteProject(long marketId)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();
            try
            {
                // 1) Delete any resource rows tied to this project
                using (var deleteResources = new MySqlCommand(@"
                    DELETE FROM ProjectResources
                    WHERE MarketID = @mid;",
                    conn, tx
                ))
                {
                    deleteResources.Parameters.AddWithValue("@mid", marketId);
                    deleteResources.ExecuteNonQuery();
                }

                // 2) Delete the project row itself
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
