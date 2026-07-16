using System;
using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// The kind of col03 OUT frame an <see cref="ItmFrame"/> classified to. Mirrors the
    /// ITM command families <see cref="ItmEncoder"/> emits (protocol.md §FF 05 and the
    /// §FF 02 session enable), plus <see cref="Unknown"/> for anything the decoder does
    /// not recognise — a never-stuck fallback rather than an error.
    /// </summary>
    public enum ItmFrameType
    {
        /// <summary>Unrecognised or malformed frame — raw bytes retained, nothing decoded.</summary>
        Unknown,
        /// <summary>Session enable, <c>FF 02 02 00</c> (protocol.md §0x02 — ITM Enable).</summary>
        SessionEnable,
        /// <summary>ITM Mode gate, <c>FF 05 02 &lt;01|00&gt;</c> (protocol.md §0x02 — ITM Mode).</summary>
        Gate,
        /// <summary>DisplayReset, <c>FF 05 05 01</c> (protocol.md §0x05 — DisplayReset).</summary>
        DisplayReset,
        /// <summary>PageSet, <c>FF 05 04 &lt;deviceId&gt; &lt;page&gt;</c> (protocol.md §0x04 — PageSet).</summary>
        PageSet,
        /// <summary>ValueUpdate, <c>FF 05 01 &lt;entries&gt;</c> (protocol.md §0x01 — ValueUpdate).</summary>
        ValueUpdate,
        /// <summary>ParamDefs / suffix report, <c>FF 05 03 &lt;entries&gt;</c> (protocol.md §0x03 — ParamDefs).</summary>
        ParamDefs,
    }

    /// <summary>
    /// One decoded ValueUpdate entry (the inverse of an <see cref="ItmValue"/> as packed
    /// by <see cref="ItmEncoder.SendValues"/>): <c>[deviceId][handle][paramId-LE][size][value-LE]</c>.
    /// The value is carried as its raw little-endian bits in <see cref="Raw"/>, matching
    /// <see cref="ItmValue.Raw"/> — interpretation (i32 vs f32 vs ASCII, both size 4/…) is
    /// the renderer's job, driven by the firmware-declared type, not the wire alone.
    /// </summary>
    public readonly struct ItmValueEntry
    {
        /// <summary>Target display-device id (1=Base, 3=BME/GTSWX, 4=Bentley).</summary>
        public byte DeviceId { get; }
        /// <summary>Parameter handle the firmware assigned to this slot.</summary>
        public byte Handle { get; }
        /// <summary>Parameter ID (little-endian on the wire).</summary>
        public ushort ParamId { get; }
        /// <summary>Value width in bytes (1, 2, or 4 — text-typed params use the char count).</summary>
        public byte Size { get; }
        /// <summary>Value bits in the low <see cref="Size"/> bytes, read little-endian.</summary>
        public uint Raw { get; }

        public ItmValueEntry(byte deviceId, byte handle, ushort paramId, byte size, uint raw)
        {
            DeviceId = deviceId;
            Handle = handle;
            ParamId = paramId;
            Size = size;
            Raw = raw;
        }
    }

    /// <summary>
    /// One decoded ParamDefs entry (the inverse of an <see cref="ItmParamDef"/> as packed
    /// by <see cref="ItmEncoder.SetParamDefs"/>): <c>[deviceId][slotId][pos-LE][suffixLen][suffix]</c>.
    /// The slot id is <c>0x80 | handle</c> (protocol.md §0x03); <see cref="Handle"/> exposes
    /// the underlying handle with that marker bit cleared.
    /// </summary>
    public readonly struct ItmParamDefEntry
    {
        /// <summary>Target display-device id.</summary>
        public byte DeviceId { get; }
        /// <summary>Slot id on the wire (<c>0x80 | handle</c>).</summary>
        public byte SlotId { get; }
        /// <summary>16-bit position field (little-endian; always 0 in observed captures).</summary>
        public ushort Position { get; }
        /// <summary>ASCII suffix bytes (empty for a bare, suffix-less entry — never null).</summary>
        public byte[] Suffix { get; }

        /// <summary>The decorated handle (<c>SlotId &amp; 0x7F</c>).</summary>
        public byte Handle => (byte)(SlotId & 0x7F);

        public ItmParamDefEntry(byte deviceId, byte slotId, ushort position, byte[] suffix)
        {
            DeviceId = deviceId;
            SlotId = slotId;
            Position = position;
            Suffix = suffix ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// A single decoded col03 OUT frame. One wire report decodes to exactly one frame —
    /// ITM frames are self-contained 64-byte reports, so a garbled frame can never
    /// desync the stream that follows it. Typed payload is populated per
    /// <see cref="Type"/>; <see cref="Values"/> / <see cref="ParamDefs"/> are always
    /// non-null (empty for frame types that carry no entries).
    /// </summary>
    public sealed class ItmFrame
    {
        /// <summary>The classified frame family.</summary>
        public ItmFrameType Type { get; }

        /// <summary>Target display-device id for <see cref="ItmFrameType.PageSet"/>; otherwise 0.</summary>
        public byte DeviceId { get; }

        /// <summary>Selected page for <see cref="ItmFrameType.PageSet"/>; otherwise 0.</summary>
        public byte Page { get; }

        /// <summary>Gate state for <see cref="ItmFrameType.Gate"/> (true = on); otherwise false.</summary>
        public bool GateOn { get; }

        /// <summary>Decoded entries for <see cref="ItmFrameType.ValueUpdate"/>; empty otherwise.</summary>
        public IReadOnlyList<ItmValueEntry> Values { get; }

        /// <summary>Decoded entries for <see cref="ItmFrameType.ParamDefs"/>; empty otherwise.</summary>
        public IReadOnlyList<ItmParamDefEntry> ParamDefs { get; }

        private static readonly IReadOnlyList<ItmValueEntry> NoValues = Array.AsReadOnly(new ItmValueEntry[0]);
        private static readonly IReadOnlyList<ItmParamDefEntry> NoDefs = Array.AsReadOnly(new ItmParamDefEntry[0]);

        internal ItmFrame(ItmFrameType type, byte deviceId = 0, byte page = 0, bool gateOn = false,
            IReadOnlyList<ItmValueEntry> values = null, IReadOnlyList<ItmParamDefEntry> paramDefs = null)
        {
            Type = type;
            DeviceId = deviceId;
            Page = page;
            GateOn = gateOn;
            Values = values ?? NoValues;
            ParamDefs = paramDefs ?? NoDefs;
        }
    }

    /// <summary>
    /// Decodes the col03 OUT frames <see cref="ItmEncoder"/> produces — the encoder's
    /// inverse. It feeds the wire-driven digital twin (and encode→decode round-trip
    /// tests) so the twin renders <b>what was sent</b>, not what the host intended.
    ///
    /// <b>Never-stuck parsing (binding):</b> every input has defined behaviour and the
    /// decoder never throws. An unrecognised header, an unknown subcommand, or a truncated
    /// entry yields <see cref="ItmFrameType.Unknown"/> (or, mid-frame, ends entry parsing
    /// early keeping whatever decoded cleanly). Because each report is a complete,
    /// self-contained frame, corruption in one report cannot propagate to the next — there
    /// is no cross-frame parser state to desync (cf. protocol.md: reports are 00-padded to
    /// 64 bytes, one command per report).
    /// </summary>
    public static class ItmFrameDecoder
    {
        // col03 report layout: [0xFF][class][subcmd] then payload. Same report-id
        // tolerance the inbound classifier applies (an 0xFF may sit at offset 0..2).
        private const byte REPORT_PREFIX = 0xFF;
        private const byte CMD_ITM_ENABLE = 0x02;
        private const byte CMD_ITM_DISPLAY = 0x05;

        private const byte SUB_VALUE_UPDATE = 0x01;
        private const byte SUB_ENABLE = 0x02;    // under FF 02: session enable
        private const byte SUB_ITM_MODE = 0x02;  // under FF 05: gate
        private const byte SUB_PARAM_DEFS = 0x03;
        private const byte SUB_PAGESET = 0x04;
        private const byte SUB_DISPLAY_RESET = 0x05;

        // ValueUpdate/ParamDefs entries lead with a display-device id; the encoder only
        // emits ids 1/3/4 and 00-pads the tail, so a zero id marks the end of real
        // entries (mirrors ItmTelemetry.ParseSubscriptionReport, which skips id-0 padding).
        private const int ENTRY_HEADER = 5;

        private static readonly ItmFrame UnknownFrame = new ItmFrame(ItmFrameType.Unknown);

        /// <summary>
        /// Decodes one col03 OUT report into an <see cref="ItmFrame"/>. Never throws; an
        /// unrecognised or malformed report returns <see cref="ItmFrameType.Unknown"/>.
        /// The report buffer is read, not retained — the caller owns its lifetime (the
        /// outbound tap hands over a private copy, so this needs no defensive copy).
        /// </summary>
        /// <param name="report">The report bytes (64-byte col03 report, 00-padded).</param>
        /// <param name="len">Valid byte count; defaults to the whole buffer when &lt;= 0.</param>
        public static ItmFrame Decode(byte[] report, int len = -1)
        {
            if (report == null) return UnknownFrame;
            if (len <= 0 || len > report.Length) len = report.Length;

            // Locate the 0xFF prefix (offsets 0..2), needing the class + subcmd bytes.
            int sig = -1;
            for (int i = 0; i <= 2 && i + 2 < len; i++)
                if (report[i] == REPORT_PREFIX) { sig = i; break; }
            if (sig < 0) return UnknownFrame;

            byte cmd = report[sig + 1];
            byte sub = report[sig + 2];
            int payload = sig + 3;   // first byte after the [FF][class][subcmd] header

            if (cmd == CMD_ITM_ENABLE)
                return sub == SUB_ENABLE ? new ItmFrame(ItmFrameType.SessionEnable) : UnknownFrame;

            if (cmd != CMD_ITM_DISPLAY)
                return UnknownFrame;

            switch (sub)
            {
                case SUB_ITM_MODE:
                    // FF 05 02 <01|00>
                    if (payload >= len) return UnknownFrame;
                    return new ItmFrame(ItmFrameType.Gate, gateOn: report[payload] != 0);

                case SUB_DISPLAY_RESET:
                    // FF 05 05 01 — interface-global, carries no device/page.
                    return new ItmFrame(ItmFrameType.DisplayReset);

                case SUB_PAGESET:
                    // FF 05 04 <deviceId> <page>
                    if (payload + 1 >= len) return UnknownFrame;
                    return new ItmFrame(ItmFrameType.PageSet, deviceId: report[payload], page: report[payload + 1]);

                case SUB_VALUE_UPDATE:
                    return new ItmFrame(ItmFrameType.ValueUpdate, values: DecodeValues(report, len, payload));

                case SUB_PARAM_DEFS:
                    return new ItmFrame(ItmFrameType.ParamDefs, paramDefs: DecodeParamDefs(report, len, payload));

                default:
                    return UnknownFrame;
            }
        }

        // ValueUpdate entries: [deviceId][handle][idLo][idHi][size][value×size]. Variable
        // stride. Stop cleanly at 00-padding (id 0), a truncated entry, or an out-of-range
        // size — never over-read, never throw; keep every entry decoded so far.
        private static IReadOnlyList<ItmValueEntry> DecodeValues(byte[] report, int len, int start)
        {
            var result = new List<ItmValueEntry>();
            int i = start;
            while (i + ENTRY_HEADER <= len)
            {
                byte deviceId = report[i];
                if (deviceId == 0) break;   // padding — end of real entries

                byte size = report[i + 4];
                if (size < 1 || size > 4) break;                 // garbled stride — stop, don't guess
                if (i + ENTRY_HEADER + size > len) break;        // value truncated — stop

                byte handle = report[i + 1];
                ushort paramId = (ushort)(report[i + 2] | (report[i + 3] << 8));

                uint raw = 0;
                for (int b = 0; b < size; b++)
                    raw |= (uint)report[i + ENTRY_HEADER + b] << (8 * b);

                result.Add(new ItmValueEntry(deviceId, handle, paramId, size, raw));
                i += ENTRY_HEADER + size;
            }
            return result;
        }

        // ParamDefs entries: [deviceId][slotId][posLo][posHi][suffixLen][suffix×suffixLen].
        // Same padding/truncation discipline as DecodeValues.
        private static IReadOnlyList<ItmParamDefEntry> DecodeParamDefs(byte[] report, int len, int start)
        {
            var result = new List<ItmParamDefEntry>();
            int i = start;
            while (i + ENTRY_HEADER <= len)
            {
                byte deviceId = report[i];
                if (deviceId == 0) break;   // padding — end of real entries

                byte suffixLen = report[i + 4];
                if (i + ENTRY_HEADER + suffixLen > len) break;   // suffix truncated — stop

                byte slotId = report[i + 1];
                ushort position = (ushort)(report[i + 2] | (report[i + 3] << 8));

                byte[] suffix;
                if (suffixLen > 0)
                {
                    suffix = new byte[suffixLen];
                    Array.Copy(report, i + ENTRY_HEADER, suffix, 0, suffixLen);
                }
                else
                {
                    suffix = Array.Empty<byte>();
                }

                result.Add(new ItmParamDefEntry(deviceId, slotId, position, suffix));
                i += ENTRY_HEADER + suffixLen;
            }
            return result;
        }
    }
}
