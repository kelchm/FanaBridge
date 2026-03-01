using System.Collections.Generic;
using FanatecManaged;

namespace FanaBridge
{
    /// <summary>
    /// Identifies a specific selectable device configuration — a standalone
    /// wheel or a hub + module combination.
    /// </summary>
    public class DeviceConfig
    {
        public M_FS_WHEEL_SWTYPE WheelType { get; set; }
        public M_FS_WHEEL_SW_MODULETYPE ModuleType { get; set; }
        public WheelCapabilities Capabilities { get; set; }

        /// <summary>
        /// Stable identifier for SimHub's DeviceTypeID.
        /// Format: "Fanatec_{shortcode}" for standalone wheels,
        /// "Fanatec_{hub}_{module}" for hub+module combos.
        /// Short codes are derived from SDK enum names with the
        /// verbose prefixes stripped (e.g. FS_WHEEL_SWTYPE_PSWBMW → PSWBMW).
        /// </summary>
        public string DeviceTypeId
        {
            get
            {
                string wheel = StripEnumPrefix(WheelType.ToString(), "FS_WHEEL_SWTYPE_");
                if (ModuleType != M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED)
                {
                    string module = StripEnumPrefix(ModuleType.ToString(), "FS_WHEEL_SW_MODULETYPE_");
                    return "Fanatec_" + wheel + "_" + module;
                }
                return "Fanatec_" + wheel;
            }
        }

        /// <summary>
        /// Optional parent device type ID for logo fallback.
        /// Hub+module combos use a virtual parent like "Fanatec_Module_PBMR"
        /// so all hubs sharing the same module share one logo image.
        /// Null for standalone wheels.
        /// </summary>
        public string ParentDeviceTypeId
        {
            get
            {
                if (ModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED)
                    return null;
                string module = StripEnumPrefix(ModuleType.ToString(), "FS_WHEEL_SW_MODULETYPE_");
                return "Fanatec_Module_" + module;
            }
        }

        private static string StripEnumPrefix(string enumName, string prefix)
        {
            if (enumName.StartsWith(prefix))
                return enumName.Substring(prefix.Length);
            return enumName;
        }
    }

    /// <summary>
    /// Maps known Fanatec steering wheel types to their hardware capabilities.
    /// Config-driven and easily extensible — add a new entry to register a new wheel.
    /// </summary>
    public static class WheelCapabilityRegistry
    {
        private static readonly Dictionary<M_FS_WHEEL_SWTYPE, WheelCapabilities> Registry =
            new Dictionary<M_FS_WHEEL_SWTYPE, WheelCapabilities>();

        static WheelCapabilityRegistry()
        {
            // ── Podium Steering Wheel BMW M4 GT3 ─────────────────────────
            // Small 1” OLED (basic 3-char). No rev LEDs.
            Register(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PSWBMW, new WheelCapabilities
            {
                Name = "Fanatec Podium Steering Wheel BMW M4 GT3",
                ShortName = "Fanatec BMW M4 GT3",
                ButtonLedCount = 12,
                EncoderLedCount = 0,
                Display = DisplayType.Basic,
            });

            // ── Podium Hub (bare) ─────────────────────────────────────────
            // Just a mounting adapter — no display, no LEDs, no controls.
            // Exists for identification (SDK reports PHUB).
            // Capabilities come from the attached button module
            // via ApplyModuleOverrides().
            Register(M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PHUB, new WheelCapabilities
            {
                Name = "Fanatec Podium Hub",
                ShortName = "Fanatec Podium Hub",
                ButtonLedCount = 0,
                EncoderLedCount = 0,
                Display = DisplayType.None,
            });
        }

        /// <summary>
        /// Registers a wheel type with its capabilities. Can be called at startup
        /// to extend the registry with additional wheels.
        /// </summary>
        public static void Register(M_FS_WHEEL_SWTYPE wheelType, WheelCapabilities capabilities)
        {
            Registry[wheelType] = capabilities;
        }

