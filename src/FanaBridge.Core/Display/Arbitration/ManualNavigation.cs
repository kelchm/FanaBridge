using FanaBridge.Protocol;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// A wheel-button page change the lifecycle adopted this tick. The engine never sees
    /// raw button input — only the adopted result, after the firmware already switched —
    /// so the engine's manual-override policy is downstream of "adopt, never fight".
    /// </summary>
    public struct ManualNavigation
    {
        public ManualNavigation(ItmPage? page)
        {
            Page = page;
        }

        /// <summary>The page the wheel button landed on (content identity, not wire number),
        /// or null when the display moved to a page outside this device's catalog — there is
        /// no identity to report, and the engine rests on "wherever the wheel is" (no page
        /// intent) until a fresh rule fire or the next game start.</summary>
        public ItmPage? Page { get; }
    }
}
