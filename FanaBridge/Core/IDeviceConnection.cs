namespace FanaBridge.Core
{
    /// <summary>
    /// Connection-check surface of the HID device layer, used by
    /// <see cref="ConnectionMonitor"/> for heartbeat and disconnect logic.
    /// </summary>
    public interface IDeviceConnection
    {
        /// <summary>True if the HID streams appear to be open.</summary>
        bool IsConnected { get; }

        /// <summary>True if the USB device is still present on the HID bus.</summary>
        bool IsDevicePresent { get; }

        /// <summary>Close HID streams.</summary>
        void Disconnect();
    }
}
