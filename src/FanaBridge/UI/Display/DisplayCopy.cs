using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Display.Arbitration;
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

        /// <summary>
        /// Playlist step skipped by capability / degrade (read-only view label, P6 rider b).
        /// Wording lives here only — no view spells the skip label inline.
        /// </summary>
        public const string PlaylistStepSkipped = "skipped · can't run here";

        /// <summary>
        /// Sub-floor duration clamp marker (P2 degrade-visible). Paired with the clamped
        /// effective duration in <see cref="PlaylistStepDurationLabel"/>.
        /// </summary>
        public const string PlaylistStepDurationClamped = "clamped to floor";

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

        /// <summary>
        /// Hosted pages live on the Legacy slot — preposition form (NAMING PASS ruled
        /// vocabulary; presence-pinned for CopyLayerGuard even when no board paints it yet).
        /// </summary>
        public const string OnLegacy = "on Legacy";

        /// <summary>
        /// UI for pages not in the walk order (NAMING PASS ruled vocabulary;
        /// presence-pinned for CopyLayerGuard).
        /// </summary>
        public const string OffRotation = "off-rotation";

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

        // 8c Pages & Fields (boards 5c/5d) — blocked phase; keep copy ready.
        /// <summary>
        /// Sticky filter state line: "Showing &lt;name&gt; (n of m) — Show all fields".
        /// 8c Pages&amp;Fields charter — phase-gated until 5c/5d render.
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

        // 8c Pages & Fields (boards 5c/5d) — blocked phase; keep copy ready.
        /// <summary>
        /// Filter state line when the focused field is shared — reach restated mid-line.
        /// Example: "Showing Speed — shared across all 5 ITM pages — Show all fields".
        /// 8c Pages&amp;Fields charter — phase-gated until 5c/5d render.
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

        /// <summary>Device-block row: distinct ITM display device id.</summary>
        public const string DiagnosticsItmDeviceId = "ITM device id";

        /// <summary>Device-block row: capability-envelope summary.</summary>
        public const string DiagnosticsCapabilityEnvelope = "Capability envelope";

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

        /// <summary>Wheel-screen plane: release-edge fact this tick.</summary>
        public const string DiagnosticsReleaseEdge = "Release edge";

        /// <summary>Wheel-screen plane: dismissal latch row label.</summary>
        public const string DiagnosticsDismissalLatch = "Dismissal latch";

        /// <summary>Dismissal latch: at least one carrier is latched out.</summary>
        public const string DiagnosticsLatchActive = "active";

        /// <summary>Dismissal latch: none latched this tick.</summary>
        public const string DiagnosticsLatchClear = "clear";

        /// <summary>Dismissal latch: id-list row label.</summary>
        // "carrier" is engine vocabulary, not ruled user copy (closing-panel
        // finding): the user-facing noun is the entrypoint being dismissed.
        public const string DiagnosticsDismissalLatchIds = "Dismissed entrypoints";

        /// <summary>Capability tri-state: supported.</summary>
        public const string DiagnosticsCapSupported = "yes";

        /// <summary>Capability tri-state: not supported.</summary>
        public const string DiagnosticsCapUnsupported = "no";

        /// <summary>Capability tri-state: untested (null).</summary>
        public const string DiagnosticsCapUntested = "untested";

        /// <summary>Snapshot bit: condition satisfied.</summary>
        public const string DiagnosticsSnapSatisfied = "satisfied";

        /// <summary>Snapshot bit: active activation.</summary>
        public const string DiagnosticsSnapActive = "active";

        /// <summary>Snapshot bit: inactive.</summary>
        public const string DiagnosticsSnapInactive = "inactive";

        /// <summary>Snapshot bit: eligible this tick.</summary>
        public const string DiagnosticsSnapEligible = "eligible";

        /// <summary>Snapshot bit: not eligible this tick.</summary>
        public const string DiagnosticsSnapIneligible = "ineligible";

        /// <summary>Snapshot bit: fresh fire this tick.</summary>
        public const string DiagnosticsSnapFreshFire = "fresh fire";

        /// <summary>Snapshot bit: fired this tick (including window restart).</summary>
        public const string DiagnosticsSnapFired = "fired";

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

        /// <summary>Tri-state capability word for diagnostics.</summary>
        public static string DiagnosticsCapTriState(bool? value)
        {
            if (value == null)
                return DiagnosticsCapUntested;
            return value.Value ? DiagnosticsCapSupported : DiagnosticsCapUnsupported;
        }

        /// <summary>
        /// Capability-envelope summary line: field param count + screen-command tri-states.
        /// </summary>
        public static string DiagnosticsCapabilityEnvelopeSummary(
            int fieldParamCount,
            bool? logo,
            bool? blank,
            bool? white,
            bool? logoInverted)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} fields · logo {1} · blank {2} · white {3} · logo inverted {4}",
                fieldParamCount,
                DiagnosticsCapTriState(logo),
                DiagnosticsCapTriState(blank),
                DiagnosticsCapTriState(white),
                DiagnosticsCapTriState(logoInverted));
        }

        /// <summary>ITM device id display (numeric wire id).</summary>
        public static string DiagnosticsItmDeviceIdValue(byte id)
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Join ordered latch ids for the diagnostics id-list line.
        /// </summary>
        public static string DiagnosticsLatchIdList(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count == 0)
                return DiagnosticsLatchClear;
            if (ids.Count == 1)
                return ids[0] ?? string.Empty;
            return string.Join(", ", ids);
        }

        /// <summary>
        /// Per-carrier snapshot detail beyond RemainingMs (bits already on the record).
        /// </summary>
        public static string DiagnosticsSnapshotDetail(
            bool conditionSatisfied,
            bool active,
            bool eligible,
            bool freshFire,
            bool firedThisTick,
            int? remainingMs)
        {
            var parts = new List<string>(6)
            {
                active ? DiagnosticsSnapActive : DiagnosticsSnapInactive,
                eligible ? DiagnosticsSnapEligible : DiagnosticsSnapIneligible,
            };
            if (conditionSatisfied)
                parts.Add(DiagnosticsSnapSatisfied);
            if (freshFire)
                parts.Add(DiagnosticsSnapFreshFire);
            else if (firedThisTick)
                parts.Add(DiagnosticsSnapFired);
            if (remainingMs.HasValue)
                parts.Add(DiagnosticsRemainingMs(remainingMs.Value));
            return string.Join(" · ", parts);
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

        /// <summary>
        /// Read-only playlist step line: "Logo · 60 s" or "Logo inverted · skipped · can't run here".
        /// </summary>
        public static string PlaylistStepLine(string stepName, string durationOrSkip)
        {
            if (string.IsNullOrEmpty(durationOrSkip))
                return stepName ?? string.Empty;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1}",
                stepName ?? string.Empty,
                durationOrSkip);
        }

        /// <summary>Playlist step duration label: "60 s".</summary>
        public static string PlaylistStepDuration(int durationMs)
        {
            if (durationMs >= 1000 && durationMs % 1000 == 0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} s",
                    durationMs / 1000);
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ms",
                durationMs);
        }

        /// <summary>
        /// Read-only duration label for a playlist step. Sub-floor authored durations
        /// render the clamped effective value + <see cref="PlaylistStepDurationClamped"/>
        /// (P2 degrade-visible; document value stays intact).
        /// </summary>
        public static string PlaylistStepDurationLabel(PlaylistStep step)
        {
            if (step == null || !step.DurationMsPresent)
                return null;

            int authored = step.DurationMs;
            bool clamped = step.DurationClampedAtRuntime
                || authored < SeatArbiter.MinDwellMs;
            if (!clamped)
                return PlaylistStepDuration(authored);

            int effective = SeatArbiter.MinDwellMs;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1}",
                PlaylistStepDuration(effective),
                PlaylistStepDurationClamped);
        }

        /// <summary>
        /// Diagnostics idle/floor line when a playlist is active:
        /// "Outside a session · Screensaver · step Logo (skipped: …)".
        /// </summary>
        public static string DiagnosticsPlaylistFloor(
            string playlistName, string activeStepName, string skipSummary)
        {
            if (string.IsNullOrEmpty(skipSummary))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · {1}",
                    playlistName ?? string.Empty,
                    activeStepName ?? string.Empty);
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} ({2})",
                playlistName ?? string.Empty,
                activeStepName ?? string.Empty,
                skipSummary);
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

        // ── Lifetime pairs (Q8 ruled scheme) ─────────────────────────────
        // Ladder short form + form long form, both generated from LifetimeKind +
        // DurationMs. Canvas/brief lifetime sets die; ConditionSentence and ladder
        // details consume these. Cycle composes period + summon lifetime.

        /// <summary>
        /// Ladder detail-cell lifetime suffix (dimmer tail after the condition).
        /// Includes the leading " · " separator when non-empty.
        /// </summary>
        public static string LifetimeLadderSuffix(LifetimeKind kind, int durationMs = 0)
        {
            switch (kind)
            {
                case LifetimeKind.WhileTrue:
                    return " · while it's true";
                case LifetimeKind.ForDuration:
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        " · for {0} s",
                        SecondsFromMs(durationMs));
                case LifetimeKind.UntilDismissed:
                    return " · until dismissed";
                case LifetimeKind.OnChange:
                    return " · when it changes";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Form radio / label for a lifetime kind (5f "For how long").
        /// Duration kinds include the shown N seconds.
        /// </summary>
        public static string LifetimeFormLabel(LifetimeKind kind, int durationMs = 0)
        {
            switch (kind)
            {
                case LifetimeKind.WhileTrue:
                    return "While the condition is true";
                case LifetimeKind.ForDuration:
                    return LifetimeForDurationLabel(SecondsFromMs(durationMs));
                case LifetimeKind.UntilDismissed:
                    return "Until dismissed";
                case LifetimeKind.OnChange:
                    return "When the value changes";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Editable for-duration row prefix (before the seconds field): "For a duration (".
        /// Paired with <see cref="LifetimeForDurationSuffix"/>; composed form is
        /// <see cref="LifetimeForDurationLabel"/>.
        /// </summary>
        public const string LifetimeForDurationPrefix = "For a duration (";

        /// <summary>
        /// Editable for-duration row suffix (after the seconds field): " s)".
        /// </summary>
        public const string LifetimeForDurationSuffix = " s)";

        /// <summary>Composed for-duration form label: "For a duration ({N} s)".</summary>
        public static string LifetimeForDurationLabel(int seconds)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1}{2}",
                LifetimeForDurationPrefix,
                seconds,
                LifetimeForDurationSuffix);
        }

        /// <summary>
        /// Entrypoint form unit-field tooltip (condition value unit, e.g. L / s).
        /// </summary>
        public const string ConditionUnitTooltip = "unit";

        /// <summary>
        /// Cycle period composed with the selected summon's primary lifetime (Q8):
        /// " · every {N} s while it's true" / " · every {N} s for {M} s" /
        /// " · every {N} s until dismissed" / " · every {N} s when it changes".
        /// </summary>
        public static string LifetimeCycleLadderSuffix(
            int periodMs,
            LifetimeKind summonKind = LifetimeKind.WhileTrue,
            int summonDurationMs = 0)
        {
            string period = string.Format(
                CultureInfo.InvariantCulture,
                " · every {0} s",
                SecondsFromMs(periodMs));
            switch (summonKind)
            {
                case LifetimeKind.ForDuration:
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} for {1} s",
                        period,
                        SecondsFromMs(summonDurationMs > 0
                            ? summonDurationMs
                            : Lifetime.DefaultDurationMs));
                case LifetimeKind.UntilDismissed:
                    return period + " until dismissed";
                case LifetimeKind.OnChange:
                    return period + " when it changes";
                default:
                    return period + " while it's true";
            }
        }

        /// <summary>Derived flagged-children aggregate pin: " · while one is active".</summary>
        public const string LifetimeWhileOneActive = " · while one is active";

        private static int SecondsFromMs(int durationMs)
        {
            if (durationMs <= 0)
                return 0;
            // Round to nearest whole second for ladder/form display.
            return (durationMs + 500) / 1000;
        }

        // ── Priority ladder framing (5b / 5j) ────────────────────────────

        /// <summary>"PRIORITY · {N} ENTRIES" — N = ranked rows only (pinned excluded).</summary>
        public static string LadderHeaderCount(int rankedRows)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "PRIORITY · {0} ENTRIES",
                rankedRows);
        }

        /// <summary>5b ladder subtitle (full form).</summary>
        public const string PriorityLadderSubtitle =
            "drag to reorder · the top row whose condition is live owns the display";

        /// <summary>5j ladder subtitle (short form — no second clause).</summary>
        public const string PriorityLadderSubtitleShort = "drag to reorder";

        /// <summary>Ladder header add affordance (phase 3b gated).</summary>
        public const string AddAPage = "+ Add a page";

        /// <summary>Column header: rank.</summary>
        public const string ColRank = "#";

        /// <summary>Column header: page / destination.</summary>
        public const string ColPage = "PAGE";

        /// <summary>Column header: entrypoint / detail.</summary>
        public const string ColEntrypoint = "ENTRYPOINT";

        /// <summary>Column header: status (right-aligned).</summary>
        public const string ColRightNow = "RIGHT NOW";

        /// <summary>Grip glyph drawn on every ranked row.</summary>
        public const string GripGlyph = "⠿";

        /// <summary>Overflow menu glyph drawn on every row.</summary>
        public const string OverflowGlyph = "⋯";

        /// <summary>Expanded-row disclosure glyph (only when expanded — Q4).</summary>
        public const string ExpandedGlyph = "▼";

        // ── Priority detail / aggregate ───────────────────────────────────

        /// <summary>
        /// Manual row detail on Priority (always "standing" form; unmapped amber
        /// lives in the Manual expansion, not the detail cell — digest §5.2).
        /// </summary>
        public const string ManualPagingStanding =
            "standing · targets the page you last stepped to";

        /// <summary>
        /// Expanded seat count summary: "{n} entrypoints · {m} override(s)".
        /// Q9 provisional: entrypoints = enabled summons + derived aggregate;
        /// overrides = all overrides on the destination page.
        /// </summary>
        public static string SeatCountSummary(int entrypoints, int overrides)
        {
            string ep = entrypoints == 1
                ? "1 entrypoint"
                : string.Format(CultureInfo.InvariantCulture, "{0} entrypoints", entrypoints);
            string ov = overrides == 1
                ? "1 override"
                : string.Format(CultureInfo.InvariantCulture, "{0} overrides", overrides);
            return string.Format(CultureInfo.InvariantCulture, "{0} · {1}", ep, ov);
        }

        // ── Expansion sub-headers ────────────────────────────────────────

        /// <summary>ENTRYPOINTS section label.</summary>
        public const string EntrypointsSection = "ENTRYPOINTS";

        /// <summary>"the page holds rank {n} through the first of them".</summary>
        public static string EntrypointsSectionHint(int rank)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "the page holds rank {0} through the first of them",
                rank);
        }

        /// <summary>Add entrypoint affordance inside a seat expansion.</summary>
        public const string AddAnEntrypoint = "+ Add an entrypoint";

        /// <summary>OVERRIDES section label.</summary>
        public const string OverridesSection = "OVERRIDES";

        /// <summary>Read-only overrides hint with link text.</summary>
        public const string OverridesReadOnlyHint =
            "read-only here — edit them on the field";

        /// <summary>Link fragment inside the overrides hint.</summary>
        public const string EditThemOnTheField = "edit them on the field";

        /// <summary>LAYERS ON THIS PAGE section label (5j).</summary>
        public const string LayersSection = "LAYERS ON THIS PAGE";

        /// <summary>Read-only layers hint with link text.</summary>
        public const string LayersReadOnlyHint =
            "read-only here — edit them on the page";

        /// <summary>Link fragment inside the layers hint.</summary>
        public const string EditThemOnThePage = "edit them on the page";

        /// <summary>Dashed BASE block label on a segment page with no base content.</summary>
        public const string BaseBlockLabel = "BASE";

        /// <summary>Dashed BASE block body when the page is layers-only.</summary>
        public const string BaseBlockBlank =
            "blank — this page is only up when one of its layers fires";

        /// <summary>Field-override writes chip: suffix.</summary>
        public const string WritesSuffix = "suffix";

        /// <summary>Layer kind chip label.</summary>
        public const string LayerChip = "layer";

        // ── Manual-row options ───────────────────────────────────────────

        /// <summary>Return-to-base checkbox label prefix.</summary>
        public const string ReturnToBaseAfter = "Return to the Base page after";

        /// <summary>Return-to-base units suffix.</summary>
        public const string SecondsOfNoInput = "s of no input";

        /// <summary>Return-to-base counting note.</summary>
        public const string CountedFromLastPress = "· counted from the last press";

        /// <summary>
        /// Manual expansion consequence when nothing is ranked below (default).
        /// </summary>
        public const string ManualShieldNoneBelow =
            "Rows below this one — currently none — can't interrupt while you're parked on a page. Drag it up to shield more of the ladder, down to shield less.";

        /// <summary>
        /// Manual expansion consequence when ranked rows sit above and nothing below.
        /// </summary>
        public static string ManualShieldNothingBelowNamed(string aboveNames)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Nothing is ranked below this row, so browsing interrupts nothing. {0} both sit above it and can still take the display.",
                aboveNames ?? string.Empty);
        }

        /// <summary>Amber line when next/previous are unmapped.</summary>
        public const string ManualUnmappedAmber =
            "Next and previous aren't mapped on this wheel, so this row can never fire.";

        // ── Overflow menu (seat; Q3 flagged — seats only this phase) ─────

        /// <summary>Menu: navigate to page fields (not a write).</summary>
        public const string EditThisPagesFields = "Edit this page's fields…";

        /// <summary>Menu: open the entrypoint form for a new summon.</summary>
        public const string AddAnEntrypointMenu = "Add an entrypoint…";

        /// <summary>Menu: disable the seat's primary summon.</summary>
        public const string TurnThisEntrypointOff = "Turn this entrypoint off";

        /// <summary>Menu: re-enable a disabled summon.</summary>
        public const string TurnThisEntrypointOn = "Turn this entrypoint on";

        /// <summary>
        /// PROVISIONAL (naming loop): remove rows only — page + authored overrides survive.
        /// Owner ruling: two distinct removal options.
        /// </summary>
        public static string RemovePageRowsOnly(string pageName)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Remove {0} from Priority (keep page & overrides)",
                pageName ?? string.Empty);
        }

        /// <summary>
        /// PROVISIONAL confirm sub-line for rows-only removal — names orphaned overrides.
        /// </summary>
        public static string RemovePageRowsOnlyConfirm(int rankCount, int overrideCount)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "The page stays on the wheel. {0} priority row(s) leave the ladder; its {1} override(s) stay authored on the page.",
                rankCount,
                overrideCount);
        }

        /// <summary>
        /// PROVISIONAL (naming loop): remove rows + the page's authored overrides.
        /// PageEntry still untouched ("the page stays on the wheel").
        /// </summary>
        public static string RemovePageAndOverrides(string pageName)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Remove {0} from Priority and delete its overrides",
                pageName ?? string.Empty);
        }

        /// <summary>PROVISIONAL confirm sub-line for destructive removal.</summary>
        public static string RemovePageAndOverridesConfirm(int rankCount, int overrideCount)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "The page stays on the wheel. {0} priority row(s) leave the ladder, along with its {1} override(s).",
                rankCount,
                overrideCount);
        }

        /// <summary>
        /// Fail-closed: destructive remove-all disabled when no resolvable catalog
        /// (cannot apply the exclusivity law).
        /// </summary>
        public const string RemovePageAndOverridesUnavailable =
            "Can't delete overrides safely without a wheel catalog — only remove from Priority (keep page & overrides) is available.";

        /// <summary>Validation note: Rest.InSessionPage rejects cycle refs.</summary>
        public const string InSessionPageMustBeItmOrHosted =
            "Base page must be an ITM or hosted page (cycles are not allowed).";

        /// <summary>Q6 / 8b: whole-row click tooltip on a layer sub-row.</summary>
        public const string OpenThisLayersForm = "Open this layer's form";

        // ── Base-row menu (UNBOARDED — owner ruling #2) ───────────────────

        /// <summary>
        /// PROVISIONAL: base-row ⋯ menu item. Deliberate mock-fidelity break —
        /// no board draws this editor; owner ordered it built from the 5n shell
        /// minus screens/playlists.
        /// </summary>
        public const string ChooseTheBasePage = "Choose the Base page…";

        // ── Explainer cards ──────────────────────────────────────────────

        /// <summary>5b explainer card label.</summary>
        public const string TwoPinnedRows = "TWO PINNED ROWS";

        /// <summary>5b TWO PINNED ROWS body.</summary>
        public const string TwoPinnedRowsBody =
            "Nothing ranks below them. Base page is where the display falls in a session; Outside a session is where it falls out of one — a row like any other, whose target is picked from the pages on this wheel, the built-in screens, or a playlist.";

        /// <summary>5b DISMISSING card label.</summary>
        public const string Dismissing = "DISMISSING";

        /// <summary>5b DISMISSING body (no canvas-only "See it happen ›" link).</summary>
        public const string DismissingBody =
            "A press of next or previous while a row owns the display waves that row off until its condition fires again, and the display falls to the next live row. A second press steps the Rotation. A row above Manual paging that fires fresh still interrupts.";

        /// <summary>5j ONE LAW card label.</summary>
        public const string OneLaw = "ONE LAW";

        /// <summary>5j ONE LAW body.</summary>
        public const string OneLawBody =
            "A layer applies whenever its page is on the wheel, however it got there. Fuel isn't in this list — it has no entrypoint, and is reached through the Rotation.";

        // ── Idle picker (5n) ─────────────────────────────────────────────

        /// <summary>Picker search placeholder.</summary>
        public const string SearchPagesScreensPlaylists =
            "Search pages, screens and playlists";

        /// <summary>Picker group: authored pages.</summary>
        public const string PagesOnThisWheel = "PAGES ON THIS WHEEL";

        /// <summary>Picker group: built-in screens.</summary>
        public const string BuiltInScreens = "BUILT-IN SCREENS";

        /// <summary>
        /// Picker group: playlists (task #22 lights this; structure via DisplayCopy only).
        /// </summary>
        public const string PlaylistsGroup = "PLAYLISTS";

        /// <summary>Trailing note on the page that is also the Base page.</summary>
        public const string AlsoTheBasePage = "also the Base page";

        /// <summary>Screen capability: supported on this device.</summary>
        public const string SupportedHere = "supported here";

        /// <summary>Screen capability: null capability (untested).</summary>
        public const string UntestedOnThisWheel = "untested on this wheel";

        /// <summary>Selected row trailing note.</summary>
        public const string Selected = "selected";

        /// <summary>5j idle note when no playlist target is set (correct today).</summary>
        public const string NoPlaylistOnThisProfile = "no playlist on this profile";

        /// <summary>Picker footer provenance.</summary>
        public const string PlaylistsWrittenBySetups =
            "Playlists are written by setups — there is no playlist editor in v1.";

        // ── Entrypoint form (5f) ─────────────────────────────────────────

        /// <summary>Form title prefix: "An entrypoint to".</summary>
        public const string AnEntrypointTo = "An entrypoint to";

        /// <summary>When section label.</summary>
        public const string When = "When";

        /// <summary>Source-kind segment: ITM field.</summary>
        public const string SourceItmField = "ITM field";

        /// <summary>Source-kind segment: SimHub property.</summary>
        public const string SourceSimHubProperty = "SimHub property";

        /// <summary>Source-kind segment: Script.</summary>
        public const string SourceScript = "Script";

        /// <summary>Live property value badge.</summary>
        public const string Live = "LIVE";

        /// <summary>Property-row hint under the click target.</summary>
        public const string PropertyRowHint =
            "The whole row is the click target; the value on the right is what the property reads right now.";

        /// <summary>Lifetime section label.</summary>
        public const string ForHowLong = "For how long";

        /// <summary>Until-dismissed amber consequence.</summary>
        public const string UntilDismissedConsequence =
            "A press of next or previous dismisses this. If those aren't mapped to a control, nothing may be able to.";

        /// <summary>Until-dismissed amber link / second sentence.</summary>
        public const string MapControlOrTimedHold =
            "Map a control, or choose a timed hold.";

        /// <summary>Echo section label.</summary>
        public const string ThisEntrypointReads = "This entrypoint reads";

        /// <summary>Echo section hint.</summary>
        public const string AssembledFromBinding =
            "assembled from the binding — the same sentence the ladder shows";

        /// <summary>Rank section label.</summary>
        public const string WhereItRanks = "Where it ranks";

        /// <summary>"Priority {n} — shared with this page's other entrypoints."</summary>
        public static string PrioritySharedRank(int rank)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Priority {0} — shared with this page's other entrypoints.",
                rank);
        }

        /// <summary>Rank section second line.</summary>
        public const string WhileRowAboveLiveWaits =
            "While a row above it is live, this one waits.";

        /// <summary>Form footer: Delete.</summary>
        public const string Delete = "Delete";

        /// <summary>Form footer: Cancel.</summary>
        public const string Cancel = "Cancel";

        /// <summary>Form footer: Save.</summary>
        public const string Save = "Save";

        // ── Segment preview (5j) ─────────────────────────────────────────

        /// <summary>Live segment preview column label.</summary>
        public const string TheSegmentsNow = "THE SEGMENTS NOW";
    }
}
