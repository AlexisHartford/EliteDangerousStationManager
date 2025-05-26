using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Services;

namespace EliteDangerousStationManager.Services
{
    public class JournalProcessor
    {
        private readonly string _journalPath;
        private readonly string commanderName;
        private readonly EddnSender eddnSender;

        public JournalProcessor(string journalPath, string commanderName)
        {
            _journalPath = journalPath;
            this.commanderName = commanderName;
            this.eddnSender = new EddnSender();
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

            var lines = new List<string>();
            using (var fs = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line);
                }
            }

            int dockedIndex = -1;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var json = JObject.Parse(lines[i]);
                if (json["event"]?.ToString() == "Docked")
                {
                    dockedSystem = json["StarSystem"]?.ToString();
                    dockedStation = json["StationName"]?.ToString();
                    dockedMarketId = json["MarketID"]?.ToObject<long>() ?? 0;
                    dockedIndex = i;
                    break;
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
                            ResourcesRequired = json["ResourcesRequired"]?.Select(r => new ResourceRequired
                            {
                                Name_Localised = r["Name_Localised"]?.ToString() ?? r["Name"]?.ToString(),
                                Name = r["Name"]?.ToString(),
                                RequiredAmount = r["RequiredAmount"]?.ToObject<int>() ?? 0,
                                ProvidedAmount = r["ProvidedAmount"]?.ToObject<int>() ?? 0,
                                Payment = r["Payment"]?.ToObject<int>() ?? 0
                            }).ToList()
                        };

                        // Send most recent one only
                        eddnSender.SendJournalEvent(commanderName, json);
                    }
                }
            }

            return latestDepot;
        }
    }
}