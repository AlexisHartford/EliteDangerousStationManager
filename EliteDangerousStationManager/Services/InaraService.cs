using System.Text.Json;
using System.Text;
using System.Net.Http;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;
using System.IO;

namespace EliteDangerousStationManager.Services
{
    public class InaraService
    {
        private readonly string commanderName;
        private readonly string inaraApiKey;
        private readonly JournalParser journalParser;
        private readonly string syncStateFile = "LastInaraSync.txt";
        private DateTime lastSyncedTime;

        public InaraService(string commander, string apiKey, string journalPath)
        {
            commanderName = commander;
            inaraApiKey = apiKey;
            journalParser = new JournalParser(journalPath);
            LoadLastSyncedTime();
        }

        private void LoadLastSyncedTime()
        {
            if (File.Exists(syncStateFile) && DateTime.TryParse(File.ReadAllText(syncStateFile), out var time))
                lastSyncedTime = time;
            else
                lastSyncedTime = DateTime.MinValue;
        }

        private void SaveLastSyncedTime(DateTime newTime)
        {
            File.WriteAllText(syncStateFile, newTime.ToString("o"));
        }

        public async Task SendUpdateToInara()
        {
            try
            {
                var newEvents = journalParser.GetNewInaraEvents(lastSyncedTime);
                if (!newEvents.Any())
                {
                    Logger.Log("No new journal events to sync.", "Info");
                    return;
                }

                var payload = new
                {
                    header = new
                    {
                        appName = "ENEX Carrier Bot",
                        appVersion = "1.0",
                        isDeveloped = true,
                        APIkey = inaraApiKey,
                        commanderName = commanderName
                    },
                    events = newEvents.Select(e => new
                    {
                        eventName = e.EventName,
                        eventTimestamp = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        eventData = e.Data
                    }).ToArray()
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                using var client = new HttpClient();
                var response = await client.PostAsync("https://inara.cz/inapi/v1/", new StringContent(json, Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log($"Sent {newEvents.Count} events to INARA.", "Success");
                    SaveLastSyncedTime(newEvents.Max(e => e.Timestamp));
                }
                else
                {
                    Logger.Log("Failed to sync to INARA: " + response.StatusCode, "Error");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("INARA update error: " + ex.Message, "Error");
            }
        }
    }
}
