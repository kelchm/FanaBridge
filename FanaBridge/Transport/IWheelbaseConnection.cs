namespace FanaBridge.Transport
{
    /// <summary>
    /// Connection-check surface of a <see cref="FanatecWheelbase"/>, used by
    /// <see cref="ConnectionMonitor"/> for heartbeat and identity polling.
    /// </summary>
    public interface IWheelbaseConnection
    {
        /// <summary>Whether a Fanatec wheelbase is currently connected.</summary>
        bool IsConnected { get; }

        /// <summary>Whether the wheelbase's HID device is still present on the bus.</summary>
        bool IsDevicePresent { get; }

        /// <summary>Release the wheelbase connection and reset identity state.</summary>
        void Disconnect();

        /// <summary>Poll the FF 08 system report for the current wheel/hub identity.</summary>
        bool PollWheelIdentity();
    }
}
