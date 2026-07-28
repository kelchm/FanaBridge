using System;
using System.Collections.Generic;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Frozen per-carrier snapshot after one evaluator tick. E4/E5/E6 consume this shape;
    /// the v9 engine does not yet emit it (selection still builds <see cref="RuleLiveState"/>).
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
            bool legacySupersededV9,
            bool eligible,
            long expiresAtMs,
            int? remainingMs)
        {
            CarrierId = carrierId;
            ConditionSatisfied = conditionSatisfied;
            Active = active;
            FreshFire = freshFire;
            FiredThisTick = firedThisTick;
            LegacySupersededV9 = legacySupersededV9;
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
        /// while already active are NOT fresh. (v9 compat: also true when Superseded
        /// was set before Fire — see contract §3.)
        /// </summary>
        public bool FreshFire { get; }

        /// <summary>
        /// Raw fire bit: true when Fire() ran this tick, including ForDuration window
        /// restarts and re-fires on a latched carrier. Policy-neutral primitive for
        /// destination-scoped re-arm (E4 latches on FiredThisTick without mutating
        /// evaluator activation).
        /// </summary>
        public bool FiredThisTick { get; }

        /// <summary>
        /// v9-path supersede latch reading only. Always false under pure v2 evaluation.
        /// Renamed from Superseded so diagnostics never imply a v2 dismissal latch.
        /// Dropped from the semantic contract for v2; retained for E8 dual-path transition.
        /// </summary>
        public bool LegacySupersededV9 { get; }

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
                runtime.Superseded,
                runtime.EligibleNow,
                runtime.ExpiresAt,
                CarrierEvaluator.RemainingMs(spec, runtime, nowMs));
    }

    /// <summary>
    /// D10 status vocabulary for v2 composed-resolution diagnostics.
    /// Distinct from v9 <see cref="RuleStatus"/> (kept for the engine path only).
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
    /// <see cref="RuleStatus"/> remains v9-engine-only on <see cref="RuleLiveState"/>.
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
    /// One-tick composed-resolution record: per-surface winners + per-carrier statuses.
    /// Shape pinned by E3; emitted by E7; one-tick golden lands with the emitter.
    /// The evaluator does NOT produce this — arbiters compose it from snapshots.
    /// </summary>
    public sealed class ComposedResolutionRecord
    {
        public ComposedResolutionRecord(
            long tickMs,
            string deviceKey,
            IReadOnlyList<SurfaceWinner> surfaceWinners,
            IReadOnlyList<CarrierResolutionStatus> carrierStatuses,
            IReadOnlyList<CarrierTickSnapshot> carrierSnapshots)
        {
            TickMs = tickMs;
            DeviceKey = deviceKey;
            SurfaceWinners = surfaceWinners;
            CarrierStatuses = carrierStatuses;
            CarrierSnapshots = carrierSnapshots;
        }

        /// <summary>Engine clock at tick evaluation.</summary>
        public long TickMs { get; }

        /// <summary>Device identity for multi-device diagnostics aggregation.</summary>
        public string DeviceKey { get; }

        /// <summary>One entry per surface that ran this tick (seat, layers, fields, wheel).</summary>
        public IReadOnlyList<SurfaceWinner> SurfaceWinners { get; }

        /// <summary>Per-carrier UI presence + row labels, ladder order within each surface.</summary>
        public IReadOnlyList<CarrierResolutionStatus> CarrierStatuses { get; }

        /// <summary>Raw evaluator snapshots (activation, fresh-fire, FiredThisTick, clocks).</summary>
        public IReadOnlyList<CarrierTickSnapshot> CarrierSnapshots { get; }
    }
}
