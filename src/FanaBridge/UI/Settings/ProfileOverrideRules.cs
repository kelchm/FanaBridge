using System;
using System.Collections.Generic;
using FanaBridge.Core.Devices.Profiles;

namespace FanaBridge.UI.Settings
{
    /// <summary>
    /// The settings page's two decisions about saved profile overrides, kept
    /// free of WPF so they can be unit tested. Overrides are keyed by the
    /// attachment's match key (<see cref="WheelProfileStore.MakeMatchKey"/>),
    /// with values from <see cref="WheelProfileStore.MakeOverrideKey"/>.
    /// </summary>
    internal static class ProfileOverrideRules
    {
        /// <summary>
        /// Records the user's profile pick for an attachment. No override is
        /// stored when the pick is what automatic resolution chooses anyway, so
        /// the attachment keeps following the default.
        /// </summary>
        /// <param name="autoResolved">What resolution picks with no override.</param>
        public static void ApplySelection(
            IDictionary<string, string> overrides, string matchKey,
            string selectedOverrideKey, WheelProfile autoResolved)
        {
            if (string.IsNullOrEmpty(matchKey))
                return;

            string autoOverrideKey = autoResolved != null
                ? WheelProfileStore.MakeOverrideKey(autoResolved)
                : null;

            if (string.Equals(selectedOverrideKey, autoOverrideKey, StringComparison.OrdinalIgnoreCase))
                overrides.Remove(matchKey);
            else
                overrides[matchKey] = selectedOverrideKey;
        }

        /// <summary>
        /// After a user profile is deleted, clears its attachment's override —
        /// only when that override named the deleted profile, so a pin to some
        /// other profile survives. Returns true when an override was removed.
        /// </summary>
        /// <param name="matchKey">The deleted profile's own attachment, not
        /// whatever happens to be attached when the delete completes.</param>
        public static bool ClearAfterDelete(
            IDictionary<string, string> overrides, string matchKey, string deletedOverrideKey)
        {
            if (string.IsNullOrEmpty(matchKey))
                return false;

            return overrides.TryGetValue(matchKey, out var stored)
                && string.Equals(stored, deletedOverrideKey, StringComparison.OrdinalIgnoreCase)
                && overrides.Remove(matchKey);
        }
    }
}
