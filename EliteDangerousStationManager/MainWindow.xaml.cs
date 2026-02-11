using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Overlay;
using EliteDangerousStationManager.Services;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;
// *** added usings ***
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static EliteDangerousStationManager.Services.DbConnectionManager;
using Application = System.Windows.Application;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.SolidColorBrush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;
using WpfApp = System.Windows.Application;



namespace EliteDangerousStationManager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private ProjectDatabaseService projectDb;
        private readonly JournalProcessor journalProcessor;
        private readonly OverlayManager overlayManager;
        private int _anchorIndex = -1; // remembers where the range starts for Shift

        private CancellationTokenSource _materialsCts;
        private bool _materialsRefreshInFlight = false;
        private string _lastMaterialsHash = null;
        private bool _failoverEventHooked = false;


        private ICollectionView _projectsView;

        private decimal totalCostToday = 0;
        private long _activeMarketId = 0;
        private decimal totalSaleToday = 0;
        private readonly HashSet<string> processedEntries = new HashSet<string>();
        private readonly List<(DateTime Timestamp, decimal Amount)> hourlyTransactions = new();

        private static bool PrimaryDbDisabledForSession = false;


        // In your MainWindow constructor, after InitializeComponent():

        



        // Child windows
        private ArchiveWindow archiveWindow;
        private ColonizationPlanner.ColonizationPlanner plannerWindow;

        private CancellationTokenSource _loadMaterialsCts;

        // Observable collections bound to the UI
        public ObservableCollection<LogEntry> LogEntries => Logger.Entries;
        public ObservableCollection<Project> Projects { get; set; } = new ObservableCollection<Project>();
        public ObservableCollection<ProjectMaterial> CurrentProjectMaterials { get; set; } = new ObservableCollection<ProjectMaterial>();

        private bool _projectsRefreshInFlight = false;

        // Kept: FleetCarrierCargoItems + CarrierMaterialOverview
        public ObservableCollection<CargoItem> FleetCarrierCargoItems { get; set; } = new ObservableCollection<CargoItem>();
        public ObservableCollection<CarrierMaterialStatus> CarrierMaterialOverview { get; set; } = new ObservableCollection<CarrierMaterialStatus>();
        private readonly Dictionary<string, int> _sessionTransfers = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer timer;
        private Project _selectedProject;
        private ScrollViewer _projectsScrollViewer;
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
                    _ = ForceRefreshMaterialsAsync();   // << single source of truth
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
        public DbStatusViewModel DbStatus { get; } = new DbStatusViewModel();
        public MainWindow()
        {
            InitializeComponent();



            ProjectsListBox.AddHandler(
                FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((s, e) => e.Handled = true),
                handledEventsToo: true);


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
                        var parsedColor = (MediaColor)MediaColorConverter.ConvertFromString(color);

                        WpfApp.Current.Resources["HighlightBrush"] =
                            new MediaBrush(parsedColor);

                        WpfApp.Current.Resources["HighlightOverlayBrush"] =
                            new MediaBrush(MediaColor.FromArgb(0x22, parsedColor.R, parsedColor.G, parsedColor.B));
                    }
                    catch
                    {
                        WpfApp.Current.Resources["HighlightBrush"] =
                            new MediaBrush(System.Windows.Media.Colors.Orange);

                        WpfApp.Current.Resources["HighlightOverlayBrush"] =
                            new MediaBrush(MediaColor.FromArgb(0x22, 255, 140, 0));
                    }
                }
            }

            DataContext = this;

            // 2) Compute the journal folder path (default)
            string journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Saved Games",
                "Frontier Developments",
                "Elite Dangerous"
            );

            // If the default folder is missing OR has no journal logs, let the user pick
            bool hasDefault = Directory.Exists(journalPath) &&
                              Directory.EnumerateFiles(journalPath, "Journal.*.log").Any();

            if (!hasDefault)
            {
                Logger.Log("Default journal folder missing or empty. Prompting user…", "Warning");
                var picked = PromptForJournalFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (string.IsNullOrWhiteSpace(picked))
                {
                    MessageBox.Show(
                        "No valid journal folder was selected. The app will exit.",
                        "Journal Folder Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    Close();
                    return;
                }
                journalPath = picked;
            }

            // 3) Read Commander name and selected journal file (now uses chosen folder)
            string selectedJournalFile = ReadCommanderNameFromJournal(journalPath);


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

            // Subscription
            journalProcessor.CarrierCargoChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCarrierCargo();
                    UpdateCarrierMaterialOverview();
                });
            };

            journalProcessor.CarrierTransferDelta += (name, delta) =>
            {
                Dispatcher.Invoke(() => ApplySessionDelta(name, delta));
            };



            // 6) Instantiate OverlayManager
            overlayManager = new OverlayManager();

            Logger.Log("Application started.", "Success");

            // 7) Defer DB initialization & initial scan until the window is shown
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // heavy work off UI thread, but awaited here
                    await Task.Run(() =>
                    {
                        projectDb = new ProjectDatabaseService();
                    });

                    // DB status (primary vs fallback)
                    bool onFailoverNow = false;
                    try { onFailoverNow = DbConnectionManager.Instance.OnFailover; } catch { /* not initialized? */ }
                    SetDatabaseStatusIndicator(true, onFailoverNow);

                    // Subscribe once to live failover updates
                    if (!_failoverEventHooked)
                    {
                        try
                        {
                            DbConnectionManager.Instance.FailoverStateChanged += onFailover =>
                                Dispatcher.Invoke(() => SetDatabaseStatusIndicator(true, onFailover));
                            _failoverEventHooked = true;
                        }
                        catch
                        {
                            // DbConnectionManager may not be used in this run
                            SetDatabaseStatusIndicator(true, onFailoverNow);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Database unavailable at startup: {ex.Message}", "Warning");
                    SetDatabaseStatusIndicator(false, false);
                    projectDb = null;
                }


                // First UI refresh, then start the timer
                await ScanForDepotAndRefreshUI();
                StartTimer();
            }, DispatcherPriority.ApplicationIdle);
        }

        private string PromptForJournalFolder(string initial = null)
        {
            try
            {
                using var dlg = new WinForms.FolderBrowserDialog
                {
                    Description = "Select your Elite Dangerous journal folder (it contains files like Journal.*.log)",
                    UseDescriptionForTitle = true,
                    SelectedPath = initial ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ShowNewFolderButton = false
                };

                while (true)
                {
                    var result = dlg.ShowDialog();
                    if (result != WinForms.DialogResult.OK)
                        return null; // user cancelled

                    var path = dlg.SelectedPath;
                    if (!Directory.Exists(path))
                    {
                        System.Windows.MessageBox.Show(
                            "That folder does not exist. Please pick a different folder.",
                            "Invalid Folder",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        continue;
                    }

                    bool hasLogs = Directory.EnumerateFiles(path, "Journal.*.log").Any();
                    if (!hasLogs)
                    {
                        System.Windows.MessageBox.Show(
                            "That folder does not contain any Elite Dangerous log files (Journal.*.log).\nPlease select the correct journal folder.",
                            "No Logs Found",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        continue;
                    }

                    return path;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Folder selection failed: {ex.Message}", "Warning");
                return null;
            }
        }




        private static string HashMaterials(IEnumerable<ProjectMaterial> rows)
        {
            // Only hash fields that affect UI render; keep deterministic ordering
            var sb = new StringBuilder();
            foreach (var r in rows.OrderBy(r => r.Material, StringComparer.OrdinalIgnoreCase))
                sb.Append(r.Material).Append('|')
                  .Append(r.Required).Append('|')
                  .Append(r.Provided).Append('|')
                  .Append(r.Needed).Append('\n');

            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes);
        }

        private void RebuildCarrierTransferredFromSession()
        {
            // Build a quick lookup of current required (Needed) for the active project rows
            var needByName = CurrentProjectMaterials
                .ToDictionary(m => m.Material, m => Math.Max(m.Needed, 0),
                              StringComparer.OrdinalIgnoreCase);

            CarrierMaterialOverview.Clear();

            // Show only materials we’ve touched this session
            foreach (var kv in _sessionTransfers.OrderBy(k => k.Key))
            {
                var name = kv.Key;
                var movedNetToCarrier = kv.Value; // + to carrier, - from carrier

                // “Transferred” column should be what you PUT ON carriers (negatives don’t count)
                var transferred = Math.Max(movedNetToCarrier, 0);

                // StillNeeded = current Needed minus what you’ve transferred this session (clamped)
                var neededNow = needByName.TryGetValue(name, out var need) ? need : 0;

                CarrierMaterialOverview.Add(new CarrierMaterialStatus
                {
                    Name = name,
                    Transferred = transferred
                });
            }
        }


        private void ApplySessionDelta(string material, int deltaToCarrier)
        {
            if (string.IsNullOrWhiteSpace(material) || deltaToCarrier == 0) return;

            if (_sessionTransfers.TryGetValue(material, out var cur))
                _sessionTransfers[material] = cur + deltaToCarrier;
            else
                _sessionTransfers[material] = deltaToCarrier;

            // Rebuild the “Carrier Transferred” table using session values only
            RebuildCarrierTransferredFromSession();
        }
        private void ResetCarrierTransferred_Click(object sender, RoutedEventArgs e)
        {
            journalProcessor?.ClearFleetCarrierCargo();
            CarrierMaterialOverview.Clear();

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


        private void UpdateCarrierMaterialOverview()
        {
            // Build only from transfers we've observed this session.
            var session = journalProcessor?.FleetCarrierCargoItems ?? new List<CargoItem>();

            CarrierMaterialOverview.Clear();

            if (session.Count == 0) return;

            // Optional: look up the selected/current project's needs per material
            // If you don't have a "current project" concept, use 0.
            foreach (var xfer in session)
            {
                int transferred = xfer.Quantity; // already positive for "to carrier", negative for "to ship"
                if (transferred == 0) continue;

                // Find that material in your current project list (if any)
                var projRow = CurrentProjectMaterials
                    .FirstOrDefault(m => string.Equals(m.Material, xfer.Name, StringComparison.OrdinalIgnoreCase));

                

                CarrierMaterialOverview.Add(new CarrierMaterialStatus
                {
                    Name = xfer.Name,
                    Transferred = transferred
                });
            }
        }


        private void SetDatabaseStatusIndicator(bool isOnline, bool onFailover)
        {
            if (!isOnline)
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
                StatusLabel.Text = "System Offline";
                return;
            }

            // Primary = green + "Database Connected"
            // Failover = orange + "Backup Connected"
            if (onFailover)
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)); // Orange
                StatusLabel.Text = "Backup Connected";
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)); // Green
                StatusLabel.Text = "Database Connected";
            }
        }

        private void CloseChildWindows()
        {
            try { plannerWindow?.Close(); } catch { }
            try { archiveWindow?.Close(); } catch { }
        }

        private string ReadCommanderNameFromJournal(string journalDir)
        {
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

        private readonly System.Threading.SemaphoreSlim _projectsLoadGate = new(1, 1);

        public async Task LoadProjects(string filter = "", int filterMode = 0)
        {
            await _projectsLoadGate.WaitAsync();
            try
            {
                // Read your setting once (replace with your real flag):
                // Option A: if you use a toggle like Settings.Default.PublicEnabled
                // bool includeServer = Settings.Default.PublicEnabled;
                // Option B: if you use a mode string you showed earlier:
                bool includeServer = !string.Equals(App.CurrentDbMode, "Local", StringComparison.OrdinalIgnoreCase);

                var (local, server) = await Task.Run(() =>
                {
                    // -------- LOCAL (SQLite) --------
                    var localProjects = new List<Project>();
                    try
                    {
                        var localRoot = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "EDStationManager");
                        Directory.CreateDirectory(localRoot);
                        var localDbPath = Path.Combine(localRoot, "stations.db");

                        var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                        {
                            DataSource = localDbPath,
                            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
                        };

                        using var localConn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString());
                        localConn.Open();

                        // Nice to have for concurrent readers
                        using (var pragma = localConn.CreateCommand())
                        { pragma.CommandText = "PRAGMA journal_mode=WAL;"; pragma.ExecuteNonQuery(); }

                        using var cmd = localConn.CreateCommand();
                        cmd.CommandText = @"
                    SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt
                    FROM Projects
                    ORDER BY SystemName, StationName;";
                        using var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            localProjects.Add(new Project
                            {
                                MarketId = rdr.IsDBNull(0) ? 0L : rdr.GetInt64(0),
                                SystemName = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                                StationName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                                CreatedBy = rdr.IsDBNull(3) ? "Unknown" : rdr.GetString(3),
                                CreatedAt = rdr.IsDBNull(4) ? DateTime.MinValue : rdr.GetDateTime(4),
                                Source = "Local"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"⚠ Local projects load failed: {ex.Message}", "Warning");
                    }

                    // ... (unchanged local code) ...

                    // -------- SERVER (MySQL) --------
                    var serverProjects = new List<Project>();
                    if (includeServer) // ⬅️ only hit MySQL when Public/Server is ON
                    {
                        try
                        {
                            if (projectDb != null)
                            {
                                serverProjects = projectDb.LoadProjects();
                            }
                            else
                            {
                                DbConnectionManager.Instance.Execute(cmd =>
                                {
                                    cmd.CommandText =
                                        "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt FROM Projects ORDER BY SystemName;";
                                    using var rdr = cmd.ExecuteReader();
                                    while (rdr.Read())
                                    {
                                        serverProjects.Add(new Project
                                        {
                                            MarketId = Convert.ToInt64(rdr["MarketID"]),
                                            SystemName = rdr["SystemName"]?.ToString(),
                                            StationName = rdr["StationName"]?.ToString(),
                                            CreatedBy = rdr["CreatedBy"] == DBNull.Value ? "Unknown" : rdr["CreatedBy"]?.ToString(),
                                            CreatedAt = rdr["CreatedAt"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(rdr["CreatedAt"]),
                                            Source = "Server"
                                        });
                                    }
                                    return 0;
                                }, sqlPreview: "SELECT … FROM Projects");
                            }

                            foreach (var p in serverProjects) p.Source = "Server";
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"⚠ Skipped Server projects (Public OFF or error): {ex.Message}", "Warning");
                        }
                    }

                    return (localProjects, serverProjects);
                });

                // -------- MERGE (respect the flag) --------
                IEnumerable<Project> mergedEnum = server.Count > 0
                    ? server.Concat(local.Where(lp => !server.Any(sp => sp.MarketId == lp.MarketId)))
                    : local; // ⬅️ when Public OFF, we only show local

                var merged = mergedEnum
                    .OrderBy(p => p.SystemName)
                    .ThenBy(p => p.StationName)
                    .ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    Projects.Clear();
                    foreach (var p in merged) Projects.Add(p);
                });

                Logger.Log(
                    $"✅ Loaded {merged.Count} projects (Local {local.Count}{(server.Count > 0 ? $" + Server {server.Count}" : "")}).",
                    "Info");
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Failed to load projects: {ex.Message}", "Error");
                await Dispatcher.InvokeAsync(() => Projects.Clear());
            }
            finally
            {
                _projectsLoadGate.Release();
            }
        }
        private void RefreshCarrierColumnsFromDb()
        {
            try
            {
                if (SelectedProjects == null || SelectedProjects.Count == 0 || projectDb == null) return;

                var serverProjects = SelectedProjects.Where(p => p.Source == "Server").ToList();
                if (serverProjects.Count == 0) return;

                // Merge per-carrier amounts across selected server projects
                var merged = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                foreach (var proj in serverProjects)
                {
                    var perCarrier = projectDb.GetLinkedCarrierStockPerCarrier(proj.MarketId)
                                   ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in perCarrier) // kv.Key = material, kv.Value = carrier->count
                    {
                        if (!merged.TryGetValue(kv.Key, out var acc))
                            merged[kv.Key] = acc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                        foreach (var c in kv.Value)
                            acc[c.Key] = acc.TryGetValue(c.Key, out var have) ? have + c.Value : c.Value;
                    }
                }

                // Apply back to the rows (REPLACE the dictionary so bindings fire)
                foreach (var row in CurrentProjectMaterials)
                {
                    if (merged.TryGetValue(row.Material, out var map) && map != null)
                        row.SetCarrierAmounts(new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase));
                    else
                        row.SetCarrierAmounts(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                }

                BuildCarrierColumnsIfChanged();     // adds/removes dynamic carrier columns
                MaterialsDataGrid?.Items.Refresh(); // force a visual refresh of the grid
                UpdateCarrierMaterialOverview();    // keep the side overview in sync
            }
            catch (Exception ex)
            {
                Logger.Log($"RefreshCarrierColumnsFromDb failed: {ex.Message}", "Warning");
            }
        }


        private void StartTimer()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        journalProcessor.ScanForDepotEvents();

                        // 👇 make this async so you can use await
                        Dispatcher.BeginInvoke(async () =>
                        {
                            try
                            {
                                // 🔁 Refresh the Projects list every tick, but don’t overlap calls
                                if (!_projectsRefreshInFlight)
                                {
                                    _projectsRefreshInFlight = true;
                                    try
                                    {
                                        await ScanForDepotAndRefreshUI();
                                    }
                                    finally
                                    {
                                        _projectsRefreshInFlight = false;
                                    }
                                }

                                // ✅ Keep your existing light UI work
                                _ = RefreshMaterialsIfChangedAsync(); // async, hashed, non-blocking
                                UpdateCarrierMaterialOverview();
                                LastUpdate = DateTime.Now.ToString("HH:mm:ss");

                                if (SelectedProjects != null && SelectedProjects.Any(p => p.Source == "Server"))
                                {
                                    RefreshCarrierColumnsFromDb();
                                }
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
            };

            LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            timer.Start();
        }

        // with this:
        private bool _overlayManuallyHidden = false; // true = user pressed "Hide Overlay"

        private void ShowOverlayIfAllowed()
        {
            // Only show if user DIDN'T hide it AND there is a selection
            if (_overlayManuallyHidden || SelectedProjects == null || SelectedProjects.Count == 0)
            {
                overlayManager.CloseOverlay();
                return;
            }

            overlayManager.ShowOverlay(CurrentProjectMaterials);
        }

        private void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            // Flip the manual state
            _overlayManuallyHidden = !_overlayManuallyHidden;

            if (_overlayManuallyHidden)
            {
                overlayManager.CloseOverlay();
                ToggleOverlayButton.Content = "Show Overlay";
                Logger.Log("Overlay hidden.", "Info");
            }
            else
            {
                // Only show if something is selected; otherwise we wait
                ShowOverlayIfAllowed();
                ToggleOverlayButton.Content = "Hide Overlay";
                Logger.Log(
                    SelectedProjects != null && SelectedProjects.Count > 0
                        ? "Overlay shown."
                        : "Overlay will appear when a project is selected.",
                    "Info");
            }
        }


        private async Task ScanForDepotAndRefreshUI()
        {
            try
            {
                await LoadProjects();

                var selectedIds = SelectedProjects.Select(p => p.MarketId).ToHashSet();

                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    string keyword = SearchBox.Text.ToLower();
                    int mode = SearchMode.SelectedIndex;

                    var locals = Projects.Where(p => p.Source == "Local");
                    var servers = Projects.Where(p => p.Source == "Server" && (
                        (mode == 0 && (p.StationName ?? "").ToLower().Contains(keyword)) ||
                        (mode == 1 && ((p.CreatedBy ?? "").ToLower().Contains(keyword))) ||
                        (mode == 2 && (p.SystemName ?? "").ToLower().Contains(keyword))
                    ));

                    var combined = locals.Concat(servers)
                                         .OrderBy(p => p.Source == "Server")
                                         .ThenBy(p => p.SystemName)
                                         .ToList();

                    Projects.Clear();
                    foreach (var p in combined)
                        Projects.Add(p);
                }

                // restore both SelectedProjects AND the per-item flag
                SelectedProjects.Clear();
                foreach (var p in Projects)
                {
                    bool sel = selectedIds.Contains(p.MarketId);
                    p.IsSelected = sel;
                    if (sel) SelectedProjects.Add(p);
                }

                // 🔧 ALSO sync the ListBox's SelectedItems so the click handler sees it as selected
                ProjectsListBox.SelectedItems.Clear();
                foreach (var p in Projects.Where(x => x.IsSelected))
                    ProjectsListBox.SelectedItems.Add(p);


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

            var combined = new Dictionary<string, ProjectMaterial>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in projects)
            {
                _activeMarketId = project?.MarketId ?? 0;   // <-- add this early
                try
                {
                    if (project.Source == "Local")
                    {
                        // ✅ Use the same LOCAL path & column names as elsewhere
                        string localDbPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "EDStationManager",
                            "stations.db"
                        );

                        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
                        conn.Open();

                        var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                        SELECT ResourceName, RequiredAmount, ProvidedAmount
                        FROM ProjectResources
                        WHERE MarketID=@mid AND RequiredAmount > ProvidedAmount
                        ORDER BY ResourceName;";
                        cmd.Parameters.AddWithValue("@mid", project.MarketId);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string name = reader.GetString(0); // ResourceName
                            int required = reader.GetInt32(1);
                            int provided = reader.GetInt32(2);

                            if (!combined.TryGetValue(name, out var existing))
                            {
                                existing = new ProjectMaterial
                                {
                                    Material = name,
                                    Required = 0,
                                    Provided = 0,
                                    Needed = 0,
                                    Category = MaterialCategories.GetCategory(name),
                                    CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                                };
                                combined[name] = existing;
                            }

                            existing.Required += required;
                            existing.Provided += provided;
                            existing.Needed = Math.Max(existing.Required - existing.Provided, 0);
                        }
                    }
                    else
                    {
                        // ✅ MySQL for Server via DbConnectionManager (single shared connection + op-aware logs)
                        DbConnectionManager.Instance.Execute(cmd =>
                        {
                            cmd.CommandText = @"
        SELECT ResourceName, RequiredAmount, ProvidedAmount
        FROM ProjectResources
        WHERE MarketID = @mid
          AND RequiredAmount > ProvidedAmount
        ORDER BY ResourceName;";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@mid", project.MarketId);

                            using var reader = cmd.ExecuteReader();
                            while (reader.Read())
                            {
                                string name = reader.GetString("ResourceName");
                                int required = reader.GetInt32("RequiredAmount");
                                int provided = reader.GetInt32("ProvidedAmount");

                                if (!combined.TryGetValue(name, out var existing))
                                {
                                    existing = new ProjectMaterial
                                    {
                                        Material = name,
                                        Required = 0,
                                        Provided = 0,
                                        Needed = 0,
                                        Category = MaterialCategories.GetCategory(name),
                                        CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                                    };
                                    combined[name] = existing;
                                }

                                existing.Required += required;
                                existing.Provided += provided;
                                existing.Needed = Math.Max(existing.Required - existing.Provided, 0);
                            }

                            return 0; // Execute<T> requires a return value
                        },
                        // short preview so your [DB][FAIL] log shows exactly what was running
                        sqlPreview: "SELECT … FROM ProjectResources WHERE MarketID=@mid AND RequiredAmount>ProvidedAmount ORDER BY ResourceName");

                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error loading materials for {project.StationName}: {ex.Message}", "Warning");
                }
            }

            // ✅ Merge per-carrier amounts across all SERVER projects (sums by carrier)
            try
            {
                if (projectDb != null && projects.Any(p => p.Source == "Server"))
                {
                    var mergedCarrierMaps = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var p in projects.Where(p => p.Source == "Server"))
                    {
                        var perCarrier = projectDb.GetLinkedCarrierStockPerCarrier(p.MarketId)
                                         ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                        foreach (var kv in perCarrier)
                        {
                            var resource = kv.Key;
                            var map = kv.Value; // carrier -> amount

                            if (!mergedCarrierMaps.TryGetValue(resource, out var acc))
                                mergedCarrierMaps[resource] = acc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                            foreach (var c in map)
                                acc[c.Key] = acc.TryGetValue(c.Key, out var existing) ? existing + c.Value : c.Value;
                        }
                    }

                    // attach to rows (ensure non-null)
                    foreach (var kv in combined)
                    {
                        if (mergedCarrierMaps.TryGetValue(kv.Key, out var map) && map != null)
                            kv.Value.CarrierAmounts = map;
                        else
                            kv.Value.CarrierAmounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    // keep CarrierAmounts non-null
                    foreach (var v in combined.Values)
                        v.CarrierAmounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Per-carrier merge failed: {ex.Message}", "Warning");
            }

            // ✅ Update UI with merged materials
            var sorted = combined.Values
                .Where(m => m.Needed > 0)
                .OrderBy(m => MaterialCategories.CategoryOrder.IndexOf(m.Category))
                .ThenBy(m => m.Material)
                .ToList();

            CurrentProjectMaterials.Clear();
            foreach (var row in sorted)
                CurrentProjectMaterials.Add(row);

            Logger.Log($"Loaded {CurrentProjectMaterials.Count} combined materials.", "Info");
            UpdateCarrierMaterialOverview();

            // Only show carrier columns if at least one SERVER project selected and carriers exist
            if (projects.Any(p => p.Source == "Server"))
                BuildCarrierColumnsIfChanged();
            else
                ClearCarrierColumns();
        }

        private void LoadMaterialsForProject(Project project)
        {
            CurrentProjectMaterials.Clear();
            if (project == null) return;

            try
            {
                if (project.Source == "Local")
                {
                    // ✅ Use the same local DB path you used elsewhere
                    string localDbPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "EDStationManager",
                        "stations.db"
                    );

                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
                    conn.Open();

                    var cmd = conn.CreateCommand();
                    // ✅ Column is ResourceName in your local schema
                    cmd.CommandText = @"
        SELECT ResourceName, RequiredAmount, ProvidedAmount
        FROM ProjectResources
        WHERE MarketID = @mid
          AND RequiredAmount > ProvidedAmount
        ORDER BY ResourceName;";
                    cmd.Parameters.AddWithValue("@mid", project.MarketId);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        int required = reader.GetInt32(1);
                        int provided = reader.GetInt32(2);
                        int needed = required - provided;

                        var mat = new ProjectMaterial
                        {
                            Material = reader.GetString(0), // ResourceName
                            Required = required,
                            Provided = provided,
                            Needed = needed,
                            Category = MaterialCategories.GetCategory(reader.GetString(0)),
                            CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                        };
                        CurrentProjectMaterials.Add(mat);
                    }
                }
                else
                {
                    // ✅ MySQL for Server via DbConnectionManager (single shared connection + op-aware logs)
                    DbConnectionManager.Instance.Execute(cmd =>
                    {
                        cmd.CommandText = @"
        SELECT ResourceName, RequiredAmount, ProvidedAmount
        FROM ProjectResources
        WHERE MarketID = @mid
          AND RequiredAmount > ProvidedAmount
        ORDER BY ResourceName;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@mid", project.MarketId);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var materialName = reader.GetString("ResourceName");
                            int required = reader.GetInt32("RequiredAmount");
                            int provided = reader.GetInt32("ProvidedAmount");
                            int needed = required - provided;

                            var mat = new ProjectMaterial
                            {
                                Material = materialName,
                                Required = required,
                                Provided = provided,
                                Needed = Math.Max(needed, 0),
                                Category = MaterialCategories.GetCategory(materialName),
                                CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                            };
                            CurrentProjectMaterials.Add(mat);
                        }

                        return 0; // Execute<T> requires a return value
                    },
                    // short preview so failures tell you exactly what query/op broke
                    sqlPreview: "SELECT … FROM ProjectResources WHERE MarketID=@mid AND RequiredAmount>ProvidedAmount ORDER BY ResourceName");

                    // ✅ Attach per-carrier only for Server projects and only if DB is available
                    if (projectDb != null)
                    {
                        var perCarrier = projectDb.GetLinkedCarrierStockPerCarrier(project.MarketId)
                                        ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                        foreach (var row in CurrentProjectMaterials)
                        {
                            if (perCarrier.TryGetValue(row.Material, out var map) && map != null)
                                row.CarrierAmounts = map;
                            else
                                row.CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        }
                    }

                }

                // sort -> refill
                var ordered = CurrentProjectMaterials
                    .OrderBy(m => MaterialCategories.CategoryOrder.IndexOf(m.Category))
                    .ThenBy(m => m.Material)
                    .ToList();

                CurrentProjectMaterials.Clear();
                foreach (var row in ordered)
                    CurrentProjectMaterials.Add(row);

                // branch columns: Local=no carriers, Server=build carriers
                if (project.Source == "Local")
                {
                    foreach (var row in CurrentProjectMaterials)
                        row.CarrierAmounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    ClearCarrierColumns();
                }
                else
                {
                    BuildCarrierColumnsIfChanged();
                }

                Logger.Log($"Loaded {CurrentProjectMaterials.Count} materials for {project}", "Info");
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading materials: " + ex.Message, "Warning");
            }

            UpdateCarrierMaterialOverview();
            // Keep for safety; BuildCarrierColumnsIfChanged will clear when no carriers:
            BuildCarrierColumnsIfChanged();
        }


        // --- New: clear dynamic carrier columns when none should be shown ---
        private void ClearCarrierColumns()
        {
            if (MaterialsDataGrid == null) return;
            while (MaterialsDataGrid.Columns.Count > 2)
                MaterialsDataGrid.Columns.RemoveAt(2);
            _currentCarrierColumns = new List<string>();
        }
        
        private List<string> _currentCarrierColumns = new();

        private async Task<List<ProjectMaterial>> FetchMaterialsForSelectionAsync(IEnumerable<Project> projects, CancellationToken ct)
        {
            if (projects == null) return new List<ProjectMaterial>();

            return await Task.Run(() =>
            {
                var selected = projects.ToList();
                if (selected.Count == 0) return new List<ProjectMaterial>();

                var combined = new Dictionary<string, ProjectMaterial>(StringComparer.OrdinalIgnoreCase);

                foreach (var project in selected)
                {
                    ct.ThrowIfCancellationRequested();

                    if (project.Source == "Local")
                    {
                        // === SQLite path ===
                        string localDbPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "EDStationManager", "stations.db");

                        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
                        conn.Open();

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                    SELECT ResourceName, RequiredAmount, ProvidedAmount
                    FROM ProjectResources
                    WHERE MarketID = @mid
                      AND RequiredAmount > ProvidedAmount
                    ORDER BY ResourceName;";
                        cmd.Parameters.AddWithValue("@mid", project.MarketId);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var name = reader.GetString(0);
                            var required = reader.GetInt32(1);
                            var provided = reader.GetInt32(2);

                            if (!combined.TryGetValue(name, out var row))
                            {
                                row = new ProjectMaterial
                                {
                                    Material = name,
                                    Required = 0,
                                    Provided = 0,
                                    Needed = 0,
                                    Category = MaterialCategories.GetCategory(name),
                                    CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                                };
                                combined[name] = row;
                            }

                            row.Required += required;
                            row.Provided += provided;
                            row.Needed = Math.Max(row.Required - row.Provided, 0);
                        }
                    }
                    else
                    {
                        // === MySQL path via shared DbConnectionManager (no ad-hoc opens) ===
                        DbConnectionManager.Instance.Execute(cmd =>
                        {
                            cmd.CommandText = @"
                        SELECT ResourceName, RequiredAmount, ProvidedAmount
                        FROM ProjectResources
                        WHERE MarketID = @mid
                          AND RequiredAmount > ProvidedAmount
                        ORDER BY ResourceName;";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@mid", project.MarketId);

                            using var reader = cmd.ExecuteReader();
                            while (reader.Read())
                            {
                                var name = reader.GetString(0);
                                var required = reader.GetInt32(1);
                                var provided = reader.GetInt32(2);

                                if (!combined.TryGetValue(name, out var row))
                                {
                                    row = new ProjectMaterial
                                    {
                                        Material = name,
                                        Required = 0,
                                        Provided = 0,
                                        Needed = 0,
                                        Category = MaterialCategories.GetCategory(name),
                                        CarrierAmounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                                    };
                                    combined[name] = row;
                                }

                                row.Required += required;
                                row.Provided += provided;
                                row.Needed = Math.Max(row.Required - row.Provided, 0);
                            }

                            return 0; // Execute<T> requires a return value
                        },
                        // SQL preview for op-aware logging (#4)
                        sqlPreview: "SELECT … FROM ProjectResources WHERE MarketID=@mid AND RequiredAmount>ProvidedAmount ORDER BY ResourceName");
                    }
                }

                // Merge per-carrier amounts if any Server projects selected
                if (projectDb != null && selected.Any(p => p.Source == "Server"))
                {
                    var mergedCarrierMaps = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var p in selected.Where(p => p.Source == "Server"))
                    {
                        var perCarrier = projectDb.GetLinkedCarrierStockPerCarrier(p.MarketId)
                                         ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                        foreach (var kv in perCarrier)
                        {
                            var resource = kv.Key;
                            var map = kv.Value; // carrier -> amount
                            if (!mergedCarrierMaps.TryGetValue(resource, out var acc))
                                mergedCarrierMaps[resource] = acc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                            foreach (var c in map)
                                acc[c.Key] = acc.TryGetValue(c.Key, out var existing) ? existing + c.Value : c.Value;
                        }
                    }

                    foreach (var kv in combined)
                    {
                        if (mergedCarrierMaps.TryGetValue(kv.Key, out var map) && map != null)
                            kv.Value.CarrierAmounts = map;
                        else
                            kv.Value.CarrierAmounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    foreach (var v in combined.Values)
                        v.CarrierAmounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                // Sort like the UI
                var sorted = combined.Values
                    .OrderBy(m => MaterialCategories.CategoryOrder.IndexOf(m.Category))
                    .ThenBy(m => m.Material, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return sorted;
            }, ct);
        }

        // Top of the class (same as your refresh code)
        private static DateTime _lastMaterialsWarnUtc = DateTime.MinValue;

        private static void WarnMaterialsOncePer(TimeSpan minInterval, string message)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMaterialsWarnUtc) >= minInterval)
            {
                _lastMaterialsWarnUtc = now;
                Logger.Log(message, "Warning");
            }
        }

        private static async Task<T> RetryMySqlAsync<T>(Func<Task<T>> work, int attempts = 3, int firstDelayMs = 300)
        {
            int delay = firstDelayMs;
            for (int i = 1; ; i++)
            {
                try { return await work().ConfigureAwait(false); }
                catch (MySql.Data.MySqlClient.MySqlException) when (i < attempts)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay *= 2;
                }
            }
        }


        // Replace your method with this version
        private async Task RefreshMaterialsIfChangedAsync()
        {
            if (_materialsRefreshInFlight) return;
            _materialsRefreshInFlight = true;

            _materialsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _materialsCts = cts;

            try
            {
                var snapshot = SelectedProjects?.ToList() ?? new List<Project>();
                bool needsServer = snapshot.Any(p => p.Source == "Server");

                var rows = await RetryMySqlAsync(() =>
                    FetchMaterialsForSelectionAsync(snapshot, cts.Token)
                );

                var newHash = HashMaterials(rows);
                if (newHash == _lastMaterialsHash) return;
                _lastMaterialsHash = newHash;

                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    var cvs = (CollectionViewSource)FindResource("MaterialsView");
                    CurrentProjectMaterials.Clear();
                    foreach (var r in rows) CurrentProjectMaterials.Add(r);
                    cvs.View.Refresh();

                    UpdateCarrierMaterialOverview();
                    if (needsServer) BuildCarrierColumnsIfChanged();
                    else ClearCarrierColumns();

                    ShowOverlayIfAllowed();
                }), DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
            catch (MySqlException ex)
            {
                WarnMaterialsOncePer(TimeSpan.FromSeconds(20), $"Materials refresh failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                WarnMaterialsOncePer(TimeSpan.FromSeconds(10), $"Materials refresh failed: {ex.Message}");
            }
            finally
            {
                _materialsRefreshInFlight = false;
            }
        }



        private void BuildCarrierColumnsIfChanged()
        {
            if (MaterialsDataGrid == null) return;

            var carriers = CurrentProjectMaterials
                .SelectMany(m => m.CarrierAmounts?.Where(kv => kv.Value >= 0).Select(kv => kv.Key)
                                  ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();

            if (carriers.Count == 0)
            {
                ClearCarrierColumns();
                return;
            }

            if (_currentCarrierColumns.SequenceEqual(carriers, StringComparer.OrdinalIgnoreCase))
                return;

            // keep first 2 fixed columns
            while (MaterialsDataGrid.Columns.Count > 2)
                MaterialsDataGrid.Columns.RemoveAt(2);

            MaterialsDataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Remain (-Carriers)",
                Binding = new Binding("RemainingAfterCarriers") { TargetNullValue = 0, FallbackValue = 0 },
                Width = 130
            });

            foreach (var carrier in carriers)
            {
                // 👇 NOTE the single quotes around {carrier}
                var binding = new Binding($"CarrierAmounts['{carrier}']")
                {
                    Mode = BindingMode.OneWay,
                    TargetNullValue = 0,
                    FallbackValue = 0
                };

                MaterialsDataGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = carrier,   // short code
                    Binding = new Binding($"CarrierAmounts[{carrier}]"),
                    Width = 90
                });
            }


            _currentCarrierColumns = carriers;
        }

        private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not Project project) return;

            // capture scroll
            _projectsScrollViewer ??= FindVisualChild<ScrollViewer>(ProjectsListBox);
            double v = _projectsScrollViewer?.VerticalOffset ?? 0;
            double h = _projectsScrollViewer?.HorizontalOffset ?? 0;

            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;

            int cur = IndexOfProject(project);
            if (cur < 0) return;

            bool selectionChanged = false;

            if (!ctrl && !shift)
            {
                // toggle-off if it's the only one selected
                if (SelectedProjects.Count == 1 && ReferenceEquals(SelectedProjects[0], project))
                {
                    foreach (var p in Projects) p.IsSelected = false;
                    _anchorIndex = -1;
                }
                else
                {
                    foreach (var p in Projects) p.IsSelected = false;
                    project.IsSelected = true;
                    _anchorIndex = cur;
                }
                selectionChanged = true;
            }
            else if (ctrl && !shift)
            {
                project.IsSelected = !project.IsSelected;
                if (project.IsSelected) _anchorIndex = cur;
                selectionChanged = true;
            }
            else if (shift)
            {
                if (_anchorIndex < 0) _anchorIndex = cur;
                int start = Math.Min(_anchorIndex, cur);
                int end = Math.Max(_anchorIndex, cur);

                if (!ctrl)
                    foreach (var p in Projects) p.IsSelected = false;

                for (int i = start; i <= end; i++)
                    ((Project)ProjectsListBox.Items[i]).IsSelected = true;

                selectionChanged = true;
            }

            // 🔧 Single source of truth → push to both collections
            SelectedProjects.Clear();
            foreach (var p in Projects)
                if (p.IsSelected) SelectedProjects.Add(p);

            ProjectsListBox.SelectedItems.Clear();
            foreach (var p in SelectedProjects)
                ProjectsListBox.SelectedItems.Add(p);

            e.Handled = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _projectsScrollViewer?.ScrollToHorizontalOffset(h);
                _projectsScrollViewer?.ScrollToVerticalOffset(v);
            }), DispatcherPriority.Loaded);

            if (selectionChanged)
            {
                // Not strictly needed for ObservableCollection, but harmless
                OnPropertyChanged(nameof(SelectedProjects));
                RefreshMaterialsIfChangedAsync();
            }
        }

        // In MainWindow
        private async Task ForceRefreshMaterialsAsync()
        {
            // cancel & let the current task exit quickly
            _materialsCts?.Cancel();

            // make sure the next run doesn't skip due to same hash
            _lastMaterialsHash = null;

            // allow a new refresh to start
            _materialsRefreshInFlight = false;

            // clear the current rows immediately so UI can't show stale data
            CurrentProjectMaterials.Clear();
            ClearCarrierColumns();

            // kick an immediate rebuild for the current selection
            await RefreshMaterialsIfChangedAsync();
        }


        private int IndexOfProject(Project p)
        {
            // Projects is your ObservableCollection<Project>
            return ProjectsListBox.Items.IndexOf(p);
        }

        



        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _projectsView?.Refresh();
        }
        private void SearchMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _projectsView?.Refresh();
        }
        private void ListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is Button || d is System.Windows.Controls.Primitives.ToggleButton
                    || d is Hyperlink)
                    return; // let interactive controls handle the click

                if (d is ListBoxItem) break;
                d = VisualTreeHelper.GetParent(d);
            }
            e.Handled = true; // swallow row background clicks
        }
        private void ProjectsListBox_Loaded(object sender, RoutedEventArgs e)
        {
            _projectsScrollViewer ??= FindVisualChild<ScrollViewer>(ProjectsListBox);
            if (ProjectsListBox.ItemsSource != null)
            {
                _projectsView = CollectionViewSource.GetDefaultView(ProjectsListBox.ItemsSource);
                _projectsView.Filter = ProjectFilter;  // never unset; the logic handles empty query
            }
        }
        private bool ProjectFilter(object item)
        {
            if (item is not Project project) return false;

            // 1) Always show Local
            if (string.Equals(project.Source, "Local", StringComparison.OrdinalIgnoreCase))
                return true;

            // From here on: Server projects only
            string query = (SearchBox.Text ?? "").Trim().ToLowerInvariant();

            // 2) Empty query? Hide Server projects
            if (string.IsNullOrEmpty(query))
                return false;

            // 3) Optional: enforce 2+ chars before matching Server projects
            // if (query.Length < 2) return false;

            // 4) Match by selected mode
            string mode = (SearchMode.SelectedItem as ComboBoxItem)?.Content?.ToString();

            return mode switch
            {
                "Project Name" => (project.StationName ?? "").ToLowerInvariant().Contains(query),
                "Created By" => (project.CreatedBy ?? "").ToLowerInvariant().Contains(query),
                "System Name" => (project.SystemName ?? "").ToLowerInvariant().Contains(query),
                _ => (project.StationName ?? "").ToLowerInvariant().Contains(query)
            };
        }




        private void ListBoxItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true; // never auto-jump
        }

        // Utility: find a visual child
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(parent); i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T ok) return ok;
                var sub = FindVisualChild<T>(child);
                if (sub != null) return sub;
            }
            return null;
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
                            // ✅ Delete from MySQL via DbConnectionManager (single shared connection + tx + op-aware logs)
                            DbConnectionManager.Instance.Execute(cmd =>
                            {
                                using var tx = cmd.Connection.BeginTransaction();
                                cmd.Transaction = tx;

                                // 1) delete resources
                                cmd.CommandText = "DELETE FROM ProjectResources WHERE MarketID = @mid;";
                                cmd.Parameters.Clear();
                                cmd.Parameters.AddWithValue("@mid", proj.MarketId);
                                cmd.ExecuteNonQuery();

                                // 2) delete project
                                cmd.CommandText = "DELETE FROM Projects WHERE MarketID = @mid;";
                                cmd.Parameters.Clear();
                                cmd.Parameters.AddWithValue("@mid", proj.MarketId);
                                cmd.ExecuteNonQuery();

                                tx.Commit();
                                return 0; // Execute<T> requires a return
                            },
                            // Shows up in your [DB][FAIL] logs if anything goes wrong
                            sqlPreview: "DELETE ProjectResources; DELETE Projects WHERE MarketID=@mid");

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

        private sealed class ResourceRow
        {
            public string Name { get; init; } = "";
            public int Required { get; init; }
            public int Provided { get; init; }
            public int Payment { get; init; }
        }

        private async void SyncProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not Project project)
                return;

            Logger.Log($"[SYNC] Starting sync for project: {project.StationName} (MarketID {project.MarketId})", "Info");

            if (string.Equals(project.Source, "Server", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show($"'{project.StationName}' is already on the server.", "Sync Skipped",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            btn.IsEnabled = false;

            try
            {
                // ----- Read everything we need from LOCAL first -----
                string localDbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EDStationManager",
                    "stations.db"
                );

                long marketId = project.MarketId; // ensure this is Int64 in your Project model
                string? systemName = null;
                string? stationName = null;
                string? createdBy = null;
                DateTime createdAt = DateTime.UtcNow;

                var resources = new List<ResourceRow>();

                using (var localConn = new SqliteConnection($"Data Source={localDbPath}"))
                {
                    await localConn.OpenAsync();

                    // Project row
                    using (var getProject = localConn.CreateCommand())
                    {
                        getProject.CommandText =
                            "SELECT MarketID, SystemName, StationName, CreatedBy, CreatedAt " +
                            "FROM Projects WHERE MarketID=@mid";
                        getProject.Parameters.AddWithValue("@mid", marketId);

                        using var reader = await getProject.ExecuteReaderAsync();
                        if (!await reader.ReadAsync())
                            throw new InvalidOperationException("Local project not found.");

                        // READ (prefer safe getters)
                        marketId = reader.GetInt64(0);
                        systemName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        stationName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        createdBy = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        createdAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4);
                    }

                    // Resources
                    using (var getResources = localConn.CreateCommand())
                    {
                        getResources.CommandText =
                            "SELECT ResourceName, RequiredAmount, ProvidedAmount, Payment " +
                            "FROM ProjectResources WHERE MarketID=@mid";
                        getResources.Parameters.AddWithValue("@mid", marketId);

                        using var resReader = await getResources.ExecuteReaderAsync();
                        while (await resReader.ReadAsync())
                        {
                            resources.Add(new ResourceRow
                            {
                                Name = resReader.IsDBNull(0) ? "" : resReader.GetString(0),
                                Required = resReader.IsDBNull(1) ? 0 : resReader.GetInt32(1),
                                Provided = resReader.IsDBNull(2) ? 0 : resReader.GetInt32(2),
                                Payment = resReader.IsDBNull(3) ? 0 : resReader.GetInt32(3),
                            });
                        }
                    }

                    // ----- Push to SERVER using pooled DbConnectionManager in ONE TX -----
                    await DbConnectionManager.Instance.ExecuteAsync<int>(async pooledCmd =>
                    {
                        // We’ll use the provided connection for multiple commands inside a transaction
                        using var tx = pooledCmd.Connection!.BeginTransaction();
                        try
                        {
                            // Upsert project
                            using (var upsertProj = pooledCmd.Connection.CreateCommand())
                            {
                                upsertProj.Transaction = tx;
                                upsertProj.CommandText = @"
INSERT INTO Projects (MarketID, SystemName, StationName, CreatedBy, CreatedAt)
VALUES (@mid, @sys, @st, @cb, @ca)
ON DUPLICATE KEY UPDATE 
    SystemName=@sys,
    StationName=@st,
    CreatedBy=@cb,
    CreatedAt=@ca;";
                                upsertProj.Parameters.Add("@mid", MySqlDbType.Int64).Value = marketId;
                                upsertProj.Parameters.Add("@sys", MySqlDbType.VarChar).Value = systemName ?? "";
                                upsertProj.Parameters.Add("@st", MySqlDbType.VarChar).Value = stationName ?? "";
                                upsertProj.Parameters.Add("@cb", MySqlDbType.VarChar).Value = createdBy ?? "";
                                upsertProj.Parameters.Add("@ca", MySqlDbType.DateTime).Value = createdAt;
                                await upsertProj.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }

                            // Upsert resources (re-use one prepared command for speed)
                            using (var upsertRes = pooledCmd.Connection.CreateCommand())
                            {
                                upsertRes.Transaction = tx;
                                upsertRes.CommandText = @"
INSERT INTO ProjectResources (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
VALUES (@mid, @mat, @req, @prov, @pay)
ON DUPLICATE KEY UPDATE 
    RequiredAmount=@req,
    ProvidedAmount=@prov,
    Payment=@pay;";
                                var pMid = upsertRes.Parameters.Add("@mid", MySqlDbType.Int64);
                                var pMat = upsertRes.Parameters.Add("@mat", MySqlDbType.VarChar);
                                var pReq = upsertRes.Parameters.Add("@req", MySqlDbType.Int32);
                                var pProv = upsertRes.Parameters.Add("@prov", MySqlDbType.Int32);
                                var pPay = upsertRes.Parameters.Add("@pay", MySqlDbType.Int32);

                                foreach (var r in resources)
                                {
                                    pMid.Value = marketId;
                                    pMat.Value = r.Name ?? "";
                                    pReq.Value = r.Required;
                                    pProv.Value = r.Provided;
                                    pPay.Value = r.Payment;
                                    await upsertRes.ExecuteNonQueryAsync().ConfigureAwait(false);
                                }
                            }

                            await tx.CommitAsync().ConfigureAwait(false);
                            return 0;
                        }
                        catch
                        {
                            await tx.RollbackAsync().ConfigureAwait(false);
                            throw;
                        }
                    }, sqlPreview: $"Sync Project {marketId}");

                    // ----- If server sync OK → delete from LOCAL -----
                    using (var deleteRes = localConn.CreateCommand())
                    {
                        deleteRes.CommandText = "DELETE FROM ProjectResources WHERE MarketID=@mid";
                        deleteRes.Parameters.AddWithValue("@mid", marketId);
                        await deleteRes.ExecuteNonQueryAsync();
                    }
                    using (var deleteProject = localConn.CreateCommand())
                    {
                        deleteProject.CommandText = "DELETE FROM Projects WHERE MarketID=@mid";
                        deleteProject.Parameters.AddWithValue("@mid", marketId);
                        await deleteProject.ExecuteNonQueryAsync();
                    }
                }

                // ----- UI notify -----
                Logger.Log($"✅ Project '{project.StationName}' synced successfully and removed from local DB.", "Success");
                MessageBox.Show(
                    $"Project '{project.StationName}' synced to server and removed from local DB!",
                    "Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                LoadProjects();
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Sync failed for {project.StationName}: {ex.Message}", "Error");
                MessageBox.Show($"Sync failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }
        private static string CleanCommander(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();

            // strip common prefixes/labels
            s = s.Replace("Commander:", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("CMDR", "", StringComparison.OrdinalIgnoreCase)
                 .Replace(":", "");

            return s.Trim();
        }
        private void OpenArchive_Click(object sender, RoutedEventArgs e)
        {
            var commander = this.CommanderName;
            if (archiveWindow == null || !archiveWindow.IsLoaded)
            {
                archiveWindow = new ArchiveWindow(projectDb, commander);
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
                    var parsedColor = (MediaColor)MediaColorConverter.ConvertFromString(color);

                    var solidBrush = new MediaBrush(parsedColor);
                    var overlayBrush = new MediaBrush(MediaColor.FromArgb(0x22, parsedColor.R, parsedColor.G, parsedColor.B));

                    WpfApp.Current.Resources["HighlightBrush"] = solidBrush;
                    WpfApp.Current.Resources["HighlightOverlayBrush"] = overlayBrush;
                }
                catch
                {
                    WpfApp.Current.Resources["HighlightBrush"] =
                        new MediaBrush(System.Windows.Media.Colors.Orange);
                    WpfApp.Current.Resources["HighlightOverlayBrush"] =
                        new MediaBrush(MediaColor.FromArgb(0x22, 255, 140, 0));
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

        public static class MaterialCategories
        {
            public static readonly List<string> CategoryOrder = new()
            {
                "Chemicals",
                "Consumer Items",
                "Foods",
                "Industrial Materials",
                "Legal Drugs",
                "Machinery",
                "Medicines",
                "Metals",
                "Minerals",
                "Salvage",
                "Slavery",
                "Technology",
                "Textiles",
                "Waste",
                "Weapons",
                "Other"
            };
            public static int GetOrderIndex(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return int.MaxValue;
                // exact match first
                int ix = CategoryOrder.IndexOf(category);
                if (ix >= 0) return ix;
                // case-insensitive fallback
                ix = CategoryOrder.FindIndex(c =>
                    c.Equals(category, StringComparison.OrdinalIgnoreCase));
                return ix >= 0 ? ix : int.MaxValue;
            }

            public static readonly Dictionary<string, string> CategoryMap =
                new(StringComparer.OrdinalIgnoreCase)
                {
                    // Chemicals
                    { "liquid oxygen", "Chemicals" },
                    { "water", "Chemicals" },
                    { "surface stabilisers", "Chemicals" },
                    { "pesticides", "Chemicals" },

                    // Foods
                    { "food cartridges", "Foods" },
                    { "fruit and vegetables", "Foods" },
                    { "grain", "Foods" },

                    // Industrial Materials
                    { "ceramic composites", "Industrial Materials" },
                    { "cmm composite", "Industrial Materials" },
                    { "insulating membrane", "Industrial Materials" },
                    { "polymers", "Industrial Materials" },
                    { "semiconductors", "Industrial Materials" },
                    { "superconductors", "Industrial Materials" },

                    //Medicines
                    { "agri-medicines", "Medicines" },

                    //Waste
                    { "biowaste", "Waste" }, 

                    // Machinery
                    { "power generators", "Machinery" },
                    { "water purifiers", "Machinery" },
                    { "building fabricators", "Machinery" },
                    { "emergency power cells", "Machinery" },
                    { "crop harvesters", "Machinery" },
                    { "geological equipment", "Machinery"},

                    // Metals
                    { "aluminium", "Metals" },
                    { "aluminum", "Metals" },
                    { "steel", "Metals" },
                    { "titanium", "Metals" },
                    { "copper", "Metals" },
                    { "indite", "Metals" },

                    // Technology
                    { "computer components", "Technology" },
                    { "medical diagnostic equipment", "Technology" },
                    { "land enrichment systems", "Technology" },
                    { "structural regulators", "Technology" },
                    { "micro controllers", "Technology" },
                    { "bioreducing lichen", "Technology" },
                    { "h.e. suits", "Technology" },
                    { "muon imager", "Technology" },
                    { "resonating separators", "Technology" },
                    { "robotics", "Technology" },

                    // Weapons
                    { "non-lethal weapons", "Weapons" },

                    // Consumer Items
                    { "evacuation shelter", "Consumer Items" },
                    { "survival equipment", "Consumer Items" }
                };

            public static string GetCategory(string resource)
            {
                if (string.IsNullOrWhiteSpace(resource)) return "Other";
                return CategoryMap.TryGetValue(resource.Trim().ToLower(), out var cat)
                    ? cat
                    : "Other";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
