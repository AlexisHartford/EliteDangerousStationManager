using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    public class JournalProcessor
    {
        private readonly string _journalPath;
        private readonly string commanderName;
        private readonly EddnSender eddnSender;
        private readonly InaraSender inaraSender;

        private ReadState lastState = new();
        private string stateFilePath;

        public List<CargoItem> FleetCarrierCargoItems { get; private set; } = new();

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
            this.inaraSender = new InaraSender();

            stateFilePath = Path.Combine(_journalPath, "lastread.state");
            LoadReadState();
        }

        private void LoadReadState()
        {
            if (File.Exists(stateFilePath))
            {
                var lines = File.ReadAllLines(stateFilePath);
                if (lines.Length == 2)
                {
                    lastState.FileName = lines[0];
                    if (long.TryParse(lines[1], out long pos))
                        lastState.Position = pos;
                }
            }
        }

        private void SaveReadState(string fileName, long position)
        {
            File.WriteAllLines(stateFilePath, new[] { fileName, position.ToString() });
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
            try
            {
                using (var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
                    SaveReadState(lastState.FileName, lastState.Position);
                }
            }
            catch (IOException ex)
            {
                Logger.Log($"Error reading journal file '{latestFile}': {ex.Message}", "Warning");
                return null;
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

            if (dockedIndex == -1) return null;

            bool dockedOnFleetCarrier = false;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var json = JObject.Parse(lines[i]);
                if (json["event"]?.ToString() == "Docked" &&
                    json["StationType"]?.ToString() == "FleetCarrier")
                {
                    dockedOnFleetCarrier = true;
                    break;
                }
            }

            ColonisationConstructionDepotEvent latestDepot = null;

            foreach (var line in lines)
            {
                var json = JObject.Parse(line);
                string evt = json["event"]?.ToString();

                if (dockedOnFleetCarrier)
                {
                    if (evt == "MarketSell" || evt == "MarketBuy")
                    {
                        string name = json["Type_Localised"]?.ToString() ?? json["Type"]?.ToString();
                        int count = json["Count"]?.ToObject<int>() ?? 0;

                        if (evt == "MarketSell")
                            AddToCarrierCargo(name, count);
                        else if (evt == "MarketBuy")
                            AddToCarrierCargo(name, -count);
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
                else
                {
                    if (evt == "ColonisationConstructionDepot")
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
                                ResourcesRequired = json["ResourcesRequired"]?.Select(r => new ResourceRequired
                                {
                                    Name_Localised = r["Name_Localised"]?.ToString() ?? r["Name"]?.ToString(),
                                    Name = r["Name"]?.ToString(),
                                    RequiredAmount = r["RequiredAmount"]?.ToObject<int>() ?? 0,
                                    ProvidedAmount = r["ProvidedAmount"]?.ToObject<int>() ?? 0,
                                    Payment = r["Payment"]?.ToObject<int>() ?? 0
                                }).ToList()
                            };

                            eddnSender.SendJournalEvent(commanderName, json);
                        }
                    }
                }
            }

            return latestDepot;
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
