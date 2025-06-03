using EliteDangerousStationManager.Helpers;
using EliteDangerousStationManager.Models;
using System.IO;
using System.Text.Json;

namespace EliteDangerousStationManager.Services
{
    public class InaraService
    {
        private readonly string commanderName;
        private readonly string APIkey;
        private readonly JournalParser journalParser;
        private readonly InaraApiService apiService;
        private DateTime lastSyncedTime;

        public InaraService(string commanderName, string APIkey, string journalPath)
        {
            this.commanderName = commanderName;
            this.APIkey = APIkey;
            this.journalParser = new JournalParser(journalPath, commanderName);

            this.apiService = new InaraApiService(commanderName, APIkey);
            this.lastSyncedTime = LoadLastSyncedTime();
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

                await apiService.SendEventsAsync(newEvents);

                var latestTime = newEvents.Max(e => e.Timestamp);
                SaveLastSyncedTime(latestTime);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error during INARA update: {ex.Message}", "Error");
            }
        }

        private DateTime LoadLastSyncedTime()
        {
            try
            {
                if (File.Exists("lastInaraSync.txt"))
                {
                    string text = File.ReadAllText("lastInaraSync.txt");
                    if (DateTime.TryParse(text, out var dt))
                        return dt;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load last sync time: {ex.Message}", "Warning");
            }
            return DateTime.UtcNow.AddHours(-1); // default to past hour
        }

        private void SaveLastSyncedTime(DateTime timestamp)
        {
            try
            {
                File.WriteAllText("lastInaraSync.txt", timestamp.ToString("O"));
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save last sync time: {ex.Message}", "Warning");
            }
        }
    }
}