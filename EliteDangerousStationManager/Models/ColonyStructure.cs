using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EliteDangerousStationManager.Models
{
    public class ColonyStructure
    {
        public string Name { get; set; }
        public string MaxPad { get; set; }
        public string Prerequisites { get; set; }
        public string T2 { get; set; }
        public string T3 { get; set; }
        public int Security { get; set; }
        public int TechLevel { get; set; }
        public int Wealth { get; set; }
        public int StandardOfLiving { get; set; }
        public int DevelopmentLevel { get; set; }
        public string FacilityEconomy { get; set; }
        public string EconomyInfluence { get; set; }
        public int InitPopInc { get; set; }
        public int MaxPopInc { get; set; }
    }
}
