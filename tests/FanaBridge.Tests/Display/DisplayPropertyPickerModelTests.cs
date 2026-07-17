using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The property picker's SimHub-free model (<see cref="DisplayPropertyPickerModel"/>):
    /// the curated FanaBridge built-ins come first, the rest group by their first dotted
    /// segment, the substring filter is case-insensitive and spans segments, empty groups
    /// drop their header, and each property row carries the kind the picker commits
    /// (built-in vs SimHub property).
    /// </summary>
    public class DisplayPropertyPickerModelTests
    {
        private static readonly IReadOnlyList<string> BuiltIns =
            new[] { "Speed", "Gear", "Fuel" };

        private static readonly IReadOnlyList<string> AllProps = new[]
        {
            "GameData.Fuel",
            "GameData.SpeedKmh",
            "DataCorePlugin.GameRunning",
            "InputStatus.ControlMapperPlugin.Up Shift",
            "Plain",                       // no dotted namespace
        };

        private static DisplayPropertyPickerModel Model()
            => new DisplayPropertyPickerModel(BuiltIns, AllProps);

        private static List<string> Headers(IEnumerable<PickerRow> rows)
            => rows.Where(r => r.IsHeader).Select(r => r.Text).ToList();

        private static List<PickerRow> Props(IEnumerable<PickerRow> rows)
            => rows.Where(r => r.IsProperty).ToList();

        [Fact]
        public void Unfiltered_BuiltInGroupIsFirst_ThenAlphabeticalSimHubGroups()
        {
            var rows = Model().Rows("");
            var headers = Headers(rows);

            Assert.Equal(DisplayPropertyPickerModel.BuiltInGroup, headers[0]);
            // The SimHub groups follow, alphabetical: DataCorePlugin, GameData, General
            // (the no-dot bucket), InputStatus.
            Assert.Equal(new[]
            {
                DisplayPropertyPickerModel.BuiltInGroup,
                "DataCorePlugin", "GameData", "General", "InputStatus",
            }, headers);
        }

        [Fact]
        public void BuiltInRows_CarryBuiltInKind_InTheirGivenOrder()
        {
            var rows = Model().Rows("");
            int biHeader = rows.ToList().FindIndex(r => r.Text == DisplayPropertyPickerModel.BuiltInGroup);

            // The three built-ins follow their header, in the order supplied, all BuiltIn.
            Assert.Equal("Speed", rows[biHeader + 1].PropertyName);
            Assert.Equal("Gear", rows[biHeader + 2].PropertyName);
            Assert.Equal("Fuel", rows[biHeader + 3].PropertyName);
            Assert.All(new[] { rows[biHeader + 1], rows[biHeader + 2], rows[biHeader + 3] },
                r => Assert.Equal(PropertyKind.BuiltIn, r.PropertyKind));
        }

        [Fact]
        public void SimHubRows_CarrySimHubKind()
        {
            var fuel = Props(Model().Rows(""))
                .Single(r => r.PropertyName == "GameData.Fuel");
            Assert.Equal(PropertyKind.SimHubProperty, fuel.PropertyKind);
        }

        [Fact]
        public void NoDotName_GoesToTheGeneralGroup()
        {
            var rows = Model().Rows("").ToList();
            int general = rows.FindIndex(r => r.IsHeader && r.Text == DisplayPropertyPickerModel.UngroupedName);
            Assert.True(general >= 0);
            Assert.Equal("Plain", rows[general + 1].PropertyName);
        }

        [Fact]
        public void Filter_IsCaseInsensitiveSubstring_MatchingWithinSegments()
        {
            // "shift" hits only the CM role (deep in a dotted name) — nothing else.
            var rows = Model().Rows("shift");
            var props = Props(rows);
            Assert.Single(props);
            Assert.Equal("InputStatus.ControlMapperPlugin.Up Shift", props[0].PropertyName);
            // The matched group's header is present; every other group is dropped.
            Assert.Equal(new[] { "InputStatus" }, Headers(rows));
        }

        [Fact]
        public void Filter_MatchesBuiltInsToo_KeepingTheFanaBridgeGroupFirst()
        {
            // "fuel" matches the built-in "Fuel" AND "GameData.Fuel".
            var rows = Model().Rows("fuel");
            var headers = Headers(rows);
            Assert.Equal(new[] { DisplayPropertyPickerModel.BuiltInGroup, "GameData" }, headers);

            var props = Props(rows);
            Assert.Contains(props, r => r.PropertyName == "Fuel" && r.PropertyKind == PropertyKind.BuiltIn);
            Assert.Contains(props, r => r.PropertyName == "GameData.Fuel" && r.PropertyKind == PropertyKind.SimHubProperty);
        }

        [Fact]
        public void Filter_NoMatches_YieldsNothing()
        {
            var rows = Model().Rows("zzz-nothing-matches");
            Assert.Empty(rows);
            Assert.Equal(0, Model().PropertyCount("zzz-nothing-matches"));
        }

        [Fact]
        public void PropertyCount_ExcludesHeaders()
        {
            // 3 built-ins + 5 SimHub props = 8 selectable rows, no matter how many headers.
            Assert.Equal(8, Model().PropertyCount(""));
        }

        [Fact]
        public void Duplicates_AreCollapsed()
        {
            var model = new DisplayPropertyPickerModel(
                new[] { "Speed", "Speed" },
                new[] { "GameData.Fuel", "GameData.Fuel" });
            Assert.Equal(2, model.PropertyCount(""));   // one built-in + one SimHub
        }

        [Fact]
        public void NullInputs_AreEmptyNotThrowing()
        {
            var model = new DisplayPropertyPickerModel(null, null);
            Assert.Empty(model.Rows(""));
            Assert.Equal(0, model.PropertyCount(""));
        }
    }
}