        /// <summary>
        /// Looks up the capabilities for a given steering wheel type and optional
        /// button module. Returns a default capability set for unrecognized wheels.
        /// </summary>
        public static WheelCapabilities GetCapabilities(
            M_FS_WHEEL_SWTYPE wheelType,
            M_FS_WHEEL_SW_MODULETYPE moduleType = M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED)
        {
            if (!Registry.TryGetValue(wheelType, out WheelCapabilities baseCaps))
            {
                SimHub.Logging.Current.Info(
                    "WheelCapabilityRegistry: No profile for wheel type " + wheelType +
                    ", using defaults");
                return WheelCapabilities.None;
            }

            // Apply button module overrides for the Podium Hub
            if (wheelType == M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PHUB)
            {
                return ApplyModuleOverrides(baseCaps, moduleType);
            }

            return baseCaps;
        }

        /// <summary>
        /// Returns true if the given wheel type has a registered capability profile.
        /// </summary>
        public static bool IsKnownWheel(M_FS_WHEEL_SWTYPE wheelType)
        {
            return Registry.ContainsKey(wheelType);
        }

        /// <summary>
        /// Enumerates all registered wheel types and their base capabilities.
        /// </summary>
        public static IEnumerable<KeyValuePair<M_FS_WHEEL_SWTYPE, WheelCapabilities>> GetAllRegistered()
        {
            return Registry;
        }

        // ── Known hub module types ───────────────────────────────────────

        /// <summary>
        /// Module types that pair with the Podium Hub to form selectable devices.
        /// Add new modules here and in the switch in ApplyModuleOverrides().
        /// </summary>
        private static readonly M_FS_WHEEL_SW_MODULETYPE[] KnownHubModules = new[]
        {
            M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_PBMR,
            M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_PBME,
        };

        /// <summary>
        /// Enumerates all selectable device configurations — standalone wheels
        /// plus hub + module combinations. The bare hub is excluded since it
        /// has no useful capabilities on its own.
        /// Used by FanatecDevicesRegistry to emit DeviceDescriptors.
        /// </summary>
        public static IEnumerable<DeviceConfig> GetDeviceConfigurations()
        {
            foreach (var entry in Registry)
            {
                var wheelType = entry.Key;
                var baseCaps = entry.Value;

                if (wheelType == M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_PHUB)
                {
                    // Emit one config per known module instead of the bare hub
                    foreach (var moduleType in KnownHubModules)
                    {
                        yield return new DeviceConfig
                        {
                            WheelType = wheelType,
                            ModuleType = moduleType,
                            Capabilities = ApplyModuleOverrides(baseCaps, moduleType),
                        };
                    }
                }
                else
                {
                    // Standalone wheel — emit directly
                    yield return new DeviceConfig
                    {
                        WheelType = wheelType,
                        ModuleType = M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED,
                        Capabilities = baseCaps,
                    };
                }
            }
        }

        // ── Module overrides ─────────────────────────────────────────────

        /// <summary>
        /// Creates a modified copy of the base capabilities with button module
        /// features applied. The base object is not mutated.
        /// </summary>
        private static WheelCapabilities ApplyModuleOverrides(
            WheelCapabilities baseCaps,
            M_FS_WHEEL_SW_MODULETYPE moduleType)
        {
            // Each case describes the complete capability set for that
            // hub + module configuration.  To add a new module: add a case.
            switch (moduleType)
            {
                case M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_PBMR:
                    // Button Module Rally: small 1” OLED (basic 3-char).
                    // 9 RGB button LEDs, 3 encoders each with an RGB LED.
                    return new WheelCapabilities
                    {
                        Name = "Fanatec Podium Hub + Button Module Rally",
                        ShortName = "Fanatec Podium Hub + BMR",
                        ButtonLedCount = 9,
                        EncoderLedCount = 3,
                        Display = DisplayType.Basic,
                    };

                case M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_PBME:
                    // Button Module Endurance: larger OLED with ITM support.
                    // No button LEDs — Rev/Flag LEDs are on col03.
                    // 9 Rev LEDs (RPM indicator), 6 Flag LEDs (status indicators).
                    return new WheelCapabilities
                    {
                        Name = "Fanatec Podium Hub + Button Module Endurance",
                        ShortName = "Fanatec Podium Hub + BME",
                        ButtonLedCount = 0,
                        EncoderLedCount = 0,
                        RevLedCount = 9,
                        FlagLedCount = 6,
                        Display = DisplayType.Itm,
                    };

                default:
                    // Unknown or no module — bare hub, nothing useful
                    return baseCaps;
            }
        }
    }
}
