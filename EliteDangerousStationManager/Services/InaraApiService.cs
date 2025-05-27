using System.Net.Http;
using System.Text;
using System.Text.Json;
using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;

namespace EliteDangerousStationManager.Services
{
    public class InaraApiService
    {
        private readonly string commanderName;
        private readonly string apiKey;
        private readonly HttpClient client = new();

        public InaraApiService(string commanderName, string apiKey)
        {
            this.commanderName = commanderName;
            this.apiKey = apiKey;
        }

        public async Task SendEventsAsync(List<InaraEvent> events)
        {
            if (events.Count == 0)
            {
                Logger.Log("Inara sync skipped: no events to send.", "Info");
                return;
            }

            var payload = new
            {
                header = new
                {
                    appName = "ENEX Station Manager",
                    appVersion = "1.0",
                    isDeveloped = true,
                    APIkey = apiKey,
                    commanderName = commanderName
                },
                events = events.Select(e => new
                {
                    eventName = e.EventName,
                    eventTimestamp = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    eventData = e.Data
                })
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            try
            {
                Logger.Log($"Sending Inara payload:\n{json}", "Debug");

                var response = await client.PostAsync("https://inara.cz/inapi/v1/",
                    new StringContent(json, Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    Logger.Log($"Inara sync failed ({(int)response.StatusCode}): {body}", "Error");
                }
                else
                {
                    Logger.Log($"Inara sync success: {events.Count} events sent.", "Success");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Inara sync exception: {ex.Message}", "Error");
            }
        }
    }
}
