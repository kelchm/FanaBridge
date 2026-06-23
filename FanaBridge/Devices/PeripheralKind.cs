namespace FanaBridge.Devices
{
    /// <summary>
    /// What a logical peripheral represents. A peripheral is reached <i>through</i> a
    /// device/transport but is not the same as one — e.g. a wheelbase
    /// (<see cref="DeviceClass.Base"/>) surfaces a <see cref="Base"/> peripheral plus
    /// a hosted <see cref="Wheel"/>/<see cref="Hub"/> and <see cref="Module"/>.
    /// Kept separate from <see cref="DeviceClass"/> on purpose.
    /// </summary>
    public enum PeripheralKind
    {
        Unknown = 0,
        Base,
        Wheel,
        Hub,
        Module,
        Pedals,
        Shifter,
        Handbrake,
    }
}
