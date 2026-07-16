using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>
    /// A wheel-button page change the lifecycle adopted this tick. The engine never sees
    /// raw button input — only the adopted result, after the firmware already switched —
    /// so the engine's manual-override policy is downstream of "adopt, never fight".
    /// </summary>
    public struct ManualNavigation
    {
        public ManualNavigation(ItmPage page)
        {
            Page = page;
        }

        /// <summary>The page the wheel button landed on (content identity, not wire number).</summary>
        public ItmPage Page { get; }
    }

    /// <summary>
    /// One tick's worth of engine input, built fresh by the caller each frame. The engine
    /// holds no reference to it beyond the tick.
    /// </summary>
    public struct RuleEngineInput
    {
        /// <summary>Whether game telemetry is flowing (caller computes: game running with
        /// live data). Gates rule eligibility and, on its rising edge, reverts the resting
        /// target to the base page.</summary>
        public bool InGame { get; set; }

        /// <summary>Live property values for this tick's condition evaluation.</summary>
        public IPropertyReader Properties { get; set; }

        /// <summary>FanaBridge action names fired since the last tick, for
        /// <see cref="ConditionKind.ActionTriggered"/> rules. Null means none.</summary>
        public IReadOnlyList<string> TriggeredActions { get; set; }

        /// <summary>Set on the tick where the lifecycle adopted a wheel-button page change.
        /// Only meaningful for the ITM engine; the legacy engine ignores it.</summary>
        public ManualNavigation? Manual { get; set; }
    }
}
