using System;
using FanaBridge.Core;
using FanatecManaged;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Identifies a specific selectable device configuration — a standalone
    /// wheel or a hub + module combination — built from a <see cref="WheelProfile"/>.
    /// </summary>
    public class DeviceConfig
    {
        /// <summary>The wheel profile this config was built from.</summary>
        public WheelProfile Profile { get; set; }

        /// <summary>Resolved capabilities (computed view of the profile).</summary>
        public WheelCapabilities Capabilities { get; set; }

        /// <summary>
        /// Stable identifier for SimHub's DeviceTypeID.
        /// Derived from the profile's match criteria.
        /// Format: "Fanatec_{wheelType}" or "Fanatec_{wheelType}_{moduleType}".
        /// </summary>
        public string DeviceTypeId
        {
            get
            {
                if (Profile?.Match == null)
                    return "Fanatec_" + (Profile?.Id ?? "Unknown");

                if (!string.IsNullOrEmpty(Profile.Match.ModuleType))
                    return "Fanatec_" + Profile.Match.WheelType + "_" + Profile.Match.ModuleType;

                return "Fanatec_" + Profile.Match.WheelType;
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
                if (Profile?.Match == null || string.IsNullOrEmpty(Profile.Match.ModuleType))
                    return null;
                return "Fanatec_Module_" + Profile.Match.ModuleType;
            }
        }

        /// <summary>SDK wheel type enum, resolved from profile match criteria.</summary>
        public M_FS_WHEEL_SWTYPE WheelType
        {
            get
            {
                if (Profile?.Match == null) return M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;
                string fullName = "FS_WHEEL_SWTYPE_" + Profile.Match.WheelType;
                if (Enum.TryParse(fullName, out M_FS_WHEEL_SWTYPE result))
                    return result;
                return M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;
            }
        }

        /// <summary>SDK module type enum, resolved from profile match criteria.</summary>
        public M_FS_WHEEL_SW_MODULETYPE ModuleType
        {
            get
            {
                if (Profile?.Match == null || string.IsNullOrEmpty(Profile.Match.ModuleType))
                    return M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;
                string fullName = "FS_WHEEL_SW_MODULETYPE_" + Profile.Match.ModuleType;
                if (Enum.TryParse(fullName, out M_FS_WHEEL_SW_MODULETYPE result))
                    return result;
                return M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;
            }
        }
    }
}
