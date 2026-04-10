using System;
using System.Diagnostics;
using FanaBridge.Protocol;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// <see cref="ISegmentDisplay"/> backed by a real Fanatec 7-segment display
    /// via <see cref="DisplayEncoder"/>. Handles deduplication (skip if unchanged)
    /// and keepalive (resend after 30s to prevent hardware display timeout).
    /// </summary>
    public class HardwareSegmentDisplay : ISegmentDisplay
    {
        /// <summary>
        /// The hardware display blanks itself after ~30 seconds without commands.
        /// </summary>
        internal const long KeepAliveMs = 30_000;

        private readonly DisplayEncoder _encoder;
        private readonly Func<long> _clock;

        private byte _lastSeg0;
        private byte _lastSeg1;
        private byte _lastSeg2;
        private long _lastSendMs;
        private bool _hasSent;

        /// <summary>
        /// Creates a hardware display adapter.
        /// </summary>
        /// <param name="encoder">The protocol encoder for col01 HID output.</param>
        public HardwareSegmentDisplay(DisplayEncoder encoder)
            : this(encoder, DefaultClock)
        {
        }

        /// <summary>
        /// Creates a hardware display adapter with an injectable clock (for testing).
        /// </summary>
        internal HardwareSegmentDisplay(DisplayEncoder encoder, Func<long> clock)
        {
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public void Send(byte seg0, byte seg1, byte seg2)
        {
            if (_hasSent && seg0 == _lastSeg0 && seg1 == _lastSeg1 && seg2 == _lastSeg2
                && _clock() - _lastSendMs < KeepAliveMs)
            {
                return; // dedup: skip if unchanged and not stale
            }

            _lastSeg0 = seg0;
            _lastSeg1 = seg1;
            _lastSeg2 = seg2;
            _lastSendMs = _clock();
            _hasSent = true;
            _encoder.SetDisplay(seg0, seg1, seg2);
        }

        public void Clear()
        {
            _encoder.ClearDisplay();
            _lastSeg0 = 0;
            _lastSeg1 = 0;
            _lastSeg2 = 0;
            _lastSendMs = _clock();
            _hasSent = true;
        }

        public void SendCommand(DeviceCommand command)
        {
            // Currently only FanatecLogo is defined; the firmware logo command
            // is sent via DisplayEncoder when available. For now, treat as a
            // keepalive-extending event.
            _lastSendMs = _clock();
        }

        public void Keepalive()
        {
            if (!_hasSent) return;
            if (_clock() - _lastSendMs >= KeepAliveMs)
            {
                _lastSendMs = _clock();
                _encoder.SetDisplay(_lastSeg0, _lastSeg1, _lastSeg2);
            }
        }

        private static long DefaultClock()
        {
            return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        }
    }
}
