using FanaBridge.Display.Host;
using FanaBridge.Profiles;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure shell routing decisions for the Display tab's hub-and-spoke views: which
    /// Overview chrome a wheel gets, which rule set the Triggers editor opens on, and
    /// how Virtual pages is reached. Unit-tested with no WPF — the panel only applies
    /// these decisions. Off / mode-header gates stay in <see cref="DisplayModeHeaderModel"/>.
    /// </summary>
    internal static class DisplayShellRouting
    {
        /// <summary>
        /// Which rule set the Triggers editor binds when opened from Overview.
        /// Basic (non-ITM) wheels only have the legacy surface →
        /// <see cref="TriggerRuleSet.Legacy"/>; so does an ITM wheel whose control is
        /// Legacy only — its Overview, monitor rows, and row-click navigation are all
        /// legacy then. The ITM set opens only while ITM is the active world.
        /// </summary>
        public static TriggerRuleSet TriggersRuleSetFor(DisplayType displayType, string displayControl)
            => displayType == DisplayType.Itm && !DisplayModeHeaderModel.IsLegacy(displayControl)
                ? TriggerRuleSet.Itm
                : TriggerRuleSet.Legacy;

        /// <summary>True when the Overview should show the legacy mirror card: basic
        /// wheels while control is not Off, and ITM wheels while control is Legacy —
        /// the legacy world is the whole display then (rendered at the ITM panel size,
        /// see <see cref="UseWideLegacyFace"/>).</summary>
        public static bool ShowLegacyOverview(DisplayType displayType, string displayControl)
            => !DisplayModeHeaderModel.IsOff(displayControl)
                && (displayType != DisplayType.Itm
                    || DisplayModeHeaderModel.IsLegacy(displayControl));

        /// <summary>True when the legacy Overview face renders inside the ITM mirror's
        /// wide 4:1 panel: an ITM wheel's legacy page lives on the same physical display,
        /// so the card must not shrink when control flips Itm ↔ Legacy. Basic wheels
        /// keep the small 3-char face.</summary>
        public static bool UseWideLegacyFace(DisplayType displayType)
            => displayType == DisplayType.Itm;

        /// <summary>True when the Overview should show the ITM live cards (mirror +
        /// activity + ITM priority list) — ITM wheel with ITM control active.</summary>
        public static bool ShowItmOverview(DisplayType displayType, string displayControl)
            => displayType == DisplayType.Itm
                && !DisplayModeHeaderModel.IsOff(displayControl)
                && DisplayModeHeaderModel.IsSameControl(
                    displayControl ?? DisplaySettings.ControlItm, DisplaySettings.ControlItm);

        /// <summary>
        /// v2 document removed: restore the v1 Overview live surface under the current
        /// control. Exactly one of (ITM live, Legacy live, Off card) is true — never a
        /// blank panel.
        /// </summary>
        public static void V1OverviewSurfaceAfterV2Removed(
            DisplayType displayType,
            string displayControl,
            out bool showItmLive,
            out bool showLegacyLive,
            out bool showOffCard)
        {
            showOffCard = DisplayModeHeaderModel.IsOff(displayControl);
            if (showOffCard)
            {
                showItmLive = false;
                showLegacyLive = false;
                return;
            }

            showItmLive = ShowItmOverview(displayType, displayControl);
            showLegacyLive = ShowLegacyOverview(displayType, displayControl);
        }

        /// <summary>True when Virtual pages is reachable: not Off. ITM wheels reach it
        /// via the Page-6 card / footer link; basic wheels via the Overview link.</summary>
        public static bool CanOpenVirtualPages(string displayControl)
            => !DisplayModeHeaderModel.IsOff(displayControl);

        /// <summary>Overview footer / link label for Virtual pages.</summary>
        public static string VirtualPagesLinkLabel(DisplayType displayType)
            => displayType == DisplayType.Itm
                ? "Legacy screens (Page 6)"
                : "Edit virtual pages →";

        /// <summary>Caption under the legacy Overview face when the rule path has a
        /// published screen name; otherwise a fallback built from the legacy DisplayMode
        /// setting (mirror truth = wire truth — no fabricated segments without a snapshot).
        /// <paramref name="legacyPageActive"/> false (ITM wheel, DisplayMode "None" —
        /// nothing is driven on the wire) ignores the screen name: the snapshot still
        /// carries resolve-only segments then, and the mirror must not claim them.</summary>
        public static string LegacyMirrorCaption(string legacyScreenName, string displayMode,
            bool legacyPageActive = true)
        {
            if (legacyPageActive && !string.IsNullOrEmpty(legacyScreenName))
                return legacyScreenName;
            if (string.IsNullOrEmpty(displayMode)
                || string.Equals(displayMode, "None", System.StringComparison.OrdinalIgnoreCase))
                return "Blank";
            return displayMode;
        }

        /// <summary>Whether the Overview legacy face should paint snapshot segments
        /// (rule-driven) vs stay blank with a caption-only fallback. Requires the wire
        /// to actually be driven (<paramref name="legacyPageActive"/>): with the page
        /// off the stack resolves segments for the snapshot only, and painting them
        /// would break mirror truth = wire truth.</summary>
        public static bool UseRuleDrivenSegments(byte[] legacySegments, bool legacyPageActive = true)
            => legacyPageActive && legacySegments != null && legacySegments.Length >= 3;
    }
}
