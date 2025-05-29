using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ColonizationPlanner
{
    public partial class ColonizationPlanner : Window
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
        public class SelectedStructure
        {
            public string Name { get; set; }
            public bool IsBuilt { get; set; }
        }
        public class SavedProject
        {
            public string ProjectName { get; set; }
            public List<SavedStructure> Structures { get; set; } = new();

        }
        public class ColonyProject
        {
            public string SystemName { get; set; }
            public List<StructureSelection> SelectedStructures { get; set; } = new();
        }
        public class StructureSelection
        {
            public string StructureName { get; set; }
            public bool IsBuilt { get; set; }
        }

        public class SavedStructure
        {
            public string Name { get; set; }
            public bool IsBuilt { get; set; }
        }


        private List<ColonyStructure> allStructures = new();
        private List<ComboBox> comboBoxes = new();
        private readonly string savePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EDStationManager", "colonization_plan.json");


        public ColonizationPlanner()
        {
            InitializeComponent();
            LoadStructures();
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            RefreshProjectDropdown(); // ← make sure this runs
            //AddStructureRow();
        }
        private const string SavePath = "ColonizationPlannerProjects.json";
        private List<SavedProject> savedProjects = new();

        

        private void ProjectDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Optionally auto-load here
        }

        private void RefreshProjectDropdown()
        {
            try
            {
                if (!File.Exists(savePath))
                {
                    ProjectDropdown.ItemsSource = null;
                    return;
                }

                var allProjects = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColonyProject>>(File.ReadAllText(savePath));
                var names = allProjects?
                    .Where(p => !string.IsNullOrWhiteSpace(p.SystemName))
                    .Select(p => p.SystemName)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                ProjectDropdown.ItemsSource = names;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load project list:\n" + ex.Message, "Error");
            }
        }



        private void LoadStructures()
        {
            allStructures = new List<ColonyStructure>
            {
                    new ColonyStructure {
        Name = "Orbital - Starport - Coriolis",
        MaxPad = "L", Prerequisites = "", T2 = "-3", T3 = "1",
        Security = -2, TechLevel = 2, Wealth = 3,
        StandardOfLiving = 3, DevelopmentLevel = 3,
        FacilityEconomy = "Colony", EconomyInfluence = "",
        InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Orbital - Starport - Asteroid Base",
        MaxPad = "L", Prerequisites = "", T2 = "-3", T3 = "1",
        Security = -1, TechLevel = 3, Wealth = 5,
        StandardOfLiving = -4, DevelopmentLevel = 7,
        FacilityEconomy = "Extraction", EconomyInfluence = "",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Starport - Ocellus",
        MaxPad = "L", Prerequisites = "", T2 = "", T3 = "-6",
        Security = -3, TechLevel = 7, Wealth = 8,
        StandardOfLiving = 5, DevelopmentLevel = 9,
        FacilityEconomy = "Colony", EconomyInfluence = "",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Starport - Orbis",
        MaxPad = "L", Prerequisites = "", T2 = "", T3 = "-6",
        Security = -3, TechLevel = 7, Wealth = 8,
        StandardOfLiving = 5, DevelopmentLevel = 9,
        FacilityEconomy = "Colony", EconomyInfluence = "",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Outpost - Commercial",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = -1, TechLevel = 0, Wealth = 3,
        StandardOfLiving = 5, DevelopmentLevel = 0,
        FacilityEconomy = "Colony", EconomyInfluence = "",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Outpost - Industrial",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 3, Wealth = 0,
        StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "Industrial", EconomyInfluence = "Industrial",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Outpost - Criminal",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = -2, TechLevel = 0, Wealth = 3,
        StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Contraband", EconomyInfluence = "Contraband",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Outpost - Civilian",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = -1, TechLevel = 1, Wealth = 2,
        StandardOfLiving = 1, DevelopmentLevel = 0,
        FacilityEconomy = "Colony", EconomyInfluence = "",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Outpost - Scientific",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 3, Wealth = 0,
        StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Outpost - Military",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = 2, TechLevel = 0, Wealth = 0,
        StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Military", EconomyInfluence = "Military",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Satellite",
        MaxPad = "❌", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 0,
        StandardOfLiving = 1, DevelopmentLevel = 2,
        FacilityEconomy = "", EconomyInfluence = "",
        InitPopInc = 1, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Comm Station",
        MaxPad = "❌", Prerequisites = "", T2 = "1", T3 = "",
        Security = 1, TechLevel = 3, Wealth = 0,
        StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "", EconomyInfluence = "",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Space Farm",
        MaxPad = "❌", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 0,
        StandardOfLiving = 5, DevelopmentLevel = 1,
        FacilityEconomy = "Agricultural", EconomyInfluence = "Agricultural",
        InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
    Name = "Orbital - Installation - Pirate Base",
    MaxPad = "❌", Prerequisites = "", T2 = "1", T3 = "",
    Security = -4, TechLevel = 0, Wealth = 4, StandardOfLiving = 0, DevelopmentLevel = 0,
    FacilityEconomy = "Contraband", EconomyInfluence = "Contraband", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Mining Outpost",
        MaxPad = "❌", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 4, StandardOfLiving = -2, DevelopmentLevel = 0,
        FacilityEconomy = "Extraction", EconomyInfluence = "Extraction", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Relay Station",
        MaxPad = "❌", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 1, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 1,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Military",
        MaxPad = "❌", Prerequisites = "Settlement - Military", T2 = "-1", T3 = "1",
        Security = 7, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Military", EconomyInfluence = "Military", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Security Station",
        MaxPad = "❌", Prerequisites = "Installation - Relay Station", T2 = "-1", T3 = "1",
        Security = 9, TechLevel = 0, Wealth = 0, StandardOfLiving = 3, DevelopmentLevel = 3,
        FacilityEconomy = "Military", EconomyInfluence = "Military", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Government",
        MaxPad = "❌", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = 2, TechLevel = 0, Wealth = 0, StandardOfLiving = 7, DevelopmentLevel = 3,
        FacilityEconomy = "", EconomyInfluence = "", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Medical",
        MaxPad = "❌", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = 0, TechLevel = 3, Wealth = 0, StandardOfLiving = 5, DevelopmentLevel = 0,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Research Station",
        MaxPad = "❌", Prerequisites = "Settlement - Research Bio", T2 = "-1", T3 = "1",
        Security = 0, TechLevel = 8, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Tourist",
        MaxPad = "❌", Prerequisites = "Satellite ➔ Tourism Settlement", T2 = "-1", T3 = "1",
        Security = -3, TechLevel = 0, Wealth = 6, StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "Tourism", EconomyInfluence = "Tourism", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Orbital - Installation - Space Bar",
        MaxPad = "❌", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = -2, TechLevel = 0, Wealth = 3, StandardOfLiving = 3, DevelopmentLevel = 0,
        FacilityEconomy = "Tourism", EconomyInfluence = "Tourism", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Planetary Port - Outpost Civilian",
        MaxPad = "L", Prerequisites = "", T2 = "1", T3 = "",
        Security = -2, TechLevel = 0, Wealth = 0, StandardOfLiving = 3, DevelopmentLevel = 0,
        FacilityEconomy = "Colony", EconomyInfluence = "", InitPopInc = 2, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Planetary Port - Outpost Industrial",
        MaxPad = "L", Prerequisites = "", T2 = "1", T3 = "",
        Security = -1, TechLevel = 0, Wealth = 3, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Industrial", EconomyInfluence = "Industrial", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Planetary Port - Outpost Scientific",
        MaxPad = "L", Prerequisites = "", T2 = "1", T3 = "",
        Security = -1, TechLevel = 5, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 1,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Planetary Port - Port",
        MaxPad = "L", Prerequisites = "", T2 = "", T3 = "-6",
        Security = -3, TechLevel = 5, Wealth = 5, StandardOfLiving = 7, DevelopmentLevel = 10,
        FacilityEconomy = "Colony", EconomyInfluence = "", InitPopInc = 10, MaxPopInc = 10
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Agriculture T1 S",
        MaxPad = "S", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 0, StandardOfLiving = 3, DevelopmentLevel = 0,
        FacilityEconomy = "Agricultural", EconomyInfluence = "Agricultural", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Agriculture T1 M",
        MaxPad = "S/L", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 0, StandardOfLiving = 7, DevelopmentLevel = 0,
        FacilityEconomy = "Agricultural", EconomyInfluence = "Agricultural", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Agriculture T2 L",
        MaxPad = "L", Prerequisites = "", T2 = "-1", T3 = "2",
        Security = 0, TechLevel = 0, Wealth = 0, StandardOfLiving = 10, DevelopmentLevel = 0,
        FacilityEconomy = "Agricultural", EconomyInfluence = "Agricultural", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
    Name = "Surface - Settlement - Extraction T1 S",
    MaxPad = "S", Prerequisites = "", T2 = "1", T3 = "",
    Security = 0, TechLevel = 0, Wealth = 3, StandardOfLiving = 0, DevelopmentLevel = 0,
    FacilityEconomy = "Extraction", EconomyInfluence = "Extraction", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Extraction T1 M",
        MaxPad = "M/L", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 5, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Extraction", EconomyInfluence = "Extraction", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Extraction T2 L",
        MaxPad = "L", Prerequisites = "", T2 = "-1", T3 = "2",
        Security = 2, TechLevel = 8, Wealth = -2, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Extraction", EconomyInfluence = "Extraction", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Industrial T1 S",
        MaxPad = "S", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "Industrial", EconomyInfluence = "Industrial", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Industrial T1 M",
        MaxPad = "M/L", Prerequisites = "", T2 = "1", T3 = "",
        Security = 0, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 6,
        FacilityEconomy = "Industrial", EconomyInfluence = "Industrial", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Industrial T2 L",
        MaxPad = "L", Prerequisites = "", T2 = "-1", T3 = "2",
        Security = 0, TechLevel = 3, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 9,
        FacilityEconomy = "Industrial", EconomyInfluence = "Industrial", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Military T1 S",
        MaxPad = "M", Prerequisites = "", T2 = "1", T3 = "",
        Security = 2, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Military", EconomyInfluence = "Military", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Military T1 M",
        MaxPad = "S/M", Prerequisites = "", T2 = "1", T3 = "",
        Security = 4, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Military", EconomyInfluence = "Military", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Military T2 L",
        MaxPad = "L", Prerequisites = "", T2 = "-1", T3 = "2",
        Security = 7, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "Military", EconomyInfluence = "Military", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Research Bio T1 S",
        MaxPad = "S", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = 3, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 1,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Research Bio T1 M",
        MaxPad = "S", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = 7, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 1,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Research Bio T2 L",
        MaxPad = "L", Prerequisites = "", T2 = "-1", T3 = "2",
        Security = 10, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Tourism T1 S",
        MaxPad = "M", Prerequisites = "Installation - Satellite", T2 = "-1", T3 = "1",
        Security = -1, TechLevel = 0, Wealth = 1, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Tourism", EconomyInfluence = "Tourism", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Tourism T1 M",
        MaxPad = "L", Prerequisites = "Installation - Satellite", T2 = "-1", T3 = "1",
        Security = -1, TechLevel = 0, Wealth = 3, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Tourism", EconomyInfluence = "Tourism", InitPopInc = 1, MaxPopInc = 1
    },
    new ColonyStructure {
        Name = "Surface - Settlement - Tourism T2 L",
        MaxPad = "L", Prerequisites = "Installation - Satellite", T2 = "-1", T3 = "2",
        Security = -2, TechLevel = 0, Wealth = 5, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Tourism", EconomyInfluence = "Tourism", InitPopInc = 1, MaxPopInc = 1
    },new ColonyStructure {
    Name = "Surface - Hub - Extraction",
    MaxPad = "❌", Prerequisites = "Settlement - Extraction", T2 = "-1", T3 = "1",
    Security = 0, TechLevel = 0, Wealth = 10, StandardOfLiving = -4, DevelopmentLevel = 3,
    FacilityEconomy = "Extraction", EconomyInfluence = "Extraction", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Civilian",
        MaxPad = "❌", Prerequisites = "Settlement - Agriculture", T2 = "-1", T3 = "1",
        Security = -3, TechLevel = 0, Wealth = 0, StandardOfLiving = 3, DevelopmentLevel = 3,
        FacilityEconomy = "", EconomyInfluence = "", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Exploration",
        MaxPad = "❌", Prerequisites = "Installation - Comm Station", T2 = "-1", T3 = "1",
        Security = -1, TechLevel = 7, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 3,
        FacilityEconomy = "Tourism", EconomyInfluence = "Tourism", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Outpost",
        MaxPad = "❌", Prerequisites = "Installation - Space Farm", T2 = "-1", T3 = "1",
        Security = -2, TechLevel = 0, Wealth = 0, StandardOfLiving = 3, DevelopmentLevel = 3,
        FacilityEconomy = "", EconomyInfluence = "", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Scientific",
        MaxPad = "❌", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = 0, TechLevel = 10, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Military",
        MaxPad = "❌", Prerequisites = "Mil. Settlement ➔ Mil. Installation", T2 = "-1", T3 = "1",
        Security = 10, TechLevel = 0, Wealth = 0, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "Military", EconomyInfluence = "Military", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Refinery",
        MaxPad = "❌", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = -1, TechLevel = 3, Wealth = 5, StandardOfLiving = -2, DevelopmentLevel = 7,
        FacilityEconomy = "", EconomyInfluence = "Refinery", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - High Tech",
        MaxPad = "❌", Prerequisites = "", T2 = "-1", T3 = "1",
        Security = -2, TechLevel = 10, Wealth = 3, StandardOfLiving = 0, DevelopmentLevel = 0,
        FacilityEconomy = "High Tech", EconomyInfluence = "High Tech", InitPopInc = 0, MaxPopInc = 0
    },
    new ColonyStructure {
        Name = "Surface - Hub - Industrial",
        MaxPad = "❌", Prerequisites = "Installation - Mining Outpost", T2 = "-1", T3 = "1",
        Security = 3, TechLevel = 5, Wealth = -4, StandardOfLiving = 3, DevelopmentLevel = 0,
        FacilityEconomy = "Industrial", EconomyInfluence = "Industrial", InitPopInc = 0, MaxPopInc = 0
    },




            };
        }

        private void AddStructureRow_Click(object sender, RoutedEventArgs e)
        {
            AddStructureRow();
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            string name = ProjectNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please enter a project/system name.", "Missing Name");
                return;
            }

            List<ColonyProject> allProjects = File.Exists(savePath)
                ? Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColonyProject>>(File.ReadAllText(savePath))
                : new();

            allProjects.RemoveAll(p => p.SystemName == name);

            var project = new ColonyProject
            {
                SystemName = name,
                SelectedStructures = StructureSelectorPanel.Children
                    .OfType<Grid>()
                    .Select(g =>
                    {
                        var cb = g.Children.OfType<ComboBox>().FirstOrDefault();
                        var check = g.Children.OfType<CheckBox>().FirstOrDefault();
                        return new StructureSelection
                        {
                            StructureName = (cb?.SelectedItem as ColonyStructure)?.Name,
                            IsBuilt = check?.IsChecked == true
                        };
                    })
                    .Where(s => !string.IsNullOrEmpty(s.StructureName))
                    .ToList()
            };

            allProjects.Add(project);

            File.WriteAllText(savePath, Newtonsoft.Json.JsonConvert.SerializeObject(allProjects, Newtonsoft.Json.Formatting.Indented));
            MessageBox.Show($"Project '{name}' saved!", "Success");
            RefreshProjectDropdown();
        }


        private void LoadProject_Click(object sender, RoutedEventArgs e)
        {
            string name = ProjectDropdown.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please select a saved project to load.", "Missing Selection");
                return;
            }

            var allProjects = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColonyProject>>(File.ReadAllText(savePath));
            var project = allProjects?.FirstOrDefault(p => p.SystemName == name);

            if (project == null)
            {
                MessageBox.Show($"Project '{name}' not found.", "Error");
                return;
            }

            StructureSelectorPanel.Children.Clear();
            comboBoxes.Clear();

            foreach (var s in project.SelectedStructures)
                AddStructureRow(s.StructureName, s.IsBuilt);

            UpdateTotals();
        }

        private void DeleteProject_Click(object sender, RoutedEventArgs e)
        {
            string selectedName = ProjectDropdown.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                MessageBox.Show("No project selected to delete.", "Warning");
                return;
            }

            if (!File.Exists(savePath)) return;

            var allProjects = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColonyProject>>(File.ReadAllText(savePath));

            var remaining = allProjects.Where(p => p.SystemName != selectedName).ToList();

            var result = MessageBox.Show($"Are you sure you want to delete the project \"{selectedName}\"?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                File.WriteAllText(savePath, Newtonsoft.Json.JsonConvert.SerializeObject(remaining, Formatting.Indented));
                RefreshProjectDropdown();
                ProjectDropdown.SelectedIndex = -1;
                ProjectNameBox.Text = "";
                StructureSelectorPanel.Children.Clear();
                UpdateTotals();

                MessageBox.Show("Project deleted successfully.", "Deleted");
            }
        }


        private void AddStructureRow(string selectedName = null, bool built = false)
        {
            var rowGrid = new Grid { Margin = new Thickness(2) };
            for (int i = 0; i < 16; i++)
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { SharedSizeGroup = $"col{i}", Width = GridLength.Auto });

            var cb = new ComboBox
            {
                ItemsSource = allStructures,
                DisplayMemberPath = "Name",
                Margin = new Thickness(5),
                Width = 240
            };

            Grid.SetColumn(cb, 0);
            rowGrid.Children.Add(cb);
            comboBoxes.Add(cb);

            for (int i = 1; i <= 13; i++)
            {
                var tb = new TextBlock
                {
                    Foreground = Brushes.White,
                    Margin = new Thickness(5),
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetColumn(tb, i);
                rowGrid.Children.Add(tb);
            }

            var builtBox = new CheckBox
            {
                Margin = new Thickness(5),
                IsChecked = built
            };
            Grid.SetColumn(builtBox, 14);
            rowGrid.Children.Add(builtBox);

            var removeBtn = new Button { Content = "❌", Margin = new Thickness(5) };
            removeBtn.Click += (s, e) =>
            {
                StructureSelectorPanel.Children.Remove(rowGrid);
                comboBoxes.Remove(cb);
                UpdateTotals();
            };
            Grid.SetColumn(removeBtn, 15);
            rowGrid.Children.Add(removeBtn);

            // ✅ Add to panel BEFORE triggering selection update
            StructureSelectorPanel.Children.Add(rowGrid);

            // ✅ Set selection AFTER adding row to visual tree
            if (selectedName != null)
            {
                cb.SelectedItem = allStructures.FirstOrDefault(s => s.Name == selectedName);
                StructureComboBox_SelectionChanged(cb, null); // triggers text + colors
            }

            cb.SelectionChanged += StructureComboBox_SelectionChanged;
        }

        private void SaveProject(string systemName)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));

            List<ColonyProject> allProjects = new();

            if (File.Exists(savePath))
            {
                string json = File.ReadAllText(savePath);
                allProjects = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColonyProject>>(json) ?? new();
            }

            // Remove any existing project with this name
            allProjects.RemoveAll(p => p.SystemName == systemName);

            // Add current selection
            var project = new ColonyProject
            {
                SystemName = systemName,
                SelectedStructures = StructureSelectorPanel.Children
                    .OfType<Grid>()
                    .Select(g =>
                    {
                        var cb = g.Children.OfType<ComboBox>().FirstOrDefault();
                        var check = g.Children.OfType<CheckBox>().FirstOrDefault();
                        return new StructureSelection
                        {
                            StructureName = (cb?.SelectedItem as ColonyStructure)?.Name,
                            IsBuilt = check?.IsChecked == true
                        };
                    })
                    .Where(s => s.StructureName != null)
                    .ToList()
            };

            allProjects.Add(project);
            File.WriteAllText(savePath, Newtonsoft.Json.JsonConvert.SerializeObject(allProjects, Newtonsoft.Json.Formatting.Indented));

            MessageBox.Show($"Saved project: {systemName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadProject(string systemName)
        {
            if (!File.Exists(savePath)) return;

            string json = File.ReadAllText(savePath);
            var allProjects = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColonyProject>>(json) ?? new();

            var project = allProjects.FirstOrDefault(p => p.SystemName == systemName);
            if (project == null)
            {
                MessageBox.Show($"No saved project named: {systemName}");
                return;
            }

            StructureSelectorPanel.Children.Clear();
            comboBoxes.Clear();

            foreach (var s in project.SelectedStructures)
            {
                AddStructureRow(s.StructureName, s.IsBuilt);
            }

            UpdateTotals();
        }

        private void StructureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cb || cb.SelectedItem is not ColonyStructure s || cb.Parent is not Grid grid) return;

            string[] values =
            {
                s.MaxPad, s.Prerequisites, s.T2, s.T3,
                s.Security.ToString(), s.TechLevel.ToString(), s.Wealth.ToString(),
                s.StandardOfLiving.ToString(), s.DevelopmentLevel.ToString(),
                s.FacilityEconomy, s.EconomyInfluence,
                s.InitPopInc.ToString(), s.MaxPopInc.ToString()
            };

            for (int i = 1; i <= 13; i++)
            {
                if (grid.Children[i] is TextBlock tb)
                {
                    tb.Text = values[i - 1];
                    tb.Background = GetCellColor(i, values[i - 1]);
                }
            }

            UpdateTotals();
        }

        private Brush GetCellColor(int column, string value)
        {
            if (!int.TryParse(value, out int v)) return Brushes.Transparent;

            // Only apply color to numeric stat columns (Security, Tech, Wealth, SoL, Dev)
            if (column < 5 || column > 9) return Brushes.Transparent;

            return v switch
            {
                <= -4 => new SolidColorBrush(Color.FromRgb(128, 0, 0)),
                -3 => new SolidColorBrush(Color.FromRgb(160, 30, 30)),
                -2 => new SolidColorBrush(Color.FromRgb(190, 60, 60)),
                -1 => new SolidColorBrush(Color.FromRgb(220, 90, 90)),
                0 => new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                1 => new SolidColorBrush(Color.FromRgb(50, 100, 50)),
                2 => new SolidColorBrush(Color.FromRgb(60, 140, 60)),
                3 => new SolidColorBrush(Color.FromRgb(70, 170, 70)),
                4 => new SolidColorBrush(Color.FromRgb(90, 200, 90)),
                _ => new SolidColorBrush(Color.FromRgb(110, 230, 110))
            };
        }

        private void UpdateTotals()
        {
            int sec = 0, tech = 0, wealth = 0, sol = 0, dev = 0;

            foreach (var cb in comboBoxes)
            {
                if (cb.SelectedItem is ColonyStructure s)
                {
                    sec += s.Security;
                    tech += s.TechLevel;
                    wealth += s.Wealth;
                    sol += s.StandardOfLiving;
                    dev += s.DevelopmentLevel;
                }
            }

            TotalSecurity.Text = $"Security: {sec}";
            TotalTech.Text = $"Tech: {tech}";
            TotalWealth.Text = $"Wealth: {wealth}";
            TotalSoL.Text = $"SoL: {sol}";
            TotalDev.Text = $"Dev: {dev}";
        }
    }
}
