using System;

namespace EliteDangerousStationManager.Models
{
    public class ArchivedProject
    {
        public long MarketId { get; set; }
        public string SystemName { get; set; }
        public string StationName { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ArchivedAt { get; set; }

        public TimeSpan Duration => ArchivedAt - CreatedAt;

        public override string ToString()
        {
            return $"{SystemName} / {StationName} by {CreatedBy} - Duration: {Duration:%d}d {Duration:hh\\:mm}";
        }

        // 🆕 Add this property
        public string Source { get; set; }  // "Local" or "Server"
    }
}
