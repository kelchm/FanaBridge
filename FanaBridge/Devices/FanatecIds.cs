namespace FanaBridge.Devices
{
    /// <summary>
    /// Well-known USB identifiers for the device-binding layer. The canonical Fanatec
    /// vendor id lives here so probes, discovery, the transport, and the SimHub
    /// adapters all share one definition.
    /// </summary>
    internal static class FanatecIds
    {
        /// <summary>Fanatec's USB vendor id (0x0EB7).</summary>
        public const ushort VendorId = 0x0EB7;
    }
}
