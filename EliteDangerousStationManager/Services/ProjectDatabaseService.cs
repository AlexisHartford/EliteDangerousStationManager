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

            var cmd = new MySqlCommand("SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt FROM Projects ORDER BY SystemName;", conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var project = new Project
                {
                    MarketId = reader.GetInt64("MarketID"),
                    SystemName = reader.GetString("SystemName"),
                    StationName = reader.GetString("StationName"),
                    CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? "Unknown" : reader.GetString("CreatedBy"),
                    CreatedAt = reader.IsDBNull(reader.GetOrdinal("CreatedAt"))
                        ? DateTime.MinValue
                        : reader.GetDateTime("CreatedAt") // ✅ CORRECT
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
                    SystemName = @system, 
                    StationName = @station, 
                    CreatedBy = @creator;", conn);


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

            // Copy project into ProjectsArchive with timestamp
            var insertCmd = new MySqlCommand(@"
        INSERT INTO ProjectsArchive (MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt)
        SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, NOW()
        FROM Projects
        WHERE MarketID = @id;", conn);
            insertCmd.Parameters.AddWithValue("@id", marketId);
            insertCmd.ExecuteNonQuery();

            // Delete from live Projects table
            var deleteCmd = new MySqlCommand("DELETE FROM Projects WHERE MarketID = @id;", conn);
            deleteCmd.Parameters.AddWithValue("@id", marketId);
            deleteCmd.ExecuteNonQuery();

            Logger.Log($"Archived project with MarketID {marketId}", "Success");
        }
        public List<ArchivedProject> LoadArchivedProjects()
        {
            var projects = new List<ArchivedProject>();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            var cmd = new MySqlCommand("SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt FROM ProjectsArchive ORDER BY ArchivedAt DESC;", conn);
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


    }
}
