using System;

namespace FanaBridge.Devices.Profiles
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

        /// <summary>Profile-match wheel code (e.g. "PSWBMW", "PHUB") from the profile.</summary>
        public string WheelCode => Profile?.Match?.WheelType;

        /// <summary>Button-module code, resolved from the profile match criteria, or null.</summary>
        public string ModuleCode => string.IsNullOrEmpty(Profile?.Match?.ModuleType) ? null : Profile.Match.ModuleType;

        /// <summary>
        /// Whether this descriptor matches the wheel/hub + module currently
        /// attached to the wheelbase. Matching is EXACT on both wheel and
        /// module: a bare-hub descriptor (<see cref="ModuleCode"/> null) does
        /// NOT match a hub that has a module attached — that hub is represented
        /// by the corresponding hub+module descriptor instead. This is the
        /// single predicate shared by device-state reporting
        /// (<c>GetDeviceState</c>) and capability resolution
        /// (<c>FanatecPlugin.ResolveCapsFor</c>) so the two can never diverge:
        /// when they did, a module hot-attached to an already-connected hub was
        /// ignored until the hub was reseated.
        /// </summary>
        /// <remarks>
        /// Exact module matching means an attached module with no dedicated
        /// profile leaves the bare hub unmatched (no graceful fallback).
        /// Composing base ⊕ hub ⊕ module dynamically is tracked in issue #16.
        /// </remarks>
        public bool MatchesAttachment(bool wheelDetected, string attachedWheelCode, string attachedModule)
        {
            return wheelDetected
                && string.Equals(WheelCode, attachedWheelCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ModuleCode, attachedModule, StringComparison.OrdinalIgnoreCase);
        }
    }
}
