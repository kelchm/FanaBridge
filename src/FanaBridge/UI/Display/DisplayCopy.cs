using System.Globalization;
using FanaBridge.Display.Schema2;

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

        /// <summary>
        /// Priority ladder pinned base-row rank cell (same spelling as
        /// <see cref="FieldBase"/>; scoped so the two ladder homes never collide in docs).
        /// </summary>
        public const string PriorityBaseRank = "base";

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

        // ── Overview section / card labels (board 5a) ─────────────────────

        /// <summary>Overview hub title.</summary>
        public const string Overview = "Overview";

        /// <summary>Mirror card section label (uppercased by style).</summary>
        public const string OnTheWheelNow = "ON THE WHEEL NOW";

        /// <summary>Priority card section label (uppercased by style).</summary>
        public const string PrioritySection = "PRIORITY";

        /// <summary>Legend card label under the ladder.</summary>
        public const string ReadingIt = "READING IT";

        /// <summary>Device settings card section label.</summary>
        public const string ThisDevice = "THIS DEVICE";

        /// <summary>Controls card section label.</summary>
        public const string Controls = "CONTROLS";

        // ── Spoke / action links ─────────────────────────────────────────

        /// <summary>Mirror-card spoke to Pages &amp; Fields.</summary>
        public const string PagesAndFieldsSpoke = "Pages & Fields ›";

        /// <summary>Priority-card spoke to the full Priority view.</summary>
        public const string PrioritySpoke = "Priority ›";

        /// <summary>Controls-card link out to SimHub Control mapper.</summary>
        public const string OpenControlMapperSpoke = "Open Control mapper ›";

        /// <summary>
        /// NEW affordance (RE-SEQUENCE ruling): Controls-card link to the minimal
        /// diagnostics panel. Not on the design board — sanctioned as a product
        /// feature that replaces the cancelled bench-kit trace file.
        /// </summary>
        public const string DiagnosticsSpoke = "Diagnostics ›";

        /// <summary>View name for the Diagnostics spoke (no chevron).</summary>
        public const string Diagnostics = "Diagnostics";

        /// <summary>View name for the Pages &amp; Fields spoke (no chevron).</summary>
        public const string PagesAndFields = "Pages & Fields";

        /// <summary>
        /// Disabled-spoke tooltip while the destination view is a later phase.
        /// Example: "Opens the Pages &amp; Fields view — arriving in a later build".
        /// </summary>
        public static string SpokeArrivingLater(string viewName)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Opens the {0} view — arriving in a later build",
                viewName ?? string.Empty);
        }

        // ── Mode / profile summary ───────────────────────────────────────

        /// <summary>Surface word for an ITM-capable wheel.</summary>
        public const string ItmDisplay = "ITM display";

        /// <summary>Surface word for a segment-only wheel.</summary>
        public const string SegmentDisplay = "Segment display";

        /// <summary>Header divider between surface word and situation pill.</summary>
        public const string ModeProfileDivider = "|";

        /// <summary>In-game situation pill.</summary>
        public const string InGame = "in game";

        /// <summary>Idle situation pill (no live game data).</summary>
        public const string SituationIdle = "idle";

        // ── Settings card ────────────────────────────────────────────────

        /// <summary>Display Mode group title (topmost in THIS DEVICE).</summary>
        public const string DisplayMode = "Display Mode";

        /// <summary>Mode hint for ITM wheels (three segments).</summary>
        public const string ModeHintItm =
            "Three states because this wheel has both displays. A segment-only wheel gets two — On / Off — there being no ITM display to turn off.";

        /// <summary>Mode hint for segment-only wheels (two segments).</summary>
        public const string ModeHintSegment =
            "Two states because this wheel has only a segment display.";

        /// <summary>Ruled reject-toggle label (§B6; C2).</summary>
        public const string RejectUncommandedChanges =
            "Reject un-commanded page changes";

        /// <summary>Reject-toggle explainer under the label.</summary>
        public const string RejectUncommandedChangesExplainer =
            "Anything that moves the wheel off our page — its own button combos, or the page-loss bug some wheels have — is put back. Turn it off and those changes are adopted; the wheel's button then steps the firmware's order and the page it remembers moves with it.";

        // ── Controls card ────────────────────────────────────────────────

        /// <summary>Next-page mapping row label.</summary>
        public const string NextPage = "Next page";

        /// <summary>Previous-page mapping row label.</summary>
        public const string PreviousPage = "Previous page";

        /// <summary>Unmapped control-mapper field value.</summary>
        public const string NotMapped = "not mapped";

        /// <summary>Read-only marker on mapping fields.</summary>
        public const string ReadOnly = "read-only";

        /// <summary>Controls consequence when rejection is on.</summary>
        public const string ControlsConsequenceRejectOn =
            "While un-commanded page changes are rejected, a press of the wheel's own button is put straight back.";

        /// <summary>Controls consequence when rejection is off.</summary>
        public const string ControlsConsequenceRejectOff =
            "With rejection off, that press is adopted — and it dismisses whatever hold is showing, exactly as a mapped press would.";

        /// <summary>Amber consequence when neither next nor previous is mapped.</summary>
        public const string ControlsConsequenceNothingMapped =
            "Nothing is mapped here, so the Manual paging row can never fire and nothing can be dismissed by hand.";

        // ── Ladder framing ───────────────────────────────────────────────

        /// <summary>Priority card subtitle under the section label.</summary>
        public const string LadderSubtitle =
            "the top page whose entrypoint is live is the one you see";

        /// <summary>Full legend sentence under READING IT.</summary>
        public const string LadderLegend =
            "waiting its condition is false · outranked true, but a row above it won · off-screen true and unbeaten, but its page isn't up. The winner carries the badge. OFF and DISMISSED are row labels, not states of the same kind.";

        /// <summary>Base-row detail when nothing above is live.</summary>
        public const string WhenNothingAboveIsLive = "when nothing above is live";

        /// <summary>Em-dash placeholder in pinned-row status cells.</summary>
        public const string StatusDash = "—";

        /// <summary>Idle-row destination label.</summary>
        public const string OutsideASession = "Outside a session";

        /// <summary>Idle-row target prefix glyph.</summary>
        public const string IdleTargetPrefix = "→";

        /// <summary>Playlist badge on an idle target (task #22 lights this path).</summary>
        public const string PlaylistBadge = "PLAYLIST";

        /// <summary>Built-in screen: the wheel's logo.</summary>
        public const string TheWheelsLogo = "The wheel's logo";

        /// <summary>Built-in screen: blank display.</summary>
        public const string ABlankDisplay = "A blank display";

        /// <summary>Built-in screen: white fill.</summary>
        public const string WhiteScreen = "White";

        /// <summary>Built-in screen: inverted logo.</summary>
        public const string LogoInvertedScreen = "Logo inverted";

        /// <summary>Mirror watermark (constant; not live telemetry).</summary>
        public const string MirrorWatermark = "MIRROR";

        /// <summary>
        /// PROVISIONAL (design-backlog) O1: empty-state body when Display Mode is Off.
        /// Design session owns the real board; this is a neutral placeholder.
        /// </summary>
        public const string ModeOffEmptyState =
            "Display is off — FanaBridge is not driving this wheel.";

        // ── Diagnostics panel (minimal product feature; no board) ────────

        /// <summary>Section label: per-tick ladder participants.</summary>
        public const string DiagnosticsLadderSection = "LADDER";

        /// <summary>Section label: device-level block from the composed record.</summary>
        public const string DiagnosticsDeviceSection = "DEVICE";

        /// <summary>Section label: concurrent wheel-screen plane.</summary>
        public const string DiagnosticsWheelScreenSection = "WHEEL SCREEN";

        /// <summary>Section label: standing manual row bookkeeping.</summary>
        public const string DiagnosticsManualSection = "MANUAL";

        /// <summary>Section label: base / idle floor lines.</summary>
        public const string DiagnosticsFloorSection = "FLOOR";

        /// <summary>Empty-state when no composed resolution is published this tick.</summary>
        public const string DiagnosticsEmptyState =
            "No resolution this tick — waiting for a live composed record.";

        /// <summary>Device-block row: device / wheel key from the record.</summary>
        public const string DiagnosticsDeviceKey = "Device key";

        /// <summary>Device-block row: current-page knowledge state.</summary>
        public const string DiagnosticsPageKnowledge = "Page knowledge";

        /// <summary>Page knowledge: no baseline yet.</summary>
        public const string DiagnosticsPageUnknown = "unknown";

        /// <summary>Page knowledge: synced on an uncataloged parameter set.</summary>
        public const string DiagnosticsPageUncataloged = "known · uncataloged";

        /// <summary>Device-block row: director reject edge this tick.</summary>
        public const string DiagnosticsRevertedThisTick = "Reverted this tick";

        /// <summary>Device-block row: director adopt-with-warning edge this tick.</summary>
        public const string DiagnosticsAdoptWarnedThisTick = "Adopt warned this tick";

        /// <summary>Device-block: no device block on this record slice.</summary>
        public const string DiagnosticsNoDeviceBlock = "No device block on this record";

        /// <summary>Wheel-screen plane: surface is held (a screen owns it).</summary>
        public const string DiagnosticsHeld = "held";

        /// <summary>Wheel-screen plane: surface is released (idle floor).</summary>
        public const string DiagnosticsReleased = "released";

        /// <summary>Wheel-screen plane: owner row label.</summary>
        public const string DiagnosticsOwner = "Owner";

        /// <summary>Wheel-screen plane: held/released row label.</summary>
        public const string DiagnosticsHoldState = "Hold";

        /// <summary>Wheel-screen plane: dismissal latch row label.</summary>
        public const string DiagnosticsDismissalLatch = "Dismissal latch";

        /// <summary>Dismissal latch: at least one carrier is latched out.</summary>
        public const string DiagnosticsLatchActive = "active";

        /// <summary>Dismissal latch: none latched this tick.</summary>
        public const string DiagnosticsLatchClear = "clear";

        /// <summary>Manual section: no remembered target yet.</summary>
        public const string DiagnosticsManualNothingPaged = "nothing paged to yet";

        // ── Edit-session concurrency (Q14 write seam) ────────────────────

        /// <summary>
        /// Surfaced when <c>DisplayConfigV2EditSession.TryApply</c> finds the live host
        /// document is no longer the identity captured at open (another writer published
        /// while the session was open). Carried on
        /// <c>DisplayConfigV2ApplyResult.Message</c>; views show this ruled string and do
        /// not invent their own conflict copy. VIEW consumption lands with the Priority
        /// round.
        /// </summary>
        public const string ConfigEditConflict =
            "This document changed while you were editing. Your changes were not applied.";

        /// <summary>
        /// Surfaced when a session clone fails closed (serializer refuse) so a validation
        /// probe never pretends a silent default document was clean. Not a publish path —
        /// notes only.
        /// </summary>
        public const string ConfigEditCloneFailed =
            "Could not clone the working document for validation.";

        /// <summary>Manual section: owns-display fact key.</summary>
        public const string DiagnosticsOwnsDisplay = "Owns display";

        /// <summary>Manual section: ms since last press fact key.</summary>
        public const string DiagnosticsSinceLastPress = "Since last press";

        /// <summary>Manual section: returned-to-base fact value.</summary>
        public const string DiagnosticsReturnedToBase = "returned";

        /// <summary>Boolean yes for diagnostics facts.</summary>
        public const string DiagnosticsYes = "yes";

        /// <summary>Boolean no for diagnostics facts.</summary>
        public const string DiagnosticsNo = "no";

        /// <summary>Timing detail: ms since event.</summary>
        public static string DiagnosticsMs(long ms)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} ms", ms);
        }

        /// <summary>Timing detail: remaining hold window.</summary>
        public static string DiagnosticsRemainingMs(int remainingMs)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ms remaining",
                remainingMs);
        }

        /// <summary>Page knowledge with a known wire page.</summary>
        public static string DiagnosticsPageKnown(byte wirePage, string catalogName)
        {
            if (string.IsNullOrEmpty(catalogName))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "known · wire {0}",
                    wirePage);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "known · wire {0} · {1}",
                wirePage,
                catalogName);
        }

        /// <summary>Key · value line for a diagnostics fact row.</summary>
        public static string DiagnosticsFactLine(string key, string value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1}",
                key ?? string.Empty,
                value ?? string.Empty);
        }

        // ── Condition operator phrases (finite grammar; O8) ──────────────

        /// <summary>Level operator &lt;.</summary>
        public const string OpBelow = "is below";

        /// <summary>Level operator ≤.</summary>
        public const string OpAtOrBelow = "is at or below";

        /// <summary>Level operator &gt;.</summary>
        public const string OpAbove = "is above";

        /// <summary>Level operator ≥.</summary>
        public const string OpAtOrAbove = "is at or above";

        /// <summary>Level operator =.</summary>
        public const string OpEquals = "is";

        /// <summary>Level operator ≠.</summary>
        public const string OpNotEquals = "is not";

        /// <summary>Bool operator isTrue.</summary>
        public const string OpIsOn = "is on";

        /// <summary>Bool operator isFalse.</summary>
        public const string OpIsOff = "is off";

        /// <summary>onChange any / lifetime whileTrue edge phrasing.</summary>
        public const string OpChanges = "changes";

        /// <summary>onChange up.</summary>
        public const string OpIncreases = "increases";

        /// <summary>onChange down.</summary>
        public const string OpDecreases = "decreases";

        // ── Overview format methods ──────────────────────────────────────

        /// <summary>"2 of its 4 entrypoint overrides are firing".</summary>
        public static string EntrypointsFiringLine(int firing, int total)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} of its {1} entrypoint overrides are firing",
                firing,
                total);
        }

        /// <summary>Manual-paging detail: remembered target + mapping state.</summary>
        public static string ManualPagingDetail(
            bool hasRememberedTarget,
            bool nextMapped,
            bool prevMapped)
        {
            string left = hasRememberedTarget
                ? "targets the page you last stepped to"
                : "nothing paged to yet";
            if (!nextMapped && !prevMapped)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · no next/previous mapped",
                    left);
            }

            if (nextMapped && prevMapped)
                return left;

            if (nextMapped)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · previous not mapped",
                    left);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · next not mapped",
                left);
        }

        /// <summary>Idle-row detail: "→ Screensaver · logo 60 s → blank".</summary>
        public static string IdleTargetLine(string targetName, string summary)
        {
            if (string.IsNullOrEmpty(summary))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1}",
                    IdleTargetPrefix,
                    targetName ?? string.Empty);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} · {2}",
                IdleTargetPrefix,
                targetName ?? string.Empty,
                summary);
        }

        /// <summary>Mirror caption: "ITM 5 · Tire Temps" (page name only — 8b D1).</summary>
        public static string PageCaption(string badge, string name)
        {
            if (string.IsNullOrEmpty(badge))
                return name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                return badge;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1}",
                badge,
                name);
        }

        /// <summary>ITM page badge from catalog index: "ITM 5".</summary>
        public static string ItmPageBadge(int index)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", ModeItm, index);
        }

        /// <summary>
        /// ITM badge when the catalog index is unknown (fallback spelling of
        /// <see cref="ModeItm"/>).
        /// </summary>
        public const string ItmBadge = ModeItm;

        /// <summary>Drawn cycle-badge join glyph between two page badges.</summary>
        public const string CycleBadgeJoin = "⇄";

        /// <summary>
        /// Child label for the outranked second clause: "FN1 override".
        /// </summary>
        public static string OverrideChildLabel(string childName)
        {
            if (string.IsNullOrEmpty(childName))
                return Override;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1}",
                childName,
                Override);
        }

        /// <summary>
        /// Condition sentence: level comparison.
        /// Example: "Fuel remaining is below 4.0 L".
        /// </summary>
        public static string ConditionLevelSentence(
            string sourcePhrase,
            string operatorPhrase,
            string valueWithUnit)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2}",
                sourcePhrase ?? string.Empty,
                operatorPhrase ?? string.Empty,
                valueWithUnit ?? string.Empty);
        }

        /// <summary>
        /// Condition sentence: boolean source.
        /// Example: "Pit limiter is on".
        /// </summary>
        public static string ConditionBoolSentence(string sourcePhrase, string operatorPhrase)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1}",
                sourcePhrase ?? string.Empty,
                operatorPhrase ?? string.Empty);
        }

        /// <summary>
        /// Condition sentence: onChange / edge.
        /// Example: "Brake bias changes".
        /// </summary>
        public static string ConditionChangeSentence(string sourcePhrase, string changePhrase)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1}",
                sourcePhrase ?? string.Empty,
                changePhrase ?? string.Empty);
        }

        /// <summary>
        /// Value with optional unit: "4.0 L", "0.5 s", or bare "4".
        /// </summary>
        public static string ConditionValue(double value, string unit)
        {
            string num = value.ToString("0.###", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(unit))
                return num;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", num, unit);
        }

        /// <summary>
        /// Second detail clause when a row is outranked and a child is off-screen.
        /// Example: "this entrypoint is outranked; the page's FN1 override is off-screen".
        /// </summary>
        public static string OutrankedOffScreenClause(string childLabel)
        {
            if (string.IsNullOrEmpty(childLabel))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "this entrypoint is {0}",
                    Outranked);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "this entrypoint is {0}; the page's {1} is {2}",
                Outranked,
                childLabel,
                OffScreen);
        }

        /// <summary>Join a primary detail with an em-dash second clause.</summary>
        public static string DetailWithClause(string primary, string clause)
        {
            if (string.IsNullOrEmpty(clause))
                return primary ?? string.Empty;
            if (string.IsNullOrEmpty(primary))
                return clause;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} — {1}",
                primary,
                clause);
        }

        /// <summary>
        /// Condition phrase with a cycle carrier-kind suffix.
        /// Example: "in the pit box · cycle (2+ pages)" (first-mention glossary form).
        /// </summary>
        public static string ConditionWithCycleSuffix(string conditionPhrase, bool firstMention)
        {
            string cycle = firstMention ? CycleDefinition : Cycle;
            if (string.IsNullOrEmpty(conditionPhrase))
                return cycle;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1}",
                conditionPhrase,
                cycle);
        }

        /// <summary>Operator phrase for a <see cref="ConditionOperator"/>.</summary>
        public static string OperatorPhrase(ConditionOperator op)
        {
            switch (op)
            {
                case ConditionOperator.LessThan: return OpBelow;
                case ConditionOperator.LessOrEqual: return OpAtOrBelow;
                case ConditionOperator.GreaterThan: return OpAbove;
                case ConditionOperator.GreaterOrEqual: return OpAtOrAbove;
                case ConditionOperator.Equals: return OpEquals;
                case ConditionOperator.NotEquals: return OpNotEquals;
                case ConditionOperator.IsTrue: return OpIsOn;
                case ConditionOperator.IsFalse: return OpIsOff;
                default: return string.Empty;
            }
        }

        /// <summary>Change-direction phrase for onChange lifetimes.</summary>
        public static string ChangeDirectionPhrase(ChangeDirection direction)
        {
            switch (direction)
            {
                case ChangeDirection.Up: return OpIncreases;
                case ChangeDirection.Down: return OpDecreases;
                default: return OpChanges;
            }
        }
    }
}
