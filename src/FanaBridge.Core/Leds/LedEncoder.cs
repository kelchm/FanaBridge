using System;
using System.Collections.Generic;
using FanaBridge.Core.Transport;

namespace FanaBridge.Core.Leds
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
        /// Derived from the commit offset: the payload occupies report offsets
        /// 3..17 and the commit flag sits at offset 18, immediately after it —
        /// a 16th slot would be overwritten by the commit byte on every send,
        /// so the two constants must never drift apart.
        /// </summary>
        public const int INTENSITY_PAYLOAD_SIZE = BUTTON_INTENSITY_COMMIT_OFFSET - HEADER_SIZE;  // 15

        private readonly IDeviceTransport _transport;

        // ── Dirty tracking — skip redundant HID writes ───────────────────
        // Color tracking keyed by subcmd; missing entry = dirty (forces send).
        // Only ever touched from send paths: ForceDirty is called cross-thread
        // (WheelChanged can fire from the settings UI while a driver send task is
        // in flight), so it must not mutate this dictionary directly — a concurrent
        // Clear against TryGetValue/insert corrupts it on net48.
        private readonly Dictionary<byte, ushort[]> _lastColors = new Dictionary<byte, ushort[]>();
        // Button intensity tracking (separate: unique staging protocol, byte[] payload)
        private byte[] _lastIntensities;
        // Set by ForceDirty (any thread), consumed at the top of each send path
        // (sender thread) — see ForceDirty.
        private volatile bool _forceDirty;

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
            // colors may be null or empty for wheels that have mono LEDs but no color LEDs,
            // in which case we skip subcmd 0x02 and send only the subcmd 0x03 intensity report.
            if (colors != null && colors.Length > MAX_BUTTON_LEDS) return false;
            if (intensityPayload == null || intensityPayload.Length != INTENSITY_PAYLOAD_SIZE) return false;

            using (_transport.BeginBatch())
            {
                ConsumePendingForceDirty();

                bool hasColors = colors != null && colors.Length > 0;
                int ledCount = hasColors ? colors.Length : 0;

                // Check color changes via dictionary-based tracking
                ushort[] lastC = null;
                bool colorsChanged = false;
                if (hasColors)
                {
                    colorsChanged = true;
                    if (_lastColors.TryGetValue(SUBCMD_BUTTON_COLORS, out lastC) && lastC.Length == ledCount)
                    {
                        colorsChanged = false;
                        for (int i = 0; i < ledCount; i++)
                        {
                            if (colors[i] != lastC[i]) { colorsChanged = true; break; }
                        }
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

                if (hasColors && colorsChanged && intensitiesChanged)
                {
                    ok = SendButtonColorReport(colors, commit: false);
                    ok = SendButtonIntensityReport(intensityPayload, commit: true) && ok;
                }
                else if (hasColors && colorsChanged)
                {
                    ok = SendButtonColorReport(colors, commit: true);
                }
                else
                {
                    ok = SendButtonIntensityReport(intensityPayload, commit: true);
                }

                if (ok)
                {
                    if (hasColors)
                    {
                        if (lastC == null || lastC.Length != ledCount)
                        {
                            lastC = new ushort[ledCount];
                            _lastColors[SUBCMD_BUTTON_COLORS] = lastC;
                        }
                        Array.Copy(colors, lastC, ledCount);
                    }

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
        /// Safe from any thread: sets a flag consumed on the sender's own thread,
        /// so the tracking state is never mutated concurrently with a send.
        /// </summary>
        public void ForceDirty()
        {
            _forceDirty = true;
        }

        // Applies a pending ForceDirty on the sender's thread, before the dirty
        // check. If ForceDirty lands mid-send, the flag simply stays set for the
        // next send — a forced resend is never lost.
        private void ConsumePendingForceDirty()
        {
            if (!_forceDirty) return;
            _forceDirty = false;
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

            ConsumePendingForceDirty();

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
