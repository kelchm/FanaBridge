using System.Globalization;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Copy-layer law for the v2 Display UI: every new v2 view takes user-visible
    /// strings ONLY from here. Keys are naming-proof — schema keys never appear as
    /// user copy. Ruled vocabulary (DECISIONS §7e NAMING PASS + FIELD FILTER + SHARED
    /// FIELDS; design round 8c) lives as named constants and InvariantCulture format
    /// methods; each ruled term appears once in this table.
    /// </summary>
    public static class DisplayCopy
    {
        // ── Statuses (D10 check words) ───────────────────────────────────

        /// <summary>Condition false / no live activation.</summary>
        public const string Waiting = "waiting";

        /// <summary>Active but lost its surface's priority ladder.</summary>
        public const string Outranked = "outranked";

        /// <summary>Would win its ladder, but the owning surface is not up.</summary>
        public const string OffScreen = "off-screen";

        // ── Row labels (config facts; never statuses) ────────────────────

        /// <summary>Carrier is off in config.</summary>
        public const string Off = "OFF";

        /// <summary>Latched out by a dismiss while still Active+Eligible.</summary>
        public const string Dismissed = "DISMISSED";

        /// <summary>Not runnable on this wheel / surface.</summary>
        public const string CantRunHere = "CAN'T RUN HERE";

        // ── Diagnostics vocabulary (non-check presence + extra labels) ───

        /// <summary>Winning and painting — not a D10 check word; empty in the status column.</summary>
        public const string OnScreen = "";

        /// <summary>Diagnostics: capability untested on this wheel.</summary>
        public const string Untested = "untested";

        /// <summary>Diagnostics: kept as-is (foreign / non-owned stamp).</summary>
        public const string KeptAsIs = "kept as-is";

        /// <summary>Diagnostics: no wheel present for this carrier.</summary>
        public const string NoWheel = "no wheel";

        /// <summary>Diagnostics: paused.</summary>
        public const string Paused = "paused";

        /// <summary>Diagnostics: outside runs/session scope this tick.</summary>
        public const string OutOfSessionScope = "out of session scope";

        // ── Modes ────────────────────────────────────────────────────────

        /// <summary>ITM-wheel mode segment: full ITM world.</summary>
        public const string ModeItm = "ITM";

        /// <summary>ITM-wheel mode segment: Legacy Only.</summary>
        public const string ModeLegacyOnly = "Legacy Only";

        /// <summary>ITM-wheel mode segment: Off (shared spelling with segment-only Off).</summary>
        public const string ModeOff = "Off";

        /// <summary>Segment-only wheel mode segment: On.</summary>
        public const string ModeOn = "On";

        // ── Badges & hosted pages ────────────────────────────────────────

        /// <summary>Badge on hosted pages (ITM wheels where kinds mix).</summary>
        public const string LegacyBadge = "LEGACY";

        /// <summary>Hosted pages live on the Legacy slot — preposition form.</summary>
        public const string OnLegacy = "on Legacy";

        // ── Entrypoint ───────────────────────────────────────────────────

        /// <summary>Form checkbox label — full sentence only on forms.</summary>
        public const string EntrypointFlag = "Acts as an entrypoint";

        /// <summary>Inline glyph alone (tooltip carries <see cref="EntrypointFlag"/>).</summary>
        public const string EntrypointGlyph = "↑";

        /// <summary>Tooltip for the inline ↑ glyph.</summary>
        public const string EntrypointTooltip = EntrypointFlag;

        // ── Row / surface labels ─────────────────────────────────────────

        /// <summary>Override noun (vs layer).</summary>
        public const string Override = "override";

        /// <summary>Layer noun (vs override).</summary>
        public const string Layer = "layer";

        /// <summary>Priority surface title.</summary>
        public const string Priority = "Priority";

        /// <summary>Page-level passive default — never "rest".</summary>
        public const string BasePage = "Base page";

        /// <summary>Field ladder pinned base row.</summary>
        public const string FieldBase = "base";

        // ── Rotation & manual paging ─────────────────────────────────────

        /// <summary>UI for the frozen pageOrder key — in-rotation pages.</summary>
        public const string Rotation = "Rotation";

        /// <summary>UI for pages not in the walk order.</summary>
        public const string OffRotation = "off-rotation";

        /// <summary>Standing manual row title.</summary>
        public const string ManualPaging = "Manual paging";

        // ── Cycle ────────────────────────────────────────────────────────

        /// <summary>First-mention glossary: a cycle is two or more pages.</summary>
        public const string CycleDefinition = "cycle (2+ pages)";

        /// <summary>Short form after first mention.</summary>
        public const string Cycle = "cycle";

        // ── Shared fields ────────────────────────────────────────────────

        /// <summary>Shared-field scope word — never "global".</summary>
        public const string Shared = "Shared";

        // ── Filter clear action ──────────────────────────────────────────

        /// <summary>Named clear action on the field-filter state line (never a bare ×).</summary>
        public const string ShowAllFields = "Show all fields";

        // ── Format methods (InvariantCulture) ────────────────────────────

        /// <summary>
        /// Reach line for a shared field: every ITM page, or N of M.
        /// </summary>
        /// <param name="placed">Catalog pages that place this field.</param>
        /// <param name="total">Total ITM pages on the wheel.</param>
        public static string ReachLine(int placed, int total)
        {
            if (total > 0 && placed >= total)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · appears on every ITM page",
                    Shared);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · on {1} of {2} ITM pages",
                Shared,
                placed,
                total);
        }

        /// <summary>
        /// Sticky filter state line: "Showing &lt;name&gt; (n of m) — Show all fields".
        /// </summary>
        /// <param name="name">Focused field display name.</param>
        /// <param name="index">1-based index among the filtered set.</param>
        /// <param name="count">Size of the filtered set (usually total fields).</param>
        public static string FilterStateLine(string name, int index, int count)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Showing {0} ({1} of {2}) — {3}",
                name ?? string.Empty,
                index,
                count,
                ShowAllFields);
        }

        /// <summary>
        /// Filter state line when the focused field is shared — reach restated mid-line.
        /// Example: "Showing Speed — shared across all 5 ITM pages — Show all fields".
        /// </summary>
        public static string FilterStateLineShared(string name, int totalItmPages)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Showing {0} — shared across all {1} ITM pages — {2}",
                name ?? string.Empty,
                totalItmPages,
                ShowAllFields);
        }
    }
}
