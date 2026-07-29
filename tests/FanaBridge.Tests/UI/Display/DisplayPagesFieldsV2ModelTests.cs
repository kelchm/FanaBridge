using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Pure model tests for Pages &amp; Fields regions (selection/filter state machine,
    /// shared-focus survival, reach lines, scope groups). No WPF.
    /// </summary>
    public class DisplayPagesFieldsV2ModelTests
    {
        // ── Selection / filter state machine ─────────────────────────────

        [Fact]
        public void ToggleFocus_SetsAndClears_OnReclick()
        {
            ushort? focus = null;
            focus = DisplayPagesFieldsV2Model.ToggleFocus(focus, 10);
            Assert.Equal((ushort)10, focus);
            // Clear route 2: re-click.
            focus = DisplayPagesFieldsV2Model.ToggleFocus(focus, 10);
            Assert.Null(focus);
        }

        [Fact]
        public void ClearFocus_AllNamedRoutes_YieldNull()
        {
            // Routes 1 (named action), 3 (empty chrome), 4 (Esc) share ClearFocus.
            Assert.Null(DisplayPagesFieldsV2Model.ClearFocus());
        }

        [Fact]
        public void Project_Focus_ProducesFilterStateLine_AndNarrowsCollection()
        {
            var catalog = TireTempsCatalog();
            var doc = DocWithPageFields();
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:tyreTemps",
                focusedParamId: 10);

            Assert.True(model.IsFiltered);
            Assert.Equal((ushort)10, model.FocusedParamId);
            Assert.NotNull(model.FilterStateLine);
            Assert.Contains(DisplayCopy.ShowAllFields, model.FilterStateLine);
            // Focused collection: one section in THIS PAGE group.
            Assert.Equal(1, model.ScopeGroups.Sum(g => g.Sections.Count));
        }

        [Fact]
        public void Project_NoFocus_HasNoFilterStateLine()
        {
            // D3: all-fields is the absence of the state line.
            var catalog = TireTempsCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                DocWithPageFields(), EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:tyreTemps",
                focusedParamId: null);

            Assert.False(model.IsFiltered);
            Assert.Null(model.FilterStateLine);
        }

        // ── Shared-focus survival (D10) ──────────────────────────────────

        [Fact]
        public void SharedFocus_SurvivesPageSwitch_WhenStillPlaced()
        {
            var catalog = SharedSpeedCatalog();
            // speed (param 4) on both pages.
            Assert.True(DisplayPagesFieldsV2Model.FocusSurvivesPageSwitch(
                catalog, 4, "lapInfo"));
            Assert.True(DisplayPagesFieldsV2Model.FocusSurvivesPageSwitch(
                catalog, 4, "tyreTemps"));

            var doc = DocWithSharedSpeed();
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:lapInfo",
                focusedParamId: 4);

            Assert.Equal((ushort)4, model.FocusedParamId);
            Assert.Null(model.FocusClearAnnouncement);
            Assert.NotNull(model.FilterStateLine);
            Assert.Contains("shared across all", model.FilterStateLine);
        }

        [Fact]
        public void SharedFocus_ClearsWithAnnouncement_OnNonPlacingPage()
        {
            var catalog = SharedSpeedCatalog();
            // fl (param 10) only on tyreTemps.
            Assert.False(DisplayPagesFieldsV2Model.FocusSurvivesPageSwitch(
                catalog, 10, "lapInfo"));

            var doc = DocWithPageFields();
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:lapInfo",
                focusedParamId: 10);

            Assert.Null(model.FocusedParamId);
            Assert.NotNull(model.FocusClearAnnouncement);
            Assert.Contains("isn't on this page", model.FocusClearAnnouncement);
            Assert.Contains(DisplayCopy.ShowAllFields.Replace("Show all fields", "").Trim(),
                model.FocusClearAnnouncement.Contains("showing all fields")
                    ? model.FocusClearAnnouncement
                    : "showing all fields");
            Assert.Contains("showing all fields", model.FocusClearAnnouncement);
        }

        // ── Reach lines from real catalog ────────────────────────────────

        [Fact]
        public void ReachLine_FullReach_AppearsOnEveryItmPage()
        {
            var catalog = SharedSpeedCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                DocWithSharedSpeed(), EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:tyreTemps");

            var shared = model.ScopeGroups
                .FirstOrDefault(g => g.Header == DisplayCopy.ScopeGroupShared);
            Assert.NotNull(shared);
            var speed = shared.Sections.FirstOrDefault(s => s.ParamId == 4);
            Assert.NotNull(speed);
            Assert.Equal(DisplayCopy.ReachLine(2, 2), speed.ReachLine);
            Assert.Contains("every ITM page", speed.ReachLine);
        }

        [Fact]
        public void ReachLine_PartialReach_OnNOfM()
        {
            // lastLapTime on 1 of 2 pages only — still THIS PAGE group (placed == 1).
            var catalog = SharedSpeedCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                new DisplayConfigV2 { Settings = new SettingsBlock { Mode = SettingsMode.On } },
                EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:lapInfo");

            var thisPage = model.ScopeGroups
                .FirstOrDefault(g => g.Header == DisplayCopy.ScopeGroupThisPage);
            Assert.NotNull(thisPage);
            // lastLapTime param 20 is page-only.
            Assert.Contains(thisPage.Sections, s => s.ParamId == 20);
            Assert.All(thisPage.Sections.Where(s => s.ParamId == 20),
                s => Assert.Null(s.ReachLine));
        }

        [Fact]
        public void FilterStateLineShared_PartialReach_AnnouncesPlacedOfTotal()
        {
            // lastLapTime-style: place a "shared" field on 2 of 5 pages via catalog.
            var catalog = PartialReachCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                new DisplayConfigV2 { Settings = new SettingsBlock { Mode = SettingsMode.On } },
                EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:p1",
                focusedParamId: 99);

            Assert.True(model.IsFiltered);
            Assert.NotNull(model.FilterStateLine);
            // 2 of 5 — not "all 5".
            Assert.Contains("shared across 2 of 5", model.FilterStateLine);
            Assert.DoesNotContain("shared across all", model.FilterStateLine);
        }

        [Fact]
        public void RotationIn_AbsentPageOrder_PresentsCompiledDefaultWalk()
        {
            var catalog = SharedSpeedCatalog();
            var doc = new DisplayConfigV2
            {
                Settings = new SettingsBlock { Mode = SettingsMode.On },
                // pageOrder absent
            };
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog);

            // Compiled default = catalog pages in index order → not empty.
            Assert.NotEmpty(model.RotationIn);
            Assert.Equal(catalog.Itm.Pages.Count, model.RotationIn.Count);
        }

        [Fact]
        public void RotationIn_ExplicitEmpty_StaysEmpty()
        {
            var catalog = SharedSpeedCatalog();
            var doc = new DisplayConfigV2
            {
                Settings = new SettingsBlock { Mode = SettingsMode.On },
                PageOrder = new List<PageRef>(),
            };
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog);

            Assert.Empty(model.RotationIn);
        }

        [Fact]
        public void PreviewHits_RegionDedup_FirstPlacedWins()
        {
            // Two fields share one drawn region — only the first-placed gets a hit.
            var catalog = SharedRegionCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                new DisplayConfigV2 { Settings = new SettingsBlock { Mode = SettingsMode.On } },
                EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:page");

            Assert.Single(model.PreviewHits);
            Assert.Equal((ushort)1, model.PreviewHits[0].ParamId); // first-placed
        }

        // ── Scope groups (D14) ───────────────────────────────────────────

        [Fact]
        public void ScopeGroups_SharedThenThisPage()
        {
            var catalog = SharedSpeedCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                DocWithSharedSpeed(), EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:tyreTemps");

            Assert.Equal(2, model.ScopeGroups.Count);
            Assert.Equal(DisplayCopy.ScopeGroupShared, model.ScopeGroups[0].Header);
            Assert.Equal(DisplayCopy.ScopeGroupThisPage, model.ScopeGroups[1].Header);
            Assert.Contains(model.ScopeGroups[0].Sections, s => s.ParamId == 4);
            Assert.Contains(model.ScopeGroups[1].Sections, s => s.ParamId == 10);
        }

        [Fact]
        public void NoCatalog_FlatCollection_NoReachLines()
        {
            var doc = DocWithPageFields();
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog: null,
                selectedPageKey: "itm:tyreTemps");

            Assert.Empty(model.ScopeGroups);
            Assert.NotEmpty(model.FlatSections);
            Assert.All(model.FlatSections, s => Assert.Null(s.ReachLine));
        }

        // ── Preview hits (D6 gear/speed) ─────────────────────────────────

        [Fact]
        public void PreviewHits_IncludeCenterColumnFields()
        {
            var catalog = SharedSpeedCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                DocWithSharedSpeed(), EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:tyreTemps");

            Assert.Contains(model.PreviewHits, h => h.ParamId == 4 && h.Column == "center");
            Assert.Contains(model.PreviewHits, h => h.ParamId == 10);
        }

        [Fact]
        public void PreviewHits_PickedOutline_WhenFocused()
        {
            var catalog = SharedSpeedCatalog();
            var model = DisplayPagesFieldsV2Model.Project(
                DocWithSharedSpeed(), EmptyConnected(), null, DisplayType.Itm, catalog,
                selectedPageKey: "itm:tyreTemps",
                focusedParamId: 10);

            var fl = model.PreviewHits.First(h => h.ParamId == 10);
            Assert.True(fl.IsPicked);
            Assert.All(model.PreviewHits.Where(h => h.ParamId != 10),
                h => Assert.False(h.IsPicked));
        }

        // ── Mode off ─────────────────────────────────────────────────────

        [Fact]
        public void ModeOff_HidesContent_ShowsEmptyState()
        {
            var doc = DocWithPageFields();
            doc.Settings.Mode = SettingsMode.Off;
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, TireTempsCatalog());

            Assert.False(model.ShowContent);
            Assert.Equal(DisplayCopy.ModeOffEmptyState, model.ModeOffEmptyState);
            Assert.Empty(model.PageButtons);
        }

        // ── Page strip ───────────────────────────────────────────────────

        [Fact]
        public void PageStrip_ItmThenHosted_WithDividerBoundary()
        {
            var doc = DocWithPageFields();
            doc.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "alerts",
                Name = "Alerts",
            });
            var model = DisplayPagesFieldsV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, TireTempsCatalog());

            Assert.True(model.PageButtons.Count >= 2);
            int firstHosted = -1;
            for (int i = 0; i < model.PageButtons.Count; i++)
            {
                if (!model.PageButtons[i].IsItm)
                {
                    firstHosted = i;
                    break;
                }
            }
            Assert.True(firstHosted > 0);
            Assert.Equal(DisplayCopy.LegacyBadge, model.PageButtons[firstHosted].Badge);
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static WheelCatalog TireTempsCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "fl", ParamId = 10, ShortCode = "FL",
                            DisplayLabel = "FL",
                            Suffix = new FieldSuffixCapability { Supported = true, Width = 1 },
                            Value = new FieldValueCapability { Numeric = true },
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "fr", ParamId = 11, ShortCode = "FR",
                            DisplayLabel = "FR",
                        },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "tyreTemps",
                            Index = 5,
                            Name = "Tire Temps",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement
                                {
                                    Field = "fl",
                                    Region = new FieldRegion { Row = "top", Column = "left" },
                                },
                                new CatalogFieldPlacement
                                {
                                    Field = "fr",
                                    Region = new FieldRegion { Row = "top", Column = "right" },
                                },
                            },
                        },
                    },
                },
            };
        }

        private static WheelCatalog SharedSpeedCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "speed", ParamId = 4, ShortCode = "SPD",
                            DisplayLabel = "Speed",
                            Suffix = new FieldSuffixCapability { Supported = false },
                            Value = new FieldValueCapability { Numeric = true },
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "fl", ParamId = 10, ShortCode = "FL",
                            DisplayLabel = "FL",
                            Suffix = new FieldSuffixCapability { Supported = true, Width = 1 },
                            Value = new FieldValueCapability { Numeric = true },
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "lastLapTime", ParamId = 20, ShortCode = "LLT",
                            DisplayLabel = "Last lap",
                        },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "tyreTemps",
                            Index = 5,
                            Name = "Tire Temps",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement
                                {
                                    Field = "fl",
                                    Region = new FieldRegion { Row = "top", Column = "left" },
                                },
                                new CatalogFieldPlacement
                                {
                                    Field = "speed",
                                    Region = new FieldRegion { Row = "bottom", Column = "center" },
                                },
                            },
                        },
                        new CatalogPage
                        {
                            Id = "lapInfo",
                            Index = 1,
                            Name = "Lap Info",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement
                                {
                                    Field = "speed",
                                    Region = new FieldRegion { Row = "bottom", Column = "center" },
                                },
                                new CatalogFieldPlacement
                                {
                                    Field = "lastLapTime",
                                    Region = new FieldRegion { Row = "top", Column = "left" },
                                },
                            },
                        },
                    },
                },
            };
        }

        private static DisplayConfigV2 DocWithPageFields()
        {
            return new DisplayConfigV2
            {
                Settings = new SettingsBlock { Mode = SettingsMode.On },
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [10] = new FieldEntry
                    {
                        Base = new FieldBase
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.SimHubProperty,
                                Name = "DataCorePlugin.GameData.TyreTemperatureFrontLeft",
                            },
                            Format = "bare",
                        },
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "ov-fl-1",
                                Writes = FieldWrites.Suffix,
                                Content = new ContentObject
                                {
                                    Kind = ContentKind.Text,
                                    Text = "!",
                                },
                                Condition = new Condition
                                {
                                    Source = new ValueSource
                                    {
                                        Kind = ValueSourceKind.BuiltIn,
                                        Name = "Fuel",
                                    },
                                    Operator = ConditionOperator.GreaterThan,
                                    Value = 100,
                                },
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    },
                },
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.ItmPage,
                        CatalogPageId = "tyreTemps",
                    },
                },
            };
        }

        private static DisplayConfigV2 DocWithSharedSpeed()
        {
            var doc = DocWithPageFields();
            doc.SharedFields = new Dictionary<string, FieldEntry>
            {
                ["speed"] = new FieldEntry
                {
                    Base = new FieldBase
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = "Speed",
                        },
                    },
                    Overrides = new List<FieldOverride>(),
                },
            };
            return doc;
        }

        /// <summary>
        /// lastLapTime-style partial shared reach: param 99 on 2 of 5 ITM pages.
        /// </summary>
        private static WheelCatalog PartialReachCatalog()
        {
            var pages = new List<CatalogPage>();
            for (int i = 1; i <= 5; i++)
            {
                var placements = new List<CatalogFieldPlacement>
                {
                    new CatalogFieldPlacement
                    {
                        Field = "filler",
                        Region = new FieldRegion { Row = "top", Column = "left" },
                    },
                };
                if (i <= 2)
                {
                    placements.Add(new CatalogFieldPlacement
                    {
                        Field = "partial",
                        Region = new FieldRegion { Row = "bottom", Column = "right" },
                    });
                }
                pages.Add(new CatalogPage
                {
                    Id = "p" + i,
                    Index = i,
                    Name = "Page " + i,
                    Placements = placements,
                });
            }

            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "filler", ParamId = 1, ShortCode = "F",
                            DisplayLabel = "Filler",
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "partial", ParamId = 99, ShortCode = "PR",
                            DisplayLabel = "Partial",
                        },
                    },
                    Pages = pages,
                },
            };
        }

        private static WheelCatalog SharedRegionCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "first", ParamId = 1, ShortCode = "A",
                            DisplayLabel = "First",
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "second", ParamId = 2, ShortCode = "B",
                            DisplayLabel = "Second",
                        },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "page",
                            Index = 1,
                            Name = "Page",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement
                                {
                                    Field = "first",
                                    Region = new FieldRegion { Row = "top", Column = "left" },
                                },
                                new CatalogFieldPlacement
                                {
                                    Field = "second",
                                    // Same drawn region — A-O6 first-placed wins.
                                    Region = new FieldRegion { Row = "top", Column = "left" },
                                },
                            },
                        },
                    },
                },
            };
        }

        private static DisplayResolutionSnapshotModel EmptyConnected()
            => DisplayResolutionSnapshotModel.From(
                null, inGame: true, isConnected: true, aggregates: null, manual: null);
    }
}
