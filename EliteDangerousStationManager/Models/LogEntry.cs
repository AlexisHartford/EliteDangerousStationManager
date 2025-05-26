using System;

namespace EliteDangerousStationManager.Models
{
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public string Type { get; set; }
        public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss");
    }
}