namespace FanaBridge.Devices
{
    /// <summary>
    /// Well-known USB identifiers for the device-binding layer. The canonical
    /// Fanatec vendor id lives here so probes and discovery share one definition;
    /// <see cref="FanaBridge.Transport.FanatecWheelbase.FANATEC_VENDOR_ID"/> aliases
    /// it for the existing transport/adapter call sites.
    /// </summary>
    internal static class FanatecIds
    {
        /// <summary>Fanatec's USB vendor id (0x0EB7).</summary>
        public const ushort VendorId = 0x0EB7;
    }
}
