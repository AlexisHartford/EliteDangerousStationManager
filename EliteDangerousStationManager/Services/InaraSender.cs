using EliteDangerousStationManager.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace EliteDangerousStationManager.Services
{
    public class InaraSender
    {
        private readonly string apiKey;
        private string commanderName;
        private static readonly HttpClient httpClient = new HttpClient();
        private const string inaraEndpoint = "https://inara.cz/inapi/v1/";
        private readonly System.Timers.Timer syncTimer;
        private static bool timerStarted = false;
        private readonly string journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Frontier Developments", "Elite Dangerous");

        public InaraSender()
        {
            try
            {
                string configPath = ConfigHelper.GetSettingsFilePath();

                if (File.Exists(configPath))
                {
                    apiKey = File.ReadLines(configPath).FirstOrDefault()?.Trim();
                    commanderName = ReadCommanderNameFromJournal();
                }

                else
                {
                    Logger.Log("settings.config not found.", "Error");
                }

                if (!timerStarted)
                {
                    timerStarted = true;
                    syncTimer = new System.Timers.Timer(60000);
                    syncTimer.Elapsed += async (sender, e) => await TriggerJournalSync();
                    syncTimer.AutoReset = true;
                    syncTimer.Start();

                    Logger.Log("Inara sync timer started (1-minute interval).", "Info");
                }
                else
                {
                    syncTimer = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize InaraSender: {ex.Message}", "Error");
            }
        }
        private string ReadCommanderNameFromJournal()
        {
            try
            {
                var latestFile = Directory.GetFiles(journalPath, "Journal.*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (latestFile == null)
                {
                    Logger.Log("No journal file found while reading commander name.", "Warning");
                    return "UnknownCommander";
                }

                var lines = File.ReadLines(latestFile).Reverse();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var json = JObject.Parse(line);
                    if (json["event"]?.ToString() == "Commander")
                    {
                        var name = json["Name"]?.ToString();
                        Logger.Log($"Commander name from journal: {name}", "Info");
                        return name ?? "UnknownCommander";
                    }
                }

                Logger.Log("Commander event not found in journal.", "Warning");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error reading commander name from journal: {ex.Message}", "Error");
            }

            return "UnknownCommander";
        }


        public void StartSyncTimer()
        {
            if (syncTimer != null && !syncTimer.Enabled)
            {
                syncTimer.Start();
                Logger.Log("Inara sync timer manually started.", "Info");
            }
        }

        private async Task TriggerJournalSync()
        {
            try
            {
                var latestFile = Directory.GetFiles(journalPath, "Journal.*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(latestFile))
                {
                    await SendEventsFromJournalAsync(latestFile);
                }
                else
                {
                    Logger.Log("No journal file found to process.", "Warning");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to trigger journal sync: {ex.Message}", "Error");
            }
        }

        public async Task SendEventsFromJournalAsync(string journalFile)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Logger.Log("API key is not configured.", "Error");
                return;
            }

            List<object> inaraEvents = new List<object>();

            try
            {
                var lines = new List<string>();
                using (var fs = new FileStream(journalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            lines.Add(line);
                    }
                }

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("event"))
                        continue;

                    var json = JObject.Parse(line);
                    string evt = json["event"]?.ToString();
                    string timestamp = FormatTimestamp(json["timestamp"]?.ToString());

                    switch (evt)
                    {
                        case "Reputation":
                            {
                                float fedRep = (json["Federation"]?.ToObject<float>() ?? 0f) / 100f;
                                float empRep = (json["Empire"]?.ToObject<float>() ?? 0f) / 100f;
                                float aliRep = (json["Alliance"]?.ToObject<float>() ?? 0f) / 100f;

                                if (fedRep > 0)
                                {
                                    inaraEvents.Add(new
                                    {
                                        eventName = "setCommanderReputationMajorFaction",
                                        eventTimestamp = timestamp,
                                        eventData = new
                                        {
                                            majorfactionName = "federation",
                                            majorfactionReputation = fedRep
                                        }
                                    });
                                }

                                if (empRep > 0)
                                {
                                    inaraEvents.Add(new
                                    {
                                        eventName = "setCommanderReputationMajorFaction",
                                        eventTimestamp = timestamp,
                                        eventData = new
                                        {
                                            majorfactionName = "empire",
                                            majorfactionReputation = empRep
                                        }
                                    });
                                }

                                if (aliRep > 0)
                                {
                                    inaraEvents.Add(new
                                    {
                                        eventName = "setCommanderReputationMajorFaction",
                                        eventTimestamp = timestamp,
                                        eventData = new
                                        {
                                            majorfactionName = "alliance",
                                            majorfactionReputation = aliRep
                                        }
                                    });
                                }
                                break;
                            }

                        case "Powerplay":
                            inaraEvents.Add(new
                            {
                                eventName = "setCommanderRankPower",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    powerName = json["Power"]?.ToString(),
                                    rankValue = json["Rank"]?.ToObject<int>() ?? 0,
                                    meritsValue = json["Merits"]?.ToObject<int>() ?? 0
                                }
                            });
                            break;
                        case "SellExplorationData":
                            if (json["Discovered"] is JArray bodies)
                            {
                                foreach (var body in bodies)
                                {
                                    inaraEvents.Add(new
                                    {
                                        eventName = "addCommanderExplorationDiscovery",
                                        eventTimestamp = timestamp,
                                        eventData = new
                                        {
                                            starSystemName = body?.ToString(),
                                            bodyName = body?.ToString()
                                        }
                                    });
                                }
                            }
                            break;
                        case "CommunityGoalReward":
                            inaraEvents.Add(new
                            {
                                eventName = "addCommanderCommunityGoal",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    communityGoalName = json["Name"]?.ToString(),
                                    reward = json["Reward"]?.ToObject<int>() ?? 0
                                }
                            });
                            break;
                        case "MaterialCollected":
                            inaraEvents.Add(new
                            {
                                eventName = "setCommanderInventoryMaterials",
                                eventTimestamp = timestamp,
                                eventData = new[]
                                {
                                    new
                                    {
                                        itemName = json["Name"]?.ToString(),
                                        itemCount = json["Count"]?.ToObject<int>() ?? 0
                                    }
                                }
                            });
                            break;
                        case "EngineerContribution":
                            if (!string.IsNullOrWhiteSpace(json["Engineer"]?.ToString()))
                            {
                                inaraEvents.Add(new
                                {
                                    eventName = "addCommanderEngineerCraft",
                                    eventTimestamp = timestamp,
                                    eventData = new
                                    {
                                        engineerName = json["Engineer"]?.ToString(),
                                        commodityName = json["Commodity"]?.ToString(),
                                        commodityCount = json["Quantity"]?.ToObject<int>() ?? 0
                                    }
                                });
                            }
                                break;

                        case "EngineerProgress":
                            if (json["Engineers"] is JArray engineers)
                            {
                                foreach (var eng in engineers)
                                {
                                    string name = eng["Engineer"]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        inaraEvents.Add(new
                                        {
                                            eventName = "setCommanderRankEngineer",
                                            eventTimestamp = timestamp,
                                            eventData = new[]
                                            {
                                                new
                                                {
                                                    engineerName = name,
                                                    rankStage = eng["Progress"]?.ToString(),
                                                    rankValue = eng["Rank"]?.ToObject<int?>()
                                                }
                                            }
                                        });
                                    }
                                }
                            }
                            break;

                        case "Docked":
                            inaraEvents.Add(new
                            {
                                eventName = "addCommanderTravelDock",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    starsystemName = json["StarSystem"]?.ToString(),
                                    stationName = json["StationName"]?.ToString(),
                                    shipType = json["Ship"]?.ToString(),
                                    shipGameID = json["ShipID"]?.ToObject<int?>()
                                }
                            });
                            break;
                        case "FSDJump":
                            inaraEvents.Add(new
                            {
                                eventName = "addCommanderTravelFSDJump",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    starsystemName = json["StarSystem"]?.ToString(),
                                    starsystemCoords = new[] {
                                        json["StarPos"]?[0]?.ToObject<double>() ?? 0,
                                        json["StarPos"]?[1]?.ToObject<double>() ?? 0,
                                        json["StarPos"]?[2]?.ToObject<double>() ?? 0
                                    },
                                    jumpDistance = json["JumpDist"]?.ToObject<double?>(),
                                    shipType = json["Ship"]?.ToString(),
                                    shipGameID = json["ShipID"]?.ToObject<int?>()
                                }
                            });
                            break;
                        case "PVPKill":
                            inaraEvents.Add(new
                            {
                                eventName = "addCommanderPvPKill",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    opponentName = json["Victim"]?.ToString()
                                }
                            });
                            break;
                        case "Cargo":
                            if (json["Inventory"] is JArray cargoItems)
                            {
                                inaraEvents.Add(new
                                {
                                    eventName = "setCommanderInventoryCargo",
                                    eventTimestamp = timestamp,
                                    eventData = cargoItems.Select(item => new
                                    {
                                        itemName = item["Name"]?.ToString(),
                                        itemCount = item["Count"]?.ToObject<int?>() ?? 0,
                                        isStolen = item["Stolen"]?.ToObject<bool?>() ?? false
                                    }).ToArray()
                                });
                            }
                            break;
                        case "MissionAccepted":
                            inaraEvents.Add(new
                            {
                                eventName = "setCommanderMissionAccepted",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    missionName = json["Name"]?.ToString(),
                                    missionGameID = json["MissionID"]?.ToObject<long?>()
                                }
                            });
                            break;
                        case "MissionCompleted":
                            inaraEvents.Add(new
                            {
                                eventName = "setCommanderMissionCompleted",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    missionGameID = json["MissionID"]?.ToObject<long?>()
                                }
                            });
                            break;
                        case "MissionAbandoned":
                            inaraEvents.Add(new
                            {
                                eventName = "setCommanderMissionAbandoned",
                                eventTimestamp = timestamp,
                                eventData = new
                                {
                                    missionGameID = json["MissionID"]?.ToObject<long?>()
                                }
                            });
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error while parsing journal: {ex.Message}", "Error");
                return;
            }

            if (inaraEvents.Count == 0)
            {
                Logger.Log("No Inara events generated from journal.", "Info");
                return;
            }

            var payload = new
            {
                header = new
                {
                    appName = "ENEX Carrier Bot",
                    appVersion = "1.0",
                    isBeingDeveloped = false,
                    APIkey = apiKey,
                    commanderName = commanderName
                },
                events = inaraEvents
            };

            string jsonPayload = JsonConvert.SerializeObject(payload, Formatting.Indented);
            Logger.Log("Sending payload to Inara:", "Info");
            //Logger.Log(jsonPayload, "Info");

            //Logger.Log("🚧 [TEST MODE] Payload prepared for Inara. Skipping HTTP POST.", "Info");
            // Uncomment the following lines when ready to send to Inara:
             var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
             var response = await httpClient.PostAsync(inaraEndpoint, content);
             string result = await response.Content.ReadAsStringAsync();
            // Logger.Log($"Inara response: {result}", "Info");
        }

        private string FormatTimestamp(string ts)
        {
            if (DateTime.TryParse(ts, out var dt))
                return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
    }
}
