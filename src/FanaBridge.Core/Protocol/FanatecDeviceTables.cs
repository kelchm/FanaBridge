using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Reference tables mapping the raw FF 08 system-report bytes to FanaBridge
    /// device codes. Each code is the identifier used throughout FanaBridge (it
    /// equals the matching profile's <c>wheelType</c> / <c>moduleType</c>).
    ///
    /// One entry per line, keyed by the raw byte. Wheels and hubs share the
    /// attachment-byte (0x18) space but are kept in separate tables — the table a
    /// byte lives in IS its category. Only hubs accept a button module.
    ///
    /// This is reference DATA only; the decode logic lives in
    /// <see cref="FanatecIdentity"/>.
    /// </summary>
    internal static class FanatecDeviceTables
    {
        // Attachment wire byte (0x18) -> wheel code.
        public static readonly IReadOnlyDictionary<byte, string> Wheels = new Dictionary<byte, string>
        {
            { 0x01, "CSSWBMW" },
            { 0x02, "CSSWFORM" },
            { 0x03, "CSSWPORSCHE" },
            { 0x07, "CSLESWP1X" },
            { 0x08, "CSLESWP1PS4" },
            { 0x09, "CSLESWMCL" },
            { 0x0A, "CSSWFORMV2" },
            { 0x0B, "CSLESWMCLV2" },
            { 0x0E, "PSWBENT" },
            { 0x0F, "PSWBMW" },
            { 0x10, "GTSWPRO" },
            { 0x12, "CSLESWWRC" },
            { 0x13, "CSSWBMWV2" },
            { 0x14, "CSSWRS" },
            { 0x16, "CSSWF1ESV2" },
            { 0x17, "PSWBMW" },      // hardware revision of PSWBMW (0x0F)?
            { 0x18, "GTSWX" },
            { 0x1B, "CSSWPVGT" },
            { 0x1C, "CSSWFORMV3" },
            { 0x1D, "CSLSWGT3" },
        };

        // Attachment wire byte (0x18) -> hub code. Hubs accept a button module.
        public static readonly IReadOnlyDictionary<byte, string> Hubs = new Dictionary<byte, string>
        {
            { 0x04, "CSSWUH" },
            { 0x06, "CSSWUHX" },
            { 0x0C, "PHUB" },
            { 0x11, "CSLSWUH" },
            { 0x15, "CSUHV2" },
            { 0x1E, "WHEELHUB" },
        };

        // Module byte (0x1F) -> button-module code.
        public static readonly IReadOnlyDictionary<byte, string> Modules = new Dictionary<byte, string>
        {
            { 0x01, "PBME" },
            { 0x02, "PBMR" },
        };

        // BaseType byte (0x02) -> wheelbase code.
        public static readonly IReadOnlyDictionary<byte, string> Wheelbases = new Dictionary<byte, string>
        {
            {  1, "CSWV2" },
            {  2, "CSWV25" },
            {  3, "CSLE_1_0" },
            {  4, "CSLE_1_1" },
            {  5, "CSLEPS4" },
            {  6, "PDD1" },
            {  7, "PDD1_PS4" },
            {  8, "PDD2" },
            {  9, "GTDDPRO" },
            { 10, "CSLDD" },
            { 11, "CSDD" },
            { 12, "CSDDPlus" },
            { 13, "PDD25" },
            { 14, "PDD25PLUS" },
            { 99, "CSWV1" },
        };

        // ── Friendly display names (UI only) ──────────────────────────────
        // Concise, human-readable names keyed by FanaBridge code, for the
        // Device Status readout. Display-only: identity/profile matching always
        // uses the codes above, never these strings. Missing entries fall back
        // to the code (then the raw byte), so an unmapped device still shows.
        //
        // Transitional — these should ultimately live in per-component profiles
        // (issue #16). Keep them aligned with Resources/Profiles/*.json.

        // Wheelbase code -> friendly name.
        public static readonly IReadOnlyDictionary<string, string> WheelbaseNames = new Dictionary<string, string>
        {
            { "CSWV1",     "ClubSport V1" },
            { "CSWV2",     "ClubSport V2" },
            { "CSWV25",    "ClubSport V2.5" },
            { "CSLE_1_0",  "CSL Elite" },
            { "CSLE_1_1",  "CSL Elite V1.1" },
            { "CSLEPS4",   "CSL Elite+ (PS4)" },
            { "CSLDD",     "CSL DD" },
            { "CSDD",      "ClubSport DD" },
            { "CSDDPlus",  "ClubSport DD+" },
            { "GTDDPRO",   "GT DD Pro" },
            { "PDD1",      "Podium DD1" },
            { "PDD1_PS4",  "Podium DD1 (PS4)" },
            { "PDD2",      "Podium DD2" },
            { "PDD25",     "Podium DD" },
            { "PDD25PLUS", "Podium DD+" },
        };

        // Wheel/hub code -> friendly name.
        public static readonly IReadOnlyDictionary<string, string> AttachmentNames = new Dictionary<string, string>
        {
            // Wheels
            { "CSSWBMW",     "ClubSport BMW M3 GT2" },
            { "CSSWFORM",    "ClubSport Formula Carbon" },
            { "CSSWPORSCHE", "ClubSport Porsche 918 RSR" },
            { "CSLESWP1X",   "CSL Elite P1 (Xbox)" },
            { "CSLESWP1PS4", "CSL Elite P1 (PS4)" },
            { "CSLESWMCL",   "CSL Elite McLaren GT3 V1" },
            { "CSSWFORMV2",  "ClubSport Formula V2.5" },
            { "CSLESWMCLV2", "CSL Elite McLaren GT3 V2" },
            { "PSWBENT",     "Podium Bentley GT3" },
            { "PSWBMW",      "Podium BMW M4 GT3" },
            { "GTSWPRO",     "GT DD Pro Wheel" },
            { "CSLESWWRC",   "CSL Elite WRC" },
            { "CSSWBMWV2",   "ClubSport BMW M3 GT2 V2" },
            { "CSSWRS",      "ClubSport RS" },
            { "CSSWF1ESV2",  "ClubSport F1 Esports V2" },
            { "GTSWX",       "GT Extreme" },
            { "CSSWPVGT",    "CSL Elite Porsche Vision GT" },
            { "CSSWFORMV3",  "ClubSport Formula V3" },
            { "CSLSWGT3",    "CSL GT3" },
            // Hubs
            { "CSSWUH",      "ClubSport Universal Hub" },
            { "CSSWUHX",     "ClubSport Universal Hub (Xbox)" },
            { "PHUB",        "Podium Hub" },
            { "CSLSWUH",     "CSL Universal Hub" },
            { "CSUHV2",      "ClubSport Universal Hub V2" },
            { "WHEELHUB",    "Wheel Hub" },
        };

        // Module code -> friendly name.
        public static readonly IReadOnlyDictionary<string, string> ModuleNames = new Dictionary<string, string>
        {
            { "PBME", "Button Module Endurance" },
            { "PBMR", "Button Module Rally" },
        };
    }
}
