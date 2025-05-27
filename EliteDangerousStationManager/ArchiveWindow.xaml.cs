using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Services;

namespace EliteDangerousStationManager
{
    public partial class ArchiveWindow : Window
    {
        private readonly ProjectDatabaseService _db;
        private List<ArchivedProject> _allArchived;

        public ArchiveWindow(ProjectDatabaseService db)
        {
            InitializeComponent();
            _db = db;
            LoadData();
        }

        private void LoadData()
        {
            _allArchived = _db.LoadArchivedProjects();
            ArchivedList.ItemsSource = _allArchived;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string keyword = SearchBox.Text.ToLower();
            int mode = SearchMode.SelectedIndex;

            var filtered = _allArchived.Where(p =>
                (mode == 0 && p.StationName.ToLower().Contains(keyword)) ||
                (mode == 1 && p.CreatedBy.ToLower().Contains(keyword)) ||
                (mode == 2 && p.SystemName.ToLower().Contains(keyword))
            ).ToList();

            ArchivedList.ItemsSource = filtered;
        }
    }
}
