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
            var cmd = new MySqlCommand("SELECT MarketID, SystemName, StationName FROM Projects ORDER BY SystemName;", conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                projects.Add(new Project
                {
                    MarketId = reader.GetInt64("MarketID"),
                    SystemName = reader.GetString("SystemName"),
                    StationName = reader.GetString("StationName"),
                });
            }

            Logger.Log($"Loaded {projects.Count} projects from database.", "Info");
            return projects;
        }

        public void SaveProject(Project project)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            var cmd = new MySqlCommand(@"
                INSERT INTO Projects (MarketID, SystemName, StationName)
                VALUES (@mid, @system, @station)
                ON DUPLICATE KEY UPDATE SystemName=@system, StationName=@station;", conn);

            cmd.Parameters.AddWithValue("@mid", project.MarketId);
            cmd.Parameters.AddWithValue("@system", project.SystemName);
            cmd.Parameters.AddWithValue("@station", project.StationName);
            cmd.ExecuteNonQuery();

            Logger.Log($"Saved project {project}", "Success");
        }

        public void DeleteProject(long marketId)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            var cmd1 = new MySqlCommand("DELETE FROM ProjectResources WHERE MarketID = @id", conn);
            cmd1.Parameters.AddWithValue("@id", marketId);
            cmd1.ExecuteNonQuery();

            var cmd2 = new MySqlCommand("DELETE FROM Projects WHERE MarketID = @id", conn);
            cmd2.Parameters.AddWithValue("@id", marketId);
            cmd2.ExecuteNonQuery();

            Logger.Log($"Deleted project with MarketID {marketId}", "Success");
        }
    }
}