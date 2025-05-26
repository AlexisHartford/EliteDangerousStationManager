using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using EliteDangerousStationManager.Models;
using EliteDangerousStationManager.Helpers;

namespace EliteDangerousStationManager.Services
{
    public class InaraService
    {
        private readonly DispatcherTimer timer;
        private readonly Func<Project> getCurrentProject;
        private readonly Func<List<ProjectMaterial>> getCurrentMaterials;
        private readonly Func<List<CargoItem>> getCurrentCargo;
        private readonly string commanderName;
        private readonly string inaraApiKey;

        public InaraService(
            string commander,
            string apiKey,
            Func<Project> currentProjectAccessor,
            Func<List<ProjectMaterial>> materialAccessor,
            Func<List<CargoItem>> cargoAccessor)
        {
            commanderName = commander;
            inaraApiKey = apiKey;
            getCurrentProject = currentProjectAccessor;
            getCurrentMaterials = materialAccessor;
            getCurrentCargo = cargoAccessor;

            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += async (s, e) => await SendUpdateToInara();
        }

        public void Start() => timer.Start();

        private async Task SendUpdateToInara()
        {
            try
            {
                var project = getCurrentProject();
                var materials = getCurrentMaterials();
                var cargo = getCurrentCargo();

                if (project == null || materials == null || cargo == null)
                {
                    Logger.Log("Inara sync skipped: missing data.", "Warning");
                    return;
                }

                var payload = new
                {
                    header = new
                    {
                        appName = "EDStationManager",
                        appVersion = "1.0",
                        isDeveloped = true,
                        APIkey = inaraApiKey,
                        commanderName = commanderName
                    },
                    events = new object[]
                    {
                        new
                        {
                            eventName = "logColonisationDepot",
                            eventTimestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            eventData = new
                            {
                                marketId = project.MarketId,
                                progress = materials.Sum(m => m.Provided) / (double)materials.Sum(m => m.Required),
                                isCompleted = materials.All(m => m.Provided >= m.Required),
                                isFailed = false,
                                resources = materials.Select(m => new
                                {
                                    name = m.Material.ToLower().Replace(" ", "_"),
                                    required = m.Required,
                                    delivered = m.Provided,
                                    payment = 0
                                })
                            }
                        },
                        new
                        {
                            eventName = "logCargo",
                            eventTimestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            eventData = new
                            {
                                shipType = "FleetCarrier",
                                cargo = cargo.Select(c => new
                                {
                                    name = c.Name.ToLower().Replace(" ", "_"),
                                    count = c.Quantity
                                })
                            }
                        }
                    }
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                using var client = new HttpClient();
                var response = await client.PostAsync("https://inara.cz/inapi/v1/", new StringContent(json, Encoding.UTF8, "application/json"));

                Logger.Log(response.IsSuccessStatusCode
                    ? "INARA sync succeeded."
                    : $"INARA sync failed: {response.StatusCode}", response.IsSuccessStatusCode ? "Success" : "Error");
            }
            catch (Exception ex)
            {
                Logger.Log("Error during INARA update: " + ex.Message, "Error");
            }
        }
    }
}