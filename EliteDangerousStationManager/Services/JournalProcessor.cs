using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Configuration;
using System.Threading;
using Newtonsoft.Json.Linq;
using MySql.Data.MySqlClient;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    /// <summary>
    /// Reads Elite Dangerous journal logs and:
    ///   • Updates an in-memory FleetCarrierCargoItems list whenever
    ///     “CargoTransfer”, “MarketSell” or “MarketBuy” events occur.
    ///   • Scans for “ColonisationConstructionDepot” events and, if
    ///     a matching MarketID project already exists in the database,
    ///     updates ProjectResources only when ProvidedAmount is larger.
    /// </summary>
    public class JournalProcessor
    {
        private readonly string _journalPath;
        private readonly string _commanderName;
        private readonly EddnSender _eddnSender;
        private readonly InaraSender _inaraSender;

        // Holds the saved “last-read filename” and byte offset
        private ReadState _lastState = new ReadState();
        private readonly string _stateFilePath;

        // MySQL connection string (expects <connectionStrings> entry “EliteDB” in App.config)
        private readonly string _connectionString
            = ConfigurationManager.ConnectionStrings["EliteDB"].ConnectionString;

        /// <summary>
        /// In-memory list of items currently on the Fleet Carrier.
        /// </summary>
        public List<CargoItem> FleetCarrierCargoItems { get; } = new List<CargoItem>();

        /// <summary>
        /// Raised whenever FleetCarrierCargoItems changes due to a
        /// MarketSell, MarketBuy, or CargoTransfer event.
        /// </summary>
        public event Action CarrierCargoChanged;

        public JournalProcessor(string journalPath, string commanderName)
        {
            _journalPath = journalPath;
            _commanderName = commanderName;
            _eddnSender = new EddnSender();
            _inaraSender = new InaraSender();

            _stateFilePath = Path.Combine(_journalPath, "lastread.state");
            LoadReadState();
        }

        /// <summary>
        /// Attempts to load the last-read filename and position from disk.
        /// </summary>
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

        /// <summary>
        /// Saves the provided filename and byte-offset so we can resume next time.
        /// </summary>
        private void SaveReadState(string fileName, long position)
        {
            File.WriteAllLines(_stateFilePath, new[] { fileName, position.ToString() });
        }

        /// <summary>
        /// Scans the most recent journal file(s):
        ///   • Updates FleetCarrierCargoItems if “CargoTransfer”, “MarketSell” or “MarketBuy” is found.
        ///   • Processes “ColonisationConstructionDepot” events to update ProjectResources in the DB.
        /// </summary>
        public void ScanForDepotEvents()
        {
            // 1. Identify the most recent "Journal.*.log" file
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

            bool isNewFile = !string.Equals(
                latestFile,
                _lastState.FileName,
                StringComparison.OrdinalIgnoreCase
            );
            long startPos = isNewFile ? 0 : _lastState.Position;

            // 2. Read only the new lines since the last offset
            var lines = new List<string>();
            int maxRetries = 5;
            int delayMilliseconds = 500;
            int attempt = 0;
            bool fileOpened = false;

            while (attempt < maxRetries && !fileOpened)
            {
                try
                {
                    using var fs = new FileStream(
                        latestFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    );
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

                    fileOpened = true;
                }
                catch (IOException ex)
                {
                    attempt++;
                    if (attempt >= maxRetries)
                    {
                        Logger.Log(
                            $"Error reading '{latestFile}' after {maxRetries} attempts: {ex.Message}",
                            "Warning"
                        );
                        return;
                    }
                    Thread.Sleep(delayMilliseconds);
                }
            }

            bool usedFallback = false;

            // 3. Fallback: if no new lines were read (same file), re-read entire file
            if (!isNewFile && lines.Count == 0)
            {
                Logger.Log("No new lines since last offset; reloading entire file.", "Debug");
                usedFallback = true;

                try
                {
                    using var fs2 = new FileStream(
                        latestFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    );
                    using var sr2 = new StreamReader(fs2);

                    var allRawLines = new List<string>();
                    string entireLine;
                    while ((entireLine = sr2.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(entireLine))
                            allRawLines.Add(entireLine);
                    }

                    lines = allRawLines;
                    // Note: do NOT update the offset here, so future scans still pick up only new lines.
                }
                catch (Exception fallbackEx)
                {
                    Logger.Log(
                        $"Fallback reload failed for '{latestFile}': {fallbackEx.Message}",
                        "Warning"
                    );
                    return;
                }
            }

            // 4. Examine each line. If usedFallback==true, skip all cargo logic.
            bool cargoDidChange = false;

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

                // -- If we’re in fallback mode, skip cargo-related events entirely --
                if (!usedFallback)
                {
                    // A) Handle MarketSell / MarketBuy
                    if (evt == "MarketSell" || evt == "MarketBuy")
                    {
                        string resourceType = json["Type_Localised"]?.ToString()
                                              ?? json["Type"]?.ToString();
                        int count = json["Count"]?.ToObject<int>() ?? 0;

                        if (!string.IsNullOrWhiteSpace(resourceType) && count > 0)
                        {
                            int delta = (evt == "MarketSell") ? count : -count;
                            AddToCarrierCargo(resourceType, delta);
                            cargoDidChange = true;
                            Logger.Log(
                                $"Processed {evt}: {resourceType} x{count} ({(delta > 0 ? "+" : "")}{delta})",
                                "Debug"
                            );
                        }
                        continue;
                    }

                    // B) Handle CargoTransfer
                    if (evt == "CargoTransfer")
                    {
                        var transfers = json["Transfers"] as JArray;
                        if (transfers != null)
                        {
                            foreach (var transfer in transfers)
                            {
                                string direction = transfer["Direction"]?.ToString();
                                string type = transfer["Type_Localised"]?.ToString()
                                              ?? transfer["Type"]?.ToString();
                                int count = transfer["Count"]?.ToObject<int>() ?? 0;

                                if (!string.IsNullOrWhiteSpace(type) && count > 0)
                                {
                                    int qty = (direction == "tocarrier") ? count : -count;
                                    AddToCarrierCargo(type, qty);
                                    cargoDidChange = true;
                                    Logger.Log(
                                        $"Processed CargoTransfer: {type} x{count} {(direction == "tocarrier" ? "→ Carrier" : "← Carrier")}",
                                        "Debug"
                                    );
                                }
                            }
                        }
                        continue;
                    }
                }

                // -- Now handle Depot events (even in fallback mode) --
                if (evt != "ColonisationConstructionDepot")
                    continue;

                long marketID = json["MarketID"]?.ToObject<long>() ?? 0;
                var depotEvent = new ColonisationConstructionDepotEvent
                {
                    MarketID = marketID,
                    ConstructionProgress = json["ConstructionProgress"]?.ToObject<double>() ?? 0,
                    ConstructionComplete = json["ConstructionComplete"]?.ToObject<bool>() ?? false,
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

                Logger.Log(
                    $"Found Depot event: MarketID={marketID}, {depotEvent.ResourcesRequired.Count} resources",
                    "Info"
                );

                if (!ProjectExists(marketID))
                {
                    Logger.Log($"No project in DB for MarketID={marketID}, skipping.", "Info");
                    continue;
                }

                UpdateResourcesIfGreater(depotEvent);

            }

            // 5. After processing, if cargo changed and we were not in fallback, raise the event
            if (cargoDidChange)
                CarrierCargoChanged?.Invoke();
        }

        /// <summary>
        /// Checks (and returns true) if a row exists in Projects where MarketID=@mid.
        /// </summary>
        private bool ProjectExists(long marketID)
        {
            try
            {
                using var conn = new MySqlConnection(_connectionString);
                conn.Open();

                using var cmd = new MySqlCommand(@"
                    SELECT 1
                    FROM Projects
                    WHERE MarketID = @mid
                    LIMIT 1;
                ", conn);
                cmd.Parameters.AddWithValue("@mid", marketID);

                using var reader = cmd.ExecuteReader();
                return reader.Read();
            }
            catch (Exception ex)
            {
                Logger.Log($"ProjectExists check failed: {ex.Message}", "Warning");
                return false;
            }
        }

        /// <summary>
        /// For each resource in depotEvent.ResourcesRequired:
        ///   - If no row for (MarketID, ResourceName) exists, INSERT it.
        ///   - Otherwise, SELECT the old ProvidedAmount; if new > old, UPDATE RequiredAmount, ProvidedAmount, Payment.
        /// </summary>
        private void UpdateResourcesIfGreater(ColonisationConstructionDepotEvent depotEvent)
        {
            try
            {
                using var conn = new MySqlConnection(_connectionString);
                conn.Open();

                foreach (var res in depotEvent.ResourcesRequired)
                {
                    // Choose Name_Localised if present, else Name as the key in DB
                    string resourceName = res.Name_Localised ?? res.Name ?? "Unknown";

                    // 1. Check if this resource row already exists
                    int existingProvided = 0;
                    bool rowExists = false;

                    using (var selectCmd = new MySqlCommand(@"
                        SELECT ProvidedAmount
                        FROM ProjectResources
                        WHERE MarketID = @mid
                          AND ResourceName = @resName
                        LIMIT 1;
                    ", conn))
                    {
                        selectCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                        selectCmd.Parameters.AddWithValue("@resName", resourceName);

                        using var reader = selectCmd.ExecuteReader();
                        if (reader.Read())
                        {
                            rowExists = true;
                            existingProvided = reader.GetInt32("ProvidedAmount");
                        }
                    }

                    // 2. If row does not exist, INSERT it unconditionally
                    if (!rowExists)
                    {
                        using var insertCmd = new MySqlCommand(@"
                            INSERT INTO ProjectResources
                              (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
                            VALUES
                              (@mid, @resName, @reqAmt, @provAmt, @payment);
                        ", conn);

                        insertCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                        insertCmd.Parameters.AddWithValue("@resName", resourceName);
                        insertCmd.Parameters.AddWithValue("@reqAmt", res.RequiredAmount);
                        insertCmd.Parameters.AddWithValue("@provAmt", res.ProvidedAmount);
                        insertCmd.Parameters.AddWithValue("@payment", res.Payment);

                        insertCmd.ExecuteNonQuery();
                        Logger.Log(
                            $"Inserted '{resourceName}' for MarketID={depotEvent.MarketID} with Provided={res.ProvidedAmount}",
                            "Info"
                        );
                    }
                    else
                    {
                        // 3. If it exists and new ProvidedAmount > existing, UPDATE
                        if (res.ProvidedAmount > existingProvided)
                        {
                            using var updateCmd = new MySqlCommand(@"
                                UPDATE ProjectResources
                                   SET RequiredAmount  = @reqAmt,
                                       ProvidedAmount  = @provAmt,
                                       Payment         = @payment
                                 WHERE MarketID       = @mid
                                   AND ResourceName   = @resName;
                            ", conn);

                            updateCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                            updateCmd.Parameters.AddWithValue("@resName", resourceName);
                            updateCmd.Parameters.AddWithValue("@reqAmt", res.RequiredAmount);
                            updateCmd.Parameters.AddWithValue("@provAmt", res.ProvidedAmount);
                            updateCmd.Parameters.AddWithValue("@payment", res.Payment);

                            updateCmd.ExecuteNonQuery();
                            Logger.Log(
                                $"Updated '{resourceName}' for MarketID={depotEvent.MarketID}: Provided {existingProvided} → {res.ProvidedAmount}",
                                "Info"
                            );
                        }
                        else
                        {
                            //Logger.Log($"Skipped '{resourceName}' for MarketID={depotEvent.MarketID}: new Provided={res.ProvidedAmount} ≤ existing {existingProvided}", "Debug");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UpdateResourcesIfGreater failed: {ex.Message}", "Warning");
            }
        }

        /// <summary>
        /// Adds (or subtracts) the given quantity from FleetCarrierCargoItems.
        /// If quantity drops to zero or below, removes the item.
        /// </summary>
        /// 
      
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
            }
            else if (quantity > 0)
            {
                FleetCarrierCargoItems.Add(new CargoItem { Name = name, Quantity = quantity });
            }
        }

        private class ReadState
        {
            public string FileName { get; set; }
            public long Position { get; set; }
        }
    }
}
