namespace FanaBridge.ControlMapper
{
    /// <summary>
    /// Owns the plugin's <see cref="ControlMapperBridge"/> across its lifetime:
    /// creates it lazily the first time the integration is enabled (so a missing
    /// Control Mapper type can't affect plugin load), reconciles it to the setting,
    /// and retires it at teardown.
    ///
    /// Once <see cref="Retire"/> has run, <see cref="Reconcile"/> never creates,
    /// registers or re-activates a bridge, even if it was never created before.
    /// Creation and retirement are serialized by one lock, so a reconcile tick that
    /// races teardown either creates the bridge before it is retired (and the
    /// retired bridge then refuses to register) or sees the integration retired and
    /// does nothing.
    /// </summary>
    public class ControlMapperIntegration
    {
        private readonly object _lifecycle = new object();
        private bool _retired;

        // volatile: written on the DataUpdate thread (Reconcile), read in the
        // WheelChanged handler, which may fire from any thread. Ensures the handler
        // observes the constructed bridge; a missed read self-heals next tick.
        private volatile ControlMapperBridge _bridge;

        /// <summary>The bridge, or null if the integration has never been enabled.</summary>
        public ControlMapperBridge Bridge => _bridge;

        /// <summary>
        /// Registers (creating the bridge if needed) when <paramref name="enabled"/>,
        /// deactivates otherwise. Does nothing once retired.
        /// </summary>
        public void Reconcile(bool enabled, object pm)
        {
            if (!enabled)
            {
                _bridge?.Unregister();
                return;
            }

            ControlMapperBridge bridge;
            lock (_lifecycle)
            {
                if (_retired) return;
                bridge = _bridge ?? (_bridge = new ControlMapperBridge());
            }

            // Outside _lifecycle: registering can re-enumerate Control Mapper's
            // controllers, which dispatches synchronously to the UI thread. The bridge
            // serializes this against Retire itself.
            bridge.EnsureRegistered(pm);
            // Stamp a friendly CustomName on the connected wheel's source(s) so the UI
            // shows a readable name while the match key stays the stable id.
            bridge.StampFriendlyNames();
        }

        /// <summary>
        /// Retires the integration for good, at plugin teardown: the bridge, if any, is
        /// deactivated in place (see <see cref="ControlMapperBridge.Retire"/>) and no
        /// later <see cref="Reconcile"/> brings a provider back.
        /// </summary>
        public void Retire()
        {
            lock (_lifecycle)
            {
                _retired = true;
                _bridge?.Retire();
            }
        }
    }
}
