namespace FanaBridge.Devices
{
    /// <summary>
    /// The connection-check + service surface that
    /// <see cref="FanaBridge.Transport.ConnectionMonitor"/> drives each frame.
    /// Implemented by the device carrier (and its façade); a test stub can stand in
    /// for it. Replaces the old <c>IWheelbaseConnection</c> now that the serviced
    /// thing is a generic device, not specifically a wheelbase.
    /// </summary>
    public interface IServiceableDevice
    {
        /// <summary>Whether the device's HID transport is currently open.</summary>
        bool IsConnected { get; }

        /// <summary>Whether the device is still present on the HID bus.</summary>
        bool IsDevicePresent { get; }

        /// <summary>Release the connection and reset identity state.</summary>
        void Disconnect();

        /// <summary>
        /// Service the device: drain any pushed identity reports and advance the
        /// settle timer. Called every frame. Returns true when a new identity was
        /// committed this call.
        /// </summary>
        bool Service();
    }
}
