using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using FanaBridge.Core.Transport;

namespace FanaBridge.Core.Display.Protocol
{
    /// <summary>
    /// A single telemetry value for an ITM ValueUpdate entry (col03 <c>FF 05 01</c>).
    /// The <see cref="Raw"/> bits are emitted little-endian, <see cref="Size"/> bytes
    /// wide. Use the typed factory helpers (<see cref="UInt8"/>, <see cref="Int16"/>,
    /// <see cref="Int32"/>, <see cref="Float32"/>) rather than packing bits by hand.
    /// </summary>
    public readonly struct ItmValue
    {
        /// <summary>Parameter handle assigned to this slot during ParamDefs.</summary>
        public byte Handle { get; }

        /// <summary>Parameter ID (see the protocol reference, "ITM Parameter IDs").</summary>
        public ushort ParamId { get; }

        /// <summary>Value width on the wire: 1, 2, or 4 bytes.</summary>
        public byte Size { get; }

        /// <summary>Value bits held in the low <see cref="Size"/> bytes, emitted little-endian.</summary>
        public uint Raw { get; }

        public ItmValue(byte handle, ushort paramId, byte size, uint raw)
        {
            Handle = handle;
            ParamId = paramId;
            Size = size;
            Raw = raw;
        }

        /// <summary>An unsigned 8-bit value (e.g. GEAR, POSITION, TC_SETTING).</summary>
        public static ItmValue UInt8(byte handle, ushort paramId, byte value)
            => new ItmValue(handle, paramId, 1, value);

        /// <summary>A signed 16-bit value (e.g. SPEED).</summary>
        public static ItmValue Int16(byte handle, ushort paramId, short value)
            => new ItmValue(handle, paramId, 2, unchecked((ushort)value));

        /// <summary>A signed 32-bit value (e.g. RPM, ERS_LEVEL).</summary>
        public static ItmValue Int32(byte handle, ushort paramId, int value)
            => new ItmValue(handle, paramId, 4, unchecked((uint)value));

        /// <summary>A 32-bit float value (e.g. FUEL, LAP_TIME, BRAKE_BIAS).</summary>
        public static ItmValue Float32(byte handle, ushort paramId, float value)
            => new ItmValue(handle, paramId, 4, FloatBits(Sanitize(value)));

        /// <summary>
        /// A short ASCII-text value. Some params (e.g. ENGINE_MAPPING) are displayed by the
        /// firmware as text — map "10" travels as the two bytes '1','0', not a numeric 0x0A.
        /// Sending the wrong (numeric) form wedges the PBME firmware. Up to 4 characters,
        /// packed little-endian; the payload size is the character count.
        /// </summary>
        public static ItmValue Ascii(byte handle, ushort paramId, string text)
        {
            if (string.IsNullOrEmpty(text)) text = "0";
            int len = Math.Min(text.Length, 4);
            uint raw = 0;
            for (int i = 0; i < len; i++)
                raw |= (uint)(byte)text[i] << (8 * i);
            return new ItmValue(handle, paramId, (byte)len, raw);
        }

        // Keep NaN/Infinity and absurd magnitudes off the wire — an out-of-range float
        // can wedge the PBME firmware. Telemetry can briefly produce these before a
        // session settles.
        private static float Sanitize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            const float bound = 1_000_000f;
            if (value > bound) return bound;
            if (value < -bound) return -bound;
            return value;
        }

