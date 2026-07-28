using System;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Runtime
{
    /// <summary>
    /// The ONE immutable cross-thread envelope the per-device Display tab polls: the
    /// ITM status line (Device Status row + mirror captions), the rule-stack snapshot,
    /// the v2 composed-resolution record, and the display-values snapshot, composed
    /// together by the device instance on the DataUpdate thread and published through
    /// a single volatile field (<see cref="IDisplayPanelHost.Snapshot"/>). The parts
    /// keep their existing producers — the instance composes the status line, the
    /// rule stack / v2 composition their snapshots, the ITM driver the values — and
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
    ///    driver from the same frame; the rule part is replaced when the rebuilt
    ///    stack first composes;
    ///  - customization removed / emptied / ITM disabled — the rule part only;
    ///  - device End — the values part (the driver stops).
    ///
    /// v1 frames publish <see cref="Rules"/> and leave <see cref="ComposedResolution"/>
    /// null; v2 frames publish <see cref="ComposedResolution"/> and leave
    /// <see cref="Rules"/> null (E8 OQ-6; UI adapter is E9).
    /// </summary>
    public sealed class DisplayPanelSnapshot
    {
        internal DisplayPanelSnapshot(string itmStatus, DisplayRuleSnapshot rules,
            DisplayValuesSnapshot values, DateTime composedAtUtc,
            ComposedResolutionRecord composedResolution = null)
        {
            ItmStatus = itmStatus;
            Rules = rules;
            Values = values;
            ComposedAtUtc = composedAtUtc;
            ComposedResolution = composedResolution;
        }

        /// <summary>The ITM lifecycle status line, or null while this device isn't
        /// driving an ITM display.</summary>
        public string ItmStatus { get; }

        /// <summary>The latest rule-stack snapshot, or null while no customization is
        /// active (also null on v2 frames — see <see cref="ComposedResolution"/>).</summary>
        public DisplayRuleSnapshot Rules { get; }

        /// <summary>
        /// v2 composition diagnostics for this frame, or null on v1 frames / when no
        /// v2 document is live. Mutual exclusive with <see cref="Rules"/> for a given
        /// engine tenure.
        /// </summary>
        public ComposedResolutionRecord ComposedResolution { get; }

        /// <summary>The latest display-values snapshot (what the ITM display is
        /// showing, for the live mirror), or null while this device isn't driving an
        /// ITM display.</summary>
        public DisplayValuesSnapshot Values { get; }

        /// <summary>Wall-clock UTC when the envelope was composed (the parts carry
        /// their own composition clocks — see
        /// <see cref="DisplayRuleSnapshot.ComposedAtUtc"/>).</summary>
        public DateTime ComposedAtUtc { get; }
    }
}
