using Newtonsoft.Json;

namespace FanaBridge.Core
{
    /// <summary>
    /// Matching criteria to associate a profile with the connected hardware.
    /// Matches against SDK-reported wheel type and optional module type.
    /// </summary>
    public class ProfileMatch
    {
        /// <summary>
        /// SDK wheel type short code (e.g. "PSWBMW", "PHUB").
        /// Matched against the SDK enum name with "FS_WHEEL_SWTYPE_" stripped.
        /// </summary>
        [JsonProperty("wheelType")]
        public string WheelType { get; set; }

        /// <summary>
        /// Optional SDK module type short code (e.g. "PBMR", "PBME").
        /// Matched against the SDK enum with "FS_WHEEL_SW_MODULETYPE_" stripped.
        /// Null for standalone wheels (no module).
        /// </summary>
        [JsonProperty("moduleType", NullValueHandling = NullValueHandling.Ignore)]
        public string ModuleType { get; set; }
    }
}
