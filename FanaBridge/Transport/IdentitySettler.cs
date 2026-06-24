namespace FanaBridge.Transport
{
    /// <summary>
    /// Settles raw wheel/hub + module identity readings before they are committed.
    ///
    /// The wheelbase firmware pushes a burst of transient "in transition" FF 08
    /// reports while an attachment is (dis)connecting — e.g. a ~2 s storm of the
    /// wire byte flapping between a hub code and zero during a hub/module reconnect.
    /// Committing those raw makes the detected identity oscillate. Mirroring the
    /// Fanatec service (which sleeps after a settings-change push, then reads),
    /// this holds a changed reading until it has been quiet for a settle window,
    /// then commits whatever the burst settled to.
    ///
    /// Pure and time-injected (the caller passes a millisecond clock) so it can be
    /// unit-tested without timing.
    /// </summary>
    internal sealed class IdentitySettler
    {
        private readonly long _settleMs;

        private bool _hasCommitted;
        private byte _committedWire, _committedModule;

        private byte _pendingWire, _pendingModule;
        private bool _settling;
        private long _deadline;

        public IdentitySettler(int settleMs)
        {
            _settleMs = settleMs < 0 ? 0 : settleMs;
        }

        /// <summary>True when no change is in flight — the committed identity is trustworthy.</summary>
        public bool IsStable => !_settling;

        /// <summary>
        /// Offer a fresh reading. If it differs from the committed identity it
        /// (re)starts the settle window; every further differing reading pushes the
        /// window out, so a flapping burst only commits once it goes quiet.
        /// </summary>
        public void Offer(byte wire, byte module, long nowMs)
        {
            if (!_settling && _hasCommitted && wire == _committedWire && module == _committedModule)
                return; // unchanged steady state

            _settling = true;
            _pendingWire = wire;
            _pendingModule = module;
            _deadline = nowMs + _settleMs;
        }

        /// <summary>
        /// Advance the settle timer. Returns true — with the newly committed
        /// (wire, module) — only when a settled reading is committed AND it differs
        /// from the previously committed one. Returns false while still settling, or
        /// when the burst settled back to the existing identity.
        /// </summary>
        public bool Tick(long nowMs, out byte wire, out byte module)
        {
            wire = _committedWire;
            module = _committedModule;

            if (!_settling || nowMs < _deadline)
                return false;

            _settling = false;
            bool changed = !_hasCommitted
                || _pendingWire != _committedWire
                || _pendingModule != _committedModule;

            _committedWire = _pendingWire;
            _committedModule = _pendingModule;
            _hasCommitted = true;

            wire = _committedWire;
            module = _committedModule;
            return changed;
        }

        /// <summary>Clear all state (e.g. on disconnect).</summary>
        public void Reset()
        {
            _hasCommitted = false;
            _settling = false;
            _committedWire = _committedModule = 0;
            _pendingWire = _pendingModule = 0;
            _deadline = 0;
        }
    }
}
