using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace EliteDangerousStationManager
{
    public class InaraSender
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string commanderName;
        private readonly string apiKey;

        public InaraSender(string commanderName, string apiKey)
        {
            this.commanderName = commanderName;
            this.apiKey = apiKey;
        }

        public async Task SendEventsAsync(List<object> events)
        {
            if (events == null || events.Count == 0)
                return;

            var payload = new
            {
                header = new
                {
                    appName = "EliteDangerousStationManager",
                    appVersion = "1.0",
                    isDeveloped = true,
                    APIkey = apiKey,
                    commanderName = commanderName,
                    commanderFrontierID = 0
                },
                events = events
            };

            string json = JsonConvert.SerializeObject(payload);

            try
            {
                var response = await httpClient.PostAsync("https://inara.cz/inapi/v1/", new StringContent(json, Encoding.UTF8, "application/json"));
                string result = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"INARA Status: {response.StatusCode} - {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"INARA Error: {ex.Message}");
            }
        }
    }
}
