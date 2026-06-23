using System;
using System.Collections.Generic;
using System.Text;

namespace FanaBridge.Protocol.Schema
{
    /// <summary>
    /// The wheelbase tuning payload (col03 cmd class 0x03, SEN/FF/damper/…).
    ///
    /// <see cref="Fields"/> is the SINGLE SOURCE OF TRUTH. The same array feeds:
    ///   • decode — <see cref="TuningState.Decode"/> reads report[start + Offset]
    ///   • encode — <see cref="TuningState.EncodeWrite"/> writes them back
    ///   • docs   — <see cref="RenderDocTable"/> emits the protocol.md table (phase 3)
    ///   • tests  — golden frames round-trip bytes→state→bytes
    ///
    /// NOTE: this models the WHEELBASE payload. The button module exposes its own,
    /// different layout over the same command — see <see cref="ButtonModuleTuning"/>.
    /// No production path consumes the wheelbase payload yet; it exists as the
    /// documented structure and the basis for future wheelbase-tuning features.
    ///
    /// Payload offsets are relative to the start of the tuning data, which sits at
    /// a different HID-report offset for READ vs WRITE:
    ///   READ response : data starts at HID byte 3
    ///   WRITE command : data starts at HID byte 4  (+1 shift; subcmd is byte 2)
    /// </summary>
    public static class TuningPayload
    {
        public const int ReadDataStart = 3;
        public const int WriteDataStart = 4;

        public static readonly IReadOnlyList<ReportField> Fields = new[]
        {
            new ReportField("UserSetupIndex", 0, range: "0–4", description: "Active setup slot"),
            new ReportField("SEN",  1, description: "Steering Sensitivity"),
            new ReportField("FF",   2, description: "Force Feedback strength"),
            new ReportField("SHO",  3, description: "Shock / vibration intensity"),
            new ReportField("BLI",  4, description: "Brake Linearity"),
            new ReportField("LIN",  5, description: "Linearity (aliased as FFS in some variants)"),
            new ReportField("DEA",  6, description: "Dead Zone"),
            new ReportField("DRI",  7, signed: true, range: "-128–127", description: "Drift Mode"),
            new ReportField("FOR",  8, description: "Force"),
            new ReportField("SPR",  9, description: "Spring"),
            new ReportField("DPR", 10, description: "Damper"),
            new ReportField("NDP", 11, description: "Natural Damper"),
            new ReportField("NFR", 12, description: "Natural Friction"),
            new ReportField("BRF", 13, description: "Brake Force"),
            new ReportField("BRG", 14, description: "Brake Gain"),
            new ReportField("FEI", 15, description: "Force Effect Intensity"),
            new ReportField("MPS", 16, description: "Max Power Supply / Motor Protection"),
            new ReportField("APM", 17, description: "Advanced Paddle Mode (rotary wheels only)"),
            new ReportField("INT", 18, description: "Interactivity"),
            new ReportField("NIN", 19, description: "Natural Inertia"),
            new ReportField("FUL", 20, description: "Full Lock (steering angle)"),
            new ReportField("BIL", 21, description: "Bilateral / Balance"),
            new ReportField("ROT", 22, description: "Rotation"),
        };

        /// <summary>Number of payload bytes (the highest offset + 1).</summary>
        public const int PayloadLength = 23;

        /// <summary>Renders the "Tuning Payload Structure" markdown table from <see cref="Fields"/>.</summary>
        public static string RenderDocTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Offset | Field | Type | Range | Description |");
            sb.AppendLine("|--------|-------|------|-------|-------------|");
            foreach (var f in Fields)
            {
                string type = f.Signed ? "**sbyte**" : "byte";
                sb.AppendLine($"| {f.Offset} | {f.Name} | {type} | {f.Range} | {f.Description} |");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A typed view over the wheelbase tuning payload. Decode/Encode are driven
    /// entirely by <see cref="TuningPayload.Fields"/>; adding a field to that array
    /// is the only edit needed to make it round-trip.
    /// </summary>
    public sealed class TuningState
    {
        // payload-offset -> raw byte (signed fields are reinterpreted on access)
        private readonly byte[] _payload = new byte[TuningPayload.PayloadLength];

        /// <summary>Get/set a field by name; signed fields use sbyte semantics.</summary>
        public int this[string fieldName]
        {
            get
            {
                var f = ByName(fieldName);
                byte raw = _payload[f.Offset];
                return f.Signed ? (sbyte)raw : raw;
            }
            set
            {
                var f = ByName(fieldName);
                _payload[f.Offset] = unchecked((byte)value);
            }
        }

        /// <summary>Decode from a raw HID report; <paramref name="dataStart"/> is
        /// <see cref="TuningPayload.ReadDataStart"/> for a READ frame.</summary>
        public static TuningState Decode(byte[] report, int dataStart)
        {
            var s = new TuningState();
            foreach (var f in TuningPayload.Fields)
                s._payload[f.Offset] = report[dataStart + f.Offset];
            return s;
        }

        /// <summary>Build a 64-byte WRITE frame (<c>FF 03 00 devId …</c>).</summary>
        public byte[] EncodeWrite(byte deviceId)
        {
            var buf = new byte[Wire.Col03Length];
            Wire.BeginCol03(buf, Wire.Col03.TuningClass, 0x00); // 0x00 = WRITE subcmd
            buf[3] = deviceId;
            foreach (var f in TuningPayload.Fields)
                buf[TuningPayload.WriteDataStart + f.Offset] = _payload[f.Offset];
            return buf;
        }

        private static ReportField ByName(string name)
        {
            foreach (var f in TuningPayload.Fields)
                if (f.Name == name) return f;
            throw new ArgumentException("Unknown tuning field: " + name, nameof(name));
        }
    }
}
