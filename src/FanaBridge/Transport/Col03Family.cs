namespace FanaBridge.Transport
{
    /// <summary>
    /// The frame families multiplexed on the col03 input endpoint, as routed by
    /// the transport's reader thread (see <c>Col03FrameClassifier</c>). Each
    /// family backs one <see cref="IReportStream"/> with a single owning consumer.
    /// </summary>
    internal enum Col03Family
    {
        /// <summary>FF 08 system report — base/rim/module identity pushes.</summary>
        Identity,

        /// <summary>FF 05 — ITM subscription/page pushes.</summary>
        Itm,

        /// <summary>0xDD — SRM Conversion Kit DE FA identity replies.</summary>
        Srm,

        /// <summary>FF 03 — tuning read/write responses.</summary>
        Tuning,
    }
}
