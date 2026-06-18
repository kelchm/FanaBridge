namespace FanaBridge.Transport
{
    /// <summary>
    /// Connection-check surface of a <see cref="FanatecWheelbase"/>, used by
    /// <see cref="ConnectionMonitor"/> for heartbeat and identity servicing.
    /// </summary>
    public interface IWheelbaseConnection
    {
        /// <summary>Whether a Fanatec wheelbase is currently connected.</summary>
        bool IsConnected { get; }

        /// <summary>Whether the wheelbase's HID device is still present on the bus.</summary>
        bool IsDevicePresent { get; }

        /// <summary>Release the wheelbase connection and reset identity state.</summary>
        void Disconnect();

        /// <summary>
        /// Service identity: drain any pushed FF 08 reports and advance the settle
        /// timer. Called every frame. Returns true when a new identity was committed.
        /// </summary>
        bool UpdateIdentity();
    }
}
