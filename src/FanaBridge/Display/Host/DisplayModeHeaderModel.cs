using System;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Pure DISPLAY MODE header decisions — segment ids, mock-verbatim hint copy,
    /// header visibility, and the Off-card turn-back-on target. WPF-free so the mode
    /// chrome is unit-testable without a UI thread; the panel only applies the results.
    /// </summary>
    public static class DisplayModeHeaderModel
    {
        public const string SegmentItm = "itm";
        public const string SegmentLegacy = "legacy";
        public const string SegmentOff = "off";

        /// <summary>Segment id that mirrors a <see cref="DisplaySettings.DisplayControl"/> value.</summary>
        public static string SegmentIdFor(string displayControl)
        {
            if (string.Equals(displayControl, DisplaySettings.ControlLegacy, StringComparison.OrdinalIgnoreCase))
                return SegmentLegacy;
            if (string.Equals(displayControl, DisplaySettings.ControlOff, StringComparison.OrdinalIgnoreCase))
                return SegmentOff;
            return SegmentItm;
        }

        /// <summary>Canonical control constant for a segment id (unknown → Itm).</summary>
        public static string ControlForSegment(string segmentId)
        {
            if (string.Equals(segmentId, SegmentLegacy, StringComparison.OrdinalIgnoreCase))
                return DisplaySettings.ControlLegacy;
            if (string.Equals(segmentId, SegmentOff, StringComparison.OrdinalIgnoreCase))
                return DisplaySettings.ControlOff;
            return DisplaySettings.ControlItm;
        }

        /// <summary>Mock-verbatim mode hint under the segmented control.</summary>
        public static string ModeHint(string displayControl)
        {
            if (string.Equals(displayControl, DisplaySettings.ControlLegacy, StringComparison.OrdinalIgnoreCase))
                return "Only the 3-character legacy display is used.";
            if (string.Equals(displayControl, DisplaySettings.ControlOff, StringComparison.OrdinalIgnoreCase))
                return "FanaBridge leaves the display alone.";
            return "Legacy only shows just the 3-character display; Off hands the display back to the game.";
        }

        /// <summary>
        /// Whether the DISPLAY MODE header strip is shown: ITM Overview, or any wheel while
        /// control is Off (so the user can leave the Off trap after a caps rebind).
        /// </summary>
        public static bool ShowModeHeader(bool isItm, bool isOverview, string displayControl)
            => (isItm && isOverview) || IsOff(displayControl);

        /// <summary>True when <paramref name="displayControl"/> is Off (case-insensitive).</summary>
        public static bool IsOff(string displayControl)
            => string.Equals(displayControl, DisplaySettings.ControlOff, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Target control for the Off card's "Turn the display back on" button: Itm on ITM
        /// wheels, Legacy on basic wheels (the Off-trap recovery path).
        /// </summary>
        public static string TurnBackOnControl(bool isItm)
            => isItm ? DisplaySettings.ControlItm : DisplaySettings.ControlLegacy;

        /// <summary>
        /// Spec no-op for SetDisplayControl: true when the control value is already the
        /// target. Compared on DisplayControl alone — a disagreeing ItmEnabled mirror must
        /// not force a rewrite on a re-click of the already-selected segment.
        /// </summary>
        public static bool IsSameControl(string current, string target)
            => string.Equals(current, target, StringComparison.Ordinal);
    }
}
