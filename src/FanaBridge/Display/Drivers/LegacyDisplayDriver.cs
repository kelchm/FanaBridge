using FanaBridge.Protocol;
using GameReaderCommon;
using System;
using FanaBridge.Display.Host;

namespace FanaBridge.Display.Drivers
{
    /// <summary>
    /// Maps telemetry data to the Fanatec 3-digit 7-segment display.
    /// Supports gear, speed, and mixed display modes.
    ///
    /// Driven by a typed <see cref="DisplaySettings"/> so this can be owned by
    /// either the plugin or a DeviceInstance — no dependency on FanatecPluginSettings.
    /// </summary>
    public class LegacyDisplayDriver
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

        /// <summary>True while the page holds content this driver painted (any mode or
        /// rule-segment write) that has not yet been successfully blanked. The device
        /// instance's empty-world blank-once keys off this — the single source of truth,
        /// so a page already blanked at game exit is never re-blanked at idle.</summary>
        internal bool NeedsExitBlank => _needExitBlank;

        /// <summary>Arms the exit-blank latch for content this driver did NOT paint (the
        /// settings page's display test writes col01 directly) so a declined handback
        /// <see cref="Clear"/> keeps retrying instead of leaving the residue frozen.</summary>
        internal void ArmExitBlank() => _needExitBlank = true;

        // Rule-path segment latch (TryShowSegments): change-gate identical resolved
        // frames so effect clocks only re-send when the visible window actually moves.
        private byte _lastSeg0, _lastSeg1, _lastSeg2;
        private bool _hasLastSegments;
        private const string RuleSegmentsMode = "RuleSegments";

        public LegacyDisplayDriver(DisplayEncoder display, DisplaySettings settings)
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
            bool telemetryLive = data != null && data.GameRunning && data.NewData != null;
            if (!telemetryLive)
            {
                // Game exited (or never started): blank once on the way out, then
                // write nothing while idle — SimHub keeps the last telemetry values
                // after a game exits, so painting here would show stale data as if
                // live. (Classic mode path only — the rule path resolves idle frames
                // itself, with per-kind idle content.)
                if (_needExitBlank)
                    Clear();   // resets the latch only when accepted — declined retries
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
        /// Rule-path sink: write three segment bytes through the same change-gate
        /// and declined-send-retry machinery as the mode-based path. Identical
        /// frames never re-send (effect steps only hit the wire when the visible
        /// window actually changes). Returns <c>false</c> only when a send was
        /// attempted and declined — the next frame must retry.
        /// </summary>
        public bool TryShowSegments(byte seg0, byte seg1, byte seg2)
        {
            // Content ownership — a non-blank frame arms the same exit-blank latch
            // Update arms on live frames; an ACCEPTED all-blank frame clears it (the
            // page no longer holds our content), so the rule path's own idle blanks
            // never leave a stale "needs blanking" state behind.
            bool blankFrame = seg0 == SevenSegment.Blank && seg1 == SevenSegment.Blank
                && seg2 == SevenSegment.Blank;
            if (!blankFrame)
                _needExitBlank = true;
            else if (!_hasLastSegments && !_needExitBlank)
            {
                // Nothing of ours on the page AND nothing pending to clear — never
                // write a first blank. (_hasLastSegments alone is not "page clean":
                // Clear() resets it even when its write was DECLINED, and display-test
                // residue arms the latch without segments — both must let the blank
                // through so it retries until accepted.)
                return true;
            }

            if (_hasLastSegments
                && seg0 == _lastSeg0 && seg1 == _lastSeg1 && seg2 == _lastSeg2
                && _lastDisplayMode == RuleSegmentsMode)
                return true;

            if (!_display.SetDisplay(seg0, seg1, seg2))
                return false;

            if (blankFrame)
                _needExitBlank = false;
            _lastSeg0 = seg0;
            _lastSeg1 = seg1;
            _lastSeg2 = seg2;
            _hasLastSegments = true;
            _lastDisplayMode = RuleSegmentsMode;
            // Mode-path latches must not suppress a later Update after rule tenure.
            _lastSentGear = int.MinValue;
            _lastSentSpeed = int.MinValue;
            _lastBracketsShown = false;
            return true;
        }

        /// <summary>
        /// <see cref="TryShowSegments(byte,byte,byte)"/> over a 3-byte frame.
        /// Null/short arrays blank the display via <see cref="Clear"/>.
        /// </summary>
        public bool TryShowSegments(byte[] segments)
        {
            if (segments == null || segments.Length < 3)
                return Clear();
            return TryShowSegments(segments[0], segments[1], segments[2]);
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
            // The content latch clears only on an ACCEPTED blank — a declined write must
            // leave NeedsExitBlank armed so every retry path keeps retrying.
            if (sent)
                _needExitBlank = false;
            _currentText = "";
            _currentGear = "";
            _lastSentGear = int.MinValue;
            _lastSentSpeed = int.MinValue;
            _lastKnownGear = int.MinValue;
            _gearOverlayUntil = DateTime.MinValue;
            _lastBracketsShown = false;
            _hasLastSegments = false;
            _lastDisplayMode = null;
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
