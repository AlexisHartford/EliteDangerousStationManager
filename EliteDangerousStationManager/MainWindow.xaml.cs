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

namespace EliteDangerousStationManager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly ProjectDatabaseService projectDb;
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

        public MainWindow()
        {
            InitializeComponent();
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



        private void LoadProjects()
        {
            var loaded = projectDb.LoadProjects();
            Projects.Clear();
            foreach (var p in loaded)
                Projects.Add(p);

            if (Projects.Count > 0)
                SelectedProject = Projects[0];
        }
        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Project proj)
            {
                SelectedProject = proj; // <- This triggers LoadMaterialsForProject
            }
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
                    projectDb.DeleteProject(marketId);
                    Logger.Log($"Project complete at MarketID {marketId}. Removed.", "Success");
                }
                else
                {
                    projectDb.SaveProject(new Project
                    {
                        MarketId = marketId,
                        SystemName = system,
                        StationName = station
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
                Logger.Log("INARA API Key updated by user.", "Success");

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
                RefreshJournalData();
                LoadMaterialsForProject(SelectedProject);
                LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            };

            LastUpdate = DateTime.Now.ToString("HH:mm:ss");

            timer.Start();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}