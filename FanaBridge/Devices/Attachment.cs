namespace FanaBridge.Devices
{
    /// <summary>
    /// A peripheral hosted by a device and reported through its
    /// <see cref="DeviceSnapshot"/> — e.g. the wheel/hub and module a wheelbase
    /// reports over its FF 08 system report, or pedals surfaced by a base.
    /// </summary>
    public struct Attachment
    {
        /// <summary>What this attachment represents (Wheel, Hub, Module, …).</summary>
        public PeripheralKind Kind;

        /// <summary>
        /// FanaBridge profile-match code (e.g. "PSWBMW", "PHUB", "PBMR"), or null
        /// when the raw wire code is unrecognized.
        /// </summary>
        public string Code;

        /// <summary>
        /// The deepest firmware-defined wire byte this attachment was decoded from
        /// (the FF 08 attachment byte 0x18, or module byte 0x1F). Retained even when
        /// <see cref="Code"/> is null so an unrecognized device can still be reported.
        /// </summary>
        public byte WireCode;
    }
}
