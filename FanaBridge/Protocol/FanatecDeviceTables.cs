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
            { 0x1F, "ARCEE" },
            { 0xFE, "GENERIC" },
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
    }
}
