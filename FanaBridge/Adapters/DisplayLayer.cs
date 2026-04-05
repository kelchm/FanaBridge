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

        /// <summary>Active when expression returns non-empty. Expression controls both activation and display.</summary>
        Expression,
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

        /// <summary>Show the result of an NCalc/JavaScript expression.</summary>
        Expression,
    }

    /// <summary>
    /// How a property value is formatted for the 3-character 7-segment display.
    /// Also determines default alignment and scroll behavior.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DisplayFormat
    {
        /// <summary>Rounded integer, right-aligned, no scroll. E.g. speed, lap, position.</summary>
        Number,

        /// <summary>One decimal place, right-aligned, no scroll. E.g. fuel %.</summary>
        Decimal,

        /// <summary>Time as ss.f, right-aligned, no scroll. E.g. lap times.</summary>
        Time,

        /// <summary>Gear mapping (R/N/1/2...), centered, no scroll.</summary>
        Gear,

        /// <summary>Raw toString, left-aligned, scrolls if &gt;3 chars.</summary>
        Text,
    }

    /// <summary>
    /// What happens when formatted text exceeds the 3-segment display capacity.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OverflowStrategy
    {
        /// <summary>Resolve automatically based on DisplayFormat: Text scrolls, Time truncates left, others truncate right.</summary>
        Auto,

        /// <summary>Text scrolls across the display with configurable speed.</summary>
        Scroll,

        /// <summary>Drop leftmost segments, keep rightmost. E.g. "1.05.3" becomes "05.3".</summary>
        TruncateLeft,

        /// <summary>Drop rightmost segments, keep leftmost. E.g. "1.05.3" becomes "1.05".</summary>
        TruncateRight,
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
        private DisplayFormat _displayFormat = DisplayFormat.Number;
        private string _timeFormat = @"ss\.f";
        private OverflowStrategy _overflow = OverflowStrategy.Auto;
        private string _fixedText = "";
        private string _expression = "";
        private int _scrollSpeedMs = 250;

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
            set { if (_catalogKey != value) { _catalogKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCustom)); } }
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
            set { if (_mode != value) { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeLabel)); OnPropertyChanged(nameof(TimingLabel)); } }
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

        /// <summary>How the value is formatted and aligned on the display.</summary>
        public DisplayFormat DisplayFormat
        {
            get => _displayFormat;
            set { if (_displayFormat != value) { _displayFormat = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsGearFormat)); } }
        }

        /// <summary>TimeSpan format string used when DisplayFormat is Time. Default "ss\.f".</summary>
        public string TimeFormat
        {
            get => _timeFormat;
            set { if (_timeFormat != value) { _timeFormat = value; OnPropertyChanged(); } }
        }

        /// <summary>What happens when content exceeds the 3-segment display. Default Auto.</summary>
        public OverflowStrategy Overflow
        {
            get => _overflow;
            set { if (_overflow != value) { _overflow = value; OnPropertyChanged(); } }
        }

        /// <summary>Fixed text when Source is FixedText.</summary>
        public string FixedText
        {
            get => _fixedText;
            set { if (_fixedText != value) { _fixedText = value; OnPropertyChanged(); } }
        }

        /// <summary>NCalc or JavaScript expression (when Source is Expression).</summary>
        public string Expression
        {
            get => _expression;
            set { if (_expression != value) { _expression = value; OnPropertyChanged(); } }
        }

        /// <summary>Scroll speed in ms per step (Text format only). 0 = use global setting.</summary>
        public int ScrollSpeedMs
        {
            get => _scrollSpeedMs;
            set { if (_scrollSpeedMs != value) { _scrollSpeedMs = value; OnPropertyChanged(); } }
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
            set { if (_durationMs != value) { _durationMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimingLabel)); } }
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
        public bool IsGearFormat => _displayFormat == DisplayFormat.Gear;

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
                    case DisplayLayerMode.Constant: return "ALWAYS";
                    case DisplayLayerMode.OnChange: return "ON CHANGE";
                    case DisplayLayerMode.WhileTrue: return "WHILE TRUE";
                    case DisplayLayerMode.Expression: return "EXPRESSION";
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
