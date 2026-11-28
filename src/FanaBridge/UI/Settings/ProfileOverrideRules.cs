using System;
using System.Collections.Generic;
using FanaBridge.Core.Devices.Profiles;

namespace FanaBridge.UI.Settings
{
    /// <summary>
    /// Pure copy-on-write helpers for saved profile overrides, kept free of WPF
    /// so they can be unit tested. Overrides are keyed by the attachment's match
    /// key (<see cref="WheelProfileStore.MakeMatchKey"/>), with values from
    /// <see cref="WheelProfileStore.MakeOverrideKey"/>. Every operation returns a
    /// fresh case-insensitive map and never mutates the input dictionary in place.
    /// </summary>
    internal static class ProfileOverrideRules
    {
        /// <summary>
        /// Returns a new override map recording the user's profile pick for an
        /// attachment. No override is stored when the pick is what automatic
        /// resolution chooses anyway, so the attachment keeps following the default.
        /// </summary>
        /// <param name="autoResolved">What resolution picks with no override.</param>
        public static Dictionary<string, string> ApplySelection(
            IReadOnlyDictionary<string, string> overrides, string matchKey,
            string selectedOverrideKey, WheelProfile autoResolved)
        {
            var next = CopyCaseInsensitive(overrides);
            if (string.IsNullOrEmpty(matchKey))
                return next;

            string autoOverrideKey = autoResolved != null
                ? WheelProfileStore.MakeOverrideKey(autoResolved)
                : null;

            if (string.Equals(selectedOverrideKey, autoOverrideKey, StringComparison.OrdinalIgnoreCase))
                next.Remove(matchKey);
            else
                next[matchKey] = selectedOverrideKey;

            return next;
        }

        /// <summary>
        /// Returns a new override map with <paramref name="matchKey"/> pinned to
        /// <paramref name="overrideKey"/> (used when the wizard saves a new profile).
        /// </summary>
        public static Dictionary<string, string> WithOverride(
            IReadOnlyDictionary<string, string> overrides, string matchKey, string overrideKey)
        {
            var next = CopyCaseInsensitive(overrides);
            if (!string.IsNullOrEmpty(matchKey))
                next[matchKey] = overrideKey;
            return next;
        }

        /// <summary>
        /// Returns a new override map with the deleted profile's override cleared —
        /// only when that override named the deleted profile, so a pin to some
        /// other profile survives. Sets <paramref name="removed"/> to true when an
        /// override was removed.
        /// </summary>
        /// <param name="matchKey">The deleted profile's own attachment, not
        /// whatever happens to be attached when the delete completes.</param>
        public static Dictionary<string, string> ClearAfterDelete(
            IReadOnlyDictionary<string, string> overrides, string matchKey,
            string deletedOverrideKey, out bool removed)
        {
            var next = CopyCaseInsensitive(overrides);
            if (string.IsNullOrEmpty(matchKey))
            {
                removed = false;
                return next;
            }

            removed = next.TryGetValue(matchKey, out var stored)
                && string.Equals(stored, deletedOverrideKey, StringComparison.OrdinalIgnoreCase)
                && next.Remove(matchKey);
            return next;
        }

        private static Dictionary<string, string> CopyCaseInsensitive(
            IReadOnlyDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
                foreach (var kv in source)
                    if (kv.Key != null)
                        result[kv.Key] = kv.Value;
            return result;
        }
    }
}
