using Newtonsoft.Json.Linq;
using EliteDangerousStationManager.Models;
using System.IO;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    public class JournalParser
    {
        private readonly string journalPath;

        public JournalParser(string journalDirectory)
        {
            journalPath = journalDirectory;
        }

        public List<InaraEvent> GetNewInaraEvents(DateTime since)
        {
            var result = new List<InaraEvent>();
            var journalFiles = Directory.GetFiles(journalPath, "Journal.*.log");

            foreach (var file in journalFiles.OrderBy(f => f))
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var json = JObject.Parse(line);
                        var evtTime = json["timestamp"]?.ToObject<DateTime>() ?? DateTime.MinValue;
                        if (evtTime <= since) continue;

                        string evt = json["event"]?.ToString();

                        switch (evt)
                        {
                            case "FSDJump":
                                result.Add(new InaraEvent
                                {
                                    EventName = "addCommanderTravelFSDJump",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        starsystemName = json["StarSystem"]?.ToString(),
                                        starsystemCoords = new[]
                                        {
                                            json["StarPos"]?[0]?.ToObject<float>() ?? 0,
                                            json["StarPos"]?[1]?.ToObject<float>() ?? 0,
                                            json["StarPos"]?[2]?.ToObject<float>() ?? 0
                                        },
                                        jumpDistance = json["JumpDist"]?.ToObject<float>() ?? 0,
                                        shipType = json["Ship"]?.ToString(),
                                        shipGameID = json["ShipID"]?.ToObject<int>() ?? 0,
                                        isTaxiShuttle = json["Taxi"]?.ToObject<bool>() ?? false,
                                        isTaxiDropship = json["Multicrew"]?.ToObject<bool>() ?? false
                                    }
                                });
                                break;

                            case "Docked":
                                result.Add(new InaraEvent
                                {
                                    EventName = "addCommanderTravelDock",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        starSystem = json["StarSystem"]?.ToString(),
                                        stationName = json["StationName"]?.ToString(),
                                        marketId = json["MarketID"]?.ToObject<long>()
                                    }
                                });
                                break;

                            case "LoadGame":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderCredits",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        credits = json["Credits"]?.ToObject<long?>() ?? 0,
                                        loan = json["Loan"]?.ToObject<long?>() ?? 0
                                    }
                                });
                                break;

                            case "Rank":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderRankPilot",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        combat = json["Combat"]?.ToObject<int>(),
                                        trade = json["Trade"]?.ToObject<int>(),
                                        explore = json["Explore"]?.ToObject<int>(),
                                        cqc = json["CQC"]?.ToObject<int>(),
                                        federation = json["Federation"]?.ToObject<int>(),
                                        empire = json["Empire"]?.ToObject<int>()
                                    }
                                });
                                break;

                            case "EngineerProgress":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderRankEngineer",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        engineer = json["Engineer"]?.ToString(),
                                        rank = json["Rank"]?.ToObject<int>(),
                                        reputation = json["Reputation"]?.ToObject<double?>()
                                    }
                                });
                                break;

                            case "Powerplay":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderRankPower",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        power = json["Power"]?.ToString(),
                                        rank = json["Rank"]?.ToObject<int>(),
                                        merits = json["Merits"]?.ToObject<int>()
                                    }
                                });
                                break;

                            case "MaterialCollected":
                            case "Materials":
                                result.Add(new InaraEvent
                                {
                                    EventName = "addCommanderInventoryMaterialsItem",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        itemName = json["Name"]?.ToString(),
                                        itemCount = json["Count"]?.ToObject<int>() ?? 1
                                    }
                                });
                                break;

                            case "MissionCompleted":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderMissionCompleted",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        missionGameID = json["MissionID"]?.ToObject<long>(),
                                        donationCredits = json["Donation"]?.ToObject<int>() ?? 0,
                                        rewardCredits = json["Reward"]?.ToObject<int>() ?? 0,
                                        rewardPermits = json["PermitsAwarded"]?.Select(p => p.ToString()).ToArray() ?? new string[0],
                                        rewardCommodities = json["CommodityReward"]?.Select(c => new {
                                            itemName = c["Name"]?.ToString(),
                                            itemCount = c["Count"]?.ToObject<int>() ?? 0
                                        }).ToArray() ?? new object[0],
                                        rewardMaterials = json["MaterialsReward"]?.Select(m => new {
                                            itemName = m["Name"]?.ToString(),
                                            itemCount = m["Count"]?.ToObject<int>() ?? 0
                                        }).ToArray() ?? new object[0],
                                        minorfactionEffects = json["FactionEffects"]?.ToObject<object[]>() ?? new object[0]
                                    }
                                });
                                break;

                            case "MissionFailed":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderMissionFailed",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        missionID = json["MissionID"]?.ToObject<long>(),
                                        name = json["Name"]?.ToString()
                                    }
                                });
                                break;

                            case "MissionAbandoned":
                                result.Add(new InaraEvent
                                {
                                    EventName = "setCommanderMissionAbandoned",
                                    Timestamp = evtTime,
                                    Data = new
                                    {
                                        missionID = json["MissionID"]?.ToObject<long>(),
                                        name = json["Name"]?.ToString()
                                    }
                                });
                                break;
                        }
                    }
                }
                catch (IOException ex)
                {
                    Logger.Log($"Unable to read journal file {file}: {ex.Message}", "Warning");
                }
            }

            foreach (var e in result)
            {
                Logger.Log($"Prepared Inara event: {e.EventName} @ {e.Timestamp:yyyy-MM-ddTHH:mm:ssZ}", "Info");
            }

            return result;
        }
    }
}
