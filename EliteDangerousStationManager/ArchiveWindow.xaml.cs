using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Services;

namespace EliteDangerousStationManager
{
    public partial class ArchiveWindow : Window
    {
        private readonly ProjectDatabaseService _db;
        private List<ArchivedProject> _allArchived = new List<ArchivedProject>();

        public ArchiveWindow(ProjectDatabaseService db)
        {
            InitializeComponent();
            _db = db; // may be null if DB service failed
            LoadData();
        }

        /// <summary>
        /// Reloads archived projects from the database and refreshes the list.
        /// </summary>
        public void RefreshData()
        {
            LoadData();
        }

        private void LoadData()
        {
            _allArchived.Clear();

            // 🟢 Always load LOCAL archive
            try
            {
                var localDb = new ProjectDatabaseService("Local");
                var localArchives = localDb.LoadArchivedProjects();

                foreach (var proj in localArchives)
                    proj.Source = "Local";   // (optional) Tag where it came from

                _allArchived.AddRange(localArchives);
                Logger.Log($"📁 Loaded {localArchives.Count} LOCAL archived projects.", "Info");
            }
            catch (Exception ex)
            {
                Logger.Log($"ArchiveWindow: Failed to load LOCAL archives: {ex.Message}", "Warning");
            }

            // 🌐 Load SERVER archive only if “Public”/Server mode is enabled
            if (App.CurrentDbMode == "Server")
            {
                try
                {
                    var serverDb = new ProjectDatabaseService("Server");
                    var serverArchives = serverDb.LoadArchivedProjects();

                    foreach (var proj in serverArchives)
                        proj.Source = "Server";

                    _allArchived.AddRange(serverArchives);
                    Logger.Log($"🌐 Loaded {serverArchives.Count} SERVER archived projects.", "Info");
                }
                catch (Exception ex)
                {
                    Logger.Log($"ArchiveWindow: Failed to load SERVER archives: {ex.Message}", "Warning");
                }
            }
            else
            {
                Logger.Log("⚠ Server mode disabled — skipping server archive load.", "Warning");
            }

            ArchivedList.ItemsSource = _allArchived;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allArchived == null || !_allArchived.Any())
            {
                ArchivedList.ItemsSource = new List<ArchivedProject>();
                return;
            }

            string keyword = (SearchBox.Text ?? string.Empty).ToLowerInvariant();
            int mode = SearchMode.SelectedIndex;

            try
            {
                var filtered = _allArchived.Where(p =>
                    mode switch
                    {
                        0 => p.StationName?.ToLowerInvariant().Contains(keyword) == true,
                        1 => p.CreatedBy?.ToLowerInvariant().Contains(keyword) == true,
                        2 => p.SystemName?.ToLowerInvariant().Contains(keyword) == true,
                        _ => true
                    }
                ).ToList();

                ArchivedList.ItemsSource = filtered;
            }
            catch (Exception ex)
            {
                Logger.Log($"ArchiveWindow: Search error: {ex.Message}", "Warning");
                ArchivedList.ItemsSource = _allArchived;
            }
        }
    }
}
