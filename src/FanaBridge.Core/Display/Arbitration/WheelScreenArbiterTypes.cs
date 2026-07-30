using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Construction options for <see cref="WheelScreenArbiter"/>. Catalog screen-command
    /// capability is injected here so the arbiter stays pure (no catalog I/O).
    /// </summary>
    public sealed class WheelScreenArbiterOptions
    {
        /// <summary>
        /// Screen-command capability envelope from the wheel catalog (§14 tri-state).
        /// null envelope = every command untested (warn + allow). false = inert + CantRunHere.
        /// </summary>
        public ScreenCommandsCapability ScreenCommands { get; set; }

        /// <summary>Device key stamped on the composed-resolution slice.</summary>
        public string DeviceKey { get; set; } = "";

        /// <summary>
        /// Optional diagnostic sink. Runtime-diagnostics echo of capability tri-state
        /// (authoring-time voice is the validator when a catalog is present). Null-untested
        /// capability warns once per rule id / idle construction.
        /// </summary>
        public Action<string> Warn { get; set; }
    }

    /// <summary>One tick of wheel-screen input. Pure: caller injects clock and snapshots.</summary>
    public sealed class WheelScreenArbiterTickInput
    {
        /// <summary>Injected engine clock (ms) — keepalive keys on this only.</summary>
        public long NowMs { get; set; }

        /// <summary>Session state: idle floor only applies out of session.</summary>
        public bool InGame { get; set; } = true;

        /// <summary>
        /// Pre-evaluated snapshots for wheel-screen rules. Arbiter NEVER writes
        /// evaluator state — activation / Eligible / FreshFire are read-only input.
        /// </summary>
        public IReadOnlyList<CarrierTickSnapshot> CarrierSnapshots { get; set; }
            = Array.Empty<CarrierTickSnapshot>();

        /// <summary>
        /// Dismissal latch set scoped to the wheelScreen surface (contract §6.2).
        /// Pure INPUT: a rule id present here is suppressed unless
        /// <see cref="CarrierTickSnapshot.FreshFire"/> re-arms it this tick.
        /// Producer (E7): manual page press latches every Active wheel-screen carrier;
        /// re-arm per contract §3.1. E6 does not mutate the set.
        /// </summary>
        public IReadOnlyCollection<string> DismissedCarrierIds { get; set; }
            = Array.Empty<string>();

        /// <summary>
        /// Caller feedback for the previous tick's <see cref="WheelScreenArbiterTickResult.SendRequested"/>.
        /// <c>true</c> = wire accepted (latch + keepalive stamp). <c>false</c> = declined
        /// (do not latch; win-edge retries). <c>null</c> = no prior send / no feedback yet.
        /// </summary>
        public bool? PreviousSendAccepted { get; set; }

        /// <summary>
        /// True while the seat plane's manual row owns the display (parked page).
        /// The idle FLOOR yields to it — a manual press must page even over a
        /// blank/logo idle choice — while ranked wheel-screen RULES still compete
        /// (rules-over-rest, unchanged).
        /// </summary>
        public bool SeatManualOwnsDisplay { get; set; }
    }

    /// <summary>What the wheel-screen plane should show after one tick.</summary>
    public sealed class WheelScreenIntent
    {
        /// <summary>
        /// Kind of plane outcome this tick.
        /// </summary>
        public WheelScreenOutcomeKind Kind { get; set; }

        /// <summary>
        /// When <see cref="Kind"/> is <see cref="WheelScreenOutcomeKind.DeferredToDisplayPlane"/>,
        /// why this plane is silent so E7 can paint / park without guessing.
        /// </summary>
        public WheelScreenDeferReason DeferReason { get; set; }

        /// <summary>
        /// Screen command when <see cref="Kind"/> is <see cref="WheelScreenOutcomeKind.Screen"/>
        /// (rule winner or idle-floor screen/blank). Null on silence / deferred.
        /// </summary>
        public WheelScreenCommand? Command { get; set; }

        /// <summary>Winning rule id, or floor id when the idle floor holds a screen; null on silence/deferred.</summary>
        public string WinnerCarrierId { get; set; }

        /// <summary>
        /// Destination identity (<c>screen:logo</c>, …) when a screen is desired; null on silence/deferred.
        /// </summary>
        public string DestinationId { get; set; }

        /// <summary>
        /// Last ACCEPTED (latched) screen — mirror truth. Null when never accepted / after release.
        /// During declined retries of a new screen this stays on the previous accepted command.
        /// </summary>
        public WheelScreenCommand? LatchedCommand { get; set; }

        /// <summary>True when a send was accepted and not yet released (hardware-held).</summary>
        public bool Latched { get; set; }
    }

    /// <summary>Plane outcome discriminator.</summary>
    public enum WheelScreenOutcomeKind
    {
        /// <summary>
        /// No screen command and no deferred blank work — display plane owns the wheel
        /// for ordinary in-session / page-owned content reasons.
        /// </summary>
        Silence = 0,
        /// <summary>A firmware screen command (rule or idle floor screen/blank).</summary>
        Screen,
        /// <summary>
        /// Blank/idle compiles to a non-command path owned by the display plane
        /// (painted frame and/or park-on-Legacy). See <see cref="WheelScreenDeferReason"/>.
        /// </summary>
        DeferredToDisplayPlane,
    }

    /// <summary>
    /// Why the wheel-screen plane deferred blank/idle work to the display plane
    /// (contract §6.2 three-row blank-compile table).
    /// </summary>
    public enum WheelScreenDeferReason
    {
        None = 0,
        /// <summary><c>rest.idle</c> is a page — display plane owns the wheel.</summary>
        PageIdle,
        /// <summary>
        /// Blank on an ITM wheel without a blank command: park on Legacy and paint.
        /// Mirror of <see cref="IdleSpec.ParkOnLegacyForBlank"/>.
        /// </summary>
        ParkOnLegacyForBlank,
        /// <summary>
        /// Blank on a segment wheel without a blank command: E5/E7 paints an all-off frame.
        /// </summary>
        PaintBlankFrame,
    }

    /// <summary>Full pure-arbiter result for one tick.</summary>
    public sealed class WheelScreenArbiterTickResult
    {
        /// <summary>
        /// Composed-resolution slice for the wheel-screen surface only.
        /// E6 is the sole presence owner for <see cref="DestinationIds.WheelScreenSurfaceId"/>.
        /// </summary>
        public ComposedResolutionRecord Resolution { get; set; }

        public WheelScreenIntent Intent { get; set; }

        /// <summary>
        /// True when this plane holds col01 (desired screen winner). E7 must not write the
        /// composed segment frame and must pass this into
        /// <c>FrameComposerTickInput.SegmentSurfaceHeldByWheelScreen</c> (contract §6.2).
        /// </summary>
        public bool SurfaceHeld { get; set; }

        /// <summary>
        /// True on the tick the plane releases a previously latched screen — E7 must force
        /// a reclaim write (exit blank + next content write). Port of ReleaseSpecialIfLatched.
        /// </summary>
        public bool ReleaseEdge { get; set; }

        /// <summary>
        /// True when E7 should send a special-screen frame this tick (win-edge or keepalive).
        /// Declined sends do not latch — report via next tick's
        /// <see cref="WheelScreenArbiterTickInput.PreviousSendAccepted"/>.
        /// </summary>
        public bool SendRequested { get; set; }

        /// <summary>Command to send when <see cref="SendRequested"/>; null otherwise.</summary>
        public WheelScreenCommand? SendCommand { get; set; }

        /// <summary>Firmware pattern byte when <see cref="SendRequested"/>; null otherwise.</summary>
        public byte? SendPattern { get; set; }

        /// <summary>Carrier id of the send source (rule id or idle floor id); null when no send.</summary>
        public string SendCarrierId { get; set; }

        /// <summary>
        /// True when the winning/floor screen command has untested (null) catalog capability.
        /// Recorded outcome: the command still takes the surface (warn-and-allow §14).
        /// </summary>
        public bool WinnerCapabilityUntested { get; set; }
    }
}
