namespace FanaBridge.Devices
{
    /// <summary>
    /// What a bound <see cref="IDeviceDriver"/> is — the kind of physical device a
    /// probe recognized and bound to. Distinct from <see cref="PeripheralKind"/>:
    /// a single <see cref="Base"/> device yields Base + Wheel/Hub + Module
    /// peripherals (see the PnP driver-binding design).
    /// </summary>
    public enum DeviceClass
    {
        Unknown = 0,
        Base,
        PedalSet,
        Shifter,
        Handbrake,
        WheelDirect,
    }
}
