namespace FanaBridge.Protocol
{
    /// <summary>
    /// Decodes wheelbase + attachment + module identity from the col03 <c>FF 08</c>
    /// system report — fully over HID, with no Fanatec driver, service, or
    /// SimHub.FanatecManaged.dll.
    ///
    /// Every decode returns a FanaBridge code (the identifier used by profiles) or
    /// null — no human-readable names (those belong to the matched profile). The
    /// code tables live in <see cref="FanatecDeviceTables"/>.
    ///
    /// Report layout (offsets relative to the leading 0xFF):
    ///   byte 0x02 = BaseType, byte 0x18 = attachment wire code, byte 0x1F = module.
    /// </summary>
    public static class FanatecIdentity
    {
        // Offsets sourced from the report definition (single source of truth).
        public static readonly int OffBaseType = Schema.SystemReport.BaseType.Offset; // 0x02
        public static readonly int OffWireCode = Schema.SystemReport.WheelCode.Offset; // 0x18
        public static readonly int OffModule   = Schema.SystemReport.Module.Offset;    // 0x1F

        /// <summary>
        /// Decode the attachment wire byte (0x18) into a FanaBridge wheel/hub code,
        /// or null when nothing is attached / the code is unrecognized.
        /// </summary>
        public static string DecodeCode(byte wire)
            => FanatecDeviceTables.Wheels.TryGetValue(wire, out var w) ? w
             : FanatecDeviceTables.Hubs.TryGetValue(wire, out var h) ? h
             : null;

        /// <summary>True when the attached device is a hub (and may carry a module).</summary>
        public static bool IsHub(byte wire) => FanatecDeviceTables.Hubs.ContainsKey(wire);

        /// <summary>
        /// Decode the module byte (0x1F) into a FanaBridge module code, or null when
        /// no module is present. Only meaningful on hubs.
        /// </summary>
        public static string DecodeModule(byte raw)
            => FanatecDeviceTables.Modules.TryGetValue(raw, out var m) ? m : null;

        /// <summary>
        /// Decode the BaseType byte (0x02) into a FanaBridge wheelbase code, or null
        /// for a wheelbase newer than the table.
        /// </summary>
        public static string DecodeBaseCode(byte baseType)
            => FanatecDeviceTables.Wheelbases.TryGetValue(baseType, out var b) ? b : null;

        // ── Friendly display names (UI only) ──────────────────────────────

        /// <summary>Friendly display name for a wheelbase code, or null if unmapped.</summary>
        public static string FriendlyBase(string baseCode)
            => baseCode != null && FanatecDeviceTables.WheelbaseNames.TryGetValue(baseCode, out var n) ? n : null;

        /// <summary>Friendly display name for a wheel/hub code, or null if unmapped.</summary>
        public static string FriendlyAttachment(string wheelCode)
            => wheelCode != null && FanatecDeviceTables.AttachmentNames.TryGetValue(wheelCode, out var n) ? n : null;

        /// <summary>Friendly display name for a module code, or null if unmapped.</summary>
        public static string FriendlyModule(string moduleCode)
            => moduleCode != null && FanatecDeviceTables.ModuleNames.TryGetValue(moduleCode, out var n) ? n : null;
    }
}
