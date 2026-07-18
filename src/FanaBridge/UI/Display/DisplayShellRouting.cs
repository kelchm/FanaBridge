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
        /// <see cref="TriggerRuleSet.Legacy"/>; ITM wheels open the ITM set (Page-6 /
        /// virtual-page targets are authored via the Virtual pages path + legacy rules
        /// when the shell re-enters with <see cref="TriggerRuleSet.Legacy"/>).
        /// </summary>
        public static TriggerRuleSet TriggersRuleSetFor(DisplayType displayType)
            => displayType == DisplayType.Itm ? TriggerRuleSet.Itm : TriggerRuleSet.Legacy;

        /// <summary>True when the Overview should show the legacy 3-char mirror card
        /// (basic wheels while control is not Off).</summary>
        public static bool ShowLegacyOverview(DisplayType displayType, string displayControl)
            => displayType != DisplayType.Itm
                && !DisplayModeHeaderModel.IsOff(displayControl);

        /// <summary>True when the Overview should show the ITM live cards (mirror +
        /// activity + ITM priority list) — ITM wheel with ITM control active.</summary>
        public static bool ShowItmOverview(DisplayType displayType, string displayControl)
            => displayType == DisplayType.Itm
                && !DisplayModeHeaderModel.IsOff(displayControl)
                && DisplayModeHeaderModel.IsSameControl(
                    displayControl ?? DisplaySettings.ControlItm, DisplaySettings.ControlItm);

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
        /// setting (mirror truth = wire truth — no fabricated segments without a snapshot).</summary>
        public static string LegacyMirrorCaption(string legacyScreenName, string displayMode)
        {
            if (!string.IsNullOrEmpty(legacyScreenName))
                return legacyScreenName;
            if (string.IsNullOrEmpty(displayMode)
                || string.Equals(displayMode, "None", System.StringComparison.OrdinalIgnoreCase))
                return "Blank";
            return displayMode;
        }

        /// <summary>Whether the Overview legacy face should paint snapshot segments
        /// (rule-driven) vs stay blank with a caption-only fallback.</summary>
        public static bool UseRuleDrivenSegments(byte[] legacySegments)
            => legacySegments != null && legacySegments.Length >= 3;
    }
}
