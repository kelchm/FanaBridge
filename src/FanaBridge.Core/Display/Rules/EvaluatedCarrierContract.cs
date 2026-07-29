using System;
using System.Collections.Generic;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// v2-owned default duration constants for carrier evaluation.
    /// </summary>
    public static class CarrierDefaults
    {
        /// <summary>
        /// Default ForDuration window (5000 ms).
        /// </summary>
        public const int DefaultDurationMs = 5000;
    }

    /// <summary>
    /// Frozen per-carrier snapshot after one evaluator tick. E4/E5/E6 consume this shape.
    /// See scratch/plans/display-customization/evaluated-carrier-contract.md.
    /// </summary>
    public readonly struct CarrierTickSnapshot
    {
        public CarrierTickSnapshot(
            string carrierId,
            bool conditionSatisfied,
            bool active,
            bool freshFire,
            bool firedThisTick,
            bool eligible,
            long expiresAtMs,
            int? remainingMs)
        {
            CarrierId = carrierId;
            ConditionSatisfied = conditionSatisfied;
            Active = active;
            FreshFire = freshFire;
            FiredThisTick = firedThisTick;
            Eligible = eligible;
            ExpiresAtMs = expiresAtMs;
            RemainingMs = remainingMs;
        }

        /// <summary>Stable carrier identity (rule id / summon occurrence id).</summary>
        public string CarrierId { get; }

        /// <summary>Level/Derived trigger: hysteresis-adjusted satisfied state. Edge/event: false.</summary>
        public bool ConditionSatisfied { get; }

        /// <summary>Whether the carrier has a live activation this tick (pre-selection).</summary>
        public bool Active { get; }

        /// <summary>
        /// Fresh-fire identity (v2): true when this tick's Fire created a new claim —
        /// the carrier had no live activation immediately before Fire. Window restarts
        /// while already active are NOT fresh.
        /// </summary>
        public bool FreshFire { get; }

        /// <summary>
        /// Raw fire bit: true when Fire() ran this tick, including ForDuration window
        /// restarts and re-fires on a latched carrier. Policy-neutral primitive for
        /// destination-scoped re-arm (E4 latches on FiredThisTick without mutating
        /// evaluator activation).
        /// </summary>
        public bool FiredThisTick { get; }

        /// <summary>Eligibility after runs/InGame gating this tick.</summary>
        public bool Eligible { get; }

        /// <summary>
        /// ForDuration absolute clock end. Undefined (published 0) when not on a timed hold —
        /// consumers must key on RemainingMs / lifetime kind, not this alone.
        /// </summary>
        public long ExpiresAtMs { get; }

        /// <summary>ForDuration remaining ms when active; null otherwise.</summary>
        public int? RemainingMs { get; }

        /// <summary>Build from evaluator runtime after <see cref="CarrierEvaluator.Evaluate"/>.</summary>
        public static CarrierTickSnapshot From(CarrierSpec spec, CarrierRuntime runtime, long nowMs)
            => new CarrierTickSnapshot(
                spec.Id,
                runtime.Satisfied,
                runtime.Active,
                runtime.FreshFireThisTick,
                runtime.FiredThisTick,
                runtime.EligibleNow,
                runtime.ExpiresAt,
                CarrierEvaluator.RemainingMs(spec, runtime, nowMs));
    }

    /// <summary>
    /// D10 status vocabulary for v2 composed-resolution diagnostics.
    /// </summary>
    public enum CarrierPresence
    {
        /// <summary>Condition false / no live activation (D10 "waiting").</summary>
        Waiting,
        /// <summary>Active but lost its surface's priority ladder.</summary>
        Outranked,
        /// <summary>Would win its ladder, but the owning surface is not up.</summary>
        OffScreen,
        /// <summary>Winning and painting.</summary>
        OnScreen,
        /// <summary>
        /// Latched out by a dismiss on this surface while still Active+Eligible.
        /// Display/wheelScreen planes only — not a D10 check word (joins OnScreen
        /// as a non-check state). Content planes stamp <see cref="CarrierRowLabels.Dismissed"/>
        /// instead and keep painting presence.
        /// </summary>
        Dismissed,
    }

    /// <summary>
    /// D10 row labels — config facts, never statuses. Flags so multiple labels may apply.
    /// </summary>
    [Flags]
    public enum CarrierRowLabels
    {
        None = 0,
        Off = 1 << 0,
        NoWheel = 1 << 1,
        Paused = 1 << 2,
        KeptAsIs = 1 << 3,
        CantRunHere = 1 << 4,
        Dismissed = 1 << 5,
        /// <summary>
        /// Diagnostics: carrier is outside its runs/session scope this tick
        /// (e.g. runs:idle while in-game). Presence stays Waiting; UI copy maps later.
        /// </summary>
        OutOfSessionScope = 1 << 6,
        /// <summary>
        /// Diagnostics: catalog capability for this command/region is untested (null).
        /// Warn-and-allow (§14) — the carrier still competes; UI may show a provisional badge.
        /// Additive (E6): stamped on wheel-screen winners/floor rows when ScreenCommands is null.
        /// </summary>
        Untested = 1 << 7,
    }

    /// <summary>
    /// One surface's winner after arbitration (seat / layer ladder / field / wheel-screen).
    /// Defined here for the composed-resolution record; not emitted until E7.
    /// Destination context lives here (not on per-carrier snapshots) by design.
    /// </summary>
    public readonly struct SurfaceWinner
    {
        public SurfaceWinner(string surfaceId, string winnerCarrierId, string destinationId)
        {
            SurfaceId = surfaceId;
            WinnerCarrierId = winnerCarrierId;
            DestinationId = destinationId;
        }

        /// <summary>Surface key (e.g. seat, segment-page, field param id, wheelScreen).</summary>
        public string SurfaceId { get; }

        /// <summary>Winning carrier id, or null when the surface rests on base/idle.</summary>
        public string WinnerCarrierId { get; }

        /// <summary>Resolved destination identity (page / screen / special), or null.</summary>
        public string DestinationId { get; }
    }

    /// <summary>
    /// Per-carrier v2 status for the composed-resolution record (D10 vocabulary).
    /// </summary>
    public readonly struct CarrierResolutionStatus
    {
        public CarrierResolutionStatus(
            string carrierId,
            string surfaceId,
            string destinationId,
            CarrierPresence? presence,
            int? remainingMs,
            CarrierRowLabels rowLabels)
        {
            CarrierId = carrierId;
            SurfaceId = surfaceId;
            DestinationId = destinationId;
            Presence = presence;
            RemainingMs = remainingMs;
            RowLabels = rowLabels;
        }

        public string CarrierId { get; }
        /// <summary>Surface this carrier competed on (required for outranked vs off-screen).</summary>
        public string SurfaceId { get; }
        /// <summary>Proposed destination context for this carrier (may differ from surface winner).</summary>
        public string DestinationId { get; }
        /// <summary>
        /// D10 presence on this surface, or null when the emitting arbiter does not own
        /// presence for <see cref="SurfaceId"/> (E4 leaves field/page surfaces null; E5 fills).
        /// </summary>
        public CarrierPresence? Presence { get; }
        public int? RemainingMs { get; }
        public CarrierRowLabels RowLabels { get; }
    }

    /// <summary>
    /// Read-side capability-envelope summary for the composed-resolution record
    /// (catalog §14). Projection of already-built field/screen capability — no
    /// engine gating lives here. Null tri-states = untested.
    /// </summary>
    public sealed class CapabilityEnvelopeSummary
    {
        public static CapabilityEnvelopeSummary Empty { get; } =
            new CapabilityEnvelopeSummary(0, null, null, null, null, null);

        public CapabilityEnvelopeSummary(
            int fieldParamCount,
            bool? screenLogo,
            bool? screenBlank,
            bool? screenWhite,
            bool? screenLogoInverted,
            bool? provisional)
        {
            FieldParamCount = fieldParamCount < 0 ? 0 : fieldParamCount;
            ScreenLogo = screenLogo;
            ScreenBlank = screenBlank;
            ScreenWhite = screenWhite;
            ScreenLogoInverted = screenLogoInverted;
            Provisional = provisional;
        }

        /// <summary>Count of params present on this wheel's field capability map.</summary>
        public int FieldParamCount { get; }

        /// <summary>Screen-command tri-state: logo.</summary>
        public bool? ScreenLogo { get; }

        /// <summary>Screen-command tri-state: blank.</summary>
        public bool? ScreenBlank { get; }

        /// <summary>Screen-command tri-state: white.</summary>
        public bool? ScreenWhite { get; }

        /// <summary>Screen-command tri-state: logo inverted.</summary>
        public bool? ScreenLogoInverted { get; }

        /// <summary>Catalog provisional badge on the screen-commands block.</summary>
        public bool? Provisional { get; }
    }

    /// <summary>
    /// One-tick composed-resolution record: per-surface winners + per-carrier statuses.
    /// Shape pinned by E3; emitted by E7; one-tick golden lands with the emitter.
    /// The evaluator does NOT produce this — arbiters compose it from snapshots.
    /// Additive device-level block (E7): page knowledge + reject edge flags for E8.
    /// Additive diagnostics publication (record-gap): ITM device id, SurfaceHeld /
    /// ReleaseEdge, dismissal latch ids, capability-envelope summary. CarrierSnapshots
    /// already carry full per-carrier evaluator state beyond RemainingMs.
    /// </summary>
    public sealed class ComposedResolutionRecord
    {
        private static readonly IReadOnlyList<string> NoDismissedIds =
            Array.Empty<string>();

        public ComposedResolutionRecord(
            long tickMs,
            string deviceKey,
            IReadOnlyList<SurfaceWinner> surfaceWinners,
            IReadOnlyList<CarrierResolutionStatus> carrierStatuses,
            IReadOnlyList<CarrierTickSnapshot> carrierSnapshots)
            : this(tickMs, deviceKey, surfaceWinners, carrierStatuses, carrierSnapshots,
                CurrentPageKnowledge.Unknown, revertedThisTick: false, adoptWarnedThisTick: false,
                hasDeviceBlock: false,
                itmDeviceId: null,
                surfaceHeld: false,
                releaseEdge: false,
                dismissedCarrierIds: null,
                capabilityEnvelope: null,
                hasCapabilityEnvelope: false)
        {
        }

        public ComposedResolutionRecord(
            long tickMs,
            string deviceKey,
            IReadOnlyList<SurfaceWinner> surfaceWinners,
            IReadOnlyList<CarrierResolutionStatus> carrierStatuses,
            IReadOnlyList<CarrierTickSnapshot> carrierSnapshots,
            CurrentPageKnowledge pageKnowledge,
            bool revertedThisTick,
            bool adoptWarnedThisTick)
            : this(tickMs, deviceKey, surfaceWinners, carrierStatuses, carrierSnapshots,
                pageKnowledge, revertedThisTick, adoptWarnedThisTick, hasDeviceBlock: true,
                itmDeviceId: null,
                surfaceHeld: false,
                releaseEdge: false,
                dismissedCarrierIds: null,
                capabilityEnvelope: null,
                hasCapabilityEnvelope: false)
        {
        }

        /// <summary>
        /// Full composition stamp: device block + read-side diagnostics publication.
        /// Partial emitter slices use the shorter constructors.
        /// </summary>
        public ComposedResolutionRecord(
            long tickMs,
            string deviceKey,
            IReadOnlyList<SurfaceWinner> surfaceWinners,
            IReadOnlyList<CarrierResolutionStatus> carrierStatuses,
            IReadOnlyList<CarrierTickSnapshot> carrierSnapshots,
            CurrentPageKnowledge pageKnowledge,
            bool revertedThisTick,
            bool adoptWarnedThisTick,
            byte? itmDeviceId,
            bool surfaceHeld,
            bool releaseEdge,
            IReadOnlyList<string> dismissedCarrierIds,
            CapabilityEnvelopeSummary capabilityEnvelope)
            : this(tickMs, deviceKey, surfaceWinners, carrierStatuses, carrierSnapshots,
                pageKnowledge, revertedThisTick, adoptWarnedThisTick, hasDeviceBlock: true,
                itmDeviceId: itmDeviceId,
                surfaceHeld: surfaceHeld,
                releaseEdge: releaseEdge,
                dismissedCarrierIds: dismissedCarrierIds,
                capabilityEnvelope: capabilityEnvelope,
                hasCapabilityEnvelope: capabilityEnvelope != null)
        {
        }

        private ComposedResolutionRecord(
            long tickMs,
            string deviceKey,
            IReadOnlyList<SurfaceWinner> surfaceWinners,
            IReadOnlyList<CarrierResolutionStatus> carrierStatuses,
            IReadOnlyList<CarrierTickSnapshot> carrierSnapshots,
            CurrentPageKnowledge pageKnowledge,
            bool revertedThisTick,
            bool adoptWarnedThisTick,
            bool hasDeviceBlock,
            byte? itmDeviceId,
            bool surfaceHeld,
            bool releaseEdge,
            IReadOnlyList<string> dismissedCarrierIds,
            CapabilityEnvelopeSummary capabilityEnvelope,
            bool hasCapabilityEnvelope)
        {
            TickMs = tickMs;
            DeviceKey = deviceKey;
            SurfaceWinners = surfaceWinners;
            CarrierStatuses = carrierStatuses;
            CarrierSnapshots = carrierSnapshots;
            PageKnowledge = pageKnowledge;
            RevertedThisTick = revertedThisTick;
            AdoptWarnedThisTick = adoptWarnedThisTick;
            HasDeviceBlock = hasDeviceBlock;
            ItmDeviceId = itmDeviceId;
            SurfaceHeld = surfaceHeld;
            ReleaseEdge = releaseEdge;
            DismissedCarrierIds = dismissedCarrierIds ?? NoDismissedIds;
            CapabilityEnvelope = hasCapabilityEnvelope
                ? (capabilityEnvelope ?? CapabilityEnvelopeSummary.Empty)
                : null;
            HasCapabilityEnvelope = hasCapabilityEnvelope;
        }

        /// <summary>Engine clock at tick evaluation.</summary>
        public long TickMs { get; }

        /// <summary>Device identity for multi-device diagnostics aggregation.</summary>
        public string DeviceKey { get; }

        /// <summary>One entry per surface that ran this tick (seat, layers, fields, wheel).</summary>
        public IReadOnlyList<SurfaceWinner> SurfaceWinners { get; }

        /// <summary>Per-carrier UI presence + row labels, ladder order within each surface.</summary>
        public IReadOnlyList<CarrierResolutionStatus> CarrierStatuses { get; }

        /// <summary>
        /// Raw evaluator snapshots (activation, fresh-fire, FiredThisTick, clocks).
        /// Full per-carrier state beyond <see cref="CarrierResolutionStatus.RemainingMs"/>.
        /// </summary>
        public IReadOnlyList<CarrierTickSnapshot> CarrierSnapshots { get; }

        // ── Additive device-level block (contract §6.1 / E7) ──────────────

        /// <summary>
        /// True when this record carries the device-level block (page knowledge + edge flags).
        /// Partial emitter slices from E4/E5/E6 alone leave this false until merge/E8 stamps it.
        /// </summary>
        public bool HasDeviceBlock { get; }

        /// <summary>
        /// Honest current-page knowledge. Connect-before-first-announcement is
        /// <see cref="CurrentPageKnowledge.Unknown"/>.
        /// </summary>
        public CurrentPageKnowledge PageKnowledge { get; }

        /// <summary>Director reject push-back issued this tick (edge flag).</summary>
        public bool RevertedThisTick { get; }

        /// <summary>Director adopt-with-warning this tick (edge flag).</summary>
        public bool AdoptWarnedThisTick { get; }

        // ── Additive diagnostics publication (record-gap round) ───────────

        /// <summary>
        /// Distinct ITM display device id (3 = standard / PBME family, 4 = Bentley, …).
        /// Null on partial emitter slices; composition stamps the constructor input.
        /// <see cref="DeviceKey"/> remains the wheel code only.
        /// </summary>
        public byte? ItmDeviceId { get; }

        /// <summary>
        /// Explicit wheel-screen col01 hold this tick (E6 <c>SurfaceHeld</c>).
        /// Not inferred from winner destination.
        /// </summary>
        public bool SurfaceHeld { get; }

        /// <summary>
        /// Explicit wheel-screen release edge this tick (E6 <c>ReleaseEdge</c>).
        /// </summary>
        public bool ReleaseEdge { get; }

        /// <summary>
        /// Dismissal latch carrier ids this tick (seat + wheel-screen latches, union).
        /// Ordered ordinal for determinism. Empty when none latched.
        /// </summary>
        public IReadOnlyList<string> DismissedCarrierIds { get; }

        /// <summary>
        /// True when <see cref="CapabilityEnvelope"/> was stamped (composition tenure).
        /// Partial slices leave this false.
        /// </summary>
        public bool HasCapabilityEnvelope { get; }

        /// <summary>
        /// Capability-envelope summary (field param count + screen-command tri-states).
        /// Null when not stamped.
        /// </summary>
        public CapabilityEnvelopeSummary CapabilityEnvelope { get; }
    }
}
