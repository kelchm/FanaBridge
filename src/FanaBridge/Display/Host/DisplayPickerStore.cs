using System;
using System.Collections.Generic;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Thin plugin-side <see cref="IDisplayPickerStore"/>: mutates the two caller-owned
    /// favorites/recents lists via <see cref="DisplayPickerHistory"/> and invokes a persist
    /// callback (SaveCommonSettings) on every change. Plugin-wide, not per-wheel.
    /// </summary>
    internal sealed class DisplayPickerStore : IDisplayPickerStore
    {
        private readonly List<string> _favorites;
        private readonly List<string> _recents;
        private readonly Action _persist;

        public DisplayPickerStore(List<string> favorites, List<string> recents, Action persist)
        {
            _favorites = favorites ?? throw new ArgumentNullException(nameof(favorites));
            _recents = recents ?? throw new ArgumentNullException(nameof(recents));
            _persist = persist;
        }

        public IReadOnlyList<string> Favorites => _favorites;

        public IReadOnlyList<string> Recents => _recents;

        public void SetFavorite(string name, bool on)
        {
            DisplayPickerHistory.SetFavorite(_favorites, name, on);
            _persist?.Invoke();
        }

        public void NoteRecent(string name)
        {
            DisplayPickerHistory.NoteRecent(_recents, name);
            _persist?.Invoke();
        }
    }
}
