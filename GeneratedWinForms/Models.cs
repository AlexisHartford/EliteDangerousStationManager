
using System;
using System.Collections.Generic;

namespace EliteDangerousStationManager
{
    public class MaterialRequirement
    {
        public int Id { get; set; }
        public string MaterialName { get; set; }
        public int QuantityRequired { get; set; }
    }

    public class ConstructionProject
    {
        public int Id { get; set; }
        public string ProjectName { get; set; }
        public List<MaterialRequirement> MaterialsRequired { get; set; }
    }

    public class LogEntry
    {
        public int Id { get; set; }
        public LogType Type { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CargoItem
    {
        public int Id { get; set; }
        public string ItemName { get; set; }
        public int Quantity { get; set; }
    }
}
