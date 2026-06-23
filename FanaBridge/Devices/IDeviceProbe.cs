namespace FanaBridge.Devices
{
    /// <summary>
    /// Recognizes a device and binds a driver to it, OS-style. Probes are tried in
    /// ascending <see cref="Priority"/> order and the first to return a non-null
    /// driver wins, which resolves ambiguous identities (e.g. a real base vs. an SRM
    /// emulator on the same VID:PID) by order rather than nested conditionals.
    /// </summary>
    public interface IDeviceProbe
    {
        /// <summary>Lower runs first (a real base probe before an SRM probe).</summary>
        int Priority { get; }

        /// <summary>Cheap pre-filter on VID:PID / descriptor — no I/O.</summary>
        bool CouldMatch(HidDeviceGroup dev);

        /// <summary>
        /// Confirm the device is ours via <b>read-only</b> identify I/O and return a
        /// bound driver, or null if it is not. Must never send tuning/flash commands.
        /// </summary>
        IDeviceDriver TryBind(HidDeviceGroup dev, FanaBridge.Transport.IDeviceTransport io);
    }
}
