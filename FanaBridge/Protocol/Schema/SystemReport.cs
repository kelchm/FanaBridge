using System.Collections.Generic;

namespace FanaBridge.Protocol.Schema
{
    /// <summary>
    /// The col03 <c>FF 08</c> system report identity fields. <see cref="Fields"/>
    /// is the single source of truth for the BaseType/WheelCode/Module byte
    /// offsets — the decode path (<see cref="FanatecIdentity"/> /
    /// <see cref="SystemReportReader"/>) and the docs table both read from here.
    /// Offsets are relative to the leading <c>0xFF</c>.
    /// </summary>
    public static class SystemReport
    {
        /// <summary>col03 command class for the system report.</summary>
        public const byte CmdClass = Wire.Col03.SystemClass; // 0x08

        public static readonly ReportField BaseType =
            new ReportField("BaseType", 0x02, description: "Wheelbase model hint (low byte of SystemConfig)");
        public static readonly ReportField WheelCode =
            new ReportField("WheelCode", 0x18, description: "Attached wheel/hub wire code (0x00 = none, 0xFF = EXT_INFO)");
        public static readonly ReportField Module =
            new ReportField("Module", 0x1F, range: "0x00–0x02", description: "Button-module presence (0x00 none, 0x01 PBME, 0x02 PBMR)");

        public static readonly IReadOnlyList<ReportField> Fields = new[] { BaseType, WheelCode, Module };

        /// <summary>
        /// The identity bytes a system-report frame carries — the schema's decode
        /// output. The schema stops at the typed WIRE bytes; the device layer
        /// (<see cref="FanatecIdentity"/> / <c>FanatecBaseDriver</c>) maps them to
        /// FanaBridge codes and the peripheral model. For protobuf (<c>ff 10</c>)
        /// device-tree reports this same shape grows into a nested tree, decoded the
        /// same way — schema owns decode, the driver owns wire→peripheral.
        /// </summary>
        public struct Values
        {
            public byte BaseType;
            public byte WheelCode;
            public byte Module;
        }

        /// <summary>
        /// Reads the identity fields out of a system-report frame, where
        /// <paramref name="sig"/> is the index of the leading <c>0xFF</c> (the field
        /// offsets are relative to it). Driven by the <see cref="Fields"/> definitions
        /// — the single source of truth for which byte means what.
        /// </summary>
        public static Values Decode(byte[] frame, int sig)
        {
            return new Values
            {
                BaseType  = frame[sig + BaseType.Offset],
                WheelCode = frame[sig + WheelCode.Offset],
                Module    = frame[sig + Module.Offset],
            };
        }
    }
}
