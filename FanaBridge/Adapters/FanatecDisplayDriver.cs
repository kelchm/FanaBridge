using FanaBridge.Protocol;
using GameReaderCommon;
using System;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Maps telemetry data to the Fanatec 3-digit 7-segment display.
    /// Supports gear, speed, and mixed display modes.
    ///
    /// Settings are read from a JObject so this can be owned by either the
    /// plugin or a DeviceInstance — no dependency on FanatecPluginSettings.
    /// </summary>
    public class FanatecDisplayDriver
    {
        private readonly DisplayEncoder _display;
        private DisplaySettings _settings;

        private string _currentText = "";
        private string _currentGear = "";

        // Rate limiter: skip display writes if value hasn't changed
        private int _lastSentGear = int.MinValue;
        private int _lastSentSpeed = int.MinValue;
        private string _lastDisplayMode;

        // GearAndSpeed overlay: show gear for a brief period after each gear change
        // TODO: make duration configurable; revisit with a proper implementation
        private static readonly TimeSpan GearOverlayDuration = TimeSpan.FromSeconds(2);
        private int _lastKnownGear = int.MinValue;
        private DateTime _gearOverlayUntil = DateTime.MinValue;

        // GearUpshiftBrackets: bracket state
        private bool _lastBracketsShown;

        public FanatecDisplayDriver(DisplayEncoder display, DisplaySettings settings)
        {
            _display = display;
            _settings = settings ?? new DisplaySettings();
        }

        /// <summary>
        /// Replaces the settings (e.g. after SetSettings in the DeviceInstance).
        /// </summary>
        public void UpdateSettings(DisplaySettings settings)
        {
            _settings = settings ?? new DisplaySettings();
        }

        /// <summary>The current display mode string ("Gear", "Speed", "GearAndSpeed", "GearUpshiftBrackets").</summary>
        public string DisplayMode
        {
            get { return _settings.DisplayMode ?? DisplaySettings.DefaultMode; }
        }

        /// <summary>Current displayed text (for SimHub properties).</summary>
        public string CurrentText { get { return _currentText; } }

        /// <summary>Current displayed gear string (for SimHub properties).</summary>
        public string CurrentGear { get { return _currentGear; } }

        /// <summary>
        /// Updates the display from telemetry. Called once per frame.
        /// </summary>
        public void Update(GameData data)
        {
            if (data.NewData == null) return;

            string mode = DisplayMode;

            switch (mode)
            {
                case "Speed":
                    UpdateSpeed(data);
                    break;

                case "GearAndSpeed":
                    UpdateGearAndSpeed(data);
                    break;

                case "GearUpshiftBrackets":
                    UpdateGearUpshiftBrackets(data);
                    break;

                case "Gear":
                default:
                    UpdateGear(data);
                    break;
            }
        }

        /// <summary>
        /// Blanks the display and resets cached state.
        /// </summary>
        public void Clear()
        {
            _display.ClearDisplay();
            _currentText = "";
            _currentGear = "";
            _lastSentGear = int.MinValue;
            _lastSentSpeed = int.MinValue;
            _lastKnownGear = int.MinValue;
            _gearOverlayUntil = DateTime.MinValue;
            _lastBracketsShown = false;
        }

        // =====================================================================
        // DISPLAY MODES
        // =====================================================================

        private void UpdateGear(GameData data)
        {
            string gearStr = data.NewData.Gear;
            int gear = ParseGear(gearStr);

            if (gear == _lastSentGear && _lastDisplayMode == "Gear")
                return;

            _display.DisplayGear(gear);
            _lastSentGear = gear;
            _lastDisplayMode = "Gear";
            _currentGear = GearToString(gear);
            _currentText = _currentGear;
        }

        private void UpdateSpeed(GameData data)
        {
            int speed = (int)Math.Round(data.NewData.SpeedKmh);
            if (speed < 0) speed = 0;
            if (speed > 999) speed = 999;

            if (speed == _lastSentSpeed && _lastDisplayMode == "Speed")
                return;

            _display.DisplaySpeed(speed);
            _lastSentSpeed = speed;
            _lastDisplayMode = "Speed";
            _currentText = speed.ToString();
        }

        private void UpdateGearAndSpeed(GameData data)
        {
            string gearStr = data.NewData.Gear;
            int gear = ParseGear(gearStr);
            int speed = (int)Math.Round(data.NewData.SpeedKmh);
            if (speed < 0) speed = 0;
            if (speed > 999) speed = 999;

            // Trigger the gear overlay whenever the gear changes
            if (gear != _lastKnownGear)
            {
                _lastKnownGear = gear;
                _gearOverlayUntil = DateTime.UtcNow + GearOverlayDuration;
            }

            if (DateTime.UtcNow < _gearOverlayUntil)
            {
                // Show gear as a temporary overlay after a gear change
                if (gear != _lastSentGear || _lastDisplayMode != "GearSpeed_Gear")
                {
                    _display.DisplayGear(gear);
                    _lastSentGear = gear;
                    _lastDisplayMode = "GearSpeed_Gear";
                    _currentGear = GearToString(gear);
                    _currentText = _currentGear;
                }
            }
            else
            {
                // Default: show speed
                if (speed != _lastSentSpeed || _lastDisplayMode != "GearSpeed_Speed")
                {
                    _display.DisplaySpeed(speed);
                    _lastSentSpeed = speed;
                    _lastDisplayMode = "GearSpeed_Speed";
                    _currentText = speed.ToString();
                }
            }
        }

        private void UpdateGearUpshiftBrackets(GameData data)
        {
            string gearStr = data.NewData.Gear;
            int gear = ParseGear(gearStr);

            bool showBrackets = data.NewData.Rpms > 0
                && data.NewData.CarSettings_RPMRedLineReached > 0;

            // Rate-limit: only write to the display when something changed
            if (gear == _lastSentGear && showBrackets == _lastBracketsShown && _lastDisplayMode == "GearUpshiftBrackets")
                return;

            _display.DisplayGearBracketed(gear, showBrackets);
            _lastSentGear      = gear;
            _lastBracketsShown = showBrackets;
            _lastDisplayMode   = "GearUpshiftBrackets";
            _currentGear       = GearToString(gear);
            _currentText       = showBrackets ? "[" + _currentGear + "]" : _currentGear;
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        /// <summary>
        /// Parses SimHub gear string to an integer: "R"=-1, "N"=0, "1"-"9"=1-9.
        /// </summary>
        private static int ParseGear(string gear)
        {
            if (string.IsNullOrEmpty(gear)) return 0;

            gear = gear.Trim().ToUpperInvariant();

            if (gear == "R" || gear == "REVERSE") return -1;
            if (gear == "N" || gear == "NEUTRAL") return 0;

            int result;
            if (int.TryParse(gear, out result))
            {
                return result;
            }

            return 0;
        }

        private static string GearToString(int gear)
        {
            if (gear == -1) return "R";
            if (gear == 0) return "N";
            return gear.ToString();
        }
    }
}
