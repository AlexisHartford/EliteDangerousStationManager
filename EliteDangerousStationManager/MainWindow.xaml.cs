using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using Newtonsoft.Json;
using System.Text.Json;
using System.Data;

namespace EliteDangerousStationManager
{
    public class CargoItem
    {
        public string Name { get; set; } = null!;
        public int Quantity { get; set; }
    }

    public class ColonisationConstructionDepotEvent
    {
        public long MarketID { get; set; }
        public double ConstructionProgress { get; set; }
        public bool ConstructionComplete { get; set; }
        public bool ConstructionFailed { get; set; }
        public System.Collections.Generic.List<ResourceRequired> ResourcesRequired { get; set; }
    }

    public class ResourceRequired
    {
        public string? Name_Localised { get; set; }
        public string? Name { get; set; }
        public int RequiredAmount { get; set; }
        public int ProvidedAmount { get; set; }
        public int Payment { get; set; }
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public string Type { get; set; }
        public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss");
    }

    public class ProjectMaterial
    {
        public string Material { get; set; }
        public int Needed { get; set; }
        public int Required { get; set; }
        public int Provided { get; set; }
    }

    public class Project : INotifyPropertyChanged
    {
        private long _marketId;
        private string _systemName;
        private string _stationName;

        public long MarketId
        {
            get => _marketId;
            set { _marketId = value; OnPropertyChanged(); }
        }

        public string SystemName
        {
            get => _systemName;
            set { _systemName = value; OnPropertyChanged(); }
        }

