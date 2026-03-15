using System;
using System.Collections.Generic;
using FanaBridge.Profiles;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Encodes and sends LED control reports for Fanatec wheels.
    /// Handles Rev, Flag, and Button (color + intensity) LED channels
    /// with automatic dirty tracking to skip redundant HID writes.
    /// </summary>
    public class LedEncoder
    {
        // ── Protocol constants (col03 report format) ─────────────────────
        private const int REPORT_LENGTH = 64;
        private const int HEADER_SIZE = 3;   // [0xFF, 0x01, subcmd]
        private const int MAX_RGB565_PER_REPORT = (REPORT_LENGTH - HEADER_SIZE) / 2;  // 30

        // Col03 LED report sub-commands
        private const byte SUBCMD_REV_COLORS = 0x00;
        private const byte SUBCMD_FLAG_COLORS = 0x01;
        private const byte SUBCMD_BUTTON_COLORS = 0x02;
        private const byte SUBCMD_BUTTON_INTENSITIES = 0x03;

        // Button LED staging protocol — fixed byte offsets in the 64-byte report.
        // The commit-byte position limits button LEDs to MAX_BUTTON_LEDS.
        private const int BUTTON_COLOR_COMMIT_OFFSET = 27;
        private const int BUTTON_INTENSITY_COMMIT_OFFSET = 18;
        private const int MAX_BUTTON_LEDS = (BUTTON_COLOR_COMMIT_OFFSET - HEADER_SIZE) / 2;  // 12

        /// <summary>
        /// Total bytes in the subcmd 0x03 intensity payload.
        /// Includes per-button intensity slots plus additional slots whose
        /// meaning varies by wheel (e.g. encoder indicator LEDs).
        /// </summary>
        public const int INTENSITY_PAYLOAD_SIZE = 16;

        private readonly IDeviceTransport _transport;

        // ── Dirty tracking — skip redundant HID writes ───────────────────
        // Color tracking keyed by subcmd; missing entry = dirty (forces send).
        private readonly Dictionary<byte, ushort[]> _lastColors = new Dictionary<byte, ushort[]>();
        // Button intensity tracking (separate: unique staging protocol, byte[] payload)
        private byte[] _lastIntensities;

        // ── Pooled report buffer — avoid per-frame heap allocations ──────
        private readonly byte[] _reportBuf = new byte[REPORT_LENGTH];

        public LedEncoder(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Sets button LED colors and the full intensity report using the staged
        /// commit protocol (subcmd 0x02 colors + subcmd 0x03 intensities).
        /// Skips HID writes when neither array has changed.
        /// </summary>
        /// <param name="colors">Per-button RGB565 values (max <see cref="MAX_BUTTON_LEDS"/>).</param>
        /// <param name="intensityPayload">Pre-composed intensity payload, exactly
        /// <see cref="INTENSITY_PAYLOAD_SIZE"/> bytes. The caller is responsible for
        /// placing button intensities, encoder intensities, etc. at the correct
        /// offsets for the current wheel configuration.</param>
        public bool SetButtonLedState(ushort[] colors, byte[] intensityPayload)
        {
            if (colors == null || colors.Length == 0 || colors.Length > MAX_BUTTON_LEDS) return false;
            if (intensityPayload == null || intensityPayload.Length != INTENSITY_PAYLOAD_SIZE) return false;

            using (_transport.BeginBatch())
            {
                int ledCount = colors.Length;

                // Check color changes via dictionary-based tracking
                ushort[] lastC;
                bool colorsChanged = true;
                if (_lastColors.TryGetValue(SUBCMD_BUTTON_COLORS, out lastC) && lastC.Length == ledCount)
                {
                    colorsChanged = false;
                    for (int i = 0; i < ledCount; i++)
                    {
                        if (colors[i] != lastC[i]) { colorsChanged = true; break; }
                    }
                }

                // Check intensity changes
                bool intensitiesChanged = true;
                if (_lastIntensities != null && _lastIntensities.Length == INTENSITY_PAYLOAD_SIZE)
                {
                    intensitiesChanged = false;
                    for (int i = 0; i < INTENSITY_PAYLOAD_SIZE; i++)
                    {
                        if (intensityPayload[i] != _lastIntensities[i]) { intensitiesChanged = true; break; }
                    }
                }

                if (!colorsChanged && !intensitiesChanged)
                    return true;

                // Stage whichever reports changed, then commit with the last one.
                bool ok = true;

                if (colorsChanged && intensitiesChanged)
                {
                    ok = SendButtonColorReport(colors, commit: false);
                    ok = SendButtonIntensityReport(intensityPayload, commit: true) && ok;
                }
                else if (colorsChanged)
                {
                    ok = SendButtonColorReport(colors, commit: true);
                }
                else
                {
                    ok = SendButtonIntensityReport(intensityPayload, commit: true);
                }

                if (ok)
                {
                    if (lastC == null || lastC.Length != ledCount)
                    {
                        lastC = new ushort[ledCount];
                        _lastColors[SUBCMD_BUTTON_COLORS] = lastC;
                    }
                    Array.Copy(colors, lastC, ledCount);

                    if (_lastIntensities == null)
                        _lastIntensities = new byte[INTENSITY_PAYLOAD_SIZE];
                    Array.Copy(intensityPayload, _lastIntensities, INTENSITY_PAYLOAD_SIZE);
                }

                return ok;
            }
        }

        /// <summary>
        /// Sets Rev LED colors via col03 (subcmd 0x00, per-LED RGB565).
        /// Color 0x0000 = off; non-zero = on with that color.
        /// Array length defines the LED count; dirty tracking is automatic.
        /// </summary>
        public bool SetRevLedColors(ushort[] colors)
        {
            return colors != null && SendSimpleLedColors(SUBCMD_REV_COLORS, colors);
        }

        /// <summary>
        /// Sets Flag LED colors via col03 (subcmd 0x01, per-LED RGB565).
        /// </summary>
        public bool SetFlagLedColors(ushort[] colors)
        {
            return colors != null && SendSimpleLedColors(SUBCMD_FLAG_COLORS, colors);
        }

        /// <summary>
        /// Marks LED state as dirty so the next send always writes to hardware.
        /// Call when the physical wheel changes — firmware resets LED state
        /// but our tracking arrays still hold the previous instance's output.
        /// </summary>
        public void ForceDirty()
        {
            _lastColors.Clear();
            _lastIntensities = null;
        }

        // ── Low-level report senders ─────────────────────────────────────

        /// <summary>
        /// Sends a simple (non-staged) LED color report.
        /// Builds a col03 report: [0xFF, 0x01, subcmd, ...RGB565 big-endian...].
        /// Skips the HID write when colors haven't changed since the last send.
        /// Uses the pooled _reportBuf to avoid per-frame allocations.
        /// </summary>
        private bool SendSimpleLedColors(byte subcmd, ushort[] colors)
        {
            int count = colors.Length;
            if (count == 0 || count > MAX_RGB565_PER_REPORT) return false;

            // Dirty check: missing entry or size mismatch forces a send
            ushort[] last;
            if (_lastColors.TryGetValue(subcmd, out last) && last.Length == count)
            {
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    if (colors[i] != last[i]) { changed = true; break; }
                }
                if (!changed) return true;
            }

            // Reuse pooled buffer — zero the payload region then fill
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = 0xFF;
            _reportBuf[1] = 0x01;
            _reportBuf[2] = subcmd;

            for (int i = 0; i < count; i++)
            {
                int offset = HEADER_SIZE + (i * 2);
                _reportBuf[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                _reportBuf[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            bool ok = _transport.SendCol03(_reportBuf);
            if (ok)
            {
                if (last == null || last.Length != count)
                {
                    last = new ushort[count];
                    _lastColors[subcmd] = last;
                }
                Array.Copy(colors, last, count);
            }
            return ok;
        }

        private bool SendButtonColorReport(ushort[] colors, bool commit)
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = 0xFF;
            _reportBuf[1] = 0x01;
            _reportBuf[2] = SUBCMD_BUTTON_COLORS;

            for (int i = 0; i < colors.Length; i++)
            {
                int offset = HEADER_SIZE + (i * 2);
                _reportBuf[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                _reportBuf[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            _reportBuf[BUTTON_COLOR_COMMIT_OFFSET] = commit ? (byte)0x01 : (byte)0x00;
            return _transport.SendCol03(_reportBuf);
        }

        private bool SendButtonIntensityReport(byte[] intensities, bool commit)
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = 0xFF;
            _reportBuf[1] = 0x01;
            _reportBuf[2] = SUBCMD_BUTTON_INTENSITIES;

            Array.Copy(intensities, 0, _reportBuf, HEADER_SIZE, intensities.Length);
            _reportBuf[BUTTON_INTENSITY_COMMIT_OFFSET] = commit ? (byte)0x01 : (byte)0x00;
            return _transport.SendCol03(_reportBuf);
        }
    }
}
