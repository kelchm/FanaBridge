using System;
using System.Collections.Generic;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Pure MRU / favorites list rules for the property picker store. Operates on caller-
    /// owned lists so the plugin-side <see cref="IDisplayPickerStore"/> is a thin delegate
    /// that mutates <c>FanatecPluginSettings</c> lists and persists. No I/O, no SimHub.
    /// </summary>
    public static class DisplayPickerHistory
    {
        /// <summary>Maximum recent picks retained (most-recent first).</summary>
        public const int RecentsCap = 15;

        /// <summary>
        /// Toggle <paramref name="name"/> in <paramref name="favorites"/>. When
        /// <paramref name="on"/> is true and the name is absent it is appended; when false
        /// every matching entry is removed. Null/empty names are ignored. Idempotent.
        /// </summary>
        public static void SetFavorite(IList<string> favorites, string name, bool on)
        {
            if (favorites == null || string.IsNullOrEmpty(name))
                return;

            if (on)
            {
                for (int i = 0; i < favorites.Count; i++)
                {
                    if (string.Equals(favorites[i], name, StringComparison.Ordinal))
                        return;
                }
                favorites.Add(name);
                return;
            }

            for (int i = favorites.Count - 1; i >= 0; i--)
            {
                if (string.Equals(favorites[i], name, StringComparison.Ordinal))
                    favorites.RemoveAt(i);
            }
        }

        /// <summary>
        /// Push <paramref name="name"/> to the front of <paramref name="recents"/> (MRU),
        /// removing any prior occurrence, then trim to <see cref="RecentsCap"/>. Null/empty
        /// names are ignored.
        /// </summary>
        public static void NoteRecent(IList<string> recents, string name)
        {
            if (recents == null || string.IsNullOrEmpty(name))
                return;

            for (int i = recents.Count - 1; i >= 0; i--)
            {
                if (string.Equals(recents[i], name, StringComparison.Ordinal))
                    recents.RemoveAt(i);
            }

            recents.Insert(0, name);

            while (recents.Count > RecentsCap)
                recents.RemoveAt(recents.Count - 1);
        }
    }
}