        // Reinterpret a float's bits as a uint without a heap allocation
        // (no BitConverter.SingleToUInt32Bits on .NET Framework 4.8).
        private static uint FloatBits(float value)
        {
            var u = new FloatUnion { F = value };
            return u.U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }
    }

    /// <summary>
    /// A single display-slot definition for an ITM ParamDefs entry (col03 <c>FF 05 03</c>).
    /// Tells the firmware which slot to populate and an optional ASCII suffix to render
    /// after the value (e.g. the "/0" total-companion marker).
    /// </summary>
    public readonly struct ItmParamDef
    {
        /// <summary>Display-layout slot identifier (e.g. 0x82, 0x85, 0x88).</summary>
        public byte SlotId { get; }

        /// <summary>16-bit position, emitted little-endian. Typically 0.</summary>
        public ushort Position { get; }

        /// <summary>ASCII suffix bytes appended after the value, or null/empty for none.</summary>
        public byte[] Suffix { get; }

        public ItmParamDef(byte slotId, ushort position = 0, byte[] suffix = null)
        {
            SlotId = slotId;
            Position = position;
            Suffix = suffix;
        }

        /// <summary>Convenience builder that encodes <paramref name="suffix"/> as ASCII bytes.</summary>
        public static ItmParamDef WithSuffix(byte slotId, string suffix, ushort position = 0)
            => new ItmParamDef(slotId, position,
                string.IsNullOrEmpty(suffix) ? null : Encoding.ASCII.GetBytes(suffix));
    }

    /// <summary>
    /// Encodes and sends ITM (telemetry display) control reports for Fanatec wheels
    /// over the col03 interface. Covers the four ITM frames documented in the protocol
    /// reference: Enable (<c>FF 02 02</c>), ParamDefs (<c>FF 05 03</c>), ValueUpdate
    /// (<c>FF 05 01</c>), and the PageSet frame (<c>FF 05 04</c>).
    ///
    /// This is a pure framing layer — like <see cref="FanaBridge.Core.Leds.LedEncoder"/> and
    /// <see cref="DisplayEncoder"/>, it builds and writes reports but holds no display
    /// state. Page selection, telemetry-to-parameter mapping, and the firmware-safety rate
    /// limits (e.g. value-update pacing) are the caller's
    /// responsibility.
    /// </summary>
    public class ItmEncoder
    {
        // ── Protocol constants (col03 report format) ─────────────────────
        private const int REPORT_LENGTH = 64;
        private const byte REPORT_PREFIX = 0xFF;
        private const int HEADER_SIZE = 3;   // [0xFF, cmd_class, subcmd]

        // Command classes (byte[1])
        private const byte CMD_ITM_ENABLE = 0x02;   // ITM enable lives on its own class
        private const byte CMD_ITM_DISPLAY = 0x05;  // ITM display: mode gate / ParamDefs / ValueUpdate / PageSet

        // Sub-commands (byte[2])
        private const byte SUBCMD_ENABLE = 0x02;
        private const byte SUBCMD_VALUE_UPDATE = 0x01;
        private const byte SUBCMD_ITM_MODE = 0x02;   // under FF 05: firmware ITM on/off gate
        private const byte SUBCMD_PARAM_DEFS = 0x03;
        private const byte SUBCMD_PAGESET = 0x04;    // PageSet: byte[3]=display-device id, byte[4]=page
        private const byte SUBCMD_DISPLAY_RESET = 0x05;  // DisplayReset: fields revert to per-field placeholders

        // First byte of every ValueUpdate/ParamDefs entry is the display-device id — which
        // display the entry targets, not a marker. Callers pass the id for the display they
        // drive; DefaultDeviceId (the wheel OLED, device 3) is used when omitted.

        /// <summary>
        /// Default ITM display-device wire id when a caller doesn't specify one: <b>3</b> — the
        /// wheel's OLED (PBME, GTSWX, Formula V3, …). Other displays: base = 1, Bentley = 4.
        /// </summary>
        public const byte DefaultDeviceId = 3;

        // Fixed-size prefixes of a single entry, before the variable tail. The leading
        // byte is the display-device id, not a marker.
        // ValueUpdate:  deviceId, handle, idLo, idHi, size          (+ Size value bytes)
        // ParamDefs:    deviceId, slotId, posLo, posHi, suffixLen   (+ suffix bytes)
        private const int VALUE_ENTRY_HEADER = 5;
        private const int PARAM_DEF_ENTRY_HEADER = 5;

        private const int PAYLOAD_CAPACITY = REPORT_LENGTH - HEADER_SIZE;   // 61

        /// <summary>Maximum parameters the firmware will track at once (per the protocol reference).</summary>
        public const int MaxParams = 16;

        /// <summary>Largest ASCII suffix that still fits a ParamDefs entry in one report.</summary>
        public const int MaxSuffixLength = PAYLOAD_CAPACITY - PARAM_DEF_ENTRY_HEADER;   // 56

        private readonly IDeviceTransport _transport;

        // Pooled report buffer — avoid per-frame heap allocations.
        private readonly byte[] _reportBuf = new byte[REPORT_LENGTH];

        public ItmEncoder(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Sets the firmware ITM mode gate (col03 <c>FF 05 02 &lt;01|00&gt;</c>) — the same
        /// on/off state the Fanatec software's ITM switch controls. This is distinct from
        /// <see cref="EnableItm"/>, which starts a display session: ITM must be gated ON
        /// here first or the session enable is ignored and no subscriptions are pushed.
        /// The gate is persistent (survives power cycles), confirmed against a capture of
        /// the official software toggling ITM.
        /// </summary>
        public bool SetItmMode(bool on)
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = REPORT_PREFIX;
            _reportBuf[1] = CMD_ITM_DISPLAY;   // 0x05
            _reportBuf[2] = SUBCMD_ITM_MODE;   // 0x02
            _reportBuf[3] = on ? (byte)0x01 : (byte)0x00;
            return _transport.SendCol03(_reportBuf);
        }

        /// <summary>
        /// Sends the ITM session enable frame (<c>FF 02 02 00</c>) — command class 0x02,
        /// separate from the <c>FF 05 02</c> ITM-mode gate (<see cref="SetItmMode"/>).
        /// byte[3] is always <c>0x00</c> (no page semantics; paging is done via <c>SetPage</c>).
        /// Send once at session start (as the official Fanatec app does).
        /// </summary>
        public bool EnableItm()
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = REPORT_PREFIX;
            _reportBuf[1] = CMD_ITM_ENABLE;
            _reportBuf[2] = SUBCMD_ENABLE;
            _reportBuf[3] = 0x00;   // not a page
            return _transport.SendCol03(_reportBuf);
        }

        /// <summary>
        /// Sends the DisplayReset frame (<c>FF 05 05 01</c>): every field on the ITM
        /// telemetry pages reverts to its per-field placeholder rendering (e.g.
        /// <c>--- / -</c> for laps, <c>--:--.-</c> for times), while the ITM session,
        /// active page, and firmware subscriptions stay untouched. It has NO effect
        /// on the Legacy ITM page — that content is written (and cleared) separately
        /// over col01. Hardware-verified; this is the only known command that clears
        /// already-written field values (an ITM off→on cycle does NOT — the firmware
        /// retains them across the cycle).
        /// </summary>
        public bool ResetDisplay()
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = REPORT_PREFIX;
            _reportBuf[1] = CMD_ITM_DISPLAY;
            _reportBuf[2] = SUBCMD_DISPLAY_RESET;
            _reportBuf[3] = 0x01;
            return _transport.SendCol03(_reportBuf);
        }

        /// <summary>
        /// Selects the active ITM page on a display device (byte[3] = wire device id,
        /// byte[4] = page). Page changes should be spaced at least 100&#160;ms apart (firmware
        /// reconfiguration time) — the caller is responsible for that spacing.
        /// </summary>
        public bool SetPage(byte deviceId, byte page)
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = REPORT_PREFIX;
            _reportBuf[1] = CMD_ITM_DISPLAY;
            _reportBuf[2] = SUBCMD_PAGESET;
            _reportBuf[3] = deviceId;
            _reportBuf[4] = page;
            return _transport.SendCol03(_reportBuf);
        }

        /// <summary>
        /// Defines the display-slot layout (subcmd 0x03). Entries are packed into as
        /// many 64-byte reports as needed, each carrying the <c>FF 05 03</c> header.
        /// Re-send after every <see cref="EnableItm"/>. Returns false if the list is
        /// null/empty, exceeds <see cref="MaxParams"/>, or any suffix exceeds
        /// <see cref="MaxSuffixLength"/>. <paramref name="deviceId"/> is the target display
        /// (defaults to <see cref="DefaultDeviceId"/>).
        /// </summary>
        public bool SetParamDefs(IReadOnlyList<ItmParamDef> defs, byte deviceId = DefaultDeviceId)
        {
            if (defs == null || defs.Count == 0 || defs.Count > MaxParams)
                return false;

            // Validate the whole list before touching the transport — otherwise an invalid
            // entry after a report boundary would return false with a partial batch sent.
            for (int j = 0; j < defs.Count; j++)
                if ((defs[j].Suffix?.Length ?? 0) > MaxSuffixLength)
                    return false;

            using (_transport.BeginBatch())
            {
                bool ok = true;
                int i = 0;
                while (i < defs.Count)
                {
                    int pos = BeginReport(SUBCMD_PARAM_DEFS);

                    for (; i < defs.Count; i++)
                    {
                        var d = defs[i];
                        int suffixLen = d.Suffix?.Length ?? 0;

                        int entryLen = PARAM_DEF_ENTRY_HEADER + suffixLen;
                        // A fresh report can always hold one entry (suffix bounded above);
                        // break only to flush a partially-filled report.
                        if (pos + entryLen > REPORT_LENGTH)
                            break;

                        _reportBuf[pos++] = deviceId;
                        _reportBuf[pos++] = d.SlotId;
                        _reportBuf[pos++] = (byte)(d.Position & 0xFF);
                        _reportBuf[pos++] = (byte)((d.Position >> 8) & 0xFF);
                        _reportBuf[pos++] = (byte)suffixLen;
                        if (suffixLen > 0)
                        {
                            Array.Copy(d.Suffix, 0, _reportBuf, pos, suffixLen);
                            pos += suffixLen;
                        }
                    }

                    ok = _transport.SendCol03(_reportBuf) && ok;
                }
                return ok;
            }
        }

        /// <summary>
        /// Sends telemetry values (subcmd 0x01). Entries are packed into as many
        /// 64-byte reports as needed, each carrying the <c>FF 05 01</c> header.
        /// Returns false if the list is null/empty, exceeds <see cref="MaxParams"/>,
        /// or any entry has a size other than 1, 2, or 4. <paramref name="deviceId"/> is the
        /// target display (defaults to <see cref="DefaultDeviceId"/>).
        /// </summary>
        public bool SendValues(IReadOnlyList<ItmValue> values, byte deviceId = DefaultDeviceId)
        {
            if (values == null || values.Count == 0 || values.Count > MaxParams)
                return false;

            // Validate every entry before touching the transport — otherwise a bad size
            // after a report boundary would return false with a partial batch sent.
            // (1/2/4 for numeric values; 3 occurs for ASCII text, e.g. a 3-char map.)
            for (int j = 0; j < values.Count; j++)
                if (values[j].Size < 1 || values[j].Size > 4)
                    return false;

            using (_transport.BeginBatch())
            {
                bool ok = true;
                int i = 0;
                while (i < values.Count)
                {
                    int pos = BeginReport(SUBCMD_VALUE_UPDATE);

                    for (; i < values.Count; i++)
                    {
                        var v = values[i];

                        int entryLen = VALUE_ENTRY_HEADER + v.Size;
                        if (pos + entryLen > REPORT_LENGTH)
                            break;

                        _reportBuf[pos++] = deviceId;
                        _reportBuf[pos++] = v.Handle;
                        _reportBuf[pos++] = (byte)(v.ParamId & 0xFF);
                        _reportBuf[pos++] = (byte)((v.ParamId >> 8) & 0xFF);
                        _reportBuf[pos++] = v.Size;

                        uint raw = v.Raw;
                        for (int b = 0; b < v.Size; b++)
                        {
                            _reportBuf[pos++] = (byte)(raw & 0xFF);
                            raw >>= 8;
                        }
                    }

                    ok = _transport.SendCol03(_reportBuf) && ok;
                }
                return ok;
            }
        }

        // Zero the pooled buffer and lay down the [0xFF, 0x05, subcmd] header,
        // returning the offset where entries begin.
        private int BeginReport(byte subcmd)
        {
            Array.Clear(_reportBuf, 0, REPORT_LENGTH);
            _reportBuf[0] = REPORT_PREFIX;
            _reportBuf[1] = CMD_ITM_DISPLAY;
            _reportBuf[2] = subcmd;
            return HEADER_SIZE;
        }
    }
}
