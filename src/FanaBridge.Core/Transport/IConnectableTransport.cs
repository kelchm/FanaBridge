using System;

namespace FanaBridge.Core.Transport
{
    /// <summary>
    /// The connect-lifecycle surface of a device transport, layered on top of
    /// <see cref="IDeviceTransport"/> (the send/read surface the encoders and
    /// protocol readers use). <see cref="FanaBridge.Core.Devices.FanatecWheelbase"/> orchestrates against
    /// this interface rather than the concrete <see cref="FanatecTransport"/>,
    /// so its identity state machine (drain → settle → commit, SRM precedence,
    /// disconnect reset) is unit-testable with a fake transport — the layer where
    /// field bugs have historically lived.
    /// </summary>
    internal interface IConnectableTransport : IDeviceTransport, IDisposable
    {
        /// <summary>Opens the HID interfaces for the given product ID.</summary>
        bool Connect(int productId);

        /// <summary>Closes the HID interfaces and stops the reader.</summary>
        void Disconnect();

        /// <summary>Whether the connected device is still present on the HID bus.</summary>
        bool IsDevicePresent { get; }

        /// <summary>Categorised outcome of the most recent <see cref="Connect"/> attempt.</summary>
        FanatecTransport.TransportConnectStatus LastConnectStatus { get; }
    }
}
