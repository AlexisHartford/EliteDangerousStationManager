using System.Collections.Generic;

namespace EliteDangerousStationManager.Models
{
    public class ColonisationConstructionDepotEvent
    {
        public long MarketID { get; set; }
        public double ConstructionProgress { get; set; }
        public bool ConstructionComplete { get; set; }
        public bool ConstructionFailed { get; set; }
        public List<ResourceRequired> ResourcesRequired { get; set; }
    }
}