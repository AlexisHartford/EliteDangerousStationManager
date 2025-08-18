using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EliteDangerousStationManager
{
    public class JournalMonitor
    {
        public static List<(string FilePath, string Commander, DateTime LastModified)> FindRecentJournalFiles()
        {
            string journalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Saved Games", "Frontier Developments", "Elite Dangerous");

            var files = Directory.GetFiles(journalDir, "Journal.*.log")
                .Select(path => new FileInfo(path))
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var recentFiles = new List<(string, string, DateTime)>();

            foreach (var file in files.Take(5))
            {
                string commander = TryGetCommanderName(file.FullName);
                if (!string.IsNullOrEmpty(commander))
                {
                    recentFiles.Add((file.FullName, commander, file.LastWriteTimeUtc));
                }
            }

            return recentFiles;
        }

        private static string TryGetCommanderName(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path).Take(50))
                {
                    var json = JObject.Parse(line);
                    if (json["event"]?.ToString() == "LoadGame" && json["Commander"] != null)
                        return json["Commander"].ToString();
                }
            }
            catch { }
            return null;
        }
    }
}
