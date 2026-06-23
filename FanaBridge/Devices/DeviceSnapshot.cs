namespace FanaBridge.Devices
{
    /// <summary>
    /// A settled, normalized view of one bound device's identity at a point in time:
    /// the device's own class/code plus the peripherals it hosts. Produced by an
    /// <see cref="IDeviceDriver"/>; consumed by the façade today and by the device
    /// manager's peripheral merge later.
    /// </summary>
    public struct DeviceSnapshot
    {
        /// <summary>The bound device's class (Base, PedalSet, …).</summary>
        public DeviceClass Class;

        /// <summary>
        /// FanaBridge code for the device itself (e.g. the wheelbase code), or null
        /// when unrecognized.
        /// </summary>
        public string Code;

        /// <summary>
        /// Raw device-identity byte (for a base, the FF 08 BaseType at offset 0x02).
        /// Retained so an unmapped base is still diagnosable.
        /// </summary>
        public byte BaseTypeByte;

        /// <summary>
        /// Whether the identity is settled (not mid-transition). False while a changed
        /// reading is still debouncing — consumers should suppress output then.
        /// </summary>
        public bool Stable;

        /// <summary>
        /// Whether any device identity has been read yet (for a base, BaseType != 0).
        /// False in the window between the transport opening and the first commit.
        /// </summary>
        public bool HasIdentity;

        /// <summary>Hosted peripherals (wheel/hub, module, …); never null after a commit.</summary>
        public Attachment[] Attachments;

        /// <summary>
        /// The full bytes of the most recent raw identity frame (FF 08 system report)
        /// this snapshot was built from — a private copy for the diagnostics capture.
        /// May be null before the first reading.
        /// </summary>
        public byte[] LastRawReport;
    }
}
