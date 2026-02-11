using System.ComponentModel;
using static EliteDangerousStationManager.MainWindow;

namespace EliteDangerousStationManager.Models
{
    public class ProjectMaterial
    {
        public string Material { get; set; }
        public int Required { get; set; }
        public int Provided { get; set; }
        public int Needed { get; set; }
        public string Category { get; set; } = "Other";
        // --- If you already have INotifyPropertyChanged implemented, keep yours and remove the duplicate below ---
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ✅ Per-carrier amounts used by the dynamic DataGrid columns:
        private Dictionary<string, int> _carrierAmounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> CarrierAmounts
        {
            get => _carrierAmounts;
            set
            {
                _carrierAmounts = value ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                OnPropertyChanged(nameof(CarrierAmounts));
                OnPropertyChanged(nameof(TotalOnCarriers));
                OnPropertyChanged(nameof(RemainingAfterCarriers));
            }
        }

        // ✅ Sum of all linked carriers for this material:
        public int TotalOnCarriers => CarrierAmounts?.Values.Sum() ?? 0;

        // ✅ How much is still needed after subtracting carrier totals:
        //     Requires you already have `Needed` on this class.
        public int RemainingAfterCarriers
            => Math.Max((int)(Needed), 0) - Math.Max(TotalOnCarriers, 0);

        // ✅ Convenience method the UI code calls to replace the dictionary
        //    and raise the correct notifications:
        public void SetCarrierAmounts(Dictionary<string, int> newMap)
        {
            CarrierAmounts = newMap ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        public int CategoryOrderIndex =>
    EliteDangerousStationManager.MainWindow.MaterialCategories.GetOrderIndex(Category);


    }


}