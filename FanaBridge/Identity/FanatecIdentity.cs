using System.Collections.Generic;
using FanatecManaged;

namespace FanaBridge.Identity
{
    /// <summary>
    /// Static decode tables + helpers for resolving Fanatec wheel identity from
    /// the col03 <c>FF 08</c> system report — fully over HID, with no Fanatec
    /// driver, service, or SimHub.FanatecManaged.dll.
    ///
    /// Report layout (offsets relative to the leading 0xFF):
    ///   byte 0x02 = BaseType, byte 0x18 = RimType(raw), byte 0x1F = Module.
    /// The decode values/names follow Fanatec's own device numbering and product
    /// names. Verified on hardware: PSWBMW raw 0x0F→20, PHUB raw 0x0C→11, matching
    /// the RimType value the Fanatec software records.
    /// </summary>
    public static class FanatecIdentity
    {
        /// <summary>FF 08 report offsets (relative to the leading 0xFF byte).</summary>
        public const int OffBaseType = 0x02;
        public const int OffRimType  = 0x18;
        public const int OffModule   = 0x1F;

        public readonly struct RimInfo
        {
            public readonly M_FS_WHEEL_SWTYPE Type;
            public readonly string Name;
            public readonly bool IsHub;
            public RimInfo(M_FS_WHEEL_SWTYPE type, string name, bool isHub)
            { Type = type; Name = name; IsHub = isHub; }
        }

