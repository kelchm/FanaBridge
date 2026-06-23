namespace FanaBridge.Devices
{
    /// <summary>
    /// Shared display-string formatting for wheel identity, so the settings device
    /// chain and the diagnostics report can never diverge. Display-only — matching
    /// always uses the codes, never these strings.
    /// </summary>
    internal static class IdentityFormatter
    {
        /// <summary>
        /// The combined display name for the attached wheel/hub(+module): the matched
        /// profile name when available, else the FanaBridge code (or an EXT_INFO /
        /// unknown marker for unmapped bytes) plus a module suffix. Mirrors the old
        /// FanatecWheelbase.DisplayName.
        /// </summary>
        public static string DisplayName(
            bool wheelDetected, string wheelCode, byte wheelWireCode, bool isHub,
            string moduleCode, byte moduleWireCode, string capsName)
        {
            if (!wheelDetected)
                return "No wheel attached";
            if (!string.IsNullOrEmpty(capsName))
                return capsName;

            string label =
                wheelCode != null     ? wheelCode :
                wheelWireCode == 0xFF ? "EXT_INFO (extended-identity wheel — please report)" :
                "Unknown (0x" + wheelWireCode.ToString("X2") + ")";
            string module =
                moduleCode != null            ? " + " + moduleCode :
                isHub && moduleWireCode != 0  ? " + Module 0x" + moduleWireCode.ToString("X2") + " (please report)" :
                                                "";
            return label + module;
        }
    }
}
