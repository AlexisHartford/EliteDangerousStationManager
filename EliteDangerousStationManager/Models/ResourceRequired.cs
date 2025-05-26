namespace EliteDangerousStationManager.Models
{
    public class ResourceRequired
    {
        public string? Name_Localised { get; set; }
        public string? Name { get; set; }
        public int RequiredAmount { get; set; }
        public int ProvidedAmount { get; set; }
        public int Payment { get; set; }
    }
}