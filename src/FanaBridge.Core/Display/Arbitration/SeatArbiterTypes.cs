using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Construction options for <see cref="SeatArbiter"/>. Catalog-derived maps are
    /// injected here so the arbiter stays pure (no catalog I/O).
    /// </summary>
    public sealed class SeatArbiterOptions
    {
        /// <summary>
        /// Field-param → primary-host catalog page id (ITM aggregate membership, §5).
        /// Missing entries for flagged ITM overrides degrade visibly (rows + labels);
        /// they never silently vanish. Plan note: a capability-envelope edge is a rebuild
        /// edge (same as config) — membership is frozen for the arbiter instance.
        /// </summary>
        public IReadOnlyDictionary<ushort, string> PrimaryHostByParam { get; set; }

        /// <summary>Device key stamped on the composed-resolution record.</summary>
        public string DeviceKey { get; set; } = "";

        /// <summary>
        /// Optional diagnostic sink (cycle-adopt reject, host-map degrade, etc.).
        /// Warn-once messages are emitted at most one time per reason key.
        /// </summary>
        public Action<string> Warn { get; set; }
    }

    /// <summary>
    /// Manual / adopted / native page press for one tick. Updates the manual-row target
    /// and may dismiss a live entrypoint destination (D8). Walk order is caller-supplied
    /// (E7a); this only steps an already-compiled list.
    /// </summary>
    public readonly struct SeatManualInput
    {
        /// <summary>
        /// Destination the press adopts as the remembered manual target.
        /// Null when the press is a pure walk step (uses current target + walk), or when
        /// <see cref="AdoptedUnknownPage"/> is true (uncataloged adopt — rest-with-no-intent).
        /// Cycle destinations are rejected (ignored + warn-once); remembered target
        /// must be a page ref.
        /// </summary>
        public string AdoptedDestinationId { get; }

        /// <summary>
        /// When true, after adopt/dismiss bookkeeping, step the compiled walk from the
        /// (possibly updated) manual target. Direction +1 (next) or -1 (previous).
        /// </summary>
        public int? WalkStep { get; }

        /// <summary>
        /// True when the director adopted a page outside this device's catalog
        /// (<c>ManualNavigation(null)</c>). E4 treats as rest-with-no-intent: no remembered
        /// page destination, no request while the wheel sits there (v9 semantic).
        /// </summary>
        public bool AdoptedUnknownPage { get; }

        public SeatManualInput(
            string adoptedDestinationId,
            int? walkStep = null,
            bool adoptedUnknownPage = false)
        {
            AdoptedDestinationId = adoptedDestinationId;
            WalkStep = walkStep;
            AdoptedUnknownPage = adoptedUnknownPage;
        }

        /// <summary>Adopt a page (and dismiss any live entrypoint).</summary>
        public static SeatManualInput Navigate(string destinationId)
            => new SeatManualInput(destinationId, walkStep: null);

        /// <summary>
        /// Adopt an uncataloged page (director identity null) — rest-with-no-intent.
        /// </summary>
        public static SeatManualInput NavigateUnknownPage()
            => new SeatManualInput(null, walkStep: null, adoptedUnknownPage: true);

        /// <summary>Step the walk from the current manual target (no adopt).</summary>
        public static SeatManualInput StepWalk(int direction = 1)
            => new SeatManualInput(null, walkStep: direction);
    }

    /// <summary>One tick of seat-surface input. Pure: caller injects clock and snapshots.</summary>
    public sealed class SeatArbiterTickInput
    {
        /// <summary>Injected engine clock (ms).</summary>
        public long NowMs { get; set; }

        /// <summary>Session state: in-game vs idle floor.</summary>
        public bool InGame { get; set; } = true;

        /// <summary>
        /// Game-identity edge (ACC → iRacing). Manual remembered target RESETS (ruling 7).
        /// Evaluator applies no game-change policy — E4 owns this.
        /// </summary>
        public bool GameChanged { get; set; }

        /// <summary>
        /// Pre-evaluated snapshots for summons, flagged layers/overrides, and any other
        /// carriers the arbiter ranks. Derived bring-up aggregates are evaluated inside
        /// the arbiter (not supplied here).
        /// </summary>
        public IReadOnlyList<CarrierTickSnapshot> CarrierSnapshots { get; set; }
            = Array.Empty<CarrierTickSnapshot>();

        /// <summary>Optional manual/adopted/native press this tick.</summary>
        public SeatManualInput? Manual { get; set; }

        /// <summary>
        /// Compiled walk destination ids (E7a output). Used only for walk-step presses.
        /// Empty / null = no walk membership.
        /// </summary>
        public IReadOnlyList<string> CompiledWalk { get; set; }
    }

    /// <summary>What the display surface should show after one tick (post-dwell).</summary>
    public sealed class SeatDisplayIntent
    {
        /// <summary>Winning destination identity (page, cycle, or rest semantic).</summary>
        public string DestinationId { get; set; }

        /// <summary>
        /// For a cycle destination: the free-running member currently under the cursor.
        /// Null when the destination is not a cycle.
        /// </summary>
        public string CycleMemberDestinationId { get; set; }

        /// <summary>
        /// Physical page the wheel should show: <see cref="CycleMemberDestinationId"/>
        /// when the destination is a cycle, otherwise <see cref="DestinationId"/>.
        /// </summary>
        public string EffectivePageDestinationId { get; set; }

        /// <summary>Cycle cursor index (0-based); -1 when not a cycle.</summary>
        public int CycleCursor { get; set; } = -1;

        /// <summary>Cycle phase ms into the current member interval; -1 when not a cycle.</summary>
        public long CyclePhaseMs { get; set; } = -1;

        /// <summary>Winning seat/satellite/manual row id, or null for rest floor.</summary>
        public string WinnerRowId { get; set; }

        /// <summary>Winning carrier id (summon / bringUp / manual), or null for rest.</summary>
        public string WinnerCarrierId { get; set; }

        /// <summary>
        /// True when <see cref="EffectivePageDestinationId"/> changed from the previous
        /// emitted intent (a page-change is warranted). False on same-destination seat
        /// handoff (D9) and false on same cycle-member identity. <b>First-tick behavior:</b>
        /// always false on the very first emitted intent (<c>_prev</c> is null) — page is
        /// unknown at connect (law 8); E7 must not treat this flag as an initial request.
        /// </summary>
        public bool DestinationChanged { get; set; }

        /// <summary>True when dwell blocked a logical winner change this tick.</summary>
        public bool DwellHeld { get; set; }

        /// <summary>
        /// Idle semantic choice when not in-game (blank / screen / page). Published on
        /// every out-of-session tick so E6 need not re-read the document — independent
        /// of whether rest currently owns the display plane.
        /// </summary>
        public IdleKind? IdleKind { get; set; }

        /// <summary>Idle screen command when <see cref="IdleKind"/> is Screen.</summary>
        public WheelScreenCommand? IdleScreen { get; set; }

        /// <summary>Idle page destination when <see cref="IdleKind"/> is Page.</summary>
        public string IdlePageDestinationId { get; set; }

        /// <summary>
        /// Idle blank compile: park on Legacy while painting (ITM without blank command).
        /// Mirrors <see cref="Schema2.IdleSpec.ParkOnLegacyForBlank"/> (validator-set).
        /// Additive for E7 — E6 also emits
        /// <c>WheelScreenDeferReason.ParkOnLegacyForBlank</c> on the wheel-screen plane.
        /// </summary>
        public bool ParkOnLegacyForBlank { get; set; }
    }

    /// <summary>Manual-row bookkeeping exposed for diagnostics and E7.</summary>
    public sealed class ManualRowState
    {
        /// <summary>
        /// Remembered page destination after the user has navigated (runtime only;
        /// never persisted). Null when never navigated / after GameChanged reset.
        /// </summary>
        public string RememberedDestinationId { get; set; }

        /// <summary>
        /// True when the user has an explicit remembered page (not merely the landing
        /// / rest default).
        /// </summary>
        public bool HasRememberedTarget { get; set; }

        /// <summary>
        /// Strip-order seed (first non-degraded hosted page in walk order) for bare
        /// Legacy arrival when no remembered page exists (FA3 / FREEZE AMENDMENT 3).
        /// Null when the walk has no hosted member (zero-hosted → silence).
        /// </summary>
        public string LandingDestinationId { get; set; }

        /// <summary>True when the manual row is the current emitted winner.</summary>
        public bool OwnsDisplay { get; set; }

        /// <summary>Ms since last manual target update / press; null if never pressed.</summary>
        public long? MsSinceLastPress { get; set; }

        /// <summary>True when returnToRestAfterMs has cleared the park this tick.</summary>
        public bool ReturnedToRest { get; set; }

        /// <summary>
        /// True when the last manual adopt was an uncataloged page (director identity null).
        /// E4 rests with no page intent — remembered destination stays cleared.
        /// </summary>
        public bool AdoptedUnknownPage { get; set; }
    }

    /// <summary>n-of-m diagnostics for one home-seat derived aggregate.</summary>
    public sealed class AggregateMembership
    {
        public string SeatId { get; set; }
        public string DestinationId { get; set; }
        public string DerivedCarrierId { get; set; }

        /// <summary>Flagged children currently active (and not split-excluded).</summary>
        public int ActiveCount { get; set; }

        /// <summary>Total membership (flagged − split children).</summary>
        public int TotalCount { get; set; }

        /// <summary>Member carrier ids in stable document order.</summary>
        public IReadOnlyList<string> MemberCarrierIds { get; set; }
            = Array.Empty<string>();

        /// <summary>
        /// True when one or more authored flagged children could not join the aggregate
        /// (e.g. missing PrimaryHostByParam entry) — degrade-visible, never silent.
        /// </summary>
        public bool MembershipDegraded { get; set; }
    }

    /// <summary>Full pure-arbiter result for one tick.</summary>
    public sealed class SeatArbiterTickResult
    {
        /// <summary>
        /// Composed-resolution record for the display surface only.
        /// Other surfaces' winners are empty — E5/E6 fill theirs later.
        /// </summary>
        public ComposedResolutionRecord Resolution { get; set; }

        public SeatDisplayIntent Intent { get; set; }

        public ManualRowState Manual { get; set; }

        /// <summary>Per home-seat aggregate n-of-m snapshot.</summary>
        public IReadOnlyList<AggregateMembership> Aggregates { get; set; }
            = Array.Empty<AggregateMembership>();

        /// <summary>
        /// Walk-step intent exposed when a press requested a step (E7a applies order;
        /// E4 only reports the resolved next/prev target it applied).
        /// </summary>
        public string WalkStepResolvedDestinationId { get; set; }

        /// <summary>
        /// True when this tick's press performed a destination dismissal and was therefore
        /// consumed (no walk step, no target change) — round-7b dismiss-and-return.
        /// </summary>
        public bool PressConsumedByDismissal { get; set; }

        /// <summary>
        /// Destination-scoped dismissal latch set this tick (display-surface summon
        /// suppression). E5 first-class input for cross-surface policy; layer/field
        /// activation is untouched (suppress-the-summon-only).
        /// </summary>
        public IReadOnlyCollection<string> DismissedCarrierIds { get; set; }
            = Array.Empty<string>();
    }
}
