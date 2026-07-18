using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.UI.Display;
using FanaBridge.UI.Display.Shared;
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

        // ── Mapped controls group (v9 unified add flow) ───────────────────

        private static DisplayPropertyPickerModel MappedModel()
            => new DisplayPropertyPickerModel(BuiltIns, AllProps,
                new[] { "Up Shift", "Headlights" });

        [Fact]
        public void MappedControls_GroupIsSecond_MappingRoleToItsProperty()
        {
            var rows = MappedModel().Rows("");
            var headers = Headers(rows);

            // FanaBridge first, Mapped controls second, then the alphabetical SimHub groups.
            Assert.Equal(DisplayPropertyPickerModel.BuiltInGroup, headers[0]);
            Assert.Equal(DisplayPropertyPickerModel.MappedGroup, headers[1]);

            // Each role maps to its live Control Mapper property, committed as a SimHub
            // property. The first property row under the Mapped controls header is Up Shift.
            var list = rows.ToList();
            int mappedHeader = list.FindIndex(r =>
                r.IsHeader && r.Text == DisplayPropertyPickerModel.MappedGroup);
            var first = list[mappedHeader + 1];
            Assert.Equal("InputStatus.ControlMapperPlugin.Up Shift", first.PropertyName);
            Assert.Equal(PropertyKind.SimHubProperty, first.PropertyKind);
            var second = list[mappedHeader + 2];
            Assert.Equal("InputStatus.ControlMapperPlugin.Headlights", second.PropertyName);
        }

        [Fact]
        public void MappedControls_FilterMatchesTheRoleName_AcrossGroups()
        {
            // "shift" now matches the mapped role AND the InputStatus CM property name.
            var rows = MappedModel().Rows("shift");
            var headers = Headers(rows);
            Assert.Equal(new[] { DisplayPropertyPickerModel.MappedGroup, "InputStatus" }, headers);

            var props = Props(rows);
            Assert.Equal(2, props.Count);
            Assert.All(props, r => Assert.Contains("Shift", r.PropertyName));
        }

        [Fact]
        public void MappedControls_AbsentWhenNoRoles_LeavesTheOtherGroupsUnchanged()
        {
            // The 2-arg model (no roles) yields no Mapped controls group at all.
            var headers = Headers(Model().Rows(""));
            Assert.DoesNotContain(DisplayPropertyPickerModel.MappedGroup, headers);
        }

        // ── Rails + scopes (v9 phase 5a) ───────────────────────────────────

        private static DisplayPropertyPickerModel ScopedModel(
            IReadOnlyList<string> favorites = null,
            IReadOnlyList<string> recents = null,
            IReadOnlyList<string> itmPages = null,
            IReadOnlyList<string> mappedRoles = null)
            => new DisplayPropertyPickerModel(BuiltIns, AllProps, mappedRoles,
                favorites, recents, itmPages);

        [Fact]
        public void Rails_Order_FixedThenSectionThenAllThenPinnedThenAutoRoots()
        {
            var rails = ScopedModel(mappedRoles: new[] { "Up Shift" }).Rails();
            var ids = rails.Select(r => r.Id).ToList();

            Assert.Equal(new[]
            {
                DisplayPropertyPickerModel.RailFavoritesId,
                DisplayPropertyPickerModel.RailRecentsId,
                DisplayPropertyPickerModel.RailItmPagesId,
                DisplayPropertyPickerModel.RailSectionRootsId,
                DisplayPropertyPickerModel.RailAllId,
                DisplayPropertyPickerModel.RailRootIdPrefix + DisplayPropertyPickerModel.BuiltInGroup,
                DisplayPropertyPickerModel.RailRootIdPrefix + DisplayPropertyPickerModel.MappedGroup,
                DisplayPropertyPickerModel.RailRootIdPrefix + "DataCorePlugin",
                DisplayPropertyPickerModel.RailRootIdPrefix + "GameData",
                DisplayPropertyPickerModel.RailRootIdPrefix + DisplayPropertyPickerModel.UngroupedName,
                DisplayPropertyPickerModel.RailRootIdPrefix + "InputStatus",
            }, ids);

            Assert.True(rails.Single(r => r.Id == DisplayPropertyPickerModel.RailSectionRootsId).IsSection);
            Assert.Equal(DisplayPropertyPickerModel.RootsSectionLabel,
                rails.Single(r => r.IsSection).Label);
            Assert.Equal(DisplayPropertyPickerModel.RailFavoritesGlyph,
                rails.Single(r => r.Id == DisplayPropertyPickerModel.RailFavoritesId).Glyph);
            Assert.Equal(DisplayPropertyPickerModel.RailAllGlyph,
                rails.Single(r => r.Id == DisplayPropertyPickerModel.RailAllId).Glyph);
        }

        [Fact]
        public void DefaultScope_FavoritesThenRecentsThenItmPages()
        {
            Assert.Equal(PickerScope.ItmPages, ScopedModel().DefaultScope());
            Assert.Equal(PickerScope.Recents,
                ScopedModel(recents: new[] { "Gear" }).DefaultScope());
            Assert.Equal(PickerScope.Favorites,
                ScopedModel(favorites: new[] { "Fuel" }, recents: new[] { "Gear" }).DefaultScope());
        }

        [Fact]
        public void Scope_Favorites_PreservesStoredOrder_NoHeaders_SurvivesCatalogAbsence()
        {
            var model = ScopedModel(favorites: new[] { "Gone.Property", "Fuel", "GameData.Fuel" });
            var rows = model.Rows(PickerScope.Favorites, "");
            Assert.Empty(Headers(rows));
            var props = Props(rows);
            Assert.Equal(new[] { "Gone.Property", "Fuel", "GameData.Fuel" },
                props.Select(r => r.PropertyName).ToArray());
            Assert.Equal(PropertyKind.SimHubProperty, props[0].PropertyKind); // absent → SimHub
            Assert.Equal(PropertyKind.BuiltIn, props[1].PropertyKind);
            Assert.Equal(PropertyKind.SimHubProperty, props[2].PropertyKind);
            Assert.All(props, r => Assert.True(r.IsFavorite));
        }

        [Fact]
        public void Scope_Recents_PreservesStoredOrder_MarksFavorites()
        {
            var model = ScopedModel(
                favorites: new[] { "Gear" },
                recents: new[] { "Fuel", "Gear", "Missing.One" });
            var props = Props(model.Rows(PickerScope.Recents, ""));
            Assert.Equal(new[] { "Fuel", "Gear", "Missing.One" },
                props.Select(r => r.PropertyName).ToArray());
            Assert.False(props[0].IsFavorite);
            Assert.True(props[1].IsFavorite);
            Assert.False(props[2].IsFavorite);
        }

        [Fact]
        public void Scope_ItmPages_ShowsSuppliedOrder_NoHeaders()
        {
            var model = ScopedModel(itmPages: new[] { "Speed", "GameData.SpeedKmh" });
            var rows = model.Rows(PickerScope.ItmPages, "");
            Assert.Empty(Headers(rows));
            Assert.Equal(new[] { "Speed", "GameData.SpeedKmh" },
                Props(rows).Select(r => r.PropertyName).ToArray());
        }

        [Fact]
        public void Scope_Root_FanaBridge_KeepsHeader_OnlyBuiltIns()
        {
            var rows = ScopedModel().Rows(PickerScope.Root(DisplayPropertyPickerModel.BuiltInGroup), "");
            Assert.Equal(new[] { DisplayPropertyPickerModel.BuiltInGroup }, Headers(rows));
            Assert.Equal(new[] { "Speed", "Gear", "Fuel" },
                Props(rows).Select(r => r.PropertyName).ToArray());
        }

        [Fact]
        public void Scope_Root_AutoGroup_KeepsHeader()
        {
            var rows = ScopedModel().Rows(PickerScope.Root("GameData"), "");
            Assert.Equal(new[] { "GameData" }, Headers(rows));
            Assert.Equal(new[] { "GameData.Fuel", "GameData.SpeedKmh" },
                Props(rows).Select(r => r.PropertyName).ToArray());
        }

        [Fact]
        public void Scope_AllProperties_MatchesLegacyUnfilteredRows()
        {
            var legacy = Props(Model().Rows("")).Select(r => r.PropertyName).ToList();
            var scoped = Props(ScopedModel().Rows(PickerScope.AllProperties, ""))
                .Select(r => r.PropertyName).ToList();
            Assert.Equal(legacy, scoped);
        }

        [Fact]
        public void NonEmptyFilter_IsGlobal_RegardlessOfScope_WithMatchSpans()
        {
            // Scope is Favorites (empty) — filter still searches the whole catalog.
            var model = ScopedModel(favorites: new[] { "Speed" });
            var rows = model.Rows(PickerScope.Favorites, "fuel");
            var headers = Headers(rows);
            Assert.Equal(new[] { DisplayPropertyPickerModel.BuiltInGroup, "GameData" }, headers);

            var fuel = Props(rows).Single(r => r.PropertyName == "Fuel");
            Assert.Equal(0, fuel.MatchStart);
            Assert.Equal(4, fuel.MatchLength);

            var gd = Props(rows).Single(r => r.PropertyName == "GameData.Fuel");
            // first case-insensitive hit of "fuel" in "GameData.Fuel" is at index 9
            Assert.Equal(9, gd.MatchStart);
            Assert.Equal(4, gd.MatchLength);
        }

        [Fact]
        public void EmptyFilter_NoMatchSpans_OnAnyScope()
        {
            var model = ScopedModel(favorites: new[] { "Fuel" });
            Assert.All(Props(model.Rows(PickerScope.Favorites, "")),
                r => Assert.True(r.MatchStart < 0));
            Assert.All(Props(model.Rows(PickerScope.AllProperties, "")),
                r => Assert.True(r.MatchStart < 0));
        }

        [Fact]
        public void MatchSpan_CaseInsensitiveFirstHit_AndNamespaceCrossing()
        {
            var model = new DisplayPropertyPickerModel(
                Array.Empty<string>(),
                new[] { "FooBarFoo", "DataCorePlugin.GameRunning" });

            var foo = Props(model.Rows(PickerScope.AllProperties, "foo")).Single();
            Assert.Equal(0, foo.MatchStart);          // first hit, not the second "Foo"
            Assert.Equal(3, foo.MatchLength);

            // "in.G" spans the last-dot boundary of DataCorePlugin.GameRunning
            var ns = Props(model.Rows(PickerScope.AllProperties, "in.G")).Single();
            Assert.Equal("DataCorePlugin.GameRunning", ns.PropertyName);
            int expected = "DataCorePlugin.GameRunning".IndexOf("in.G", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expected, ns.MatchStart);
            Assert.Equal(4, ns.MatchLength);
        }

        [Fact]
        public void PropertyCount_MirrorsRows_ForEveryScope()
        {
            var model = ScopedModel(
                favorites: new[] { "Fuel", "Gone.X" },
                recents: new[] { "Gear" },
                itmPages: new[] { "Speed" },
                mappedRoles: new[] { "Up Shift" });

            void AssertParity(PickerScope scope, string filter)
            {
                int fromRows = Props(model.Rows(scope, filter)).Count;
                Assert.Equal(fromRows, model.PropertyCount(scope, filter));
            }

            AssertParity(PickerScope.Favorites, "");
            AssertParity(PickerScope.Recents, "");
            AssertParity(PickerScope.ItmPages, "");
            AssertParity(PickerScope.AllProperties, "");
            AssertParity(PickerScope.Root("GameData"), "");
            AssertParity(PickerScope.Favorites, "fuel");
            AssertParity(PickerScope.AllProperties, "shift");
            AssertParity(PickerScope.AllProperties, "zzz-none");
        }

        [Fact]
        public void IsFavorite_OnGlobalRows_ReflectsFavoritesSet()
        {
            var model = ScopedModel(favorites: new[] { "Fuel", "GameData.Fuel" });
            var props = Props(model.Rows(PickerScope.AllProperties, ""));
            Assert.True(props.Single(r => r.PropertyName == "Fuel").IsFavorite);
            Assert.True(props.Single(r => r.PropertyName == "GameData.Fuel").IsFavorite);
            Assert.False(props.Single(r => r.PropertyName == "Gear").IsFavorite);
        }

        // ── Match spans on collapsed display text (review 0f6e26f) ─────────

        [Fact]
        public void MatchSpan_CollapsedGameData_HighlightsLeafInDisplayForm()
        {
            // Raw "DataCorePlugin.GameData.Fuel" displays as "GameData.Fuel"; "fuel" must
            // highlight the leaf in that form (index 9), not a raw-name offset.
            var model = new DisplayPropertyPickerModel(
                Array.Empty<string>(),
                new[] { "DataCorePlugin.GameData.Fuel" });
            var row = Props(model.Rows(PickerScope.AllProperties, "fuel")).Single();
            Assert.Equal("DataCorePlugin.GameData.Fuel", row.PropertyName);

            string display = PropertyGrammar.FullText(
                row.PropertyName, PropertyDisplayKind.SimHubProperty);
            Assert.Equal("GameData.Fuel", display);
            Assert.Equal(display.IndexOf("fuel", StringComparison.OrdinalIgnoreCase), row.MatchStart);
            Assert.Equal(4, row.MatchLength);
        }

        [Fact]
        public void MatchSpan_CollapsedAwayPrefixOnly_ListsRowWithNoHighlight()
        {
            // "DataCore" matches only the collapsed-away raw prefix — row still appears
            // (listing uses the raw name) but MatchStart is -1 (nothing to highlight).
            var model = new DisplayPropertyPickerModel(
                Array.Empty<string>(),
                new[] { "DataCorePlugin.GameData.Fuel" });
            var row = Props(model.Rows(PickerScope.AllProperties, "DataCore")).Single();
            Assert.Equal("DataCorePlugin.GameData.Fuel", row.PropertyName);
            Assert.True(row.MatchStart < 0);
            Assert.Equal(0, row.MatchLength);
        }

        [Fact]
        public void MatchSpan_MappedRole_PrefersRoleOverControlMapperNamespace()
        {
            // Role "Control Mode" + filter "control": highlight the role, not the
            // collapsed "ControlMapper." prefix that IndexOf would hit first.
            var model = new DisplayPropertyPickerModel(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Control Mode" });
            var row = Props(model.Rows(PickerScope.AllProperties, "control")).Single();
            Assert.Equal("InputStatus.ControlMapperPlugin.Control Mode", row.PropertyName);

            string display = PropertyGrammar.FullText(
                row.PropertyName, PropertyDisplayKind.SimHubProperty);
            Assert.Equal("ControlMapper.Control Mode", display);
            // Role starts after "ControlMapper." (14 chars); "control" in the role is at +0.
            int roleStart = display.Length - "Control Mode".Length;
            Assert.Equal(roleStart, row.MatchStart);
            Assert.Equal(7, row.MatchLength); // "control".Length
            Assert.True(row.MatchStart > 0);  // not the namespace hit at 0
        }

        // ── FanaBridge rail merge (review 0f6e26f) ─────────────────────────

        [Fact]
        public void Rails_FanaBridgeCatalogProperty_DoesNotDuplicatePinnedRail()
        {
            // Plugin publishes FanaBridge.Connected — still exactly one FanaBridge rail
            // (the pinned curated one); no second auto-root of the same name.
            var model = new DisplayPropertyPickerModel(
                BuiltIns, new[] { "FanaBridge.Connected", "GameData.Fuel" });
            var rails = model.Rails();
            var fanaRails = rails.Where(r =>
                !r.IsSection
                && string.Equals(r.Label, DisplayPropertyPickerModel.BuiltInGroup,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Single(fanaRails);
            Assert.Equal(
                DisplayPropertyPickerModel.RailRootIdPrefix + DisplayPropertyPickerModel.BuiltInGroup,
                fanaRails[0].Id);
            // GameData still appears as an auto root.
            Assert.Contains(rails, r => r.Id == DisplayPropertyPickerModel.RailRootIdPrefix + "GameData");
        }

        [Fact]
        public void Scope_Root_FanaBridge_MergesCuratedBuiltInsThenSameNamedAutoGroup()
        {
            // Opening the FanaBridge rail: curated built-ins first, then the auto-group
            // that holds FanaBridge.Connected under its own header.
            var model = new DisplayPropertyPickerModel(
                BuiltIns, new[] { "FanaBridge.Connected", "GameData.Fuel" });
            var rows = model.Rows(PickerScope.Root(DisplayPropertyPickerModel.BuiltInGroup), "")
                .ToList();

            Assert.Equal(new[]
            {
                DisplayPropertyPickerModel.BuiltInGroup,
                DisplayPropertyPickerModel.BuiltInGroup,
            }, Headers(rows));

            Assert.Equal(new[] { "Speed", "Gear", "Fuel", "FanaBridge.Connected" },
                Props(rows).Select(r => r.PropertyName).ToArray());

            // First header block is built-ins; second is the SimHub auto group.
            int h0 = rows.FindIndex(r => r.IsHeader);
            Assert.Equal(PropertyKind.BuiltIn, rows[h0 + 1].PropertyKind);
            int h1 = rows.FindIndex(h0 + 1, r => r.IsHeader);
            Assert.Equal("FanaBridge.Connected", rows[h1 + 1].PropertyName);
            Assert.Equal(PropertyKind.SimHubProperty, rows[h1 + 1].PropertyKind);
        }
    }
}
