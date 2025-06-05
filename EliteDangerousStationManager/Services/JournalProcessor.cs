using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;

namespace EliteDangerousStationManager.Services
{
    public class JournalProcessor
    {
        private readonly string _journalPath;
        private readonly string _commanderName;
        private readonly EddnSender _eddnSender;
        private readonly InaraSender _inaraSender;

        private readonly string _stateFilePath;
        private ReadState _lastState = new ReadState();

        // Once we pick a working connection string, use it for all DB calls:
        private readonly string _connectionString;

        // Cache whether DB is currently reachable.  If false, skip DB calls.
        private bool _dbAvailable;

        // Holds whether the last docking was on a FleetCarrier
        private bool _lastDockedOnFleetCarrier = false;

        // Holds the system and station name from the last Docked event
        private string _lastDockedSystem = null;
        private string _lastDockedStation = null;

        // -------------- Public cargo‐tracking members --------------

        /// <summary>
        /// In‐memory list of items currently on the fleet carrier.
        /// The UI can bind to this and display “Transferred” values.
        /// </summary>
        public List<CargoItem> FleetCarrierCargoItems { get; private set; } = new List<CargoItem>();

        /// <summary>
        /// Fires whenever FleetCarrierCargoItems is modified (add/remove/change). 
        /// The MainWindow subscribes to this to refresh its ObservableCollection.
        /// </summary>
        public event Action CarrierCargoChanged;

        // ------------------------------------------------------------

