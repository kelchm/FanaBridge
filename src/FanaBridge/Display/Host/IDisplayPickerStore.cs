using System.Collections.Generic;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Plugin-wide favorites + recents for the property picker. Narrow contract so the
    /// dialog never touches settings persistence directly — the host implementation is a
    /// thin delegate over <see cref="DisplayPickerHistory"/> that saves common settings
    /// on change. Not per-wheel.
    /// </summary>
    internal interface IDisplayPickerStore
    {
        /// <summary>User favorites (unordered set; insertion order is preserved for
        /// display). Empty when none.</summary>
        IReadOnlyList<string> Favorites { get; }

        /// <summary>Most-recently-used picks, most recent first, cap
        /// <see cref="DisplayPickerHistory.RecentsCap"/>.</summary>
        IReadOnlyList<string> Recents { get; }

        /// <summary>Add or remove <paramref name="name"/> from favorites. Idempotent.</summary>
        void SetFavorite(string name, bool on);

        /// <summary>Record a committed pick as the most-recent entry (dedup + cap).</summary>
        void NoteRecent(string name);
    }
}