        public string StationName
        {
            get => _stationName;
            set { _stationName = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }


    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private DispatcherTimer timer;
        private System.Timers.Timer _journalTimer;
        private string connectionString = "server=192.168.10.68;uid=EliteCarrierTracker;pwd=TimeMaster1966@;database=EliteCarrierTracker;";

        private ObservableCollection<CargoItem> _cargoItems;
        public ObservableCollection<CargoItem> CargoItems
        {
            get => _cargoItems;
            set { _cargoItems = value; OnPropertyChanged(); }
        }
        private ObservableCollection<ProjectMaterial> _currentProjectMaterials = new();

        public ObservableCollection<ProjectMaterial> CurrentProjectMaterials
        {
            get => _currentProjectMaterials;
            private set
            {
                _currentProjectMaterials = value;
                Log("Property changed fired for CurrentProjectMaterials", "Info");
                OnPropertyChanged(nameof(CurrentProjectMaterials));
            }
        }



        public ObservableCollection<LogEntry> LogEntries { get; set; } = new ObservableCollection<LogEntry>();
        public ObservableCollection<Project> Projects { get; set; } = new ObservableCollection<Project>();


        public string LastUpdate { get; set; }
        private long lastReadPosition = 0;
        private string dockedSystem = null;
        private string dockedStation = null;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private Project _selectedProject;
        public Project SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (_selectedProject != value)
                {
                    _selectedProject = value;
                    OnPropertyChanged(nameof(SelectedProject));

                    // Load materials for selected project to refresh UI
                    LoadMaterialsForProject(_selectedProject);
                }
            }
        }



        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            CargoItems = new ObservableCollection<CargoItem>();
            Log("Program started.", "Success");

        }

        private void ReadNewJournalLines(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(lastReadPosition, SeekOrigin.Begin);

            using var sr = new StreamReader(fs);
            string line;
            int lineNumber = 0;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lineNumber++;

                

                var json = JObject.Parse(line);
                string evt = json["event"]?.ToString();

                if (evt == "Docked")
                {
                    dockedSystem = json["StarSystem"]?.ToString();
                    dockedStation = json["StationName"]?.ToString();
                    Log($"Docked event found at {dockedSystem} / {dockedStation}", "Success");
                }

                if (evt == "ColonisationConstructionDepot" && dockedSystem != null && dockedStation != null)
                {
                    var project = new ColonisationConstructionDepotEvent
                    {
                        MarketID = json["MarketID"]?.ToObject<long>() ?? 0,
                        ConstructionProgress = json["ConstructionProgress"]?.ToObject<double>() ?? 0,
                        ConstructionComplete = json["ConstructionComplete"]?.ToObject<bool>() ?? false,
                        ConstructionFailed = json["ConstructionFailed"]?.ToObject<bool>() ?? false,
                        ResourcesRequired = json["ResourcesRequired"]?.Select(r => new ResourceRequired
                        {
                            Name_Localised = r["Name_Localised"]?.ToString() ?? r["Name"]?.ToString(),
                            Name = r["Name"]?.ToString(),
                            RequiredAmount = r["RequiredAmount"]?.ToObject<int>() ?? 0,
                            ProvidedAmount = r["ProvidedAmount"]?.ToObject<int>() ?? 0,
                            Payment = r["Payment"]?.ToObject<int>() ?? 0
                        }).ToList()
                    };

                    Log($"ColonisationConstructionDepot event found at {dockedSystem} / {dockedStation}", "Success");
                    SaveProjectToDatabase(project, dockedSystem, dockedStation);

                    dockedSystem = null;
                    dockedStation = null;
                }
            }

            lastReadPosition = fs.Position;
        }

        private void LoadMaterialsForProject(Project project)
        {
            if (project == null)
            {
                CurrentProjectMaterials.Clear();
                return;
            }

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

                var materials = reader.Cast<IDataRecord>().Select(r => new ProjectMaterial
                {
                    Material = r.GetString(r.GetOrdinal("ResourceName")),
                    Required = r.GetInt32(r.GetOrdinal("RequiredAmount")),
                    Provided = r.GetInt32(r.GetOrdinal("ProvidedAmount")),
                    Needed = Math.Max(
                        r.GetInt32(r.GetOrdinal("RequiredAmount")) - r.GetInt32(r.GetOrdinal("ProvidedAmount")),
                        0)
                }).ToList();

                // Refresh the collection
                Log($"Loaded {materials.Count} materials for project {project.MarketId}", "Info");

                CurrentProjectMaterials = new ObservableCollection<ProjectMaterial>(
    materials.Where(m => m.Needed > 0));
            }
            catch (Exception ex)
            {
                Log("Failed to load materials: " + ex.Message, "Error");
            }
        }


        public void Log(string message, string type = "Info")
        {
            Dispatcher.Invoke(() =>
            {
                LogEntries.Add(new LogEntry { Timestamp = DateTime.Now, Message = message, Type = type });
                if (LogEntries.Count > 500) LogEntries.RemoveAt(0);
            });
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartStatusCheckTimer();

            // Load data here after window fully loaded
            ReadCommanderNameFromJournal();
            ReadCargoFromJson();
            ReadDockedStationThenFindConstructionProject();
            LoadProjectsFromDatabase();
        }

        private void StartStatusCheckTimer()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += async (s, e) =>
            {
                CheckDatabaseConnection();
                Log("Tick event triggered.", "Info");

                // Refresh cargo and journal data
                ReadCargoFromJson();
                ReadNewJournalLines(lastJournalFile); // Ensure this updates database as needed

                if (SelectedProject != null)
                {
                    CurrentProjectMaterials.Clear();
                    Log("Cleared materials list.", "Info");

                    // Wait 2 seconds so UI shows the clear state
                    await Task.Delay(2000);

                    LoadMaterialsForProject(_selectedProject);
                    Log("Project materials refreshed from database.", "Info");
                }
                else
                {
                    CurrentProjectMaterials.Clear();
                    Log("No project selected during timer tick. Cleared materials list.", "Warning");
                }
            };

            timer.Start();
        }




        private void CheckDatabaseConnection()
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    conn.Open();
                    SetStatus(true);
                    //Log("Database connection successful.", "Success");
                }
            }
            catch
            {
                SetStatus(false);
                //Log("Failed to connect to database.", "Error");
            }
        }

        private void SetStatus(bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isConnected ? "#FF00FF88" : "#FFB00020"));
                StatusLabel.Text = isConnected ? "Database connected." : "Database disconnected. System offline.";
                LastUpdate = DateTime.Now.ToString("HH:mm:ss");
                OnPropertyChanged(nameof(LastUpdate));
            });
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Log("Refresh button clicked.", "Info");
            ReadCargoFromJson();
            ReadDockedStationThenFindConstructionProject();

            if (_selectedProject != null)
            {
                LoadMaterialsForProject(_selectedProject);
            }
            else
            {
                Log("No project selected to refresh.", "Warning");
            }
        }


        private void ProcessJournalButton_Click(object sender, RoutedEventArgs e)
        {
            Log("Process journal button clicked.", "Info");
            ReadCommanderNameFromJournal();
            ReadDockedStationThenFindConstructionProject();
        }

        private void CreateTestProjectButton_Click(object sender, RoutedEventArgs e)
        {
            Log("Test project button clicked.", "Info");
            ReadDockedStationThenFindConstructionProject();
        }
        private string lastJournalFile = null;
        private void ReadDockedStationThenFindConstructionProject()
        {
            try
            {
                string path = GetJournalFolderPath();
                var latestFile = Directory.GetFiles(path, "Journal.*.log")
                                        .OrderByDescending(File.GetLastWriteTime)
                                        .FirstOrDefault();

                if (latestFile == null)
                {
                    Log("No journal file found for project.", "Warning");
                    return;
                }

                if (lastJournalFile != latestFile)
                {
                    // New file detected, reset position
                    lastJournalFile = latestFile;
                    lastReadPosition = 0;
                }

                using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastReadPosition, SeekOrigin.Begin);

                using var sr = new StreamReader(fs);
                string line;

                string dockedSystem = null;
                string dockedStation = null;
                ColonisationConstructionDepotEvent lastProject = null;

                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var json = JObject.Parse(line);
                    string evt = json["event"]?.ToString();

                    if (evt == "Docked")
                    {
                        dockedSystem = json["StarSystem"]?.ToString();
                        dockedStation = json["StationName"]?.ToString();
                        Log($"Docked event found at {dockedSystem} / {dockedStation}", "Success");
                    }

                    if (evt == "ColonisationConstructionDepot" && dockedSystem != null && dockedStation != null)
                    {
                        lastProject = new ColonisationConstructionDepotEvent
                        {
                            MarketID = json["MarketID"]?.ToObject<long>() ?? 0,
                            ConstructionProgress = json["ConstructionProgress"]?.ToObject<double>() ?? 0,
                            ConstructionComplete = json["ConstructionComplete"]?.ToObject<bool>() ?? false,
                            ConstructionFailed = json["ConstructionFailed"]?.ToObject<bool>() ?? false,
                            ResourcesRequired = json["ResourcesRequired"]?.Select(r => new ResourceRequired
                            {
                                Name_Localised = r["Name_Localised"]?.ToString() ?? r["Name"]?.ToString(),
                                Name = r["Name"]?.ToString(),
                                RequiredAmount = r["RequiredAmount"]?.ToObject<int>() ?? 0,
                                ProvidedAmount = r["ProvidedAmount"]?.ToObject<int>() ?? 0,
                                Payment = r["Payment"]?.ToObject<int>() ?? 0
                            }).ToList()
                        };
                    }
                }

                // After reading all lines, save the *last* construction project if any
                if (lastProject != null && dockedSystem != null && dockedStation != null)
                {
                    Log($"Saving latest ColonisationConstructionDepot event at {dockedSystem} / {dockedStation}", "Success");
                    SaveProjectToDatabase(lastProject, dockedSystem, dockedStation);
                }

                lastReadPosition = fs.Position;
            }
            catch (Exception ex)
            {
                Log("Error processing journal: " + ex.Message, "Error");
            }
        }

        private void ReadCommanderNameFromJournal()
        {
            try
            {
                string journalPath = GetJournalFolderPath();
                var latestFile = Directory.GetFiles(journalPath, "Journal.*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (latestFile == null) { Log("No journal file found.", "Warning"); return; }

                using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd().Split('\n').Reverse();

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var json = JObject.Parse(line);
                    if (json["event"]?.ToString() == "Commander")
                    {

                        string name = json["Name"]?.ToString();
                        Dispatcher.Invoke(() =>
                        {
                            CommanderNameTextBlock.Text = $"Commander: {name}";
                        });
                        Log("Commander detected: " + name, "Success");
                        return;
                    }
                }

                Log("Commander name not found in journal.", "Warning");
            }
            catch (Exception ex)
            {
                Log("Error reading commander name: " + ex.Message, "Error");
            }
        }

        private void ReadCargoFromJson()
        {
            try
            {
                string path = GetJournalFolderPath();
                string cargoPath = Path.Combine(path, "Cargo.json");
                if (!File.Exists(cargoPath)) { Log("Cargo.json not found.", "Warning"); return; }

                var content = File.ReadAllText(cargoPath);
                var parsed = JObject.Parse(content);
                var inventory = parsed["Inventory"] as JArray;

                if (inventory != null)
                {
                    CargoItems.Clear();
                    foreach (var item in inventory)
                    {
                        CargoItems.Add(new CargoItem
                        {
                            Name = item["Name_Localised"]?.ToString() ?? item["Name"]?.ToString(),
                            Quantity = item["Count"]?.ToObject<int>() ?? 0
                        });
                    }

                    Log("Cargo data loaded.", "Success");
                }
                else Log("No inventory found in cargo file.", "Warning");
            }
            catch (Exception ex)
            {
                Log("Error reading cargo: " + ex.Message, "Error");
            }
        }

        private string GetJournalFolderPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Frontier Developments", "Elite Dangerous");
        }


        private void LoadProjectsFromDatabase()
        {
            try
            {
                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                var cmd = new MySqlCommand("SELECT MarketID, SystemName, StationName FROM Projects ORDER BY SystemName;", conn);

                using var reader = cmd.ExecuteReader();

                var loadedProjects = new List<Project>();

                while (reader.Read())
                {
                    loadedProjects.Add(new Project
                    {
                        MarketId = reader.GetInt64(reader.GetOrdinal("MarketID")),
                        SystemName = reader.GetString(reader.GetOrdinal("SystemName")),
                        StationName = reader.GetString(reader.GetOrdinal("StationName")),
                    });
                }

                Projects.Clear();

                foreach (var p in loadedProjects)
                    Projects.Add(p);

                Log($"Loaded {Projects.Count} projects from DB.", "Info");

                // Reset SelectedProject to refresh UI and materials list
                if (Projects.Count > 0)
                {
                    SelectedProject = Projects[0];
                }
                else
                {
                    SelectedProject = null;
                }
            }
            catch (Exception ex)
            {
                Log("Failed to load projects: " + ex.Message, "Error");
            }
        }


        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                if (button.DataContext is Project selectedProject)
                {
                    // Assign the SelectedProject property, assuming you have one
                    this.SelectedProject = selectedProject;

                    // Optional: show feedback
                    //MessageBox.Show($"Selected project: {selectedProject.StationName} in {selectedProject.SystemName}");

                    // Or trigger any update logic needed when project changes
                }
            }
        }
        private string[] ReadAllLinesShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new System.Collections.Generic.List<string>();
            while (!reader.EndOfStream)
            {
                lines.Add(reader.ReadLine());
            }
            return lines.ToArray();
        }


        private void SaveProjectToDatabase(ColonisationConstructionDepotEvent project, string systemName, string stationName)
        {
            try
            {
                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                var cmd = new MySqlCommand(@"
            INSERT INTO Projects (MarketID, SystemName, StationName)
            VALUES (@mid, @system, @station)
            ON DUPLICATE KEY UPDATE SystemName=@system, StationName=@station;", conn);

                cmd.Parameters.AddWithValue("@mid", project.MarketID);
                cmd.Parameters.AddWithValue("@system", systemName);
                cmd.Parameters.AddWithValue("@station", stationName);
                cmd.ExecuteNonQuery();

                var delCmd = new MySqlCommand("DELETE FROM ProjectResources WHERE MarketID=@mid;", conn);
                delCmd.Parameters.AddWithValue("@mid", project.MarketID);
                delCmd.ExecuteNonQuery();

                foreach (var res in project.ResourcesRequired)
                {
                    Log($"Resource: {res.Name_Localised ?? res.Name}, Required: {res.RequiredAmount}, Provided: {res.ProvidedAmount}, Payment: {res.Payment}", "Success");

                    var upsertCmd = new MySqlCommand(@"
                INSERT INTO ProjectResources (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
                VALUES (@mid, @name, @required, @provided, @payment)
                ON DUPLICATE KEY UPDATE 
                    RequiredAmount = VALUES(RequiredAmount), 
                    ProvidedAmount = VALUES(ProvidedAmount), 
                    Payment = VALUES(Payment);", conn);

                    upsertCmd.Parameters.AddWithValue("@mid", project.MarketID);
                    upsertCmd.Parameters.AddWithValue("@name", res.Name_Localised);
                    upsertCmd.Parameters.AddWithValue("@required", res.RequiredAmount);
                    upsertCmd.Parameters.AddWithValue("@provided", res.ProvidedAmount);
                    upsertCmd.Parameters.AddWithValue("@payment", res.Payment);

                    upsertCmd.ExecuteNonQuery();
                }

                Log($"Saved project {systemName} / {stationName} to database.", "Success");

                // Refresh projects and select new project to update UI
                LoadProjectsFromDatabase();

            }
            catch (Exception ex)
            {
                Log("Failed to save project: " + ex.Message, "Error");
            }
        }
    }
}
