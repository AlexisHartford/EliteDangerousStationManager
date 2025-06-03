using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Services;
using EliteDangerousStationManager.Overlay;
using MySql.Data.MySqlClient;
using System.Windows.Input;

namespace EliteDangerousStationManager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private ProjectDatabaseService projectDb;
        private readonly JournalProcessor journalProcessor;
        private readonly OverlayManager overlayManager;

        private readonly string connectionString =
            ConfigurationManager.ConnectionStrings["EliteDB"].ConnectionString;

        // Re-declare archiveWindow and plannerWindow:
        private ArchiveWindow archiveWindow;
        private ColonizationPlanner.ColonizationPlanner plannerWindow;

        public ObservableCollection<LogEntry> LogEntries => Logger.Entries;
        public ObservableCollection<Project> Projects { get; set; } = new ObservableCollection<Project>();
        public ObservableCollection<ProjectMaterial> CurrentProjectMaterials { get; set; } = new ObservableCollection<ProjectMaterial>();

        // Expose carrier cargo and overview to XAML
        public ObservableCollection<CargoItem> FleetCarrierCargoItems { get; set; } = new ObservableCollection<CargoItem>();
        public ObservableCollection<CarrierMaterialStatus> CarrierMaterialOverview { get; set; } = new ObservableCollection<CarrierMaterialStatus>();

        private DispatcherTimer timer;
        private Project _selectedProject;
        public Project SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (_selectedProject != value)
                {
                    _selectedProject = value;
                    OnPropertyChanged();
                    LoadMaterialsForProject(_selectedProject);
                    overlayManager.ShowOverlay(CurrentProjectMaterials);
                }
            }
        }

        private ObservableCollection<Project> _selectedProjects = new ObservableCollection<Project>();
        public ObservableCollection<Project> SelectedProjects
        {
            get => _selectedProjects;
            set
            {
                _selectedProjects = value;
                OnPropertyChanged();
                if (_selectedProjects.Count == 1)
                {
                    LoadMaterialsForProject(_selectedProjects.First());
                    overlayManager.ShowOverlay(CurrentProjectMaterials);
                }
                else if (_selectedProjects.Count > 1)
                {
                    LoadMaterialsForProjects(_selectedProjects);
                    overlayManager.ShowOverlay(CurrentProjectMaterials);
                }
                else
                {
                    CurrentProjectMaterials.Clear();
                }
            }
        }

        private string _lastUpdate;
        public string LastUpdate
        {
            get => _lastUpdate;
            set
            {
                _lastUpdate = value;
                OnPropertyChanged();
            }
        }

        public string CommanderName { get; private set; }

        public MainWindow()
        {
            InitializeComponent();

            // Ensure child windows / overlays close
            this.Closed += (s, e) => CloseChildWindows();
            this.Closed += (s, e) => overlayManager?.CloseOverlay();

            // 1) Load color settings
            string configPath = ConfigHelper.GetSettingsFilePath();
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                if (lines.Length > 1)
                {
                    string color = lines[1];
                    try
                    {
                        var parsedColor = (Color)ColorConverter.ConvertFromString(color);
                        Application.Current.Resources["HighlightBrush"] =
                            new SolidColorBrush(parsedColor);
                        Application.Current.Resources["HighlightOverlayBrush"] =
                            new SolidColorBrush(Color.FromArgb(0x22, parsedColor.R, parsedColor.G, parsedColor.B));
                    }
                    catch
                    {
                        Application.Current.Resources["HighlightBrush"] =
                            new SolidColorBrush(Colors.Orange);
                        Application.Current.Resources["HighlightOverlayBrush"] =
                            new SolidColorBrush(Color.FromArgb(0x22, 255, 140, 0));
                    }
                }
            }

            DataContext = this;

            // 2) Compute the journal folder path
            string journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Saved Games",
                "Frontier Developments",
                "Elite Dangerous"
            );

            if (!Directory.Exists(journalPath))
            {
                MessageBox.Show(
                    $"Journal folder not found:\n{journalPath}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }

            // 3) Read Commander name from journal
            ReadCommanderNameFromJournal();

            // 4) Delete any existing lastread.state so we re-scan from top‐of‐file
            string stateFile = Path.Combine(journalPath, "lastread.state");
            try
            {
                if (File.Exists(stateFile))
                {
                    File.Delete(stateFile);
                    Logger.Log("Deleted old lastread.state for a clean start.", "Debug");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete lastread.state: {ex.Message}", "Warning");
            }

            // 5) Instantiate JournalProcessor
            journalProcessor = new JournalProcessor(journalPath, CommanderName ?? "UnknownCommander");

            // Subscribe to cargo‐changed event
            journalProcessor.CarrierCargoChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCarrierCargo();
                    UpdateCarrierMaterialOverview();
                });
            };

            // 6) Instantiate OverlayManager
            overlayManager = new OverlayManager();

            Logger.Log("Application started.", "Success");

            // 7) Defer DB initialization & initial scan
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Task.Run(() =>
                {
                    // A) Force MySQL .Open() to catch connectivity errors
                    try
                    {
                        using var testConn = new MySqlConnection(connectionString);
                        testConn.Open();
                        testConn.Close();

                        projectDb = new ProjectDatabaseService(connectionString);
                        Dispatcher.Invoke(() => SetDatabaseStatusIndicator(true));
                        Dispatcher.Invoke(() => LoadProjects());
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Database unavailable at startup: {ex.Message}", "Warning");
                        Dispatcher.Invoke(() => SetDatabaseStatusIndicator(false));
                        projectDb = null;
                    }

                    // B) Initial journal scan
                    try
                    {
                        journalProcessor.ScanForDepotEvents();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Initial journal scan failed: {ex.Message}", "Warning");
                    }

                    // C) Refresh UI
                    Dispatcher.Invoke(() => ScanForDepotAndRefreshUI());
                });

                // 8) Start the 5‐second timer
                StartTimer();
            }), DispatcherPriority.ApplicationIdle);
        }

        private void SetDatabaseStatusIndicator(bool isOnline)
        {
            if (isOnline)
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00));
                StatusLabel.Text = "System Online";
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
                StatusLabel.Text = "System Offline";
            }
        }

        private void CloseChildWindows()
        {
            try { plannerWindow?.Close(); } catch { }
            try { archiveWindow?.Close(); } catch { }
        }

        private void ReadCommanderNameFromJournal()
        {
            try
            {
                string journalPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Saved Games",
                    "Frontier Developments",
                    "Elite Dangerous"
                );

                var latestFile = Directory.GetFiles(journalPath, "Journal.*.log")
                                          .OrderByDescending(File.GetLastWriteTimeUtc)
                                          .FirstOrDefault();

                if (latestFile == null)
                {
                    Logger.Log("No journal file found.", "Warning");
                    return;
                }

                using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd()
                              .Split('\n')
                              .Reverse();

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var json = Newtonsoft.Json.Linq.JObject.Parse(line);
                    if (json["event"]?.ToString() == "Commander")
                    {
                        CommanderName = json["Name"]?.ToString();
                        Dispatcher.Invoke(() =>
                        {
                            CommanderNameTextBlock.Text = $"Commander: {CommanderName}";
                        });
                        Logger.Log($"Commander detected: {CommanderName}", "Success");
                        return;
                    }
                }

                Logger.Log("Commander name not found in journal.", "Warning");
            }
            catch (Exception ex)
            {
                Logger.Log("Error reading commander name: " + ex.Message, "Error");
            }
        }

        private void LoadProjects(string filter = "", int filterMode = 0)
        {
            if (projectDb == null) return;

            try
            {
                var loaded = projectDb.LoadProjects();

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    string keyword = filter.ToLower();
                    loaded = loaded.Where(p =>
                        (filterMode == 0 && p.StationName.ToLower().Contains(keyword)) ||
                        (filterMode == 1 && (p.CreatedBy?.ToLower().Contains(keyword) ?? false))
                    ).ToList();
                }

                Projects.Clear();
                foreach (var p in loaded)
                    Projects.Add(p);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load projects: {ex.Message}", "Warning");
                Projects.Clear();
            }
        }

        private void StartTimer()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) =>
            {
                Logger.Log("Timer tick", "Info");

                Task.Run(() =>
                {
                    try
                    {
                        journalProcessor.ScanForDepotEvents();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Journal scan failed: {ex.Message}", "Warning");
                    }
                    Dispatcher.Invoke(() => ScanForDepotAndRefreshUI());
                });
                LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            };

            LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            timer.Start();
        }

        private void ScanForDepotAndRefreshUI()
        {
            if (projectDb != null)
            {
                try
                {
                    // Preserve which MarketIDs were selected before we reload:
                    var selectedIds = SelectedProjects.Select(p => p.MarketId).ToHashSet();

                    // If there's no search term, load all. Otherwise, re‐apply the current search filter.
                    if (string.IsNullOrWhiteSpace(SearchBox.Text))
                    {
                        LoadProjects();
                    }
                    else
                    {
                        // Re‐run the same filtering logic that SearchBox_TextChanged uses:
                        string keyword = SearchBox.Text.ToLower();
                        int mode = SearchMode.SelectedIndex;

                        var filtered = projectDb.LoadProjects().Where(p =>
                            (mode == 0 && p.StationName.ToLower().Contains(keyword)) ||
                            (mode == 1 && (p.CreatedBy?.ToLower().Contains(keyword) ?? false)) ||
                            (mode == 2 && (CommanderName?.ToLower().Contains(keyword) ?? false))
                        );

                        Projects.Clear();
                        foreach (var p in filtered)
                            Projects.Add(p);
                    }

                    // Restore the selections
                    SelectedProjects.Clear();
                    foreach (var p in Projects.Where(x => selectedIds.Contains(x.MarketId)))
                        SelectedProjects.Add(p);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error reloading projects: {ex.Message}", "Warning");
                }
            }

            // Now refresh materials/UI for whatever is selected:
            if (SelectedProjects.Count == 1)
            {
                LoadMaterialsForProject(SelectedProjects.First());
                overlayManager.ShowOverlay(CurrentProjectMaterials);

                // ◼ NEW: If the single selected project has no outstanding materials, auto‐archive it:
                if (CurrentProjectMaterials.Count == 0)
                {
                    var toArchive = SelectedProjects.First();
                    try
                    {
                        projectDb.ArchiveProject(toArchive.MarketId);
                        Logger.Log($"Auto‐archived '{toArchive.StationName}' (no remaining materials).", "Info");

                        //   1) Remove from active list, reload everything
                        LoadProjects();
                        SelectedProjects.Clear();
                        CurrentProjectMaterials.Clear();
                        overlayManager.ShowOverlay(CurrentProjectMaterials);

                        //   2) If archive window is open, refresh it
                        archiveWindow?.RefreshData();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to auto‐archive '{toArchive.StationName}': {ex.Message}", "Warning");
                    }
                }
            }
            else if (SelectedProjects.Count > 1)
            {
                LoadMaterialsForProjects(SelectedProjects);
                overlayManager.ShowOverlay(CurrentProjectMaterials);

                // (If you want to auto‐archive when multiple are selected only if *all* are empty,
                //  you could check CurrentProjectMaterials.Count == 0 here, but typically auto‐archive
                //  makes most sense for a single project.)
            }
            else
            {
                CurrentProjectMaterials.Clear();
            }
        }

        private void LoadMaterialsForProjects(IEnumerable<Project> projects)
        {
            if (projectDb == null) return;

            var combined = new Dictionary<string, ProjectMaterial>();
            try
            {
                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                foreach (var project in projects)
                {
                    using var cmd = new MySqlCommand(@"
                        SELECT ResourceName, RequiredAmount, ProvidedAmount
                        FROM ProjectResources
                        WHERE MarketID = @mid
                          AND RequiredAmount > ProvidedAmount
                        ORDER BY ResourceName;", conn);
                    cmd.Parameters.AddWithValue("@mid", project.MarketId);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string name = reader.GetString("ResourceName");
                        int required = reader.GetInt32("RequiredAmount");
                        int provided = reader.GetInt32("ProvidedAmount");
                        int needed = required - provided; // guaranteed > 0

                        if (!combined.TryGetValue(name, out var existing))
                        {
                            combined[name] = new ProjectMaterial
                            {
                                Material = name,
                                Required = required,
                                Provided = provided,
                                Needed = needed
                            };
                        }
                        else
                        {
                            existing.Required += required;
                            existing.Provided += provided;
                            existing.Needed = Math.Max(existing.Required - existing.Provided, 0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading combined materials: {ex.Message}", "Warning");
                combined.Clear();
            }

            CurrentProjectMaterials.Clear();
            foreach (var mat in combined.Values.Where(m => m.Needed > 0).OrderBy(m => m.Material))
                CurrentProjectMaterials.Add(mat);

            Logger.Log($"Loaded {CurrentProjectMaterials.Count} combined materials.", "Info");
        }

        private void LoadMaterialsForProject(Project project)
        {
            CurrentProjectMaterials.Clear();
            if (project == null || projectDb == null)
                return;

            try
            {
                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                var cmd = new MySqlCommand(@"
                    SELECT ResourceName, RequiredAmount, ProvidedAmount
                    FROM ProjectResources
                    WHERE MarketID = @mid
                      AND RequiredAmount > ProvidedAmount
                    ORDER BY ResourceName;", conn);
                cmd.Parameters.AddWithValue("@mid", project.MarketId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int required = reader.GetInt32("RequiredAmount");
                    int provided = reader.GetInt32("ProvidedAmount");
                    int needed = required - provided; // guaranteed > 0

                    CurrentProjectMaterials.Add(new ProjectMaterial
                    {
                        Material = reader.GetString("ResourceName"),
                        Required = required,
                        Provided = provided,
                        Needed = needed
                    });
                }

                Logger.Log($"Loaded {CurrentProjectMaterials.Count} materials for {project}", "Info");
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading materials: " + ex.Message, "Warning");
            }
        }

        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Project project)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    if (SelectedProjects.Contains(project))
                        SelectedProjects.Remove(project);
                    else
                        SelectedProjects.Add(project);
                }
                else
                {
                    SelectedProjects.Clear();
                    SelectedProjects.Add(project);
                }

                if (SelectedProjects.Count > 0)
                {
                    if (SelectedProjects.Count == 1)
                    {
                        LoadMaterialsForProject(SelectedProjects.First());
                        overlayManager.ShowOverlay(CurrentProjectMaterials);
                    }
                    else
                    {
                        LoadMaterialsForProjects(SelectedProjects);
                        overlayManager.ShowOverlay(CurrentProjectMaterials);
                    }
                }
                else
                {
                    CurrentProjectMaterials.Clear();
                }

                OnPropertyChanged(nameof(SelectedProjects));
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (projectDb == null) return;

            string keyword = SearchBox.Text.ToLower();
            int mode = SearchMode.SelectedIndex;

            try
            {
                var filtered = projectDb.LoadProjects().Where(p =>
                    (mode == 0 && p.StationName.ToLower().Contains(keyword)) ||
                    (mode == 1 && (p.CreatedBy?.ToLower().Contains(keyword) ?? false)) ||
                    (mode == 2 && (CommanderName?.ToLower().Contains(keyword) ?? false))
                );

                Projects.Clear();
                foreach (var proj in filtered)
                    Projects.Add(proj);
            }
            catch (Exception ex)
            {
                Logger.Log($"Search failed: {ex.Message}", "Warning");
            }
        }

        private void CompletedProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Project project && projectDb != null)
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to mark project '{project.StationName}' as completed?",
                    "Confirm Completion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        projectDb.ArchiveProject(project.MarketId);
                        Projects.Remove(project);

                        if (SelectedProjects.Contains(project))
                        {
                            SelectedProjects.Remove(project);
                            if (SelectedProjects.Count == 1)
                            {
                                LoadProjects();
                                LoadMaterialsForProject(SelectedProjects.First());
                                overlayManager.ShowOverlay(CurrentProjectMaterials);
                            }
                            else if (SelectedProjects.Count > 1)
                            {
                                LoadProjects();
                                LoadMaterialsForProjects(SelectedProjects);
                                overlayManager.ShowOverlay(CurrentProjectMaterials);
                            }
                            else
                            {
                                LoadProjects();
                                CurrentProjectMaterials.Clear();
                            }
                        }

                        Logger.Log($"Project '{project.StationName}' has been archived.", "Success");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to archive project: {ex.Message}", "Warning");
                    }
                }
            }
        }

        private void OwnerProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Project proj && projectDb != null)
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the project \"{proj.StationName}\"?\nThis cannot be undone.",
                    "Confirm Deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        projectDb.DeleteProject(proj.MarketId);
                        Projects.Remove(proj);
                        Logger.Log($"Project deleted: {proj.StationName} ({proj.MarketId})", "Success");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error deleting project: {ex.Message}", "Warning");
                        MessageBox.Show("Failed to delete the project.\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void RefreshCargoButton_Click(object sender, RoutedEventArgs e)
        {
            // 1) Clear the in-memory list in JournalProcessor:
            journalProcessor.FleetCarrierCargoItems.Clear();

            // 2) Clear the ObservableCollection so the UI updates immediately:
            FleetCarrierCargoItems.Clear();

            // 3) Clear the CarrierMaterialOverview so “Still Needed” is reset:
            CarrierMaterialOverview.Clear();

            Logger.Log("Carrier cargo list cleared.", "Info");
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("Manual refresh triggered.", "Info");
            Task.Run(() =>
            {
                try
                {
                    journalProcessor.ScanForDepotEvents();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Journal scan failed: {ex.Message}", "Warning");
                }
                Dispatcher.Invoke(() => ScanForDepotAndRefreshUI());
            });
            LastUpdate = DateTime.Now.ToString("HH:mm:ss");
        }

        private void OpenArchive_Click(object sender, RoutedEventArgs e)
        {
            if (archiveWindow == null || !archiveWindow.IsLoaded)
            {
                archiveWindow = new ArchiveWindow(projectDb);
                archiveWindow.Owner = this;
                archiveWindow.Closed += (s, _) => archiveWindow = null;
                archiveWindow.Show();
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;

            if (settingsWindow.ShowDialog() == true)
            {
                var key = settingsWindow.InaraApiKey;
                var color = settingsWindow.HighlightColor;

                try
                {
                    var parsedColor = (Color)ColorConverter.ConvertFromString(color);
                    var solidBrush = new SolidColorBrush(parsedColor);
                    var overlayBrush = new SolidColorBrush(Color.FromArgb(0x22, parsedColor.R, parsedColor.G, parsedColor.B));

                    Application.Current.Resources["HighlightBrush"] = solidBrush;
                    Application.Current.Resources["HighlightOverlayBrush"] = overlayBrush;
                }
                catch
                {
                    Application.Current.Resources["HighlightBrush"] = new SolidColorBrush(Colors.Orange);
                    Application.Current.Resources["HighlightOverlayBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 255, 140, 0));
                }

                Logger.Log("Settings updated.", "Success");
            }
        }

        private void OpenPlanner_Click(object sender, RoutedEventArgs e)
        {
            if (plannerWindow == null || !plannerWindow.IsLoaded)
            {
                plannerWindow = new ColonizationPlanner.ColonizationPlanner();
                plannerWindow.Owner = null;
                plannerWindow.Closed += (s, _) => plannerWindow = null;
                plannerWindow.Show();
            }
        }

        private void RefreshCarrierCargo()
        {
            FleetCarrierCargoItems.Clear();
            foreach (var item in journalProcessor.FleetCarrierCargoItems)
                FleetCarrierCargoItems.Add(item);
        }

        private void UpdateCarrierMaterialOverview()
        {
            CarrierMaterialOverview.Clear();

            foreach (var item in journalProcessor.FleetCarrierCargoItems)
            {
                var match = CurrentProjectMaterials.FirstOrDefault(m =>
                    string.Equals(m.Material, item.Name, StringComparison.OrdinalIgnoreCase));

                int stillNeeded = 0;
                if (match != null)
                {
                    int remaining = match.Required - match.Provided;
                    stillNeeded = Math.Max(remaining - item.Quantity, 0);
                }

                CarrierMaterialOverview.Add(new CarrierMaterialStatus
                {
                    Name = item.Name,
                    Transferred = item.Quantity,
                    StillNeeded = stillNeeded
                });
            }

            OnPropertyChanged(nameof(CarrierMaterialOverview));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
