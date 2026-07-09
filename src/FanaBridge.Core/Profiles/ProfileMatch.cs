using Newtonsoft.Json;

namespace FanaBridge.Profiles
{
    /// <summary>
    /// Matching criteria to associate a profile with the connected hardware.
    /// Matches against the FF 08-decoded wheel type and optional module type.
    /// </summary>
    public class ProfileMatch
    {
        /// <summary>
        /// Wheel/hub match code (e.g. "PSWBMW", "PHUB"). Matched against the
        /// rim's decoded <c>Code</c> from the FF 08 wire byte.
        /// </summary>
        [JsonProperty("wheelType")]
        public string WheelType { get; set; }

        /// <summary>
        /// Optional module type short code (e.g. "PBMR", "PBME"), matched
        /// against the module code decoded from FF 08 byte 0x1F.
        /// Null for standalone wheels (no module).
        /// </summary>
        [JsonProperty("moduleType", NullValueHandling = NullValueHandling.Ignore)]
        public string ModuleType { get; set; }
    }
}