        // Simple helper to test whether a connection string works:
        private static bool CanConnect(string connString)
        {
            try
            {
                using var conn = new MySqlConnection(connString);
                conn.Open();
                conn.Close();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public JournalProcessor(string journalPath, string commanderName)
        {
            _journalPath = journalPath;
            _commanderName = commanderName;
            _eddnSender = new EddnSender();
            _inaraSender = new InaraSender();

            // Decide which connection string to use (PrimaryDB → FallbackDB).
            string primary = ConfigurationManager.ConnectionStrings["PrimaryDB"]?.ConnectionString;
            string fallback = ConfigurationManager.ConnectionStrings["FallbackDB"]?.ConnectionString;

            if (!string.IsNullOrWhiteSpace(primary) && CanConnect(primary))
            {
                _connectionString = primary;
                _dbAvailable = true;
                Logger.Log("JournalProcessor: Connected to PrimaryDB (108.211.228.206).", "Info");
            }
            else if (!string.IsNullOrWhiteSpace(fallback) && CanConnect(fallback))
            {
                _connectionString = fallback;
                _dbAvailable = true;
                Logger.Log("JournalProcessor: PrimaryDB unreachable → using FallbackDB (192.168.10.68).", "Warning");
            }
            else
            {
                // Neither host is up, so mark DB unavailable and store fallback anyway
                _connectionString = primary ?? fallback ?? string.Empty;
                _dbAvailable = false;
                Logger.Log("JournalProcessor: Cannot connect to any DB host. All DB updates will be skipped for now.", "Warning");
            }

            _stateFilePath = Path.Combine(_journalPath, "lastread.state");
            LoadReadState();
        }

        private void LoadReadState()
        {
            if (!File.Exists(_stateFilePath))
                return;

            var lines = File.ReadAllLines(_stateFilePath);
            if (lines.Length == 2)
            {
                _lastState.FileName = lines[0];
                if (long.TryParse(lines[1], out long pos))
                    _lastState.Position = pos;
            }
        }

        private void SaveReadState(string fileName, long position)
        {
            File.WriteAllLines(_stateFilePath, new[] { fileName, position.ToString() });
        }

        /// <summary>
        /// Main loop: read new journal lines, parse events (Docked, cargo, depot, etc.), and—if DB is available—update resource tables.
        /// </summary>
        public void ScanForDepotEvents()
        {
            // 1) Identify the most recent "Journal.*.log" file
            var latestFile = Directory
                .GetFiles(_journalPath, "Journal.*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (latestFile == null)
            {
                Logger.Log("No journal file found.", "Warning");
                return;
            }

            Logger.Log($"Reading journal file: {latestFile}", "Debug");

            bool isNewFile = !string.Equals(latestFile, _lastState.FileName, StringComparison.OrdinalIgnoreCase);
            long startPos = isNewFile ? 0 : _lastState.Position;

            // 2) Read only the new lines
            var lines = new List<string>();
            int maxRetries = 5, delayMs = 500, attempt = 0;
            bool fileOpened = false;

            while (attempt < maxRetries && !fileOpened)
            {
                try
                {
                    using (var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Seek(startPos, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs);
                        string rawLine;
                        while ((rawLine = sr.ReadLine()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(rawLine))
                                lines.Add(rawLine);
                        }

                        _lastState.FileName = latestFile;
                        _lastState.Position = fs.Position;
                        SaveReadState(_lastState.FileName, _lastState.Position);
                    }
                    fileOpened = true;
                }
                catch (IOException ex)
                {
                    attempt++;
                    if (attempt >= maxRetries)
                    {
                        Logger.Log($"Error reading '{latestFile}' after {maxRetries} attempts: {ex.Message}", "Warning");
                        return;
                    }
                    Thread.Sleep(delayMs);
                }
            }

            // 3) If no new lines, reload entire file—but mark that we're in "full reload" mode.
            // … inside ScanForDepotEvents(), replace the reload-entire-file block with this:

            // 3) If nothing new, reload the entire file (so we still parse Depot events),
            //    but open with ReadWrite sharing so we don’t get “file locked” errors.
            bool isReload = false;
            if (!isNewFile && lines.Count == 0)
            {
                Logger.Log("No new lines since last offset; reloading entire file.", "Debug");
                try
                {
                    // Open the journal with FileShare.ReadWrite to avoid "in use by another process" errors:
                    using var fsReload = new FileStream(
                        latestFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    );
                    using var srReload = new StreamReader(fsReload);

                    // Read all non‐blank lines into our list
                    var allText = srReload.ReadToEnd();
                    lines = allText
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();

                    isReload = true;
                    // (Note: do NOT update _lastState.Position here,
                    //  so that future scans still only pick up truly new lines.)
                }
                catch (IOException ex)
                {
                    Logger.Log($"Failed to reload entire file: {ex.Message}", "Warning");
                    return;
                }
            }

            // 4) Parse each JSON line
            foreach (var rawLine in lines)
            {
                JObject json;
                try
                {
                    json = JObject.Parse(rawLine);
                }
                catch (Exception parseEx)
                {
                    Logger.Log($"Failed to parse JSON: {parseEx.Message}", "Warning");
                    continue;
                }

                string evt = json["event"]?.ToString();

                // ----- Docked: always track last docked info (even on reload) -----
                if (evt == "Docked")
                {
                    string stationType = json["StationType"]?.ToString();
                    string starSystem = json["StarSystem"]?.ToString();
                    string stationName = json["StationName"]?.ToString();
                    long marketID = json["MarketID"]?.ToObject<long>() ?? 0;

                    _lastDockedOnFleetCarrier = stationType == "FleetCarrier";
                    _lastDockedSystem = starSystem;
                    _lastDockedStation = stationName;

                    Logger.Log(
                        $"Docked at {stationName} ({starSystem}), MarketID {marketID}. " +
                        (_lastDockedOnFleetCarrier ? "[Docked on FleetCarrier]" : "[Docked on normal station]"),
                        "Info"
                    );

                    continue;
                }

                // ----- CARGO EVENTS: only process when NOT in full‐reload mode -----
                if (!isReload && _lastDockedOnFleetCarrier)
                {
                    // MarketBuy / MarketSell
                    if (evt == "MarketSell" || evt == "MarketBuy")
                    {
                        string name = json["Type_Localised"]?.ToString() ?? json["Type"]?.ToString();
                        int count = json["Count"]?.ToObject<int>() ?? 0;
                        int delta = (evt == "MarketSell") ? count : -count;

                        if (!string.IsNullOrWhiteSpace(name) && count > 0)
                        {
                            AddToCarrierCargo(name, delta);
                            Logger.Log(
                                $"Processed {evt}: {name} x{count} " +
                                (evt == "MarketSell" ? "→ Carrier" : "← Carrier"),
                                "Debug"
                            );
                        }

                        continue;
                    }
                    // CargoTransfer
                    else if (evt == "CargoTransfer")
                    {
                        var transfers = json["Transfers"] as JArray;
                        if (transfers != null)
                        {
                            foreach (var transfer in transfers)
                            {
                                string direction = transfer["Direction"]?.ToString();
                                string type = transfer["Type_Localised"]?.ToString() ?? transfer["Type"]?.ToString();
                                int count = transfer["Count"]?.ToObject<int>() ?? 0;

                                if (!string.IsNullOrWhiteSpace(type) && count > 0)
                                {
                                    int qty = (direction == "tocarrier") ? count : -count;
                                    AddToCarrierCargo(type, qty);
                                    Logger.Log(
                                        $"Processed CargoTransfer: {type} x{count} " +
                                        (direction == "tocarrier" ? "→ Carrier" : "← Carrier"),
                                        "Debug"
                                    );
                                }
                            }
                        }
                        continue;
                    }
                }

                // ----- DEPOT EVENTS: still run on reload or new lines, but only insert if incomplete -----
                if (evt == "ColonisationConstructionDepot")
                {
                    long marketID = json["MarketID"]?.ToObject<long>() ?? 0;
                    bool complete = json["ConstructionComplete"]?.ToObject<bool>() ?? false;

                    if (!complete)
                    {
                        // 1) Insert project if missing
                        if (_dbAvailable)
                        {
                            try
                            {
                                if (!ProjectExists(marketID))
                                    InsertNewProjectForDepot(marketID);
                            }
                            catch (MySqlException dbEx)
                            {
                                _dbAvailable = false;
                                Logger.Log($"Database went down while inserting new project: {dbEx.Message}", "Warning");
                            }
                        }
                        else
                        {
                            Logger.Log(
                                $"Depot event (incomplete) at MarketID={marketID}, but DB unavailable → skipping project insert.",
                                "Warning"
                            );
                        }

                        // 2) Update resources if project exists
                        var depotEvent = new ColonisationConstructionDepotEvent
                        {
                            MarketID = marketID,
                            ConstructionProgress = json["ConstructionProgress"]?.ToObject<double>() ?? 0,
                            ConstructionComplete = complete,
                            ConstructionFailed = json["ConstructionFailed"]?.ToObject<bool>() ?? false,
                            ResourcesRequired = json["ResourcesRequired"]?
                                                      .Select(r => new ResourceRequired
                                                      {
                                                          Name_Localised = r["Name_Localised"]?.ToString() ?? r["Name"]?.ToString(),
                                                          Name = r["Name"]?.ToString(),
                                                          RequiredAmount = r["RequiredAmount"]?.ToObject<int>() ?? 0,
                                                          ProvidedAmount = r["ProvidedAmount"]?.ToObject<int>() ?? 0,
                                                          Payment = r["Payment"]?.ToObject<int>() ?? 0
                                                      })
                                                      .ToList()
                        };

                        if (_dbAvailable && ProjectExists(marketID))
                        {
                            try
                            {
                                UpdateResourcesIfGreater(depotEvent);
                            }
                            catch (MySqlException dbEx)
                            {
                                _dbAvailable = false;
                                Logger.Log($"Database went down during resource update: {dbEx.Message}", "Warning");
                            }
                        }
                        else if (!_dbAvailable)
                        {
                            Logger.Log(
                                $"Skipping resource update for MarketID={marketID} because DB is unavailable.",
                                "Warning"
                            );
                        }
                    }
                    else
                    {
                        // Depot is already complete; do nothing (and do not insert)
                        Logger.Log($"Depot at MarketID={marketID} is already complete; skipping insert/update.", "Info");
                    }

                    continue;
                }

                // … handle other event types if needed …
            }
        }

        /// <summary>
        /// Inserts a new Project entry using the last Docked station/system for this MarketID.
        /// Called only when we see a ColonisationConstructionDepot (incomplete) for a missing MarketID.
        /// </summary>
        private void InsertNewProjectForDepot(long marketID)
        {
            // 1) Check if it already exists in Projects
            bool existsInActive = false;
            using (var conn = new MySqlConnection(_connectionString))
            {
                conn.Open();
                using var cmd = new MySqlCommand(@"
                    SELECT 1
                      FROM Projects
                     WHERE MarketID = @mid
                     LIMIT 1;",
                    conn
                );
                cmd.Parameters.AddWithValue("@mid", marketID);
                using var rdr = cmd.ExecuteReader();
                existsInActive = rdr.Read();
            }

            if (existsInActive)
                return;

            // 2) Check ProjectsArchive
            bool existsInArchive = false;
            using (var conn2 = new MySqlConnection(_connectionString))
            {
                conn2.Open();
                using var cmd2 = new MySqlCommand(@"
                    SELECT 1
                      FROM ProjectsArchive
                     WHERE MarketID = @mid
                     LIMIT 1;",
                    conn2
                );
                cmd2.Parameters.AddWithValue("@mid", marketID);
                using var rdr2 = cmd2.ExecuteReader();
                existsInArchive = rdr2.Read();
            }

            if (existsInArchive)
                return;

            // 3) Insert into Projects, using the last Docked system/station
            string starSystem = _lastDockedSystem ?? "UnknownSystem";
            string stationName = _lastDockedStation ?? "UnknownStation";

            using (var conn3 = new MySqlConnection(_connectionString))
            {
                conn3.Open();
                using var insertCmd = new MySqlCommand(@"
                    INSERT INTO Projects
                           (MarketID, SystemName, StationName, CreatedBy)
                    VALUES (@mid, @system, @station, @creator);",
                    conn3
                );
                insertCmd.Parameters.AddWithValue("@mid", marketID);
                insertCmd.Parameters.AddWithValue("@system", starSystem);
                insertCmd.Parameters.AddWithValue("@station", stationName);
                insertCmd.Parameters.AddWithValue("@creator", _commanderName ?? "Unknown");
                insertCmd.ExecuteNonQuery();
            }

            Logger.Log($"Added new project: “{stationName}” in {starSystem} (MarketID={marketID})", "Info");
        }

        /// <summary>
        /// Returns true if a row exists in Projects where MarketID = @mid.
        /// </summary>
        private bool ProjectExists(long marketID)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            using var cmd = new MySqlCommand(@"
                SELECT 1
                  FROM Projects
                 WHERE MarketID = @mid
                 LIMIT 1;",
                conn
            );
            cmd.Parameters.AddWithValue("@mid", marketID);

            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }

        /// <summary>
        /// For each ResourceRequired in depotEvent:
        /// – If no row exists in ProjectResources, INSERT it.
        /// – Otherwise, if new ProvidedAmount > existing, UPDATE the row.
        /// </summary>
        private void UpdateResourcesIfGreater(ColonisationConstructionDepotEvent depotEvent)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            foreach (var res in depotEvent.ResourcesRequired)
            {
                string resourceName = res.Name_Localised switch
                {
                    null => (res.Name ?? "Unknown"),
                    "" => res.Name ?? "Unknown",
                    var loc => loc
                };

                // 1) Check if that resource row already exists
                using var selectCmd = new MySqlCommand(@"
                    SELECT ProvidedAmount
                      FROM ProjectResources
                     WHERE MarketID     = @mid
                       AND ResourceName = @resName
                     LIMIT 1;",
                    conn
                );
                selectCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                selectCmd.Parameters.AddWithValue("@resName", resourceName);

                int existingProvided = 0;
                bool rowExists = false;
                using var rdr = selectCmd.ExecuteReader();
                if (rdr.Read())
                {
                    rowExists = true;
                    existingProvided = rdr.GetInt32("ProvidedAmount");
                }
                rdr.Close();

                // 2) If it does not exist, INSERT
                if (!rowExists)
                {
                    using var insertCmd = new MySqlCommand(@"
                        INSERT INTO ProjectResources
                          (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
                        VALUES
                          (@mid, @resName, @reqAmt, @provAmt, @payment);",
                        conn
                    );
                    insertCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                    insertCmd.Parameters.AddWithValue("@resName", resourceName);
                    insertCmd.Parameters.AddWithValue("@reqAmt", res.RequiredAmount);
                    insertCmd.Parameters.AddWithValue("@provAmt", res.ProvidedAmount);
                    insertCmd.Parameters.AddWithValue("@payment", res.Payment);
                    insertCmd.ExecuteNonQuery();

                    Logger.Log(
                        $"Inserted resource '{resourceName}' for MarketID={depotEvent.MarketID} with Provided={res.ProvidedAmount}",
                        "Info"
                    );
                }
                else
                {
                    // 3) If it exists and new provided is larger, UPDATE
                    if (res.ProvidedAmount > existingProvided)
                    {
                        using var updateCmd = new MySqlCommand(@"
                            UPDATE ProjectResources
                               SET RequiredAmount  = @reqAmt,
                                   ProvidedAmount  = @provAmt,
                                   Payment         = @payment
                             WHERE MarketID       = @mid
                               AND ResourceName   = @resName;",
                            conn
                        );
                        updateCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                        updateCmd.Parameters.AddWithValue("@resName", resourceName);
                        updateCmd.Parameters.AddWithValue("@reqAmt", res.RequiredAmount);
                        updateCmd.Parameters.AddWithValue("@provAmt", res.ProvidedAmount);
                        updateCmd.Parameters.AddWithValue("@payment", res.Payment);
                        updateCmd.ExecuteNonQuery();

                        Logger.Log(
                            $"Updated resource '{resourceName}' for MarketID={depotEvent.MarketID}: Provided {existingProvided} → {res.ProvidedAmount}",
                            "Info"
                        );
                    }
                    // else no change needed
                }
            }
        }

        /// <summary>
        /// Whenever we add or remove from FleetCarrierCargoItems, invoke this to let subscribers know.
        /// </summary>
        private void OnCarrierCargoChanged()
        {
            CarrierCargoChanged?.Invoke();
        }

        /// <summary>
        /// Adjust the in‐memory carrier cargo list by “quantity”.
        /// If quantity > 0 → add (or increase). If quantity < 0 → remove/decrease.
        /// Fires CarrierCargoChanged if anything actually changes.
        /// </summary>
        private void AddToCarrierCargo(string name, int quantity)
        {
            if (string.IsNullOrWhiteSpace(name) || quantity == 0)
                return;

            var item = FleetCarrierCargoItems.FirstOrDefault(i => i.Name == name);
            if (item != null)
            {
                item.Quantity += quantity;
                if (item.Quantity <= 0)
                    FleetCarrierCargoItems.Remove(item);
                OnCarrierCargoChanged();
            }
            else if (quantity > 0)
            {
                FleetCarrierCargoItems.Add(new CargoItem
                {
                    Name = name,
                    Quantity = quantity
                });
                OnCarrierCargoChanged();
            }
        }

        private class ReadState
        {
            public string FileName { get; set; }
            public long Position { get; set; }
        }
    }
}
