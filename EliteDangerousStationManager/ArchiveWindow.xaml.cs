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

            // Store the passed-in service (could be null if DB failed earlier)
            _db = db;

            // Load data now (may be empty if DB is down)
            LoadData();
        }

        private void LoadData()
        {
            // If _db is null, skip trying to load
            if (_db == null)
            {
                _allArchived = new List<ArchivedProject>();
                ArchivedList.ItemsSource = _allArchived;
                return;
            }

            try
            {
                // Attempt to pull archived projects; if this throws, we catch it below
                _allArchived = _db.LoadArchivedProjects();
            }
            catch (Exception ex)
            {
                // Log and fall back to an empty list
                Logger.Log($"Failed to load archived projects: {ex.Message}", "Warning");
                _allArchived = new List<ArchivedProject>();
            }

            // Bind whatever we have (empty or real)
            ArchivedList.ItemsSource = _allArchived;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Always guard against _allArchived being null
            if (_allArchived == null || _allArchived.Count == 0)
            {
                ArchivedList.ItemsSource = new List<ArchivedProject>();
                return;
            }

            string keyword = SearchBox.Text.ToLower();
            int mode = SearchMode.SelectedIndex;

            try
            {
                var filtered = _allArchived.Where(p =>
                    (mode == 0 && p.StationName?.ToLower().Contains(keyword) == true) ||
                    (mode == 1 && p.CreatedBy?.ToLower().Contains(keyword) == true) ||
                    (mode == 2 && p.SystemName?.ToLower().Contains(keyword) == true)
                ).ToList();

                ArchivedList.ItemsSource = filtered;
            }
            catch (Exception ex)
            {
                // In case something odd happens (e.g. null fields), log and reset filter
                Logger.Log($"Search error in ArchiveWindow: {ex.Message}", "Warning");
                ArchivedList.ItemsSource = _allArchived;
            }
        }
        public void RefreshData()
        {
            LoadData();
        }
    }
}
