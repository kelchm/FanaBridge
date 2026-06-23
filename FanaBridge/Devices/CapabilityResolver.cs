using System;
using FanaBridge.Profiles;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Resolves the capability profile for an attached wheel/hub(+module), honoring a
    /// user profile override.
    ///
    /// COMPOSITION SEAM (#16): today this is single-source — it looks up one profile
    /// for the attached wheel/hub(+module). The full model is compositional:
    ///   EffectiveCapabilities = wheelbase-native ⊕ wheel/hub ⊕ module
    /// (e.g. a CSL Elite base contributes its own rev LEDs; a hub contributes native
    /// features plus the module's). When that lands, the base code joins this method
    /// and the merge happens HERE rather than being bolted on elsewhere.
    /// </summary>
    internal sealed class CapabilityResolver
    {
        /// <summary>
        /// Optional callback returning a profile override id for a given wheel match
        /// key (e.g. "PHUB_PBMR"); null/empty selects default auto-resolution.
        /// </summary>
        public Func<string, string> ProfileOverrideResolver { get; set; }

        /// <summary>
        /// Resolve the capabilities for the given attachment codes. Returns
        /// <see cref="WheelCapabilities.None"/> when no profile matches.
        /// <paramref name="overrideId"/> reports the override that was applied (or
        /// null) so the caller can log it.
        /// </summary>
        public WheelCapabilities Resolve(string wheelCode, string moduleCode, out string overrideId)
        {
            string matchKey = WheelProfileStore.MakeMatchKey(wheelCode, moduleCode);
            overrideId = ProfileOverrideResolver?.Invoke(matchKey);

            var profile = WheelProfileStore.FindByWheelType(wheelCode, moduleCode, overrideId);
            return profile != null
                ? new WheelCapabilities(profile)
                : WheelCapabilities.None;
        }
    }
}
