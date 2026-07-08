using FanaBridge.Protocol;

namespace FanaBridge.Transport
{
    /// <summary>
    /// The per-family input queues behind one col03 connection. The transport's
    /// reader thread calls <see cref="Route"/> for every inbound frame; frames
    /// that classify to no family (axis/noise) are dropped, exactly as the old
    /// signature-skip drains dropped them. One instance per connection session.
    /// </summary>
    internal sealed class Col03QueueSet
    {
        // Capacities are drop-oldest bounds for the window when nothing drains
        // (e.g. the plugin manager restarting between games while the hardware
        // core stays up). Identity/Srm push rarely (attachment changes, one-shot
        // replies); Tuning is strictly elicited; Itm can burst on page flips and
        // must out-size the wheelbase's 32-report hand-off buffer.
        private const int IdentityCapacity = 16;
        private const int ItmCapacity = 64;
        private const int SrmCapacity = 16;
        private const int TuningCapacity = 8;

        private readonly HidReportQueue _identity = new HidReportQueue(IdentityCapacity);
        private readonly HidReportQueue _itm = new HidReportQueue(ItmCapacity);
        private readonly HidReportQueue _srm = new HidReportQueue(SrmCapacity);
        private readonly HidReportQueue _tuning = new HidReportQueue(TuningCapacity);

        public HidReportQueue Get(Col03Family family)
        {
            switch (family)
            {
                case Col03Family.Identity: return _identity;
                case Col03Family.Itm: return _itm;
                case Col03Family.Srm: return _srm;
                case Col03Family.Tuning: return _tuning;
                default: throw new System.ArgumentOutOfRangeException(nameof(family));
            }
        }

        /// <summary>Classifies and enqueues one inbound frame (reader thread).</summary>
        public void Route(byte[] buf, int length)
        {
            if (Col03FrameClassifier.TryClassify(buf, length, out var family))
                Get(family).Enqueue(buf, length);
        }

        /// <summary>Closes every family queue (wakes blocked readers with -1).</summary>
        public void Close()
        {
            _identity.Close();
            _itm.Close();
            _srm.Close();
            _tuning.Close();
        }
    }
}
