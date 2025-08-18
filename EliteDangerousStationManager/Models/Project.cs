using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EliteDangerousStationManager.Models
{
    public class Project : INotifyPropertyChanged
    {
        public string Source { get; set; } // "Local" or "Server"

        public long MarketId { get; set; }
        public string SystemName { get; set; }
        public string StationName { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected != value)
                {
                    isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override string ToString() => $"{SystemName} / {StationName} (MarketID: {MarketId})";
    }
}
