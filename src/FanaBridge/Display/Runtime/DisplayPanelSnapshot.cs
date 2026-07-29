using System;
using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Runtime
{
    /// <summary>
    /// The ONE immutable cross-thread envelope the per-device Display tab polls: the
    /// ITM status line, v2 composed-resolution record, and display-values snapshot, composed
    /// together by the device instance on the DataUpdate thread and published through
    /// a single volatile field (<see cref="IDisplayPanelHost.Snapshot"/>). The parts
    /// keep their existing producers — the instance composes the status line and
    /// v2 composition snapshot, the ITM driver the values — and
    /// the envelope is recomposed only when a part's reference (or the status string)
    /// actually changed, so an idle frame allocates nothing. Reference equality of
    /// the envelope itself is therefore the UI's "anything new?" check, and each
    /// part's reference its per-part re-render gate.
    ///
    /// One teardown story: every edge that invalidates a part recomposes the envelope
    /// in the same frame, so a stale part can never outlive the session it described.
    /// The edges, and what each clears:
    ///  - connection lost — everything (envelope null);
    ///  - plugin generation rebind (issue #37) — everything (envelope null);
    ///  - display type switched away from ITM — everything (envelope null);
    ///  - ITM display-id change (driver rebuild) — status and values follow the new
    ///    driver from the same frame;
    ///  - customization removed / emptied / ITM disabled — composition only;
    ///  - device End — the values part (the driver stops).
    ///
    /// O12 additive fields (read-side): <see cref="InGame"/>, seat
    /// <see cref="Aggregates"/> / <see cref="Manual"/>. Connection is envelope
    /// presence (null = disconnected).
    /// </summary>
    public sealed class DisplayPanelSnapshot
    {
        private static readonly IReadOnlyList<AggregateMembership> NoAggregates =
            Array.Empty<AggregateMembership>();

        internal DisplayPanelSnapshot(string itmStatus, DisplayValuesSnapshot values,
            DateTime composedAtUtc,
            ComposedResolutionRecord composedResolution = null,
            bool inGame = false,
            IReadOnlyList<AggregateMembership> aggregates = null,
            ManualRowState manual = null)
        {
            ItmStatus = itmStatus;
            Values = values;
            ComposedAtUtc = composedAtUtc;
            ComposedResolution = composedResolution;
            InGame = inGame;
            Aggregates = aggregates ?? NoAggregates;
            Manual = manual;
        }

        /// <summary>The ITM lifecycle status line, or null while this device isn't
        /// driving an ITM display.</summary>
        public string ItmStatus { get; }

        /// <summary>
        /// v2 composition diagnostics for this frame, or null when no document is live.
        /// </summary>
        public ComposedResolutionRecord ComposedResolution { get; }

        /// <summary>The latest display-values snapshot (what the ITM display is
        /// showing, for the live mirror), or null while this device isn't driving an
        /// ITM display.</summary>
        public DisplayValuesSnapshot Values { get; }

        /// <summary>Wall-clock UTC when the envelope was composed.</summary>
        public DateTime ComposedAtUtc { get; }

        /// <summary>
        /// O12 (a): in-game vs idle for this frame. Anchored to
        /// <c>DeviceDisplayRuntime</c> tick (<c>GameRunning &amp;&amp; NewData != null</c>).
        /// </summary>
        public bool InGame { get; }

        /// <summary>
        /// O12 (d): home-seat aggregate n-of-m from the last seat tick.
        /// Empty when no v2 composition ran.
        /// </summary>
        public IReadOnlyList<AggregateMembership> Aggregates { get; }

        /// <summary>
        /// O12 (d): manual-row state from the last seat tick, or null.
        /// </summary>
        public ManualRowState Manual { get; }
    }
}
