using FanaBridge.Protocol;
using GameReaderCommon;
using System;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Maps telemetry data to the Fanatec 3-digit 7-segment display.
    /// Supports gear, speed, and mixed display modes.
    ///
    /// Driven by a typed <see cref="DisplaySettings"/> so this can be owned by
    /// either the plugin or a DeviceInstance — no dependency on FanatecPluginSettings.
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

        // True while a game is feeding telemetry (we own the display's content);
        // on the transition out, the display is blanked — retried until the write
        // is accepted — instead of holding the last value forever. SimHub keeps
        // the last telemetry values around after a game exits, so staleness can't
        // be inferred from the data itself.
        private bool _needExitBlank;

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
            // Mode "None": the display is off — the owner blanks it once on the
            // transition; never write here, not even the exit blank. Belt-and-braces
            // with the call-site gate, so "None" can never fall through to Gear.
            if (DisplayMode == DisplaySettings.ModeNone)
                return;

            bool telemetryLive = data != null && data.GameRunning && data.NewData != null;
            if (!telemetryLive)
            {
                // Game exited (or never started): blank once on the way out, then
                // write nothing while idle — the firmware may be using the display
                // itself (e.g. the tuning menu).
                if (_needExitBlank)
                    _needExitBlank = !Clear();
                return;
            }
            _needExitBlank = true;

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
        /// Blanks the display and resets cached state. Returns whether the
        /// blanking write reached the transport, so callers that latch a
        /// "cleared" state (e.g. the legacy-page blank) can retry a declined
        /// write instead of remembering a blank that never happened. The value
        /// latches are reset either way — the next successful write should
        /// never be suppressed by pre-clear state.
        /// </summary>
        public bool Clear()
        {
            bool sent = _display.ClearDisplay();
            _currentText = "";
            _currentGear = "";
            _lastSentGear = int.MinValue;
            _lastSentSpeed = int.MinValue;
            _lastKnownGear = int.MinValue;
            _gearOverlayUntil = DateTime.MinValue;
            _lastBracketsShown = false;
            return sent;
        }

        // =====================================================================
        // DISPLAY MODES
        // =====================================================================

        private void UpdateGear(GameData data)
        {
            int gear = ParseGear(data.NewData.Gear);

            if (gear == _lastSentGear && _lastDisplayMode == "Gear")
                return;

            ShowGear(gear, "Gear");
        }

        private void UpdateSpeed(GameData data)
        {
            int speed = ReadSpeed(data);

            if (speed == _lastSentSpeed && _lastDisplayMode == "Speed")
                return;

            ShowSpeed(speed, "Speed");
        }

        private void UpdateGearAndSpeed(GameData data)
        {
            int gear = ParseGear(data.NewData.Gear);
            int speed = ReadSpeed(data);

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
                    ShowGear(gear, "GearSpeed_Gear");
            }
            else
            {
                // Default: show speed
                if (speed != _lastSentSpeed || _lastDisplayMode != "GearSpeed_Speed")
                    ShowSpeed(speed, "GearSpeed_Speed");
            }
        }

        private void UpdateGearUpshiftBrackets(GameData data)
        {
            int gear = ParseGear(data.NewData.Gear);

            bool showBrackets = data.NewData.Rpms > 0
                && data.NewData.CarSettings_RPMRedLineReached > 0;

            // Rate-limit: only write to the display when something changed
            if (gear == _lastSentGear && showBrackets == _lastBracketsShown && _lastDisplayMode == "GearUpshiftBrackets")
                return;

            if (!_display.DisplayGear(gear, showBrackets))
                return;

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
        /// Reads the speed from telemetry (SpeedLocal honours the user's km/h
        /// vs mph choice in SimHub) and clamps it to the 3-digit range.
        /// </summary>
        private static int ReadSpeed(GameData data)
        {
            int speed = (int)Math.Round(data.NewData.SpeedLocal);
            if (speed < 0) speed = 0;
            if (speed > 999) speed = 999;
            return speed;
        }

        /// <summary>
        /// Writes a gear to the display and updates the cached state under the
        /// given display-mode tag.
        /// </summary>
        private void ShowGear(int gear, string mode)
        {
            // Only latch the rate-limiter state when the write actually reached
            // the transport — a declined send must be retried next frame, not
            // remembered as "already shown".
            if (!_display.DisplayGear(gear))
                return;

            _lastSentGear = gear;
            _lastDisplayMode = mode;
            _currentGear = GearToString(gear);
            _currentText = _currentGear;
        }

        /// <summary>
        /// Writes a speed to the display and updates the cached state under the
        /// given display-mode tag.
        /// </summary>
        private void ShowSpeed(int speed, string mode)
        {
            if (!_display.DisplaySpeed(speed))
                return;

            _lastSentSpeed = speed;
            _lastDisplayMode = mode;
            _currentText = speed.ToString();
        }

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