        // Raw FF08[0x18] -> (wheel-type enum, friendly name, isHub).
        // Friendly names are the Fanatec product names; the trailing comment on
        // each row is Fanatec's internal short code for that raw value.
        // Hubs accept a button module (see ModuleDecode); wheels do not.
        private static readonly Dictionary<byte, RimInfo> RimDecode = new Dictionary<byte, RimInfo>
        {
            { 0x01, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWBMW,     "ClubSport Steering Wheel BMW",        false) }, // CSWRBMW
            { 0x02, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWFORM,    "ClubSport Steering Wheel Formula",    false) }, // CSWRFORM
            { 0x03, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWPORSCHE, "ClubSport Steering Wheel Porsche",    false) }, // CSWRPORSCHE
            { 0x04, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "ClubSport Universal Hub",             true ) }, // CSWRUH
            { 0x06, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "ClubSport Universal Hub X",           true ) }, // CSWRUHX
            { 0x07, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSLESWP1X,   "CSL Elite Steering Wheel P1 (Xbox)",  false) }, // CSLRP1X
            { 0x08, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSLESWP1PS4, "CSL Elite Steering Wheel P1 (PS4)",   false) }, // CSLRP1PS4
            { 0x09, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSLESWMCL,   "CSL Elite Steering Wheel McLaren GT3",false) }, // CSLRMCLV1_0
            { 0x0A, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWFORMV2,  "ClubSport Steering Wheel Formula V2", false) }, // CSWRFORMV2
            { 0x0B, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSLESWMCLV2, "CSL Elite Steering Wheel McLaren GT3 V2", false) }, // CSLRMCLV1_1
            { 0x0C, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PHUB,        "Podium Hub",                          true ) }, // PHUB  (verified)
            { 0x0E, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PSWBENT,     "Podium Steering Wheel Bentley GT3",   false) }, // PSWBENT
            { 0x0F, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PSWBMW,      "Podium Steering Wheel BMW M4 GT3",    false) }, // PSWBMW (verified)
            { 0x10, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "Podium Racing Wheel GT",              false) }, // DDRGT
            { 0x11, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "CSL Universal Hub",                   true ) }, // CSLRUH
            { 0x12, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSLESWWRC,   "CSL Elite Steering Wheel WRC",        false) }, // CSLRWRC
            { 0x13, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWBMWV2,   "ClubSport Steering Wheel BMW V2",     false) }, // CSSWBMWV2
            { 0x14, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWRS,      "ClubSport Steering Wheel RS",         false) }, // CSSWRS
            { 0x15, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "ClubSport Universal Hub V2",          true ) }, // CSUHV2
            { 0x16, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWF1ESV2,  "ClubSport Steering Wheel F1 Esports V2", false) }, // CSSWF1V2
            { 0x17, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PSWBMW,      "Podium Steering Wheel BMW M4 GT3",    false) }, // PSWBMW (alt raw)
            { 0x18, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "Podium Racing Wheel GT (X)",          false) }, // DDRGTX
            { 0x1B, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWPVGT,    "ClubSport Steering Wheel Podium GT",  false) }, // CSSWPVGT
            { 0x1C, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSSWFORMV3,  "ClubSport Steering Wheel Formula V3", false) }, // CSSWFORMV3
            { 0x1D, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_CSLSWGT3,    "CSL Steering Wheel GT3",              false) }, // CSLSWGT3
            { 0x1E, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "Wheel Hub",                           true ) }, // WHEELHUB
            { 0x1F, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "ClubSport RennSport Hub (ARC)",       true ) }, // ARCEE
            { 0xFE, new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNKNOWN,     "Generic Wheel",                       false) }, // GENERIC
        };

        /// <summary>Decode the raw rim byte (FF08[0x18]) into wheel/hub identity.</summary>
        public static RimInfo DecodeRim(byte raw)
        {
            return RimDecode.TryGetValue(raw, out var info)
                ? info
                : new RimInfo(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED, "No wheel", false);
        }

        public readonly struct ModuleInfo
        {
            public readonly M_FS_WHEEL_SW_MODULETYPE Type;
            public readonly string Name;
            public ModuleInfo(M_FS_WHEEL_SW_MODULETYPE type, string name) { Type = type; Name = name; }
        }

        /// <summary>Decode the module byte (FF08[0x1F]). Only present on hubs.</summary>
        public static ModuleInfo DecodeModule(byte raw)
        {
            switch (raw)
            {
                case 1:  return new ModuleInfo(M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_PBME, "Podium Button Module Endurance");
                case 2:  return new ModuleInfo(M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_PBMR, "Podium Button Module Rally");
                default: return new ModuleInfo(M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED, null);
            }
        }

        // Wheelbase model name keyed by the FF08 BaseType byte. This byte is
        // Fanatec's base-type enum (e.g. 12 = ClubSport DD+, verified on hardware).
        // Values + names follow Fanatec's own device naming.
        private static readonly Dictionary<byte, string> BaseNameByType = new Dictionary<byte, string>
        {
            {  1, "ClubSport Wheel Base V2" },         // CSWV2
            {  2, "ClubSport Wheel Base V2.5" },       // CSWV25
            {  3, "CSL Elite Wheel Base" },            // CSLE_1_0
            {  4, "CSL Elite Wheel Base V1.1" },       // CSLE_1_1
            {  5, "CSL Elite Wheel Base + (PS4)" },    // CSLEPS4
            {  6, "Podium Wheel Base DD1" },           // PDD1
            {  7, "Podium Wheel Base DD1 (PS4)" },     // PDD1_PS4
            {  8, "Podium Wheel Base DD2" },           // PDD2
            {  9, "GT DD Pro Wheel Base" },            // GTDDPRO
            { 10, "CSL DD" },                          // CSLDD
            { 11, "ClubSport DD Wheel Base" },         // CSDD
            { 12, "ClubSport DD+ Wheel Base" },        // CSDDPlus  (verified)
            { 13, "Podium Wheel Base DD" },            // PDD25
            { 14, "Podium Wheel Base DD+" },           // PDD25PLUS
            { 99, "ClubSport Wheel Base V1" },         // CSWV1
        };

        /// <summary>
        /// Human-readable wheelbase name from the FF08 BaseType byte
        /// (Fanatec's base-type enum). Falls back to surfacing the raw value
        /// (and PID) for any base newer than this table.
        /// </summary>
        public static string DecodeBaseName(int productId, byte baseType)
        {
            if (BaseNameByType.TryGetValue(baseType, out var name))
                return name;

            return baseType != 0
                ? "Fanatec Wheel Base (BaseType " + baseType + ", PID 0x" + productId.ToString("X4") + ")"
                : "Fanatec Wheel Base";
        }
    }
}
