using System;

namespace FanaBridge.Devices
{
    /// <summary>
    /// How to talk to ONE bound device — the per-class connect/identify/service
    /// logic lives behind this contract, so the carrier and (later) the manager stay
    /// class-agnostic. FF 08 push-drain, SRM request-response, and passive paths are
    /// each a different <see cref="Service"/> body in their own driver.
    /// </summary>
    public interface IDeviceDriver : IDisposable
    {
        /// <summary>The class of device this driver speaks (Base, PedalSet, …).</summary>
        DeviceClass Class { get; }

        /// <summary>Whether the underlying transport is still open.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Advance the driver one tick (push-drain | poll | passive — internal to the
        /// driver, which owns its own timing). Returns true when the
        /// <see cref="Snapshot"/> changed as a result of this call.
        /// </summary>
        bool Service();

        /// <summary>The current settled, normalized identity + hosted attachments.</summary>
        DeviceSnapshot Snapshot { get; }
    }
}
