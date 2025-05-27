using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
        private readonly ProjectDatabaseService projectDb;

        private int? lastSelectedIndex = null;

        private readonly JournalProcessor journalProcessor;
        private readonly OverlayManager overlayManager;
        private readonly string connectionString = ConfigurationManager.ConnectionStrings["EliteDB"].ConnectionString;
        private InaraService inaraService;
        public string CommanderName { get; private set; }



        public ObservableCollection<LogEntry> LogEntries => Logger.Entries;
        public ObservableCollection<Project> Projects { get; set; } = new ObservableCollection<Project>();
        public ObservableCollection<ProjectMaterial> CurrentProjectMaterials { get; set; } = new ObservableCollection<ProjectMaterial>();
        public ObservableCollection<CargoItem> CargoItems { get; set; } = new ObservableCollection<CargoItem>();
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
                UpdateCombinedMaterials();
            }
        }


        public MainWindow()
        {

            InitializeComponent();
            string configPath = "settings.config";
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                if (lines.Length > 1)
                {
                    string color = lines[1];
                    try
                    {
                        var parsedColor = (Color)ColorConverter.ConvertFromString(color);
                        Application.Current.Resources["HighlightBrush"] = new SolidColorBrush(parsedColor);
                        Application.Current.Resources["HighlightOverlayBrush"] = new SolidColorBrush(Color.FromArgb(0x22, parsedColor.R, parsedColor.G, parsedColor.B));
                    }
                    catch
                    {
                        Application.Current.Resources["HighlightBrush"] = new SolidColorBrush(Colors.Orange);
                        Application.Current.Resources["HighlightOverlayBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 255, 140, 0)); // semi-transparent
                    }
                }
            }
        
    

            DataContext = this;

            string journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Saved Games", "Frontier Developments", "Elite Dangerous");

            projectDb = new ProjectDatabaseService(connectionString);
            journalProcessor = new JournalProcessor(journalPath, CommanderName ?? "UnknownCommander");

            overlayManager = new OverlayManager();

            // ✅ Initialize InaraService here
            ReadCommanderNameFromJournal();

            inaraService = new InaraService(
                CommanderName ?? "UnknownCommander",
                "YOUR_INARA_API_KEY_HERE",
                () => SelectedProject,
                () => CurrentProjectMaterials.ToList(),
                () => CargoItems.ToList()
            );
            inaraService.Start();

            this.Closed += (s, e) => overlayManager.CloseOverlay();

            Logger.Log("Application started.", "Success");

            LoadProjects();
            RefreshJournalData();
            StartTimer();
        }

        private void ReadCommanderNameFromJournal()
        {
            try
            {
                string journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Saved Games", "Frontier Developments", "Elite Dangerous");

                var latestFile = Directory.GetFiles(journalPath, "Journal.*.log")
                                          .OrderByDescending(File.GetLastWriteTime)
                                          .FirstOrDefault();

                if (latestFile == null)
                {
                    Logger.Log("No journal file found.", "Warning");
                    return;
                }

                using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd().Split('\n').Reverse();

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
            var loaded = projectDb.LoadProjects();

            // Apply filtering if needed
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
        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Project clickedProject)
            {
                int clickedIndex = Projects.IndexOf(clickedProject);
                bool isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

                if (isShiftPressed && lastSelectedIndex.HasValue)
                {
                    int start = Math.Min(lastSelectedIndex.Value, clickedIndex);
                    int end = Math.Max(lastSelectedIndex.Value, clickedIndex);

                    for (int i = start; i <= end; i++)
                    {
                        var proj = Projects[i];
                        if (!SelectedProjects.Contains(proj))
                            SelectedProjects.Add(proj);
                    }
                }
                else
                {
                    if (SelectedProjects.Contains(clickedProject))
                        SelectedProjects.Remove(clickedProject);
                    else
                        SelectedProjects.Add(clickedProject);

                    lastSelectedIndex = clickedIndex;
                }

                UpdateCombinedMaterials();
            }
        }

        private void UpdateCombinedMaterials()
        {
            CurrentProjectMaterials.Clear();

            if (!SelectedProjects.Any()) return;

            var combined = new Dictionary<string, ProjectMaterial>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            foreach (var proj in SelectedProjects)
            {
                var cmd = new MySqlCommand(@"
                    SELECT ResourceName, RequiredAmount, ProvidedAmount
                    FROM ProjectResources
                    WHERE MarketID = @mid;", conn);

                cmd.Parameters.AddWithValue("@mid", proj.MarketId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString("ResourceName");
                    int required = reader.GetInt32("RequiredAmount");
                    int provided = reader.GetInt32("ProvidedAmount");

                    if (!combined.ContainsKey(name))
                    {
                        combined[name] = new ProjectMaterial
                        {
                            Material = name,
                            Required = 0,
                            Provided = 0,
                            Needed = 0
                        };
                    }

                    var mat = combined[name];
                    mat.Required += required;
                    mat.Provided += provided;
                    mat.Needed = Math.Max(mat.Required - mat.Provided, 0);
                }
                reader.Close();
            }

            foreach (var mat in combined.Values.OrderBy(m => m.Material))
            {
                if (mat.Needed > 0)
                    CurrentProjectMaterials.Add(mat);
            }

            Logger.Log($"Combined materials for {SelectedProjects.Count} selected projects.", "Info");
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string keyword = SearchBox.Text.ToLower();
            int mode = SearchMode.SelectedIndex;

            var filtered = projectDb.LoadProjects().Where(p =>
                (mode == 0 && p.StationName.ToLower().Contains(keyword)) ||
                (mode == 1 && (p.CreatedBy?.ToLower().Contains(keyword) ?? false))
            );

            Projects.Clear();
            foreach (var proj in filtered)
                Projects.Add(proj);
        }


        private void LoadMaterialsForProject(Project project)
        {
            CurrentProjectMaterials.Clear();
            if (project == null) return;

            try
            {
                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                var cmd = new MySqlCommand(@"
                    SELECT ResourceName, RequiredAmount, ProvidedAmount
                    FROM ProjectResources
                    WHERE MarketID = @mid
                    ORDER BY ResourceName;", conn);

                cmd.Parameters.AddWithValue("@mid", project.MarketId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int required = reader.GetInt32("RequiredAmount");
                    int provided = reader.GetInt32("ProvidedAmount");
                    int needed = Math.Max(required - provided, 0);

                    if (needed > 0)
                    {
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
                Logger.Log("Error loading materials: " + ex.Message, "Error");
            }
        }

        private void RefreshJournalData()
        {
            var project = journalProcessor.FindLatestConstructionEvent(
                out string system, out string station, out long marketId);

            if (project != null)
            {
                Logger.Log($"Found depot at MarketID {marketId} with {project.ResourcesRequired.Count} materials", "Info");

                if (project.ConstructionComplete && project.ResourcesRequired.All(r => r.ProvidedAmount >= r.RequiredAmount))
                {
                    projectDb.ArchiveProject(marketId);
                    Logger.Log($"Project complete at MarketID {marketId}. Removed.", "Success");
                }
                else
                {
                    projectDb.SaveProject(new Project
                    {
                        MarketId = marketId,
                        SystemName = system,
                        StationName = station,
                        CreatedBy = CommanderName ?? "Unknown"
                    });

                    using var conn = new MySqlConnection(connectionString);
                    conn.Open();

                    foreach (var res in project.ResourcesRequired)
                    {
                        string rawName = res.Name ?? "UnknownRaw";
                        string localName = res.Name_Localised ?? res.Name ?? "Unknown";

                        Logger.Log($"Writing: {localName} / {rawName} | Req: {res.RequiredAmount}, Prov: {res.ProvidedAmount}, Pay: {res.Payment}", "Debug");

                        var cmd = new MySqlCommand(@"
                    INSERT INTO ProjectResources (MarketID, ResourceName, RawName, RequiredAmount, ProvidedAmount, Payment)
                    VALUES (@mid, @name, @raw, @req, @prov, @pay)
                    ON DUPLICATE KEY UPDATE
                        RequiredAmount = @req,
                        ProvidedAmount = @prov,
                        Payment = @pay;", conn);

                        cmd.Parameters.AddWithValue("@mid", marketId);
                        cmd.Parameters.AddWithValue("@name", localName);
                        cmd.Parameters.AddWithValue("@raw", rawName);
                        cmd.Parameters.AddWithValue("@req", res.RequiredAmount);
                        cmd.Parameters.AddWithValue("@prov", res.ProvidedAmount);
                        cmd.Parameters.AddWithValue("@pay", res.Payment);

                        try
                        {
                            int affected = cmd.ExecuteNonQuery();
                            Logger.Log($"Updated {localName}: rows affected = {affected}", "Info");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"DB error for {localName}: {ex.Message}", "Error");
                        }
                    }

                    Logger.Log($"Updated resources for MarketID {marketId}", "Success");
                }

                LoadProjects();
                SelectedProject = Projects.FirstOrDefault(p => p.MarketId == marketId);
            }
            else
            {
                Logger.Log("No construction depot project found in journal.", "Warning");
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

                // Apply user-defined highlight color
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
                    Application.Current.Resources["HighlightOverlayBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 255, 140, 0)); // semi-transparent
                }


                Logger.Log("Settings updated.", "Success");

                inaraService = new InaraService(
                    CommanderName ?? "UnknownCommander",
                    key,
                    () => SelectedProject,
                    () => CurrentProjectMaterials.ToList(),
                    () => CargoItems.ToList()
                );
                inaraService.Start();
            }
        }


        private void StartTimer()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) =>
            {
                Logger.Log("Timer tick", "Info");

                // 🔹 Save currently selected project ID
                var previousProjectId = SelectedProject?.MarketId;

                // 🔄 Refresh journal data (may reset SelectedProject)
                RefreshJournalData();

                // 🔁 Restore previous selection if still available
                if (previousProjectId != null)
                {
                    var matched = Projects.FirstOrDefault(p => p.MarketId == previousProjectId);
                    if (matched != null)
                        SelectedProject = matched;
                }

                // ✅ Load materials if a project is selected
                if (SelectedProject != null)
                {
                    LoadMaterialsForProject(SelectedProject);
                }
                else
                {
                    Logger.Log("No project selected during timer tick.", "Info");
                    Dispatcher.Invoke(() => CurrentProjectMaterials.Clear());
                }

                LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            };

            LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            timer.Start();
        }

        private void OpenArchive_Click(object sender, RoutedEventArgs e)
        {
            var archiveWindow = new ArchiveWindow(projectDb);
            archiveWindow.Owner = this;
            archiveWindow.Show();
        }
        private void OwnerProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Project proj)
            {
                // Show confirmation dialog
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the project \"{proj.StationName}\"?\nThis cannot be undone.",
                    "Confirm Deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        // Call your DB service to delete the project
                        projectDb.DeleteProject(proj.MarketId);

                        // Remove from UI collection
                        Projects.Remove(proj);

                        Logger.Log($"Project deleted: {proj.StationName} ({proj.MarketId})", "Success");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error deleting project: {ex.Message}", "Error");
                        MessageBox.Show("Failed to delete the project.\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }



        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}