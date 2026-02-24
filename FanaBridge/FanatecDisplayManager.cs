using GameReaderCommon;
using Newtonsoft.Json.Linq;
using System;

namespace FanaBridge
{
    /// <summary>
    /// Maps telemetry data to the Fanatec 3-digit 7-segment display.
    /// Supports gear, speed, and mixed display modes.
    ///
    /// Settings are read from a JObject so this can be owned by either the
    /// plugin or a DeviceInstance — no dependency on FanatecPluginSettings.
    /// </summary>
    public class FanatecDisplayManager
    {
        private readonly FanatecDevice _device;
        private JObject _settings;

        private string _currentText = "";
        private string _currentGear = "";

        // Rate limiter: skip display writes if value hasn't changed
        private int _lastSentGear = int.MinValue;
        private int _lastSentSpeed = int.MinValue;
        private string _lastDisplayMode;

        public FanatecDisplayManager(FanatecDevice device, JObject settings)
        {
            _device = device;
            _settings = settings ?? new JObject();
        }

        /// <summary>
        /// Replaces the settings JObject (e.g. after SetSettings in the DeviceInstance).
        /// </summary>
        public void UpdateSettings(JObject settings)
        {
            _settings = settings ?? new JObject();
        }

        /// <summary>The current display mode string ("Gear", "Speed", "GearAndSpeed").</summary>
        public string DisplayMode
        {
            get { return (string)_settings["displayMode"] ?? "Gear"; }
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
            _device.ClearDisplay();
            _currentText = "";
            _currentGear = "";
            _lastSentGear = int.MinValue;
            _lastSentSpeed = int.MinValue;
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

            _device.DisplayGear(gear);
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

            _device.DisplaySpeed(speed);
            _lastSentSpeed = speed;
            _lastDisplayMode = "Speed";
            _currentText = speed.ToString();
        }

        private void UpdateGearAndSpeed(GameData data)
        {
            // Show gear in the center digit, speed scrolls on edges when available
            // Simple compromise: show gear primarily, speed when stationary or in pit
            bool inPit = data.NewData.IsInPitLane != 0;
            int speed = (int)Math.Round(data.NewData.SpeedKmh);

            if (inPit || speed < 5)
            {
                // Show speed when in pit or nearly stopped
                if (speed != _lastSentSpeed || _lastDisplayMode != "GearSpeed_Speed")
                {
                    _device.DisplaySpeed(speed);
                    _lastSentSpeed = speed;
                    _lastDisplayMode = "GearSpeed_Speed";
                    _currentText = speed.ToString();
                }
            }
            else
            {
                // Show gear when driving
                string gearStr = data.NewData.Gear;
                int gear = ParseGear(gearStr);

                if (gear != _lastSentGear || _lastDisplayMode != "GearSpeed_Gear")
                {
                    _device.DisplayGear(gear);
                    _lastSentGear = gear;
                    _lastDisplayMode = "GearSpeed_Gear";
                    _currentGear = GearToString(gear);
                    _currentText = _currentGear;
                }
            }
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
