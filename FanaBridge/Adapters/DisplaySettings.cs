using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Display configuration: a unified stack of display layers and display options.
    /// Position in the list = priority (top wins).
    /// </summary>
    public class DisplaySettings : INotifyPropertyChanged
    {
        private int _scrollSpeedMs = 250;

        /// <summary>
        /// Ordered display layer stack. Evaluated top-to-bottom; first active layer wins.
        /// Constant layers at the bottom serve as the base display and cycle among themselves.
        /// </summary>
        public ObservableCollection<DisplayLayer> Layers { get; set; }
            = new ObservableCollection<DisplayLayer>();

        /// <summary>Scroll speed for long text in milliseconds per step (50-1000).</summary>
        public int ScrollSpeedMs
        {
            get => _scrollSpeedMs;
            set { if (_scrollSpeedMs != value) { _scrollSpeedMs = System.Math.Max(50, System.Math.Min(1000, value)); OnPropertyChanged(); } }
        }

        /// <summary>Creates default settings with a sensible layer stack.</summary>
        public static DisplaySettings CreateDefault()
        {
            var settings = new DisplaySettings();
            foreach (var layer in LayerCatalog.DefaultLayers())
                settings.Layers.Add(layer);
            return settings;
        }

        /// <summary>
        /// Migrates a legacy "displayMode" string to the new layer model.
        /// </summary>
        public static DisplaySettings MigrateFromLegacy(string displayMode)
        {
            var settings = CreateDefault();

            // Disable all layers first, then enable what the legacy mode implies
            foreach (var layer in settings.Layers)
                layer.IsEnabled = false;

            switch (displayMode)
            {
                case "Speed":
                    // Replace gear base with speed
                    EnableOrAdd(settings, "SpeedKmh", DisplayLayerMode.Constant);
                    break;

                case "GearAndSpeed":
                    EnableOrAdd(settings, "SpeedKmh", DisplayLayerMode.Constant);
                    EnableLayer(settings, "GearChange");
                    break;

                case "Gear":
                default:
                    EnableLayer(settings, "Gear");
                    break;
            }

            return settings;
        }

        private static void EnableLayer(DisplaySettings settings, string key)
        {
            foreach (var l in settings.Layers)
                if (l.CatalogKey == key) { l.IsEnabled = true; return; }
        }

        private static void EnableOrAdd(DisplaySettings settings, string key, DisplayLayerMode mode)
        {
            foreach (var l in settings.Layers)
                if (l.CatalogKey == key) { l.IsEnabled = true; return; }

            var layer = LayerCatalog.CreateFromCatalog(key);
            if (layer != null)
            {
                layer.IsEnabled = true;
                settings.Layers.Add(layer);
            }
        }

        [field: System.NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
