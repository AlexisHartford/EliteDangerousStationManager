using System;

namespace EliteDangerousStationManager.Services
{
    /// <summary>
    /// Keeps track of which journal file was last read and the byte‐offset position.
    /// </summary>
    public class ReadState
    {
        public string FileName { get; set; }
        public long Position { get; set; }
    }
}
