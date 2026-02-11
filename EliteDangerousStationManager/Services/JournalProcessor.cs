using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EliteDangerousStationManager.Services
{
    public class JournalProcessor
    {
        private readonly string _journalPath;
        private readonly string _commanderName;
        private readonly EddnSender _eddnSender;
        private readonly InaraSender _inaraSender;
        private string _forcedJournalFile = null;
        private bool _isCurrentlyDocked = false;
        public event Action<JObject> EntryParsed;

        // Cold-start protection for carrier storage
        private DateTime _carrierCutoffUtc = DateTime.MinValue; // events older than this are ignored for carrier storage
        private bool _coldStartFullScan = false;                 // true when we started from pos 0

        public event Action<string, int> CarrierTransferDelta; // +N to carrier, -N from carrier

        public Dictionary<string, int> SessionTransfers { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _stateFilePath;
        private ReadState _lastState = new ReadState();

        // True only when app is running in Server mode (MySQL enabled).
        private static bool DbServerEnabled => App.CurrentDbMode == "Server";

        // Holds whether the last docking was on a FleetCarrier
        private bool _lastDockedOnFleetCarrier = false;

        // Prevent duplicate DB writes for the same journal line
        private readonly Queue<string> _recentCarrierKeys = new Queue<string>(64);
        private readonly HashSet<string> _recentCarrierKeySet = new HashSet<string>();
        private const int RecentKeyLimit = 64;

        // Holds the system and station name from the last Docked event
        private string _lastDockedSystem = null;
        private string _lastDockedStation = null;
        private readonly HashSet<long> _seenDepotMarketIDs = new HashSet<long>();

        // >>> NEW: Remember the carrier callsign (e.g., ABC-123) when docked on a Fleet Carrier
        private string _lastDockedCarrierCode = null;

        // -------------- Public cargo‐tracking members --------------
        public List<CargoItem> FleetCarrierCargoItems { get; private set; } = new List<CargoItem>();
        public event Action CarrierCargoChanged;
        // ------------------------------------------------------------


        public JournalProcessor(string journalPath, string commanderName)
        {
            _journalPath = journalPath;
            _commanderName = commanderName;
            _eddnSender = new EddnSender();
            _inaraSender = new InaraSender();

            

            _stateFilePath = Path.Combine(_journalPath, "lastread.state");
            LoadReadState();
        }

        public void ForceStartFrom(string journalFilePath)
        {
            if (!string.IsNullOrWhiteSpace(journalFilePath) && File.Exists(journalFilePath))
            {
                _forcedJournalFile = journalFilePath;
                Logger.Log($"JournalProcessor will read from forced file: {Path.GetFileName(journalFilePath)}", "Debug");
            }
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
            JObject latestDockedEvent = null;
            DateTime latestDockedTimestamp = DateTime.MinValue;

            var latestFile = _forcedJournalFile ?? Directory
                .GetFiles(_journalPath, "Journal.*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (latestFile == null)
            {
                Logger.Log("No journal file found.", "Warning");
                return;
            }

            bool isNewFile = !string.Equals(latestFile, _lastState.FileName, StringComparison.OrdinalIgnoreCase);
            bool isFirstScan = string.IsNullOrEmpty(_lastState.FileName);
            long startPos = (isNewFile || isFirstScan) ? 0 : _lastState.Position;

            // If we’re starting from the beginning (state missing/cleared), only trust events >= "now"
            _coldStartFullScan = (startPos == 0);
            if (_coldStartFullScan)
            {
                // tiny skew so events logged at the exact same second still pass
                _carrierCutoffUtc = DateTime.UtcNow.AddSeconds(-2);
            }

            var lines = new List<string>();
            try
            {
                using (var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
            }
            catch (IOException ex)
            {
                Logger.Log($"Error reading journal file: {ex.Message}", "Warning");
                return;
            }

            foreach (var rawLine in lines)
            {
                JObject json;
                try
                {
                    json = JObject.Parse(rawLine);
                    EntryParsed?.Invoke(json);
                }
                catch
                {
                    continue;
                }

                string evt = json["event"]?.ToString();

                if (IsDockedLikeEvent(json))
                {
                    HandleDockedLikeEvent(json);
                    latestDockedEvent = json; // keep your existing logic that uses latestDockedEvent
                }
                else if (evt == "Docked")
                {
                    string stationType = json["StationType"]?.ToString();
                    string starSystem = json["StarSystem"]?.ToString();

                    string rawStationName = json["StationName"]?.ToString() ?? "UnknownStation";
                    string stationName = rawStationName.Contains(';')
                        ? rawStationName.Split(';').Last().Trim()
                        : rawStationName;

                    if (rawStationName.StartsWith("$EXT_PANEL_ColonisationShip;", StringComparison.OrdinalIgnoreCase))
                        stationName = $"ColonisationShip: {stationName}";

                    if (starSystem == "UnknownSystem" || stationName == "UnknownStation")
                        return;

                    long marketId = json["MarketID"]?.ToObject<long>() ?? 0;

                    _lastDockedSystem = starSystem;
                    _lastDockedStation = stationName;
                    _lastDockedOnFleetCarrier = string.Equals(stationType, "FleetCarrier", StringComparison.OrdinalIgnoreCase);
                    _isCurrentlyDocked = true;

                    // >>> NEW: remember the carrier callsign if this is a Fleet Carrier
                    _lastDockedCarrierCode = _lastDockedOnFleetCarrier
                        ? ExtractCarrierCodeFromStationName(stationName)
                        : null;

                    //Logger.Log($"✅ Docked at {stationName} in {starSystem} (MarketID={marketId}, Type={stationType})", "Info");
                    if (_lastDockedOnFleetCarrier)
                    {
                        if (string.IsNullOrWhiteSpace(_lastDockedCarrierCode) || !IsRegisteredCarrier(_lastDockedCarrierCode))
                        {
                            // If the name didn’t include a callsign or it isn't registered, don’t write to DB later.
                            Logger.Log($"[CARRIER] Docked on FC but code missing/unregistered. Name='{stationName}', Code='{_lastDockedCarrierCode ?? "null"}'", "Warning");
                            _lastDockedCarrierCode = null;
                        }
                        else
                        {
                            Logger.Log($"[CARRIER] Docked to registered carrier: {_lastDockedCarrierCode}", "Info");
                        }
                    }


                    latestDockedEvent = json;
                }
                else if (evt == "Undocked")
                {
                    Logger.Log($"🛫 Undocked from {_lastDockedStation ?? "unknown station"} in {_lastDockedSystem ?? "unknown system"}.", "Info");
                    _lastDockedStation = null;
                    _lastDockedSystem = null;
                    _lastDockedOnFleetCarrier = false;
                    _lastDockedCarrierCode = null; // >>> NEW: clear carrier code once undocked
                    _isCurrentlyDocked = false;
                }
                else if (evt == "ColonisationConstructionDepot")
                {
                    var depot = json.ToObject<ColonisationConstructionDepotEvent>();
                    if (depot == null || depot.MarketID == 0) return;

                    var resources = depot.ResourcesRequired ?? new List<ResourceRequired>();

                    // NEW: capture whether we just created a local project
                    bool createdLocal = false;

                    if (!depot.ConstructionComplete &&
                        !ProjectExistsLocal(depot.MarketID) &&
                        !ProjectExists(depot.MarketID))
                    {
                        var sys = _lastDockedSystem;
                        var sta = _lastDockedStation;
                        if (!IsUnknownSystem(sys) && !IsUnknownStation(sta))
                            createdLocal = InsertNewProjectForDepot(depot.MarketID);   // << now returns a bool
                        else
                            Logger.Log($"[LOCAL] Skipped local insert for MarketID={depot.MarketID} (unknown system/station).", "Warning");
                    }

                    // Push resource updates when we actually have rows in this tick
                    if (resources.Count > 0)
                    {
                        bool serverHasProject = DbServerEnabled && ProjectExists(depot.MarketID);
                        bool localHasProject = ProjectExistsLocal(depot.MarketID);

                        if (serverHasProject)
                        {
                            UpdateResourcesIfGreaterServer(depot);
                        }
                        else if (localHasProject || createdLocal)  // << allow immediate seed after local create
                        {
                            UpdateResourcesIfGreaterLocal(depot);
                        }
                    }

                    // ... (archive-on-complete logic unchanged)
                }
                else if (evt == "CargoTransfer")
                {
                    if (!_lastDockedOnFleetCarrier) continue;

                    var transfers = json["Transfers"] as JArray;
                    if (transfers != null)
                    {
                        foreach (var t in transfers)
                        {
                            string direction = t["Direction"]?.ToString(); // tocarrier/toship
                            string nameRaw = t["Type_Localised"]?.ToString() ?? t["Type"]?.ToString();
                            string commodity = NormalizeCommodityName(nameRaw);
                            int count = t["Count"]?.ToObject<int>() ?? 0;
                            if (count <= 0 || string.IsNullOrWhiteSpace(commodity)) continue;

                            int delta = direction?.Equals("tocarrier", StringComparison.OrdinalIgnoreCase) == true ? count
                                      : direction?.Equals("toship", StringComparison.OrdinalIgnoreCase) == true ? -count
                                      : 0;
                            if (delta == 0) continue;

                            // De-dup
                            var key = BuildCarrierKey(json, "CargoTransfer", commodity, delta, direction ?? "");
                            if (SeenCarrierEvent(key)) continue;

                            // UI (always)
                            AddToCarrierCargo(commodity, delta);

                            // DB (recent only)
                            if (IsRecentForCarrierUpdates(json))
                                TryUpdateCarrierStorage(_lastDockedCarrierCode, commodity, delta);
                        }
                    }
                }
                else if (evt == "MarketSell")
                {
                    if (!_lastDockedOnFleetCarrier) continue;

                    string nameRaw = json["SellLocalizedName"]?.ToString()
                                     ?? json["Type_Localised"]?.ToString()
                                     ?? json["Type"]?.ToString();
                    string commodity = NormalizeCommodityName(nameRaw);
                    int count = json["Count"]?.ToObject<int>() ?? 0;
                    if (count <= 0) return;

                    var key = BuildCarrierKey(json, "MarketSell", commodity, count);
                    if (SeenCarrierEvent(key)) return;

                    AddToCarrierCargo(commodity, +count);
                    if (IsRecentForCarrierUpdates(json))
                        TryUpdateCarrierStorage(_lastDockedCarrierCode, commodity, +count);
                }
                else if (evt == "MarketBuy")
                {
                    if (!_lastDockedOnFleetCarrier) continue;

                    string nameRaw = json["BuyLocalizedName"]?.ToString()
                                     ?? json["Type_Localised"]?.ToString()
                                     ?? json["Type"]?.ToString();
                    string commodity = NormalizeCommodityName(nameRaw);
                    int count = json["Count"]?.ToObject<int>() ?? 0;
                    if (count <= 0) return;

                    var key = BuildCarrierKey(json, "MarketBuy", commodity, -count);
                    if (SeenCarrierEvent(key)) return;

                    AddToCarrierCargo(commodity, -count);
                    if (IsRecentForCarrierUpdates(json))
                        TryUpdateCarrierStorage(_lastDockedCarrierCode, commodity, -count);
                }

            }

            if (latestDockedEvent != null)
            {
                string stationName = latestDockedEvent["StationName"]?.ToString();
                string starSystem = latestDockedEvent["StarSystem"]?.ToString();
                long marketId = latestDockedEvent["MarketID"]?.ToObject<long>() ?? 0;
                string stationType = latestDockedEvent["StationType"]?.ToString() ?? "Unknown";

                string dockType;
                if (stationType == "FleetCarrier")
                    dockType = "Fleet Carrier";
                else if (stationType == "SpaceConstructionDepot")
                    dockType = "Construction Depot";
                else
                    dockType = "Normal Station";

                Logger.Log($"Docked at {stationName} ({starSystem}), MarketID {marketId}. [Docked on {dockType}]", "Info");

                bool? constructionComplete = latestDockedEvent["ConstructionComplete"]?.ToObject<bool?>();
                if (stationType == "SpaceConstructionDepot" &&
                    marketId != 0 &&
                    !_seenDepotMarketIDs.Contains(marketId) &&
                    constructionComplete == false)
                {
                    Logger.Log($"Saw depot event: MarketID={marketId}, complete=False", "Debug");
                    Logger.Log("[DEBUG] Calling InsertNewProjectForDepot(" + marketId + ")…", "Debug");
                    _seenDepotMarketIDs.Add(marketId);
                }
            }

        }
        private bool SeenCarrierEvent(string key)
        {
            if (_recentCarrierKeySet.Contains(key)) return true;
            _recentCarrierKeySet.Add(key);
            _recentCarrierKeys.Enqueue(key);
            if (_recentCarrierKeys.Count > RecentKeyLimit)
            {
                var old = _recentCarrierKeys.Dequeue();
                _recentCarrierKeySet.Remove(old);
            }
            return false;
        }

        private static string BuildCarrierKey(JObject j, string kind, string commodity, int count, string direction = "")
        {
            var ts = j["timestamp"]?.ToString() ?? "";
            return $"{kind}|{ts}|{commodity}|{count}|{direction}";
        }
        private static bool IsDockedLikeEvent(JObject j)
        {
            var evt = j["event"]?.ToString();
            if (string.Equals(evt, "Docked", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(evt, "Location", StringComparison.OrdinalIgnoreCase))
                return j["Docked"]?.ToObject<bool>() == true;
            return false;
        }

        private void HandleDockedLikeEvent(JObject j)
        {
            string stationType = j["StationType"]?.ToString();
            string starSystem = j["StarSystem"]?.ToString();

            // StationName in Location may already be the callsign (e.g. "M2X-T4Z")
            string rawStationName = j["StationName"]?.ToString() ?? "UnknownStation";
            string stationName = rawStationName.Contains(';')
                ? rawStationName.Split(';').Last().Trim()
                : rawStationName;

            if (rawStationName.StartsWith("$EXT_PANEL_ColonisationShip;", StringComparison.OrdinalIgnoreCase))
                stationName = $"ColonisationShip: {stationName}";

            if (starSystem == "UnknownSystem" || stationName == "UnknownStation")
                return;

            long marketId = j["MarketID"]?.ToObject<long>() ?? 0;

            _lastDockedSystem = starSystem;
            _lastDockedStation = stationName;
            _lastDockedOnFleetCarrier = string.Equals(stationType, "FleetCarrier", StringComparison.OrdinalIgnoreCase);
            _isCurrentlyDocked = true;

            _lastDockedCarrierCode = _lastDockedOnFleetCarrier ? ExtractCarrierCodeFromStationName(stationName) : null;

            Logger.Log($"✅ Docked (via {(j["event"]?.ToString())}) at {stationName} in {starSystem} (MarketID={marketId}, Type={stationType})", "Info");
            if (_lastDockedOnFleetCarrier && !string.IsNullOrWhiteSpace(_lastDockedCarrierCode))
                Logger.Log($"[CARRIER] Callsign: {_lastDockedCarrierCode}", "Debug");

            
        }


        private void TryUpdateCarrierStorage(string carrierCode, string resourceName, int delta)
        {
            if (string.IsNullOrWhiteSpace(resourceName) || delta == 0) return;

            if (!DbServerEnabled) { Logger.Log("[CARRIER] Skip upsert: DB unavailable", "Debug"); return; }
            if (!_lastDockedOnFleetCarrier) { Logger.Log("[CARRIER] Skip upsert: not on FC", "Debug"); return; }

            // Resolve from latest dock, and only if registered
            var code = carrierCode ?? _lastDockedCarrierCode;
            if (string.IsNullOrWhiteSpace(code) || !IsRegisteredCarrier(code))
            {
                Logger.Log($"[CARRIER] Skip upsert: unknown/unregistered code. Res='{resourceName}', Δ={delta}", "Debug");
                return;
            }

            try
            {
                UpdateCarrierStorage(code, resourceName, delta);
                OnCarrierCargoChanged();
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Carrier storage update failed: {ex.Message}", "Error");
            }
        }

        private static string NormalizeCommodityName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown";

            // Trim & lower for mapping
            string s = raw.Trim();

            // Special cases / canonical names used in your DB & UI
            // (extend this map as you notice more raw names)
            switch (s.ToLowerInvariant())
            {
                case "aluminium": return "Aluminium";
                case "aluminum": return "Aluminium";
                case "cmm composite": return "CMM Composite";
                case "liquid oxygen": return "Liquid oxygen";
                case "food cartridges": return "Food Cartridges";
                case "fruit and vegetables": return "Fruit and Vegetables";
                case "ceramic composites": return "Ceramic Composites";
                case "non-lethal weapons": return "Non-Lethal Weapons";
                case "water purifiers": return "Water Purifiers";
                // …add any other frequent ones from your ProjectResources
                default:
                    // TitleCase-ish fallback without culture surprises
                    return char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1) : "");
            }
        }

        private bool IsRecentForCarrierUpdates(JObject json)
        {
            if (_carrierCutoffUtc == DateTime.MinValue || !_coldStartFullScan)
                return true; // normal path

            var tsStr = json["timestamp"]?.ToString();
            if (string.IsNullOrWhiteSpace(tsStr)) return true; // be permissive if no ts

            if (DateTime.TryParse(tsStr,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                  out var ts))
            {
                return ts >= _carrierCutoffUtc;
            }
            return true;
        }


        private void UpdateResourcesIfGreaterLocal(ColonisationConstructionDepotEvent depotEvent)
        {
            string localDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EDStationManager",
                "stations.db"
            );

            using (var localConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}"))
            {
                localConn.Open();

                foreach (var res in depotEvent.ResourcesRequired)
                {
                    string resourceName = res.Name_Localised switch
                    {
                        null => (res.Name ?? "Unknown"),
                        "" => res.Name ?? "Unknown",
                        var loc => loc
                    };

                    using var selectCmd = new Microsoft.Data.Sqlite.SqliteCommand(@"
                SELECT ProvidedAmount FROM ProjectResources
                 WHERE MarketID=@mid AND ResourceName=@resName LIMIT 1;", localConn);
                    selectCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                    selectCmd.Parameters.AddWithValue("@resName", resourceName);

                    int existingProvided = 0;
                    bool rowExists = false;
                    using (var rdr = selectCmd.ExecuteReader())
                    {
                        if (rdr.Read())
                        {
                            rowExists = true;
                            existingProvided = rdr.GetInt32(0);
                        }
                    }

                    if (!rowExists)
                    {
                        using var insertCmd = new Microsoft.Data.Sqlite.SqliteCommand(@"
                    INSERT INTO ProjectResources
                    (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment)
                    VALUES (@mid, @resName, @reqAmt, @provAmt, @payment);", localConn);

                        insertCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                        insertCmd.Parameters.AddWithValue("@resName", resourceName);
                        insertCmd.Parameters.AddWithValue("@reqAmt", res.RequiredAmount);
                        insertCmd.Parameters.AddWithValue("@provAmt", res.ProvidedAmount);
                        insertCmd.Parameters.AddWithValue("@payment", res.Payment);
                        insertCmd.ExecuteNonQuery();

                        Logger.Log($"[LOCAL] Inserted material '{resourceName}' → Provided={res.ProvidedAmount}", "Info");
                    }
                    else if (res.ProvidedAmount > existingProvided)
                    {
                        using var updateCmd = new Microsoft.Data.Sqlite.SqliteCommand(@"
                    UPDATE ProjectResources
                       SET RequiredAmount=@reqAmt,
                           ProvidedAmount=@provAmt,
                           Payment=@payment
                     WHERE MarketID=@mid AND ResourceName=@resName;", localConn);

                        updateCmd.Parameters.AddWithValue("@mid", depotEvent.MarketID);
                        updateCmd.Parameters.AddWithValue("@resName", resourceName);
                        updateCmd.Parameters.AddWithValue("@reqAmt", res.RequiredAmount);
                        updateCmd.Parameters.AddWithValue("@provAmt", res.ProvidedAmount);
                        updateCmd.Parameters.AddWithValue("@payment", res.Payment);
                        updateCmd.ExecuteNonQuery();

                        Logger.Log($"[LOCAL] Updated material '{resourceName}' from {existingProvided} → {res.ProvidedAmount}", "Info");
                    }
                }
            }
        }

        private void UpdateResourcesIfGreaterServer(ColonisationConstructionDepotEvent depotEvent)
        {
            if (depotEvent?.ResourcesRequired == null || depotEvent.ResourcesRequired.Count == 0) return;

            DbConnectionManager.Instance.Execute(cmd =>
            {
                using var tx = cmd.Connection!.BeginTransaction();
                cmd.Transaction = tx;

                // Chunk if you ever expect large batches; 22 is tiny, but this is future-proof.
                const int CHUNK = 200;
                int total = depotEvent.ResourcesRequired.Count;
                for (int start = 0; start < total; start += CHUNK)
                {
                    int take = Math.Min(CHUNK, total - start);
                    cmd.Parameters.Clear();

                    var sb = new StringBuilder();
                    sb.Append("INSERT INTO ProjectResources (MarketID, ResourceName, RequiredAmount, ProvidedAmount, Payment) VALUES ");

                    for (int i = 0; i < take; i++)
                    {
                        if (i > 0) sb.Append(",");
                        int idx = start + i;

                        string name = depotEvent.ResourcesRequired[idx].Name_Localised;
                        if (string.IsNullOrWhiteSpace(name))
                            name = depotEvent.ResourcesRequired[idx].Name ?? "Unknown";

                        sb.Append($"(@mid{i}, @res{i}, @req{i}, @prov{i}, @pay{i})");

                        cmd.Parameters.AddWithValue($"@mid{i}", depotEvent.MarketID);
                        cmd.Parameters.AddWithValue($"@res{i}", name);
                        cmd.Parameters.AddWithValue($"@req{i}", depotEvent.ResourcesRequired[idx].RequiredAmount);
                        cmd.Parameters.AddWithValue($"@prov{i}", depotEvent.ResourcesRequired[idx].ProvidedAmount);
                        cmd.Parameters.AddWithValue($"@pay{i}", depotEvent.ResourcesRequired[idx].Payment);
                    }

                    sb.Append(@"
ON DUPLICATE KEY UPDATE
  RequiredAmount = VALUES(RequiredAmount),
  ProvidedAmount = GREATEST(ProvidedAmount, VALUES(ProvidedAmount)),
  Payment       = VALUES(Payment);");

                    cmd.CommandText = sb.ToString();
                    cmd.CommandTimeout = 5;   // or DbTimeouts.Normal if you prefer
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return 0;
            });
        }

        private bool ProjectExistsLocal(long marketID)
        {
            string localDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EDStationManager",
                "stations.db"
            );

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
            conn.Open();

            using var cmd = new Microsoft.Data.Sqlite.SqliteCommand("SELECT 1 FROM Projects WHERE MarketID=@mid LIMIT 1;", conn);
            cmd.Parameters.AddWithValue("@mid", marketID);

            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }

        private void InsertNewLocalProject(long marketID, string systemName, string stationName)
        {
            string localDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EDStationManager",
                "stations.db"
            );

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={localDbPath}");
            conn.Open();

            using var insertCmd = new Microsoft.Data.Sqlite.SqliteCommand(@"
    INSERT INTO Projects (MarketID, SystemName, StationName, CreatedBy, CreatedAt)
    VALUES (@mid, @system, @station, @creator, @createdAt);", conn);

            insertCmd.Parameters.AddWithValue("@mid", marketID);
            insertCmd.Parameters.AddWithValue("@system", systemName);
            insertCmd.Parameters.AddWithValue("@station", stationName);
            insertCmd.Parameters.AddWithValue("@creator", _commanderName ?? "Unknown");
            insertCmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);
            insertCmd.ExecuteNonQuery();
        }

        private static bool IsUnknownSystem(string s) =>
            string.IsNullOrWhiteSpace(s) || s.Equals("UnknownSystem", StringComparison.OrdinalIgnoreCase);

        private static bool IsUnknownStation(string s) =>
            string.IsNullOrWhiteSpace(s) || s.Equals("UnknownStation", StringComparison.OrdinalIgnoreCase);

        private bool InsertNewProjectForDepot(long marketID)
        {
            Logger.Log($"[DEBUG] → Entered InsertNewProjectForDepot for MarketID={marketID}", "Debug");

            string starSystem = _lastDockedSystem ?? "UnknownSystem";
            string stationName = _lastDockedStation ?? "UnknownStation";
            if (IsUnknownSystem(starSystem) || IsUnknownStation(stationName))
            {
                Logger.Log($"Skipping insert: Project has unknown system/station name (MarketID={marketID})", "Warning");
                return false;
            }

            bool existsOnServerOrArchive = false;
            try
            {
                DbConnectionManager.Instance.Execute(cmd =>
                {
                    cmd.CommandText = @"
SELECT 1 FROM Projects WHERE MarketID=@mid
UNION ALL
SELECT 1 FROM ProjectsArchive WHERE MarketID=@mid
LIMIT 1;";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@mid", marketID);
                    cmd.CommandTimeout = 5;

                    using var rdr = cmd.ExecuteReader(System.Data.CommandBehavior.SingleRow);
                    existsOnServerOrArchive = rdr.Read();
                    return 0;
                }, sqlPreview: "UNION check Projects/Archive WHERE MarketID=@mid LIMIT 1");
            }
            catch (Exception ex)
            {
                // fail-closed: don’t create a local duplicate when server state is unknown
                Logger.Log($"Server existence check failed for MarketID={marketID}: {ex.Message}. Skipping local create.", "Warning");
                return false;
            }

            if (existsOnServerOrArchive)
            {
                Logger.Log($"Skipped insert: MarketID {marketID} exists on server (active or archived).", "Info");
                return false;
            }

            if (!ProjectExistsLocal(marketID))
            {
                InsertNewLocalProject(marketID, starSystem, stationName);
                Logger.Log($"[LOCAL] Added new project: “{stationName}” in {starSystem} (MarketID={marketID})", "Info");
                return true;
            }

            Logger.Log($"[LOCAL] Project already exists locally for MarketID={marketID}; no action.", "Debug");
            return false;
        }


        public void ClearFleetCarrierCargo()
        {
            FleetCarrierCargoItems.Clear();
        }

        private bool ProjectExists(long marketID)
        {
            bool exists = false;

            DbConnectionManager.Instance.Execute(cmd =>
            {
                cmd.CommandText = @"
            SELECT 1
              FROM Projects
             WHERE MarketID = @mid
             LIMIT 1;";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@mid", marketID);

                using var reader = cmd.ExecuteReader();
                exists = reader.Read();
                return 0; // return type not used here
            }, sqlPreview: "SELECT 1 FROM Projects WHERE MarketID=@mid LIMIT 1");

            return exists;
        }


        private void OnCarrierCargoChanged()
        {
            CarrierCargoChanged?.Invoke();
        }

        private void AddToCarrierCargo(string name, int quantity)
        {
            if (string.IsNullOrWhiteSpace(name) || quantity == 0) return;

            string key = name.Trim();
            var item = FleetCarrierCargoItems.FirstOrDefault(i =>
                string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                item.Quantity += quantity;
                if (item.Quantity <= 0) FleetCarrierCargoItems.Remove(item);
            }
            else if (quantity > 0)
            {
                FleetCarrierCargoItems.Add(new CargoItem { Name = key, Quantity = quantity });
            }

            // NEW: tell the UI what *you* just moved this session
            CarrierTransferDelta?.Invoke(key, quantity);

            OnCarrierCargoChanged();
        }

        // === NEW HELPERS =======================================================

        private static string ExtractCarrierCodeFromStationName(string stationName)
        {
            if (string.IsNullOrWhiteSpace(stationName)) return null;

            // Typical carrier callsign looks like ABC-123 (alnum-alnum)
            var m = Regex.Match(stationName, @"([A-Z0-9]{3}-[A-Z0-9]{3})", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();

            // Fallback: if the whole station name itself looks like a callsign,
            // keep it; otherwise return null so we don't write junk.
            if (Regex.IsMatch(stationName, @"^[A-Z0-9]{3}-[A-Z0-9]{3}$", RegexOptions.IgnoreCase))
                return stationName.ToUpperInvariant();

            return null;
        }

        /// <summary>
        /// Upserts FleetCarrierStorage row by (CarrierCode, ResourceName).
        /// Adds delta to Count (clamped at 0).
        /// </summary>
        private void UpdateCarrierStorage(string carrierCode, string resourceName, int delta)
        {
            DbConnectionManager.Instance.Execute(cmd =>
            {
                // Step 1: Read current count
                cmd.CommandText = @"
            SELECT Count
              FROM FleetCarrierStorage
             WHERE CarrierCode=@code AND ResourceName=@name
             LIMIT 1;";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@code", carrierCode);
                cmd.Parameters.AddWithValue("@name", resourceName);

                int current = 0;
                bool exists = false;

                using (var rdr = cmd.ExecuteReader())
                {
                    if (rdr.Read())
                    {
                        exists = true;
                        current = rdr.GetInt32("Count");
                    }
                }

                int next = current + delta;
                if (next < 0) next = 0;

                // Step 2: Update, Insert, or Skip
                if (exists)
                {
                    cmd.CommandText = @"
                UPDATE FleetCarrierStorage
                   SET Count=@count
                 WHERE CarrierCode=@code AND ResourceName=@name;";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@count", next);
                    cmd.Parameters.AddWithValue("@code", carrierCode);
                    cmd.Parameters.AddWithValue("@name", resourceName);
                    cmd.ExecuteNonQuery();

                    Logger.Log($"[CARRIER] {carrierCode} • {resourceName}: {current} → {next} (Δ {delta})", "Info");
                }
                else if (next > 0)
                {
                    cmd.CommandText = @"
                INSERT INTO FleetCarrierStorage (CarrierCode, ResourceName, Count)
                VALUES (@code, @name, @count);";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@code", carrierCode);
                    cmd.Parameters.AddWithValue("@name", resourceName);
                    cmd.Parameters.AddWithValue("@count", next);
                    cmd.ExecuteNonQuery();

                    Logger.Log($"[CARRIER] {carrierCode} • {resourceName}: inserted {next}", "Info");
                }
                else
                {
                    // next == 0 and row doesn't exist → nothing to do
                    Logger.Log($"[CARRIER] {carrierCode} • {resourceName}: no row to create (Δ {delta} → 0).", "Debug");
                }

                return 0; // return type not used
            }, sqlPreview: $"FleetCarrierStorage update for {carrierCode}/{resourceName}, Δ={delta}");
        }

        private bool IsRegisteredCarrier(string carrierCode)
        {
            if (!DbServerEnabled || string.IsNullOrWhiteSpace(carrierCode)) return false;

            bool exists = false;
            try
            {
                DbConnectionManager.Instance.Execute(cmd =>
                {
                    cmd.CommandText = "SELECT 1 FROM FleetCarriers WHERE CarrierCode=@code LIMIT 1;";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@code", carrierCode);

                    using var rdr = cmd.ExecuteReader();
                    exists = rdr.Read();
                    return 0; // Execute<T> requires a return value
                },
                // shows up in your [DB][FAIL] logs with op name + Primary/Fallback
                sqlPreview: "SELECT 1 FROM FleetCarriers WHERE CarrierCode=@code LIMIT 1");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CARRIER] IsRegistered check failed: {ex.Message}", "Warning");
                return false;
            }

            return exists;
        }

        // ======================================================================

        private class ReadState
        {
            public string FileName { get; set; }
            public long Position { get; set; }
        }
    }
}
