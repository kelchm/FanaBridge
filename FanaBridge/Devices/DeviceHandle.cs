using System;
using FanaBridge.Protocol;
using FanaBridge.Transport;

namespace FanaBridge.Devices
{
    /// <summary>
    /// The per-device output surface: ONE device's transport plus the encoder set built
    /// over it. Every encoder is bound to this device's own <see cref="IDeviceTransport"/>,
    /// so its dirty-tracking state describes exactly one physical device and can never
    /// alias another's — the property that makes simultaneous multi-device output possible.
    ///
    /// The carrier (<see cref="FanatecBaseDevice"/>) owns one of these, built once over its
    /// stable transport; consumers (the plugin's forwarders today, per-device DeviceInstances
    /// later) read the encoders from the handle rather than from a process-wide singleton.
    /// Caps/identity hang off this handle in a later phase.
    /// </summary>
    public sealed class DeviceHandle
    {
        /// <summary>The device's HID transport — the one all encoders here write through.</summary>
        public IDeviceTransport Transport { get; }

        /// <summary>col03 LED encoder (Rev/Flag/Button) for this device.</summary>
        public LedEncoder Leds { get; }

        /// <summary>col01 legacy LED encoder (legacy/RevStripe wheels) for this device.</summary>
        public LegacyLedEncoder LegacyLeds { get; }

        /// <summary>Display (7-seg / text) encoder for this device.</summary>
        public DisplayEncoder Display { get; }

        /// <summary>Encoder-mode / tuning controller for this device.</summary>
        public FanatecTuningController Tuning { get; }

        public DeviceHandle(IDeviceTransport transport, Action<string> logWarn = null, Action<string> logInfo = null)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Leds = new LedEncoder(transport);
            LegacyLeds = new LegacyLedEncoder(transport);
            Display = new DisplayEncoder(transport);
            Tuning = new FanatecTuningController(transport, logWarn, logInfo);
        }

        /// <summary>
        /// Resets the LED dirty-tracking so the next write reaches hardware unconditionally.
        /// Called when this device's rim changes (firmware resets LED state but the encoder
        /// caches still hold the previous rim's last output). Scoped to THIS device — a rim
        /// swap on one base never clears another's caches.
        /// </summary>
        public void ForceLedResend()
        {
            Leds.ForceDirty();
            LegacyLeds.ForceDirty();
        }
    }
}
