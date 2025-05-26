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
using System.Text;
using System.Configuration;
using EliteDangerousStationManager;

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

        public override string ToString()
        {
            return $"{SystemName} / {StationName} (MarketID: {MarketId})";
        }
    }



    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private OverlayWindow overlay;
        private DispatcherTimer timer;
        private System.Timers.Timer _journalTimer; 
        private readonly string connectionString = ConfigurationManager.ConnectionStrings["EliteDB"].ConnectionString;
        private DispatcherTimer inaraTimer;
        private List<object> pendingInaraEvents = new();
        private InaraSender inaraSender;


        private void ShowOverlay()
        {
            overlay = new OverlayWindow();
            overlay.Left = 100;
            overlay.Top = 100;
            overlay.Show();

            overlay.OverlayMaterialsList.ItemsSource = CurrentProjectMaterials
    .Where(m => m.Needed > 0)
    .ToList();
        }



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
            set
            {
                _currentProjectMaterials = value;
                OnPropertyChanged();
            }
        }




        public ObservableCollection<LogEntry> LogEntries { get; set; } = new ObservableCollection<LogEntry>();
        public ObservableCollection<Project> Projects { get; set; } = new ObservableCollection<Project>();


        public string LastUpdate { get; set; }
        private long lastReadPosition = 0;
        private readonly string lastReadFilePath = "lastread.txt";
        private string dockedSystem = null;
        private string dockedStation = null;
        private long? activeMarketID = null;
        private bool userSelectedProject = false;
        private bool userClickedToSelect = false;




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
                    var oldProject = _selectedProject?.ToString() ?? "NULL";
                    var newProject = value?.ToString() ?? "NULL";

                    Log($"SelectedProject changing from [{oldProject}] to [{newProject}]", "Info");

                    _selectedProject = value;
                    OnPropertyChanged(nameof(SelectedProject));

                    // Load materials for selected project to refresh UI
                    if (_selectedProject != null)
                    {
                        Log($"Loading materials for newly selected project: {_selectedProject.MarketId}", "Info");
                        LoadMaterialsForProject(_selectedProject);
                    }
                    else
                    {
                        Log("Selected project is null, clearing materials", "Warning");
                        Dispatcher.Invoke(() =>
                        {
                            CurrentProjectMaterials.Clear();
                        });
                    }
                }
            }
        }



        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            CargoItems = new ObservableCollection<CargoItem>();
            Log("Program started.", "Success");
            // Automatically close the overlay when the main window closes
            this.Closed += (s, e) =>
            {
                if (overlay != null && overlay.IsLoaded)
                {
                    overlay.Close();
                    overlay = null;
                    Log("Overlay closed along with main window.", "Info");
                }
            };

        }

        private void ReadNewJournalLines(string file)
        {
            // Load last read position from file if available
            if (File.Exists(lastReadFilePath))
            {
                string posText = File.ReadAllText(lastReadFilePath);
                if (long.TryParse(posText, out long savedPos))
                {
                    lastReadPosition = savedPos;
                }
            }

            var lines = new List<string>();

            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(lastReadPosition, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line);
                }

                lastReadPosition = fs.Position;
                File.WriteAllText(lastReadFilePath, lastReadPosition.ToString());
            }

            if (lines.Count == 0)
            {
                Log("No new lines read from journal file.", "Info");
                return;
            }

            // STEP 1 — Find ONLY the latest Docked event (ignore the rest)
            JObject latestDockedJson = null;
            int dockedIndex = -1;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var json = JObject.Parse(lines[i]);
                if (json["event"]?.ToString() == "Docked")
                {
                    latestDockedJson = json;
                    dockedIndex = i;
                    break; // ✅ STOP after finding the first (most recent) Docked
                }
            }

            if (latestDockedJson == null || dockedIndex == -1)
            {
                Log("No Docked event found in new journal lines.", "Warning");
                return;
            }

            string latestDockedSystem = latestDockedJson["StarSystem"]?.ToString();
            string latestDockedStation = latestDockedJson["StationName"]?.ToString();
            long dockedMarketID = latestDockedJson["MarketID"]?.ToObject<long>() ?? 0;

            if (string.IsNullOrWhiteSpace(latestDockedSystem) || string.IsNullOrWhiteSpace(latestDockedStation) || dockedMarketID == 0)
            {
                Log("Docked event missing required info.", "Warning");
                return;
            }

            Log($"Latest Docked event: {latestDockedSystem} / {latestDockedStation} (MarketID={dockedMarketID})", "Info");

            // STEP 2 — Search for the first ColonisationConstructionDepot AFTER that Docked
            for (int i = dockedIndex + 1; i < lines.Count; i++)
            {
                var json = JObject.Parse(lines[i]);
                if (json["event"]?.ToString() == "ColonisationConstructionDepot")
                {
                    long depotMarketID = json["MarketID"]?.ToObject<long>() ?? 0;

                    // ✅ Only save if MarketID matches
                    if (depotMarketID == dockedMarketID)
                    {
                        var project = new ColonisationConstructionDepotEvent
                        {
                            MarketID = depotMarketID,
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

                        Log($"ColonisationConstructionDepot found after dock. Saving to DB.", "Success");
                        SaveProjectToDatabase(project, latestDockedSystem, latestDockedStation);
                        return;
                    }
                    else
                    {
                        Log($"Depot MarketID ({depotMarketID}) does not match Docked MarketID ({dockedMarketID}). Skipping.", "Info");
                        return;
                    }
                }
            }

            Log("No ColonisationConstructionDepot event found after Docked event.", "Info");
        }

        private void LoadMaterialsForProject(Project project)
        {
            if (project == null)
            {
                Log("LoadMaterialsForProject called with null project", "Warning");
                Dispatcher.Invoke(() =>
                {
                    CurrentProjectMaterials.Clear();
                });
                return;
            }

            try
            {
                Log($"LoadMaterialsForProject called for MarketID: {project.MarketId} ({project.SystemName}/{project.StationName})", "Info");

                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                var cmd = new MySqlCommand(@"
            SELECT ResourceName, RequiredAmount, ProvidedAmount
            FROM ProjectResources
            WHERE MarketID = @mid
            ORDER BY ResourceName;", conn);

                cmd.Parameters.AddWithValue("@mid", project.MarketId);

                using var reader = cmd.ExecuteReader();

                var materials = new List<ProjectMaterial>();
                int rowCount = 0;
                int addedCount = 0;

                while (reader.Read())
                {
                    rowCount++;
                    var required = reader.GetInt32("RequiredAmount");
                    var provided = reader.GetInt32("ProvidedAmount");
                    var needed = Math.Max(required - provided, 0);

                    if (needed > 0) // ✅ Only include materials still needed
                    {
                        materials.Add(new ProjectMaterial
                        {
                            Material = reader.GetString("ResourceName"),
                            Required = required,
                            Provided = provided,
                            Needed = needed
                        });

                        addedCount++;
                    }
                }

                Log($"SQL returned {rowCount} total rows for MarketID: {project.MarketId}", "Info");
                Log($"Materials added to UI (Needed > 0): {addedCount}", "Info");

                // Update the UI on the main thread
                Dispatcher.Invoke(() =>
                {
                    CurrentProjectMaterials.Clear();
                    Log($"Cleared existing materials. Adding {materials.Count} to UI", "Info");

                    foreach (var material in materials)
                    {
                        CurrentProjectMaterials.Add(material);
                        Log($"Added material: {material.Material} (Needed: {material.Needed})", "Info");
                    }

                    Log($"CurrentProjectMaterials.Count after update: {CurrentProjectMaterials.Count}", "Info");
                });
            }
            catch (Exception ex)
            {
                Log($"Failed to load materials for project {project?.MarketId}: {ex.Message}", "Error");
                Log($"Stack trace: {ex.StackTrace}", "Error");
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
            try
            {
                Log("Testing MySQL connection...", "Info");
                using var conn = new MySqlConnection(connectionString);
                conn.Open();
                Log("✅ Connection to MySQL succeeded!", "Success");
            }
            catch (Exception ex)
            {
                Log("❌ Connection failed: " + ex.Message, "Error");
            }

            StartStatusCheckTimer();
            InitializeInaraSync();

            // Load data here after window fully loaded
            ReadCommanderNameFromJournal();
            ReadCargoFromJson();
            ReadDockedStationThenFindConstructionProject();
            LoadProjectsFromDatabase();
            ShowOverlay();
        }
        private void InitializeInaraSync()
        {
            inaraSender = new InaraSender(commanderName, "YOUR_INARA_API_KEY_HERE");

            inaraTimer = new DispatcherTimer();
            inaraTimer.Interval = TimeSpan.FromMinutes(1);
            inaraTimer.Tick += async (s, e) =>
            {
                if (pendingInaraEvents.Count > 0)
                {
                    var toSend = new List<object>(pendingInaraEvents);
                    pendingInaraEvents.Clear();
                    await inaraSender.SendEventsAsync(toSend);
                }
            };

            inaraTimer.Start();
        }
        private void ProcessJournalLine(string line)
        {
            var json = JObject.Parse(line);
            string evt = json["event"]?.ToString();

            if (evt == "ColonisationConstructionDepot")
            {
                var marketId = (long)json["MarketID"];
                var progress = (double)json["ConstructionProgress"];
                var isCompleted = (bool)json["ConstructionComplete"];
                var isFailed = (bool)json["ConstructionFailed"];

                var resourcesList = new List<object>();
                foreach (var res in json["ResourcesRequired"])
                {
                    resourcesList.Add(new
                    {
                        name = res["Name"].ToString().Replace("$", "").Replace("_name;", "").ToLower(),
                        required = (int)res["RequiredAmount"],
                        delivered = (int)res["ProvidedAmount"],
                        payment = (int)res["Payment"]
                    });
                }

                var eventData = new
                {
                    eventName = "logColonisationDepot",
                    eventTimestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    eventData = new
                    {
                        marketId = marketId,
                        progress = progress,
                        isCompleted = isCompleted,
                        isFailed = isFailed,
                        resources = resourcesList
                    }
                };

                pendingInaraEvents.Add(eventData);
            }
        }

        private void StartStatusCheckTimer()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (s, e) =>
            {
                CheckDatabaseConnection();
                Log("=== TIMER TICK START ===", "Info");

                // Refresh cargo data
                ReadCargoFromJson();

                // Info current state
                Log($"SelectedProject: {(_selectedProject?.ToString() ?? "NULL")}", "Info");
                Log($"userSelectedProject: {userSelectedProject}", "Info");
                Log($"Projects.Count: {Projects.Count}", "Info");
                Log($"CurrentProjectMaterials.Count before refresh: {CurrentProjectMaterials.Count}", "Info");

                // Handle project materials refresh
                if (_selectedProject != null)
                {
                    Log($"Refreshing materials for selected project: {_selectedProject.MarketId}", "Info");
                    LoadMaterialsForProject(_selectedProject);
                    if (overlay != null && overlay.IsLoaded)
                    {
                        overlay.OverlayMaterialsList.ItemsSource = CurrentProjectMaterials
                            .Where(m => m.Needed > 0)
                            .ToList();
                    }
                }
                else
                {
                    Log("No project selected. Checking if we should auto-select...", "Info");

                    // Check if we have projects but none selected - auto-select first one
                    if (!userSelectedProject && Projects.Count > 0)
                    {
                        Log($"Auto-selecting first project: {Projects[0].ToString()}", "Info");
                        SelectedProject = Projects[0];
                        // LoadMaterialsForProject will be called by the SelectedProject setter
                    }
                    else
                    {
                        Log("Clearing materials - no project to select", "Warning");
                        Dispatcher.Invoke(() =>
                        {
                            CurrentProjectMaterials.Clear();
                        });
                    }
                }

                Log($"CurrentProjectMaterials.Count after refresh: {CurrentProjectMaterials.Count}", "Info");

                // Rest of journal processing...
                try
                {
                    string journalFolder = GetJournalFolderPath();
                    var latestFile = Directory.GetFiles(journalFolder, "Journal.*.log")
                                              .OrderByDescending(File.GetLastWriteTime)
                                              .FirstOrDefault();

                    if (latestFile != null)
                    {
                        var lines = ReadAllLinesShared(latestFile);
                        ColonisationConstructionDepotEvent latestProject = null;

                        // Step 1: Find the most recent ColonisationConstructionDepot event
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            var line = lines[i];
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var json = JObject.Parse(line);
                            if (json["event"]?.ToString() == "ColonisationConstructionDepot")
                            {
                                latestProject = new ColonisationConstructionDepotEvent
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
                                break;
                            }
                        }

                        if (latestProject != null)
                        {
                            // Step 2: Find the most recent Docked event
                            long dockedMarketID = 0;
                            string dockedSystemName = null;
                            string dockedStationName = null;

                            for (int i = lines.Length - 1; i >= 0; i--)
                            {
                                var line = lines[i];
                                if (string.IsNullOrWhiteSpace(line)) continue;

                                var json = JObject.Parse(line);
                                if (json["event"]?.ToString() == "Docked")
                                {
                                    dockedMarketID = json["MarketID"]?.ToObject<long>() ?? 0;
                                    dockedSystemName = json["StarSystem"]?.ToString();
                                    dockedStationName = json["StationName"]?.ToString();
                                    break;
                                }
                            }

                            // Step 3: Only save project if MarketID matches
                            if (dockedMarketID != 0 && dockedMarketID == latestProject.MarketID)
                            {
                                Log($"MarketID match detected: {dockedMarketID}. Saving project to DB.", "Success");
                                SaveProjectToDatabase(latestProject, dockedSystemName, dockedStationName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Error checking ColonisationConstructionDepot during timer tick: " + ex.Message, "Error");
                }

                Log("=== TIMER TICK END ===", "Info");
            };

            // Ensure dockedSystem and dockedStation are populated at startup
            ReadDockedStationThenFindConstructionProject();

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
            Log("=== REFRESH BUTTON CLICKED ===", "Info");
            Log($"Before refresh - userSelectedProject: {userSelectedProject}, SelectedProject: {(_selectedProject?.ToString() ?? "NULL")}", "Info");

            ReadCargoFromJson();
            ReadDockedStationThenFindConstructionProject();
            LoadProjectsFromDatabase(); // This will handle selection logic

            Log($"After refresh - userSelectedProject: {userSelectedProject}, SelectedProject: {(_selectedProject?.ToString() ?? "NULL")}", "Info");
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
                    lastJournalFile = latestFile;
                    lastReadPosition = 0;
                }

                using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastReadPosition, SeekOrigin.Begin);

                List<string> lines = new List<string>();

                using (var sr = new StreamReader(fs, Encoding.UTF8, true, 1024, leaveOpen: true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            lines.Add(line);
                    }
                }

                // Now fs is still open here, so this works:
                lastReadPosition = fs.Length;


                // Find the latest Docked event by scanning backwards
                int dockedIndex = -1;
                string dockedSystem = null;
                string dockedStation = null;
                long dockedMarketID = 0;

                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    var json = JObject.Parse(lines[i]);
                    string evt = json["event"]?.ToString();

                    if (evt == "Docked")
                    {
                        dockedSystem = json["StarSystem"]?.ToString();
                        dockedStation = json["StationName"]?.ToString();
                        dockedMarketID = json["MarketID"]?.ToObject<long>() ?? 0;
                        dockedIndex = i;
                        Log($"Latest Docked event found at {dockedSystem} / {dockedStation} with MarketID {dockedMarketID}", "Success");
                        break;
                    }
                }

                if (dockedIndex == -1)
                {
                    Log("No Docked event found in recent journal lines.", "Warning");
                    lastReadPosition = fs.Position;
                    return;
                }

                // Find ColonisationConstructionDepot event with matching MarketID, searching forward from dockedIndex
                ColonisationConstructionDepotEvent matchedProject = null;
                for (int i = dockedIndex; i < lines.Count; i++)
                {
                    var json = JObject.Parse(lines[i]);
                    string evt = json["event"]?.ToString();

                    if (evt == "ColonisationConstructionDepot")
                    {
                        long marketID = json["MarketID"]?.ToObject<long>() ?? 0;
                        if (marketID == dockedMarketID)
                        {
                            matchedProject = new ColonisationConstructionDepotEvent
                            {
                                MarketID = marketID,
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
                            Log($"Matching ColonisationConstructionDepot event found for MarketID {marketID}", "Success");
                            break;
                        }
                    }
                }

                if (matchedProject != null)
                {
                    if (matchedProject.ConstructionComplete && matchedProject.ResourcesRequired.All(r => r.ProvidedAmount >= r.RequiredAmount))
                    {
                        DeleteProjectFromDatabase(matchedProject.MarketID);
                        Log($"Project at MarketID {matchedProject.MarketID} is complete. Deleted from database.", "Success");
                    }
                    else
                    {
                        SaveProjectToDatabase(matchedProject, dockedSystem, dockedStation);
                    }
                }
                else
                {
                    Log("No matching ColonisationConstructionDepot event found for the latest docked station.", "Warning");
                }

                // Update lastReadPosition to the end of file for next read
                lastReadPosition = fs.Length;
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
                Log("=== LOADING PROJECTS FROM DATABASE ===", "Info");
                Log($"Current state - SelectedProject: {(_selectedProject?.ToString() ?? "NULL")}", "Info");

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

                Log($"Found {loadedProjects.Count} projects in database", "Info");

                // ✅ Remember currently selected MarketID
                long? previouslySelectedId = _selectedProject?.MarketId;

                Projects.Clear();
                foreach (var p in loadedProjects)
                {
                    Projects.Add(p);
                    Log($"Added project: {p}", "Debug");
                }

                Log($"Projects collection now has {Projects.Count} items", "Info");

                // ✅ Restore previous selection if possible
                if (previouslySelectedId != null)
                {
                    var matching = Projects.FirstOrDefault(p => p.MarketId == previouslySelectedId);
                    if (matching != null)
                    {
                        Log($"Restoring previously selected project: {matching}", "Info");
                        SelectedProject = matching;
                        return; // do not override selection
                    }
                    else
                    {
                        Log("Previously selected project no longer exists. Will select first if available.", "Warning");
                    }
                }

                // Default selection fallback
                if (Projects.Count > 0)
                {
                    Log($"Auto-selecting first project: {Projects[0]}", "Info");
                    SelectedProject = Projects[0];
                }
                else
                {
                    SelectedProject = null;
                    Log("No projects available to select.", "Warning");
                }

                Log($"Final selected project: {(_selectedProject?.ToString() ?? "NULL")}", "Info");
            }
            catch (Exception ex)
            {
                Log("Failed to load projects: " + ex.Message, "Error");
                Log($"Stack trace: {ex.StackTrace}", "Error");
            }
        }

        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            Log("=== SELECT PROJECT BUTTON CLICKED ===", "Info");

            if (sender is Button button && button.DataContext is Project selectedProject)
            {
                Log($"User manually selecting project: {selectedProject}", "Info");

                userClickedToSelect = true; // ✅ User chose it manually

                SelectedProject = selectedProject;

                Log($"SelectedProject after manual selection: {(_selectedProject?.ToString() ?? "NULL")}", "Info");
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
        private void DeleteProjectFromDatabase(long marketID)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            using var cmd = new MySqlCommand("DELETE FROM ProjectResources WHERE MarketID = @id", conn);
            cmd.Parameters.AddWithValue("@id", marketID);
            cmd.ExecuteNonQuery();

            using var cmd2 = new MySqlCommand("DELETE FROM Projects WHERE MarketID = @id", conn);
            cmd2.Parameters.AddWithValue("@id", marketID);
            cmd2.ExecuteNonQuery();
        }
        private void SaveProjectToDatabase(ColonisationConstructionDepotEvent project, string systemName, string stationName)
        {
            try
            {
                using var conn = new MySqlConnection(connectionString);
                conn.Open();

                // Insert or update the Projects table
                var cmd = new MySqlCommand(@"
            INSERT INTO Projects (MarketID, SystemName, StationName)
            VALUES (@mid, @system, @station)
            ON DUPLICATE KEY UPDATE SystemName=@system, StationName=@station;", conn);

                cmd.Parameters.AddWithValue("@mid", project.MarketID);
                cmd.Parameters.AddWithValue("@system", systemName);
                cmd.Parameters.AddWithValue("@station", stationName);
                cmd.ExecuteNonQuery();

                // Clean up existing resources for this MarketID
                var delCmd = new MySqlCommand("DELETE FROM ProjectResources WHERE MarketID=@mid;", conn);
                delCmd.Parameters.AddWithValue("@mid", project.MarketID);
                delCmd.ExecuteNonQuery();

                // Insert each resource
                foreach (var res in project.ResourcesRequired)
                {
                    string name = res.Name_Localised ?? res.Name ?? "Unknown";
                    int required = res.RequiredAmount;
                    int provided = res.ProvidedAmount;
                    int payment = res.Payment;

                    //Log($"Saving Resource: {name} | Required: {required} | Provided: {provided} | Payment: {payment}", "Info");

                    var upsertCmd = new MySqlCommand(@"
                INSERT INTO ProjectResources (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
                VALUES (@mid, @name, @required, @provided, @payment)
                ON DUPLICATE KEY UPDATE 
                    RequiredAmount = VALUES(RequiredAmount), 
                    ProvidedAmount = VALUES(ProvidedAmount), 
                    Payment = VALUES(Payment);", conn);

                    upsertCmd.Parameters.AddWithValue("@mid", project.MarketID);
                    upsertCmd.Parameters.AddWithValue("@name", name);
                    upsertCmd.Parameters.AddWithValue("@required", required);
                    upsertCmd.Parameters.AddWithValue("@provided", provided);
                    upsertCmd.Parameters.AddWithValue("@payment", payment);

                    upsertCmd.ExecuteNonQuery();
                }

                Log($"Saved project {systemName} / {stationName} to database.", "Success");

                // Refresh UI
                LoadProjectsFromDatabase();
            }
            catch (Exception ex)
            {
                Log("Failed to save project: " + ex.Message, "Error");
            }
        }
    }
}
