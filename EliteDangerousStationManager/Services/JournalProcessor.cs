using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;
using Newtonsoft.Json;
using MySql.Data.MySqlClient;
using System.Configuration;


namespace EliteDangerousStationManager.Services
{
    public class JournalProcessor
    {
        private readonly string _journalPath;
        private readonly string commanderName;
        private readonly EddnSender eddnSender;

        private ReadState lastState = new();
        private string stateFilePath;

        public List<CargoItem> FleetCarrierCargoItems { get; private set; } = new();
        private readonly string connectionString = ConfigurationManager.ConnectionStrings["EliteDB"].ConnectionString;


        private class ReadState
        {
            public string FileName { get; set; }
            public long Position { get; set; }
        }

        public JournalProcessor(string journalPath, string commanderName)
        {
            _journalPath = journalPath;
            this.commanderName = commanderName;
            this.eddnSender = new EddnSender();

            stateFilePath = Path.Combine(_journalPath, "lastread.state");
            LoadReadState();
        }

        private void LoadReadState()
        {
            if (File.Exists(stateFilePath))
            {
                try
                {
                    var json = File.ReadAllText(stateFilePath);
                    lastState = JsonConvert.DeserializeObject<ReadState>(json) ?? new ReadState();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to load read state: {ex.Message}", "Error");
                    lastState = new ReadState();
                }
            }

        }

        private void SaveReadState()
        {
            File.WriteAllText(stateFilePath, JsonConvert.SerializeObject(lastState));
        }

        public void ResetReadState()
        {
            lastState = new ReadState
            {
                FileName = null,
                Position = 0
            };
            SaveReadState();
        }

        public ColonisationConstructionDepotEvent FindLatestConstructionEvent(out string dockedSystem, out string dockedStation, out long dockedMarketId)
        {
            dockedSystem = null;
            dockedStation = null;
            dockedMarketId = 0;

            var latestFile = Directory.GetFiles(_journalPath, "Journal.*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (latestFile == null)
            {
                Logger.Log("No journal file found.", "Warning");
                return null;
            }

            bool isNewFile = !string.Equals(latestFile, lastState.FileName, StringComparison.OrdinalIgnoreCase);
            long startPos = isNewFile ? 0 : lastState.Position;

            var lines = new List<string>();
            using (var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(startPos, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line);
                }

                lastState.FileName = latestFile;
                lastState.Position = fs.Position;
                SaveReadState(); // ✅ Save new read position normally
            }

            int dockedIndex = -1;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var json = JObject.Parse(lines[i]);
                var evt = json["event"]?.ToString();

                if (evt == "Docked")
                {
                    dockedSystem = json["StarSystem"]?.ToString();
                    dockedStation = json["StationName"]?.ToString();
                    dockedMarketId = json["MarketID"]?.ToObject<long>() ?? 0;
                    dockedIndex = i;
                    break;
                }
            }

            // Track if docked on a FleetCarrier
            bool dockedOnFleetCarrier = false;
            long dockedCarrierMarketId = 0;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var json = JObject.Parse(lines[i]);
                if (json["event"]?.ToString() == "Docked" &&
                    json["StationType"]?.ToString() == "FleetCarrier")
                {
                    dockedOnFleetCarrier = true;
                    dockedCarrierMarketId = json["MarketID"]?.ToObject<long>() ?? 0;
                    break;
                }
            }

            foreach (var line in lines.Skip(dockedIndex >= 0 ? dockedIndex : 0))

            {
                var json = JObject.Parse(line);
                string evt = json["event"]?.ToString();

                if (evt == "MarketSell" || evt == "MarketBuy")
                {
                    if (dockedOnFleetCarrier)
                    {
                        string name = json["Type_Localised"]?.ToString() ?? json["Type"]?.ToString();
                        int count = json["Count"]?.ToObject<int>() ?? 0;

                        if (evt == "MarketSell")
                            AddToCarrierCargo(name, count);
                        else if (evt == "MarketBuy")
                            AddToCarrierCargo(name, -count);
                    }
                }
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

                            if (!string.IsNullOrEmpty(type) && count > 0)
                            {
                                int quantity = direction == "tocarrier" ? count : -count;
                                AddToCarrierCargo(type, quantity);
                            }
                        }
                    }
                }
            }

            if (dockedIndex == -1) return null;

            ColonisationConstructionDepotEvent latestDepot = null;

            for (int i = dockedIndex; i < lines.Count; i++)
            {
                var json = JObject.Parse(lines[i]);
                if (json["event"]?.ToString() == "ColonisationConstructionDepot")
                {
                    var marketID = json["MarketID"]?.ToObject<long>() ?? 0;
                    if (marketID == dockedMarketId)
                    {
                        latestDepot = new ColonisationConstructionDepotEvent
                        {
                            MarketID = marketID,
                            ConstructionProgress = json["ConstructionProgress"]?.ToObject<double>() ?? 0,
                            ConstructionComplete = json["ConstructionComplete"]?.ToObject<bool>() ?? false,
                            ConstructionFailed = json["ConstructionFailed"]?.ToObject<bool>() ?? false,
                            ResourcesRequired = new List<ResourceRequired>()
                        };

                        foreach (var r in json["ResourcesRequired"] ?? new JArray())
                        {
                            string name = r["Name"]?.ToString();
                            string nameLocalised = r["Name_Localised"]?.ToString() ?? name;
                            int required = r["RequiredAmount"]?.ToObject<int>() ?? 0;
                            int provided = r["ProvidedAmount"]?.ToObject<int>() ?? 0;
                            int payment = r["Payment"]?.ToObject<int>() ?? 0;

                            int existingProvided = GetProvidedAmountFromDb(name, marketID);

                            if (provided > existingProvided)
                            {
                                // Only update if new provided amount is higher
                                UpdateMaterialProvidedAmount(name, marketID, provided);
                            }

                            latestDepot.ResourcesRequired.Add(new ResourceRequired
                            {
                                Name = name,
                                Name_Localised = nameLocalised,
                                RequiredAmount = required,
                                ProvidedAmount = provided,
                                Payment = payment
                            });
                        }

                        eddnSender.SendJournalEvent(commanderName, json);

                    }
                }
            }

            return latestDepot;
        }

        private int GetProvidedAmountFromDb(string materialName, long marketId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            var cmd = new MySqlCommand("SELECT ProvidedAmount FROM ProjectResources WHERE Name = @name AND MarketId = @marketId", conn);
            cmd.Parameters.AddWithValue("@name", materialName);
            cmd.Parameters.AddWithValue("@marketId", marketId);

            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : 0;
        }

        private void UpdateMaterialProvidedAmount(string materialName, long marketId, int newAmount)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            var cmd = new MySqlCommand("UPDATE ProjectMaterials SET ProvidedAmount = @amount WHERE Name = @name AND MarketId = @marketId", conn);
            cmd.Parameters.AddWithValue("@amount", newAmount);
            cmd.Parameters.AddWithValue("@name", materialName);
            cmd.Parameters.AddWithValue("@marketId", marketId);

            cmd.ExecuteNonQuery();
        }


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
                FleetCarrierCargoItems.Add(new CargoItem
                {
                    Name = name,
                    Quantity = quantity
                });
            }
        }
    }
}
