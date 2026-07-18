using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Host;
using Xunit;

namespace FanaBridge.Tests.Display.Host
{
    /// <summary>
    /// Pure favorites / recents list rules (<see cref="DisplayPickerHistory"/>): MRU order,
    /// cap 15, dedup-on-renote, and favorite toggle idempotence.
    /// </summary>
    public class DisplayPickerHistoryTests
    {
        [Fact]
        public void NoteRecent_PushesMostRecentFirst()
        {
            var recents = new List<string>();
            DisplayPickerHistory.NoteRecent(recents, "A");
            DisplayPickerHistory.NoteRecent(recents, "B");
            DisplayPickerHistory.NoteRecent(recents, "C");
            Assert.Equal(new[] { "C", "B", "A" }, recents);
        }

        [Fact]
        public void NoteRecent_DedupsOnRenote_MovesToFront()
        {
            var recents = new List<string> { "A", "B", "C" };
            DisplayPickerHistory.NoteRecent(recents, "B");
            Assert.Equal(new[] { "B", "A", "C" }, recents);
        }

        [Fact]
        public void NoteRecent_CapsAt15_DropsOldest()
        {
            var recents = new List<string>();
            for (int i = 0; i < DisplayPickerHistory.RecentsCap + 3; i++)
                DisplayPickerHistory.NoteRecent(recents, "p" + i);

            Assert.Equal(DisplayPickerHistory.RecentsCap, recents.Count);
            Assert.Equal("p" + (DisplayPickerHistory.RecentsCap + 2), recents[0]); // newest
            Assert.Equal("p3", recents[recents.Count - 1]); // oldest kept (0..2 dropped)
            Assert.DoesNotContain("p0", recents);
            Assert.DoesNotContain("p1", recents);
            Assert.DoesNotContain("p2", recents);
        }

        [Fact]
        public void NoteRecent_NullOrEmpty_IsNoOp()
        {
            var recents = new List<string> { "A" };
            DisplayPickerHistory.NoteRecent(recents, null);
            DisplayPickerHistory.NoteRecent(recents, "");
            DisplayPickerHistory.NoteRecent(null, "B");
            Assert.Equal(new[] { "A" }, recents);
        }

        [Fact]
        public void SetFavorite_On_AddsOnce_Idempotent()
        {
            var fav = new List<string>();
            DisplayPickerHistory.SetFavorite(fav, "Fuel", on: true);
            DisplayPickerHistory.SetFavorite(fav, "Fuel", on: true);
            Assert.Equal(new[] { "Fuel" }, fav);
        }

        [Fact]
        public void SetFavorite_Off_RemovesAllMatches_Idempotent()
        {
            var fav = new List<string> { "Fuel", "Gear", "Fuel" };
            DisplayPickerHistory.SetFavorite(fav, "Fuel", on: false);
            Assert.Equal(new[] { "Gear" }, fav);
            DisplayPickerHistory.SetFavorite(fav, "Fuel", on: false);
            Assert.Equal(new[] { "Gear" }, fav);
        }

        [Fact]
        public void SetFavorite_NullOrEmpty_IsNoOp()
        {
            var fav = new List<string> { "A" };
            DisplayPickerHistory.SetFavorite(fav, null, on: true);
            DisplayPickerHistory.SetFavorite(fav, "", on: true);
            DisplayPickerHistory.SetFavorite(null, "B", on: true);
            Assert.Equal(new[] { "A" }, fav);
        }

        [Fact]
        public void SetFavorite_PreservesInsertionOrderOfOthers()
        {
            var fav = new List<string> { "A", "B", "C" };
            DisplayPickerHistory.SetFavorite(fav, "D", on: true);
            DisplayPickerHistory.SetFavorite(fav, "B", on: false);
            Assert.Equal(new[] { "A", "C", "D" }, fav.ToArray());
        }
    }
}
