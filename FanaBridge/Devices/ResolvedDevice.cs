using FanaBridge.Profiles;

namespace FanaBridge.Devices
{
    /// <summary>
    /// The connected device a SimHub <c>DeviceInstance</c> is currently bound to: the
    /// per-device output <see cref="Handle"/> (transport + encoders) plus the
    /// capabilities resolved for that device's attachment and whether its identity is
    /// settled. Returned by <c>FanatecPlugin.ResolveDeviceFor</c>; null when no connected
    /// device hosts a matching rim. Re-resolved each frame, so a rim moved between bases
    /// simply re-binds on the far base's reconnect — no special mid-flight handling.
    /// </summary>
    public sealed class ResolvedDevice
    {
        /// <summary>Transport + encoder set of the device hosting the matched rim.</summary>
        public DeviceHandle Handle { get; }

        /// <summary>Capabilities for the matched attachment (respecting any user override).</summary>
        public WheelCapabilities Caps { get; }

        /// <summary>Whether the hosting device's identity is settled (not mid-transition).</summary>
        public bool Stable { get; }

        public ResolvedDevice(DeviceHandle handle, WheelCapabilities caps, bool stable)
        {
            Handle = handle;
            Caps = caps;
            Stable = stable;
        }
    }
}
