// FanaBridge-owned replacements for the wheel/module identity enums that were
// previously provided by SimHub.FanatecManaged.dll. Declared in the original
// `FanatecManaged` namespace so existing `using FanatecManaged;` consumers
// (DeviceConfig, FanatecWheelDeviceInstance, SettingsControl, the wizard, and
// FanatecSdkManager) compile unchanged once the DLL reference is dropped.
//
// Member names MUST stay `FS_WHEEL_SWTYPE_<code>` / `FS_WHEEL_SW_MODULETYPE_<code>`
// because WheelProfileStore.StripWheelPrefix and DeviceConfig.Enum.TryParse key
// profiles off the stripped code (e.g. "PSWBMW", "PHUB", "PBMR"). Values follow
// Fanatec's RimType numbering where known (see FanatecIdentity), so the
// `FanaBridge.WheelType` property surfaces the same number the Fanatec registry
// records.
//
// Identity itself is read over pure HID from the col03 FF 08 system report — no
// Fanatec driver, no SimHub DLL. See FanatecIdentity / Ff08IdentityReader.
namespace FanatecManaged
{
    /// <summary>Steering-wheel / hub type. Values mirror the Fanatec RimType enum.</summary>
    public enum M_FS_WHEEL_SWTYPE
    {
        FS_WHEEL_SWTYPE_UNINITIALIZED = 0,

        // ClubSport Steering Wheels
        FS_WHEEL_SWTYPE_CSSWBMW       = 1,
        FS_WHEEL_SWTYPE_CSSWFORM      = 2,
        FS_WHEEL_SWTYPE_CSSWPORSCHE   = 3,
        FS_WHEEL_SWTYPE_CSSWFORMV2    = 9,
        FS_WHEEL_SWTYPE_CSSWBMWV2     = 15,
        FS_WHEEL_SWTYPE_CSSWRS        = 16,
        FS_WHEEL_SWTYPE_CSSWF1ESV2    = 18,
        FS_WHEEL_SWTYPE_CSSWPVGT      = 22,
        FS_WHEEL_SWTYPE_CSSWFORMV3    = 23,

        // CSL (Elite) Steering Wheels
        FS_WHEEL_SWTYPE_CSLESWP1X     = 6,
        FS_WHEEL_SWTYPE_CSLESWP1PS4   = 7,
        FS_WHEEL_SWTYPE_CSLESWMCL     = 8,
        FS_WHEEL_SWTYPE_CSLESWMCLV2   = 10,
        FS_WHEEL_SWTYPE_CSLESWWRC     = 14,
        FS_WHEEL_SWTYPE_CSLSWGT3      = 24,

        // GT Steering Wheels (no entry in the current filter table; kept for profiles)
        FS_WHEEL_SWTYPE_GTSWPRO       = 101,
        FS_WHEEL_SWTYPE_GTSWX         = 102,

        // Podium Steering Wheels
        FS_WHEEL_SWTYPE_PSWBENT       = 19,
        FS_WHEEL_SWTYPE_PSWBMW        = 20,

        // Hubs (accept button modules)
        FS_WHEEL_SWTYPE_PHUB          = 11,

        // Present but not yet mapped to a profile (filter table has them).
        FS_WHEEL_SWTYPE_UNKNOWN       = 29,
    }

    /// <summary>Attached button-module type (only hubs accept a module).</summary>
    public enum M_FS_WHEEL_SW_MODULETYPE
    {
        FS_WHEEL_SW_MODULETYPE_UNINITIALIZED = 0,
        FS_WHEEL_SW_MODULETYPE_PBME          = 1,
        FS_WHEEL_SW_MODULETYPE_PBMR          = 2,
    }
}
