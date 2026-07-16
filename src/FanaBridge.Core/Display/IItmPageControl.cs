using System;
using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>
    /// The narrow seam through which the page director sees the ITM lifecycle: enough to
    /// observe where the display is (state, confirmed page, sync generation) and to ask for
    /// a page. Everything else about the lifecycle — bring-up, push confirmation, recovery,
    /// send discipline — stays behind it, so the director unit-tests against a fake and the
    /// device wiring supplies <see cref="ItmLifecyclePageControl"/> around the real controller.
    /// </summary>
    public interface IItmPageControl
    {
        /// <summary>The lifecycle's current state (see <see cref="ItmLifecycleState"/>).</summary>
        ItmLifecycleState State { get; }

        /// <summary>The wire page the display is known to be on, or null while unknown
        /// (idle, cold bring-up, wheel change). Adopted from firmware pushes — never assumed
        /// from a sent PageSet.</summary>
        byte? CurrentWirePage { get; }

        /// <summary>Increments every time a push is adopted (sync, re-sync, wheel-button
        /// page change). The director detects landings — and manual navigation — on its edges.</summary>
        long SyncGeneration { get; }

        /// <summary>Asks the lifecycle to show a wire page. Queued behind any in-flight
        /// procedure; dropped while idle or user-disabled; a same-page request is a no-op.</summary>
        void RequestPage(byte wirePage);
    }

    /// <summary>
    /// The production <see cref="IItmPageControl"/>: a read-through wrapper over the device's
    /// <see cref="ItmLifecycleController"/> (reachable via ItmDisplayDriver.Lifecycle — no new
    /// surface on either). The controller reports "page unknown" as 0; the seam translates
    /// that to null so consumers never compare against a magic number.
    /// </summary>
    public sealed class ItmLifecyclePageControl : IItmPageControl
    {
        private readonly ItmLifecycleController _lifecycle;

        public ItmLifecyclePageControl(ItmLifecycleController lifecycle)
        {
            _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        }

        public ItmLifecycleState State => _lifecycle.State;

        public byte? CurrentWirePage
            => _lifecycle.CurrentPage == 0 ? (byte?)null : _lifecycle.CurrentPage;

        public long SyncGeneration => _lifecycle.SyncGeneration;

        public void RequestPage(byte wirePage) => _lifecycle.RequestPage(wirePage);
    }
}
