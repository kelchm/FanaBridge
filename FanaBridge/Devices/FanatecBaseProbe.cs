using FanaBridge.Transport;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Recognizes a Fanatec wheelbase: a Fanatec-VID device exposing a col03 (64-byte)
    /// control collection. Runs FIRST (lowest priority value) so that on an ambiguous
    /// VID:PID — e.g. <c>0EB7:0005</c>, which is both a real CSL Elite and the SRM
    /// emulation generation — a real base claims the device via FF 08 before a later
    /// SRM probe gets a chance.
    /// </summary>
    internal sealed class FanatecBaseProbe : IDeviceProbe
    {
        public int Priority => 10;

        public bool CouldMatch(HidDeviceGroup dev)
            => dev != null && dev.Vid == FanatecIds.VendorId && dev.HasCol03;

        public IDeviceDriver TryBind(HidDeviceGroup dev, IDeviceTransport io)
        {
            if (dev == null || io == null)
                return null;

            // Identify-only confirm: a Fanatec col03 device is the base. Seed the
            // initial identity over FF 08 (read-only; the base ignores it if it has
            // nothing to report). Binding is not gated on the read succeeding — the
            // base will push on the next attachment change.
            var driver = new FanatecBaseDriver(io);
            driver.Initialize();
            return driver;
        }
    }
}
