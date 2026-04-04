using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// How a display layer decides when it is active.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DisplayLayerMode
    {
        /// <summary>Always active (base layer). Filtered by running/idle flags.</summary>
        Constant,

        /// <summary>Active for a fixed duration when a watched property changes.</summary>
        OnChange,

        /// <summary>Active while a condition property is truthy.</summary>
        WhileTrue,
    }

    /// <summary>
    /// How a layer obtains its display text.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DisplaySource
    {
        /// <summary>Show the value of a SimHub property.</summary>
        Property,

        /// <summary>Show a fixed string.</summary>
        FixedText,
    }

    /// <summary>
    /// A single layer in the display stack. Layers are evaluated top-to-bottom;
    /// the first active layer wins. Constant layers cycle among themselves.
    /// </summary>
    public class DisplayLayer : INotifyPropertyChanged
    {
        private string _name = "";
        private string _catalogKey;
        private bool _isEnabled = true;
        private DisplayLayerMode _mode = DisplayLayerMode.Constant;

        // Data source
        private DisplaySource _source = DisplaySource.Property;
        private string _propertyName = "";
        private string _format = "";
        private string _fixedText = "";
        private bool _centerDisplay;

        // Trigger (OnChange / WhileTrue)
        private string _watchProperty = "";
        private int _durationMs = 2000;

        // Visibility (Constant mode)
        private bool _showWhenRunning = true;
        private bool _showWhenIdle;

        /// <summary>User-visible name.</summary>
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        /// <summary>Key into <see cref="LayerCatalog"/>. Null for custom layers.</summary>
        public string CatalogKey
        {
            get => _catalogKey;
            set { if (_catalogKey != value) { _catalogKey = value; OnPropertyChanged(); } }
        }

        /// <summary>Master enable/disable.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        /// <summary>When this layer activates.</summary>
        public DisplayLayerMode Mode
        {
            get => _mode;
            set { if (_mode != value) { _mode = value; OnPropertyChanged(); } }
        }

        /// <summary>How the display value is obtained.</summary>
        public DisplaySource Source
        {
            get => _source;
            set { if (_source != value) { _source = value; OnPropertyChanged(); } }
        }

        /// <summary>SimHub property path for the display value.</summary>
        public string PropertyName
        {
            get => _propertyName;
            set { if (_propertyName != value) { _propertyName = value; OnPropertyChanged(); } }
        }

        /// <summary>Format string. "gear" for centered gear display.</summary>
        public string Format
        {
            get => _format;
            set { if (_format != value) { _format = value; OnPropertyChanged(); } }
        }

        /// <summary>Fixed text when Source is FixedText.</summary>
        public string FixedText
        {
            get => _fixedText;
            set { if (_fixedText != value) { _fixedText = value; OnPropertyChanged(); } }
        }

        /// <summary>Center the value on the 3-digit display.</summary>
        public bool CenterDisplay
        {
            get => _centerDisplay;
            set { if (_centerDisplay != value) { _centerDisplay = value; OnPropertyChanged(); } }
        }

        /// <summary>Property to watch for OnChange/WhileTrue triggers.</summary>
        public string WatchProperty
        {
            get => _watchProperty;
            set { if (_watchProperty != value) { _watchProperty = value; OnPropertyChanged(); } }
        }

        /// <summary>Duration in ms for OnChange layers.</summary>
        public int DurationMs
        {
            get => _durationMs;
            set { if (_durationMs != value) { _durationMs = value; OnPropertyChanged(); } }
        }

        /// <summary>Show when a game session is active (Constant mode).</summary>
        public bool ShowWhenRunning
        {
            get => _showWhenRunning;
            set { if (_showWhenRunning != value) { _showWhenRunning = value; OnPropertyChanged(); } }
        }

        /// <summary>Show when no game is running (Constant mode).</summary>
        public bool ShowWhenIdle
        {
            get => _showWhenIdle;
            set { if (_showWhenIdle != value) { _showWhenIdle = value; OnPropertyChanged(); } }
        }

        /// <summary>Whether this uses the special gear display.</summary>
        [JsonIgnore]
        public bool IsGearFormat =>
            string.Equals(Format, "gear", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether this is a user-created custom layer.</summary>
        [JsonIgnore]
        public bool IsCustom => string.IsNullOrEmpty(CatalogKey);

        /// <summary>Short description for the list UI.</summary>
        [JsonIgnore]
        public string ModeLabel
        {
            get
            {
                switch (Mode)
                {
                    case DisplayLayerMode.Constant: return "CONSTANT";
                    case DisplayLayerMode.OnChange: return "ON CHANGE";
                    case DisplayLayerMode.WhileTrue: return "WHILE TRUE";
                    default: return "";
                }
            }
        }

        /// <summary>Timing label for the list UI.</summary>
        [JsonIgnore]
        public string TimingLabel
        {
            get
            {
                if (Mode == DisplayLayerMode.OnChange)
                    return string.Format("{0:0.#}s", DurationMs / 1000.0);
                return "";
            }
        }

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
