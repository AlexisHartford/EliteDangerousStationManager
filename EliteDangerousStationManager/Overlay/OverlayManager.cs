using System.Collections.ObjectModel;
using System.Windows;
using EliteDangerousStationManager.Models;

namespace EliteDangerousStationManager.Overlay
{
    public class OverlayManager
    {
        private OverlayWindow _overlay;

        public void ShowOverlay(ObservableCollection<ProjectMaterial> materials)
        {
            if (_overlay == null || !_overlay.IsLoaded)
            {
                _overlay = new OverlayWindow();
                _overlay.Show();
            }

            _overlay.OverlayMaterialsList.ItemsSource = materials;
        }

        public void CloseOverlay()
        {
            if (_overlay != null && _overlay.IsLoaded)
            {
                _overlay.Close();
                _overlay = null;
            }
        }
    }
}