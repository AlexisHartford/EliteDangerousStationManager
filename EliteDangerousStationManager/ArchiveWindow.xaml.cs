using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MessageBox = System.Windows.MessageBox;

namespace EliteDangerousStationManager
{
    public partial class ArchiveWindow : Window
    {
        private readonly string _currentCommander;            // raw (already cleaned by caller)
        private readonly string _currentCommanderNorm;        // normalized for compare
        private readonly ProjectDatabaseService _db;
        private List<ArchivedProject> _allArchived = new List<ArchivedProject>();

        // 👇 Add this read-only property so XAML can bind to it
        public string CurrentCommander { get; private set; } = string.Empty;

        public ArchiveWindow(ProjectDatabaseService db, string currentCommander)
        {
            // set BEFORE InitializeComponent so XAML sees it
            _currentCommander = currentCommander ?? string.Empty;
            _currentCommanderNorm = Normalize(_currentCommander);
            CurrentCommander = _currentCommander;

            InitializeComponent();
            _db = db;
            LoadData();
        }

        private static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();

            // Remove common prefixes/labels and punctuation noise
            s = s.Replace("Commander:", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("CMDR", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("Created By:", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("Owner:", "", StringComparison.OrdinalIgnoreCase)
                 .Replace(":", "");

            // Collapse internal whitespace
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

            return s.Trim().ToLowerInvariant();  // case-insensitive compare
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
                    var serverArchives = DbConnectionManager.Instance.Execute(cmd =>
                    {
                        cmd.CommandText = @"
                SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt, ArchivedAt
                FROM ProjectsArchive
                ORDER BY ArchivedAt DESC;";
                        using var rdr = cmd.ExecuteReader();

                        var list = new List<ArchivedProject>();
                        while (rdr.Read())
                        {
                            list.Add(new ArchivedProject
                            {
                                MarketId = rdr.GetInt64(rdr.GetOrdinal("MarketID")),
                                SystemName = rdr["SystemName"]?.ToString(),
                                StationName = rdr["StationName"]?.ToString(),
                                CreatedBy = rdr["CreatedBy"]?.ToString(),
                                CreatedAt = rdr.IsDBNull(rdr.GetOrdinal("CreatedAt")) ? DateTime.MinValue : Convert.ToDateTime(rdr["CreatedAt"]),
                                ArchivedAt = rdr.IsDBNull(rdr.GetOrdinal("ArchivedAt")) ? DateTime.MinValue : Convert.ToDateTime(rdr["ArchivedAt"]),
                                Source = "Server"
                            });
                        }
                        return list;
                    },
                    // shows up in your [DB][FAIL] logs with the caller op + Primary/Fallback context
                    sqlPreview: "SELECT … FROM ProjectsArchive ORDER BY ArchivedAt DESC");

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
        private void DeleteArchived_Click(object sender, RoutedEventArgs e)
        {
            
            if (sender is not FrameworkElement fe || fe.DataContext is not ArchivedProject proj)
                return;

            var ownerNorm = Normalize(proj.CreatedBy);

            // DEBUG (optional): see what you're comparing
            // Logger.Log($"Owner check: UI='{_currentCommander}' / '{_currentCommanderNorm}', Row='{proj.CreatedBy}' / '{ownerNorm}'", "Debug");

            if (!string.Equals(ownerNorm, _currentCommanderNorm, StringComparison.Ordinal))
            {
                MessageBox.Show("Only the project owner can delete an archived project.",
                                "Not allowed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }


            var where = proj.Source?.ToUpperInvariant() == "SERVER" ? "SERVER" : "LOCAL";
            var confirm = MessageBox.Show(
                $"Remove “{proj.StationName}” (Market ID {proj.MarketId}) from the {where} archive?",
                "Confirm delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                if (where == "LOCAL")
                {
                    // Use your local DB service to delete the archived row
                    var localDb = new ProjectDatabaseService("Local");
                    // Implement the helper if you don't already have it (shown further below)
                    localDb.DeleteArchivedProject(proj.MarketId);
                }
                else
                {
                    // Only when running in server/public mode
                    if (App.CurrentDbMode == "Server")
                    {
                        DbConnectionManager.Instance.Execute(cmd =>
                        {
                            cmd.CommandText = @"
                        DELETE FROM ProjectsArchive
                        WHERE MarketID = @id AND CreatedBy = @owner;";
                            var p1 = cmd.CreateParameter(); p1.ParameterName = "@id"; p1.Value = proj.MarketId; cmd.Parameters.Add(p1);
                            var p2 = cmd.CreateParameter(); p2.ParameterName = "@owner"; p2.Value = _currentCommander; cmd.Parameters.Add(p2);
                            return cmd.ExecuteNonQuery();
                        },
                        sqlPreview: "DELETE FROM ProjectsArchive WHERE MarketID=@id AND CreatedBy=@owner");
                    }
                }

                Logger.Log($"🗑 Deleted archived project {proj.MarketId} from {where} archive.", "Success");
                RefreshData(); // reload list
            }
            catch (Exception ex)
            {
                Logger.Log($"ArchiveWindow: delete failed: {ex.Message}", "Error");
                MessageBox.Show("Delete failed. Check the log for details.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
