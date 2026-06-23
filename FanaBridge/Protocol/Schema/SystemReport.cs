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
    }
}
