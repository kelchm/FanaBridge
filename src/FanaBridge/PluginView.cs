using FanaBridge.Core.Devices;

namespace FanaBridge
{
    /// <summary>
    /// One consistent view of a <see cref="FanatecPlugin"/> generation and its
    /// captured <see cref="WheelbaseSnapshot"/>, taken once at a decision's entry
    /// point via <see cref="FanatecPlugin.CaptureView"/>. Everything the decision
    /// uses afterwards — encoders, wheelbase, settings, capabilities — is reached
    /// through this view so state never crosses cores.
    /// </summary>
    internal readonly struct PluginView
    {
        public FanatecPlugin Plugin { get; }
        public FanatecWheelbase Wheelbase { get; }
        public WheelbaseSnapshot Snapshot { get; }

        public bool HasPlugin => Plugin != null;

        private PluginView(FanatecPlugin plugin, FanatecWheelbase wheelbase, WheelbaseSnapshot snapshot)
        {
            Plugin = plugin;
            Wheelbase = wheelbase;
            Snapshot = snapshot;
        }

        internal static PluginView Capture(FanatecPlugin plugin)
        {
            if (plugin == null)
                return default;
            var wheelbase = plugin.Wheelbase;
            return new PluginView(plugin, wheelbase, wheelbase?.Snapshot);
        }
    }
}
