using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Services;
using EliteDangerousStationManager.Overlay;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;

namespace EliteDangerousStationManager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private ProjectDatabaseService projectDb;
        private readonly JournalProcessor journalProcessor;
        private readonly OverlayManager overlayManager;

        private decimal totalCostToday = 0;
        private decimal totalSaleToday = 0;
        private readonly HashSet<string> processedEntries = new HashSet<string>();
        private readonly List<(DateTime Timestamp, decimal Amount)> hourlyTransactions = new();

        private static bool PrimaryDbDisabledForSession = false;





        // We no longer keep a separate “connectionString” field; we let ProjectDatabaseService pick it up:
        // private readonly string connectionString = ConfigurationManager.ConnectionStrings["EliteDB"].ConnectionString;

        // Child windows
        private ArchiveWindow archiveWindow;
        private ColonizationPlanner.ColonizationPlanner plannerWindow;

        // Observable collections bound to the UI
        public ObservableCollection<LogEntry> LogEntries => Logger.Entries;
        public ObservableCollection<Project> Projects { get; set; } = new ObservableCollection<Project>();
        public ObservableCollection<ProjectMaterial> CurrentProjectMaterials { get; set; } = new ObservableCollection<ProjectMaterial>();

        // We have removed any “FleetCarrierCargoItems” and “CarrierMaterialOverview” from MainWindow
        // because JournalProcessor no longer exposes them:
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
                    ShowOverlayIfAllowed();
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
                    ShowOverlayIfAllowed();
                }
                else if (_selectedProjects.Count > 1)
                {
                    LoadMaterialsForProjects(_selectedProjects);
                    ShowOverlayIfAllowed();
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

            // 3) Read Commander name and selected journal file
            string selectedJournalFile = ReadCommanderNameFromJournal();

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

            // Tell it to start from selected file if available
            if (!string.IsNullOrWhiteSpace(selectedJournalFile))
            {
                journalProcessor.ForceStartFrom(selectedJournalFile);
            }

            // — Re‐add this subscription —
            journalProcessor.CarrierCargoChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCarrierCargo();
                    UpdateCarrierMaterialOverview();
                });
            };

            // We no longer subscribe to “CarrierCargoChanged” or “NewStationDocked”
            // because those members were removed from JournalProcessor.

            // 6) Instantiate OverlayManager
            overlayManager = new OverlayManager();

            Logger.Log("Application started.", "Success");

            // 7) Defer DB initialization & initial scan until the window is shown
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        // ✅ No more dbMode. Always initialize ProjectDatabaseService normally.
                        projectDb = new ProjectDatabaseService();
                        Dispatcher.Invoke(() => SetDatabaseStatusIndicator(true));
                        Dispatcher.Invoke(() => LoadProjects());
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Database unavailable at startup: {ex.Message}", "Warning");
                        Dispatcher.Invoke(() => SetDatabaseStatusIndicator(false));
                        projectDb = null;
                    }

                    // ✅ B) Perform the very first scan for depot events (off UI thread)
                    try
                    {
                        journalProcessor.ScanForDepotEvents();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Initial journal scan failed: {ex.Message}", "Warning");
                    }

                    // ✅ C) Refresh UI on the UI thread
                    Dispatcher.Invoke(async () => await ScanForDepotAndRefreshUI());
                });

                // ✅ 8) Start the 5-second timer
                StartTimer();
            }), DispatcherPriority.ApplicationIdle);


        }


        private void RefreshCarrierCargo()
        {
            FleetCarrierCargoItems.Clear();

            Logger.Log("Refreshing carrier cargo...", "Debug");
            foreach (var item in journalProcessor.FleetCarrierCargoItems)
            {
                Logger.Log($"→ {item.Name} x{item.Quantity}", "Debug");

                FleetCarrierCargoItems.Add(new CargoItem
                {
                    Name = item.Name,
                    Quantity = item.Quantity
                });
            }
        }
        private static string Normalize(string input)
        {
            return input?.ToLowerInvariant().Replace(" ", "").Trim() ?? "";
        }



        private void UpdateCarrierMaterialOverview()
        {
            CarrierMaterialOverview.Clear();

            foreach (var item in journalProcessor.FleetCarrierCargoItems)
            {
                var match = CurrentProjectMaterials
                    .FirstOrDefault(m => Normalize(m.Material) == Normalize(item.Name));


                int stillNeeded = 0;


                if (match != null)
                {

                    stillNeeded = match.Needed - item.Quantity;
                }


                CarrierMaterialOverview.Add(new CarrierMaterialStatus
                {
                    Name = match?.Material ?? item.Name,
                    Transferred = item.Quantity,
                    StillNeeded = stillNeeded
                });

            }

            OnPropertyChanged(nameof(CarrierMaterialOverview));
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

        private string ReadCommanderNameFromJournal()
        {
            string journalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Saved Games",
                "Frontier Developments",
                "Elite Dangerous"
            );

            var recentFiles = Directory
                .GetFiles(journalDir, "Journal.*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5)
                .ToList();

            var commanderMap = new Dictionary<string, string>(); // FilePath -> CommanderName

            foreach (var file in recentFiles)
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    string allText = sr.ReadToEnd();

                    var lines = allText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse();
                    foreach (var line in lines)
                    {
                        var json = JObject.Parse(line);
                        if (json["event"]?.ToString() == "Commander")
                        {
                            string name = json["Name"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(name) && !commanderMap.ContainsValue(name))
                            {
                                commanderMap[file] = name;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            if (commanderMap.Count == 0)
            {
                Logger.Log("No commander name found in recent journal files.", "Warning");
                return null;
            }

            var uniqueCommanders = commanderMap.Values.Distinct().ToList();
            if (uniqueCommanders.Count == 1)
            {
                CommanderName = uniqueCommanders[0];
                Dispatcher.Invoke(() =>
                    CommanderNameTextBlock.Text = $"Commander: {CommanderName}"
                );
                Logger.Log($"Commander selected: {CommanderName}", "Success");
                return commanderMap.First().Key;
            }
            else
            {
                string selectedFile = null;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var window = new SelectCommanderWindow(uniqueCommanders);
                    if (window.ShowDialog() == true)
                    {
                        CommanderName = window.SelectedCommander;
                        selectedFile = commanderMap.FirstOrDefault(kvp => kvp.Value == CommanderName).Key;
                        CommanderNameTextBlock.Text = $"Commander: {CommanderName}";
                        Logger.Log($"Commander selected: {CommanderName}", "Success");
                    }
                });

                return selectedFile;
            }

        }

        public async Task LoadProjects(string filter = "", int filterMode = 0)
        {
            try
            {
                var projects = await Task.Run(() =>
                {
                    var allProjects = new List<Project>();

                    // ✅ Load from Local SQLite
                    string localDbPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "EDStationManager",
                        "stations.db"
                    );

                    using (var localConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}"))
                    {
                        localConn.Open();

                        var ensure = localConn.CreateCommand();
                        ensure.CommandText = @"
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
                        ResourceName TEXT,
                        RequiredAmount INTEGER,
                        ProvidedAmount INTEGER,
                        Payment INTEGER
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
                        ensure.ExecuteNonQuery();

                        var cmd = localConn.CreateCommand();
                        cmd.CommandText = "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt FROM Projects";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                allProjects.Add(new Project
                                {
                                    MarketId = reader.GetInt64(0),
                                    SystemName = reader.GetString(1),
                                    StationName = reader.GetString(2),
                                    CreatedBy = reader.IsDBNull(3) ? "Unknown" : reader.GetString(3),
                                    CreatedAt = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4),
                                    Source = "Local"
                                });
                            }
                        }
                    }

                    // ✅ Only load from Server DB if in Server mode
                    if (App.CurrentDbMode == "Server")
                    {
                        try
                        {
                            var serverDb = new ProjectDatabaseService("Server");
                            var serverProjects = serverDb.LoadProjects();

                            foreach (var proj in serverProjects)
                                proj.Source = "Server";

                            allProjects.AddRange(serverProjects);
                            Logger.Log("✅ Server DB projects loaded.", "Info");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"⚠ Could not connect to Server DB: {ex.Message}", "Warning");
                        }
                    }

                    return allProjects;
                });

                // ✅ Update UI
                Projects.Clear();
                foreach (var p in projects
                    .OrderBy(p => p.Source == "Server")
                    .ThenBy(p => p.SystemName))
                {
                    Projects.Add(p);
                }

                // ✅ Clearer final log
                if (App.CurrentDbMode == "Server")
                    Logger.Log($"✅ Loaded {projects.Count} projects (Local + Server).", "Info");
                else
                    Logger.Log($"✅ Loaded {projects.Count} projects (Local only).", "Info");
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Failed to load projects: {ex.Message}", "Error");
                Projects.Clear();
            }
        }

        private void StartTimer()
        {

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) =>
            {
                Logger.Log("⏱ Timer tick", "Debug");

                Task.Run(() =>
                {
                    try
                    {
                        journalProcessor.ScanForDepotEvents();

                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                ScanForDepotAndRefreshUI();
                                UpdateCarrierMaterialOverview();
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"⚠ UI refresh failed: {ex.Message}", "Warning");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"❌ Background task failed: {ex.Message}", "Error");
                    }
                });


                LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            };

            LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            timer.Start();
        }


        private bool overlayVisible = true; // controlled by your Hide/Show button

        private void ShowOverlayIfAllowed()
        {
            if (!overlayVisible) return;
            overlayManager.ShowOverlay(CurrentProjectMaterials);
        }


        private void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (overlayVisible)
            {
                overlayVisible = false;
                overlayManager.CloseOverlay();
                ToggleOverlayButton.Content = "Show Overlay";
                Logger.Log("Overlay hidden.", "Info");
            }
            else
            {
                overlayVisible = true;
                // re-show only if you have materials selected
                if (SelectedProjects.Count == 1)
                {
                    LoadMaterialsForProject(SelectedProjects.First());
                }
                else if (SelectedProjects.Count > 1)
                {
                    LoadMaterialsForProjects(SelectedProjects);
                }
                ShowOverlayIfAllowed();
                ToggleOverlayButton.Content = "Hide Overlay";
                Logger.Log("Overlay shown.", "Info");
            }
        }



        private async Task ScanForDepotAndRefreshUI()
        {
            try
            {
                // ✅ Reload both Local + Server projects
                await LoadProjects();                // Wait here because it's async

                // ✅ Preserve selected project IDs
                var selectedIds = SelectedProjects.Select(p => p.MarketId).ToHashSet();

                // ✅ Reapply Search filter if there's any text in SearchBox
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    string keyword = SearchBox.Text.ToLower();
                    int mode = SearchMode.SelectedIndex;

                    // Filter the in-memory Projects list (keeps filtered display)
                    var filtered = Projects
                        .Where(p =>
                            (mode == 0 && p.StationName.ToLower().Contains(keyword)) ||
                            (mode == 1 && (p.CreatedBy?.ToLower().Contains(keyword) ?? false)) ||
                            (mode == 2 && p.SystemName.ToLower().Contains(keyword))
                        )
                        .OrderBy(p => p.Source == "Server") // Local first
                        .ThenBy(p => p.SystemName)
                        .ToList();

                    Projects.Clear();
                    foreach (var p in filtered)
                        Projects.Add(p);
                }

                // ✅ Restore selected projects
                SelectedProjects.Clear();
                foreach (var p in Projects.Where(x => selectedIds.Contains(x.MarketId)))
                    SelectedProjects.Add(p);

                RefreshMaterialsAndMaybeShowOverlay();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error reloading projects: {ex.Message}", "Warning");
            }
        }

        private void LoadMaterialsForProjects(IEnumerable<Project> projects)
        {
            if (projects == null || !projects.Any()) return;

            var combined = new Dictionary<string, ProjectMaterial>();

            foreach (var project in projects)
            {
                try
                {
                    if (project.Source == "Local")
                    {
                        // ✅ SQLite for Local
                        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=stations.db");
                        conn.Open();

                        var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                    SELECT Material, RequiredAmount, ProvidedAmount
                    FROM ProjectResources
                    WHERE MarketID = @mid
                      AND RequiredAmount > ProvidedAmount
                    ORDER BY Material;";
                        cmd.Parameters.AddWithValue("@mid", project.MarketId);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string name = reader.GetString(0);
                            int required = reader.GetInt32(1);
                            int provided = reader.GetInt32(2);
                            int needed = required - provided;

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
                    else
                    {
                        // ✅ MySQL for Server
                        string serverConnStr = ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
                        if (string.IsNullOrWhiteSpace(serverConnStr)) continue;

                        using var conn = new MySql.Data.MySqlClient.MySqlConnection(serverConnStr);
                        conn.Open();

                        var cmd = new MySql.Data.MySqlClient.MySqlCommand(@"
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
                            int needed = required - provided;

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
                    Logger.Log($"Error loading materials for {project.StationName}: {ex.Message}", "Warning");
                }
            }

            // ✅ Update UI with merged materials
            CurrentProjectMaterials.Clear();
            foreach (var mat in combined.Values.Where(m => m.Needed > 0).OrderBy(m => m.Material))
                CurrentProjectMaterials.Add(mat);

            Logger.Log($"Loaded {CurrentProjectMaterials.Count} combined materials.", "Info");
            UpdateCarrierMaterialOverview();
        }

        private void LoadMaterialsForProject(Project project)
        {
            CurrentProjectMaterials.Clear();
            if (project == null) return;

            try
            {
                if (project.Source == "Local")
                {
                    // ✅ SQLite for Local
                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=stations.db");
                    conn.Open();

                    var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                SELECT Material, RequiredAmount, ProvidedAmount
                FROM ProjectResources
                WHERE MarketID = @mid
                  AND RequiredAmount > ProvidedAmount
                ORDER BY Material;";
                    cmd.Parameters.AddWithValue("@mid", project.MarketId);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        int required = reader.GetInt32(1);
                        int provided = reader.GetInt32(2);
                        int needed = required - provided;

                        CurrentProjectMaterials.Add(new ProjectMaterial
                        {
                            Material = reader.GetString(0),
                            Required = required,
                            Provided = provided,
                            Needed = needed
                        });
                    }
                }
                else
                {
                    // ✅ MySQL for Server
                    string serverConnStr = ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
                    if (string.IsNullOrWhiteSpace(serverConnStr)) return;

                    using var conn = new MySql.Data.MySqlClient.MySqlConnection(serverConnStr);
                    conn.Open();

                    var cmd = new MySql.Data.MySqlClient.MySqlCommand(@"
                SELECT ResourceName, RequiredAmount, ProvidedAmount
                FROM ProjectResources
                WHERE MARKETID = @mid
                  AND RequiredAmount > ProvidedAmount
                ORDER BY ResourceName;", conn);
                    cmd.Parameters.AddWithValue("@mid", project.MarketId);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        int required = reader.GetInt32("RequiredAmount");
                        int provided = reader.GetInt32("ProvidedAmount");
                        int needed = required - provided;

                        CurrentProjectMaterials.Add(new ProjectMaterial
                        {
                            Material = reader.GetString("ResourceName"),
                            Required = required,
                            Provided = provided,
                            Needed = needed
                        });
                    }
                }

                Logger.Log($"Loaded {CurrentProjectMaterials.Count} materials for {project}", "Info");
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading materials: " + ex.Message, "Warning");
            }

            UpdateCarrierMaterialOverview();
        }

        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Project project)
            {
                // ✅ Toggle the project in the ListBox selection
                if (ProjectsListBox.SelectedItems.Contains(project))
                    ProjectsListBox.SelectedItems.Remove(project);
                else
                    ProjectsListBox.SelectedItems.Add(project);

                // ✅ Mirror ListBox.SelectedItems into SelectedProjects
                SelectedProjects.Clear();
                foreach (Project selected in ProjectsListBox.SelectedItems)
                    SelectedProjects.Add(selected);

                RefreshMaterialsAndMaybeShowOverlay();

                OnPropertyChanged(nameof(SelectedProjects));
            }
        }

        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string keyword = SearchBox.Text.ToLower();
            int mode = SearchMode.SelectedIndex;

            try
            {
                // ✅ Run filtering in a background Task to avoid blocking UI
                var filteredProjects = await Task.Run(() =>
                {
                    string commander = CommanderName?.ToLower() ?? "unknown";
                    string keywordSafe = string.IsNullOrWhiteSpace(keyword) ? commander : keyword.ToLower().Trim();

                    IEnumerable<Project> results = Projects;

                    results = results.Where(p =>
                        (mode == 0 && p.StationName.ToLower().Contains(keywordSafe)) ||
                        (mode == 1 && (p.CreatedBy?.ToLower().Contains(keywordSafe) ?? false)) ||
                        (mode == 2 && p.SystemName.ToLower().Contains(keywordSafe))
                    );

                    return results
                        .OrderBy(p => p.Source == "Server")
                        .ThenBy(p => p.SystemName)
                        .ToList();
                });

                // ✅ UI thread: Update the collection in one go
                Dispatcher.Invoke(() =>
                {
                    Projects.Clear();
                    foreach (var p in filteredProjects)
                        Projects.Add(p);
                });

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
                                ShowOverlayIfAllowed();
                            }
                            else if (SelectedProjects.Count > 1)
                            {
                                LoadProjects();
                                LoadMaterialsForProjects(SelectedProjects);
                                ShowOverlayIfAllowed();
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
            if (sender is Button btn && btn.DataContext is Project proj)
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
                        if (proj.Source == "Local")
                        {
                            // ✅ Delete from SQLite
                            string localDbPath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "EDStationManager",
                                "stations.db"
                            );

                            using var localConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
                            localConn.Open();

                            using (var deleteResources = localConn.CreateCommand())
                            {
                                deleteResources.CommandText = "DELETE FROM ProjectResources WHERE MarketID = @mid;";
                                deleteResources.Parameters.AddWithValue("@mid", proj.MarketId);
                                deleteResources.ExecuteNonQuery();
                            }

                            using (var deleteProject = localConn.CreateCommand())
                            {
                                deleteProject.CommandText = "DELETE FROM Projects WHERE MarketID = @mid;";
                                deleteProject.Parameters.AddWithValue("@mid", proj.MarketId);
                                deleteProject.ExecuteNonQuery();
                            }

                            Logger.Log($"🗑️ Deleted LOCAL project: {proj.StationName} ({proj.MarketId})", "Success");
                        }
                        else if (proj.Source == "Server")
                        {
                            // ✅ Delete from MySQL
                            using var serverConn = new MySql.Data.MySqlClient.MySqlConnection(projectDb.ConnectionString);
                            serverConn.Open();

                            using (var deleteResources = serverConn.CreateCommand())
                            {
                                deleteResources.CommandText = "DELETE FROM ProjectResources WHERE MarketID = @mid;";
                                deleteResources.Parameters.AddWithValue("@mid", proj.MarketId);
                                deleteResources.ExecuteNonQuery();
                            }

                            using (var deleteProject = serverConn.CreateCommand())
                            {
                                deleteProject.CommandText = "DELETE FROM Projects WHERE MarketID = @mid;";
                                deleteProject.Parameters.AddWithValue("@mid", proj.MarketId);
                                deleteProject.ExecuteNonQuery();
                            }

                            Logger.Log($"🗑️ Deleted SERVER project: {proj.StationName} ({proj.MarketId})", "Success");
                        }

                        // ✅ Remove from UI
                        Projects.Remove(proj);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"❌ Error deleting project: {ex.Message}", "Error");
                        MessageBox.Show("Failed to delete the project.\n" + ex.Message,
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }


        private async void SyncProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Project project)
            {
                Logger.Log($"[SYNC] Starting sync for project: {project.StationName} (MarketID {project.MarketId})", "Info");

                // 🚨 Skip if already from the server
                if (project.Source == "Server")
                {
                    MessageBox.Show($"'{project.StationName}' is already on the server.", "Sync Skipped",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 🔴 Disable the button during sync
                btn.IsEnabled = false;

                await Task.Run(async () =>
                {
                    try
                    {
                        // ✅ Use the DB already chosen by ProjectDatabaseService
                        using var serverConn = new MySql.Data.MySqlClient.MySqlConnection(projectDb.ConnectionString);
                        await serverConn.OpenAsync();

                        // ✅ Open Local SQLite
                        string localDbPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "EDStationManager",
                            "stations.db"
                        );
                        using var localConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
                        await localConn.OpenAsync();

                        // ✅ Read project from local DB
                        using (var getProject = localConn.CreateCommand())
                        {
                            getProject.CommandText = "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt FROM Projects WHERE MarketID=@mid";
                            getProject.Parameters.AddWithValue("@mid", project.MarketId);

                            using var reader = await getProject.ExecuteReaderAsync();
                            if (await reader.ReadAsync())
                            {
                                var upsertProj = new MySql.Data.MySqlClient.MySqlCommand(@"
                            INSERT INTO Projects (MarketID, SystemName, StationName, CreatedBy, CreatedAt)
                            VALUES (@mid, @sys, @st, @cb, @ca)
                            ON DUPLICATE KEY UPDATE 
                                SystemName=@sys,
                                StationName=@st,
                                CreatedBy=@cb,
                                CreatedAt=@ca;", serverConn);

                                upsertProj.Parameters.AddWithValue("@mid", reader.GetInt64(0));
                                upsertProj.Parameters.AddWithValue("@sys", reader.GetString(1));
                                upsertProj.Parameters.AddWithValue("@st", reader.GetString(2));
                                upsertProj.Parameters.AddWithValue("@cb", reader.GetString(3));

                                // ✅ Handle NULL CreatedAt safely
                                DateTime createdAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4);
                                upsertProj.Parameters.AddWithValue("@ca", createdAt);

                                await upsertProj.ExecuteNonQueryAsync();
                            }
                        }

                        // ✅ Read and transfer resources
                        using (var getResources = localConn.CreateCommand())
                        {
                            // 🔄 FIX: Use ResourceName instead of Material
                            getResources.CommandText = "SELECT ResourceName, RequiredAmount, ProvidedAmount, Payment FROM ProjectResources WHERE MarketID=@mid";
                            getResources.Parameters.AddWithValue("@mid", project.MarketId);

                            using var resReader = await getResources.ExecuteReaderAsync();
                            while (await resReader.ReadAsync())
                            {
                                var upsertRes = new MySql.Data.MySqlClient.MySqlCommand(@"
                            INSERT INTO ProjectResources (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
                            VALUES (@mid, @mat, @req, @prov, @pay)
                            ON DUPLICATE KEY UPDATE 
                                RequiredAmount=@req,
                                ProvidedAmount=@prov,
                                Payment=@pay;", serverConn);

                                upsertRes.Parameters.AddWithValue("@mid", project.MarketId);
                                upsertRes.Parameters.AddWithValue("@mat", resReader.GetString(0));
                                upsertRes.Parameters.AddWithValue("@req", resReader.GetInt32(1));
                                upsertRes.Parameters.AddWithValue("@prov", resReader.GetInt32(2));

                                // ✅ Handle NULL payments safely
                                int payment = resReader.IsDBNull(3) ? 0 : resReader.GetInt32(3);
                                upsertRes.Parameters.AddWithValue("@pay", payment);

                                await upsertRes.ExecuteNonQueryAsync();
                            }
                        }

                        // ✅ Remove from local DB (cleanup after successful sync)
                        using (var deleteRes = localConn.CreateCommand())
                        {
                            deleteRes.CommandText = "DELETE FROM ProjectResources WHERE MarketID=@mid";
                            deleteRes.Parameters.AddWithValue("@mid", project.MarketId);
                            await deleteRes.ExecuteNonQueryAsync();
                        }

                        using (var deleteProject = localConn.CreateCommand())
                        {
                            deleteProject.CommandText = "DELETE FROM Projects WHERE MarketID=@mid";
                            deleteProject.Parameters.AddWithValue("@mid", project.MarketId);
                            await deleteProject.ExecuteNonQueryAsync();
                        }

                        // ✅ Update UI
                        await Dispatcher.InvokeAsync(() =>
                        {
                            Logger.Log($"✅ Project '{project.StationName}' synced successfully and removed from local DB.", "Success");
                            MessageBox.Show($"Project '{project.StationName}' synced to server and removed from local DB!",
                                "Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                            LoadProjects(); // Refresh list
                            btn.IsEnabled = true; // re-enable button
                        });
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            Logger.Log($"❌ Sync failed for {project.StationName}: {ex.Message}", "Error");
                            MessageBox.Show($"Sync failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            btn.IsEnabled = true; // re-enable button even if failed
                        });
                    }
                });
            }
        }

        private void RefreshCargoButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear internal cargo state in journalProcessor
            journalProcessor.ClearFleetCarrierCargo();

            FleetCarrierCargoItems.Clear();
            CarrierMaterialOverview.Clear();
            Logger.Log("Fleet Carrier cargo and material overview cleared.", "Info");
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
                    Application.Current.Resources["HighlightBrush"] =
                        new SolidColorBrush(Colors.Orange);
                    Application.Current.Resources["HighlightOverlayBrush"] =
                        new SolidColorBrush(Color.FromArgb(0x22, 255, 140, 0));
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
        private void RefreshMaterialsAndMaybeShowOverlay()
        {
            if (SelectedProjects.Count == 1)
            {
                LoadMaterialsForProject(SelectedProjects.First());
            }
            else if (SelectedProjects.Count > 1)
            {
                LoadMaterialsForProjects(SelectedProjects);
            }
            else
            {
                CurrentProjectMaterials.Clear();
                return;
            }

            ShowOverlayIfAllowed();
        }


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
