using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Newtonsoft.Json;
using SimHub.Plugins;
using SimHubInputMapping = SimHub.Plugins.InputMapping;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Pure model tests for every Priority region (5b ITM / 5j segment). No WPF.
    /// </summary>
    public class DisplayPriorityV2ModelTests
    {
        private sealed class LivePluginManagerSurface
        {
            public PluginManagerSettings Settings { get; set; } = null!;
        }

        // ── Header / wheel metric ────────────────────────────────────────

        [Fact]
        public void Project_ItmWheel_UsesWidePageColumn_AndItmSurfaceWord()
        {
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Itm);

            Assert.Equal(DisplayCopy.ItmDisplay, model.SurfaceWord);
            Assert.True(model.IsItmWheel);
            Assert.Equal(236, model.PageColWidth);
            Assert.Equal(104, model.StatusColWidth);
            Assert.True(model.ShowKindBadges);
            Assert.False(model.ShowSegmentPreview);
            Assert.Equal(DisplayCopy.PriorityLadderSubtitle, model.LadderSubtitle);
        }

        [Fact]
        public void Project_SegmentWheel_UsesNarrowPageColumn_AndSegmentSurfaceWord()
        {
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Basic);

            Assert.Equal(DisplayCopy.SegmentDisplay, model.SurfaceWord);
            Assert.False(model.IsItmWheel);
            Assert.Equal(196, model.PageColWidth);
            Assert.Equal(112, model.StatusColWidth);
            Assert.False(model.ShowKindBadges);
            Assert.True(model.ShowSegmentPreview);
            Assert.Equal(DisplayCopy.PriorityLadderSubtitleShort, model.LadderSubtitle);
        }

        [Fact]
        public void LadderHeader_CountsRankedOnly_ExcludesPinned()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            // 1 seat (+ restored manual from Normalize if present) — MinimalDoc has no Normalize.
            // Without Normalize, EffectiveRows == Rows (1 seat). + base + idle pinned.
            Assert.Equal(DisplayCopy.LadderHeaderCount(1), model.LadderHeader);
            Assert.Equal(3, model.Rows.Count); // seat + base + idle
            Assert.False(model.Rows[0].IsPinned);
            Assert.True(model.Rows[1].IsBaseRow);
            Assert.True(model.Rows[2].IsIdleRow);
            Assert.Equal("PRIORITY · 1 ENTRY", model.LadderHeader);
        }

        // ── Row states / status ──────────────────────────────────────────

        [Fact]
        public void Winner_StatusEmpty_StructuralHighlight()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var resolution = ResolutionWithWinner("s1", "itm:tyreTemps");
            var model = DisplayPriorityV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            var winner = model.Rows[0];
            Assert.Equal(PriorityRowState.Winner, winner.State);
            Assert.Equal(DisplayCopy.OnScreen, winner.StatusCopy); // P1: empty
        }

        [Fact]
        public void IdleFloorWinner_LightsIdleRowOnly_NotBaseRow()
        {
            // The rest carrier fronts both floors; at idle only the idle row wins.
            var doc = DocWithSeat("tyreTemps", "s1");
            var resolution = ResolutionWithWinner(
                SeatArbiter.RestCarrierId, DestinationIds.RestIdle);
            var model = DisplayPriorityV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            Assert.Equal(
                PriorityRowState.Winner,
                model.Rows.Single(r => r.IsIdleRow).State);
            Assert.Equal(
                PriorityRowState.Pinned,
                model.Rows.Single(r => r.IsBaseRow).State);
        }

        [Fact]
        public void InSessionFloorWinner_LightsBaseRowOnly_NotIdleRow()
        {
            // In-session rest resolves to the configured page destination, but the
            // carrier stays "rest" — the base row must still win, idle must not.
            var doc = DocWithSeat("tyreTemps", "s1");
            var resolution = ResolutionWithWinner(
                SeatArbiter.RestCarrierId, "itm:lapInfo");
            var model = DisplayPriorityV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            Assert.Equal(
                PriorityRowState.Winner,
                model.Rows.Single(r => r.IsBaseRow).State);
            Assert.Equal(
                PriorityRowState.Pinned,
                model.Rows.Single(r => r.IsIdleRow).State);
        }

        [Fact]
        public void LegacyOnly_DimsItmRows_WithCantRunHere()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            doc.Settings.Mode = SettingsMode.LegacyOnly;
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            Assert.Equal(PriorityRowState.Off, model.Rows[0].State);
            Assert.Equal(DisplayCopy.CantRunHere, model.Rows[0].StatusCopy);
            Assert.Equal(DisplayCopy.SegmentDisplay, model.SurfaceWord);
            Assert.True(model.ShowSegmentPreview);

            doc.Priority.Rest.InSessionPage = new PageRef
            {
                Kind = PageRefKind.ItmPage,
                CatalogPageId = "lapInfo",
            };
            model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);
            var baseRow = model.Rows.Single(r => r.IsBaseRow);
            Assert.Equal(PriorityRowState.Off, baseRow.State);
            Assert.Equal(DisplayCopy.CantRunHere, baseRow.StatusCopy);

            doc.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "tyreTemps",
                },
            };
            model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);
            var idleRow = model.Rows.Single(r => r.IsIdleRow);
            Assert.Equal(PriorityRowState.Off, idleRow.State);
            Assert.Equal(DisplayCopy.CantRunHere, idleRow.StatusCopy);
        }

        [Fact]
        public void ModeOff_HidesLadder_ShowsEmptyState()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            doc.Settings.Mode = SettingsMode.Off;
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            Assert.False(model.ShowLadder);
            Assert.Equal(DisplayCopy.ModeOffEmptyState, model.ModeOffEmptyState);
            Assert.Empty(model.Rows);
        }

        [Fact]
        public void ManualRow_DetailIsStandingForm()
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            var manual = model.Rows.First(r => r.IsManual);
            Assert.Equal(DisplayCopy.ManualPagingStanding, manual.Detail);
            Assert.Equal(DisplayCopy.ManualPaging, manual.Destination.Name);
        }

        // ── Lifetime clauses (Q8) ────────────────────────────────────────

        [Theory]
        [InlineData(LifetimeKind.WhileTrue, 0, " · while it's true")]
        [InlineData(LifetimeKind.ForDuration, 6000, " · for 6 s")]
        [InlineData(LifetimeKind.UntilDismissed, 0, " · until dismissed")]
        [InlineData(LifetimeKind.OnChange, 0, " · when it changes")]
        public void LifetimeLadderSuffix_RuledPairs(
            LifetimeKind kind, int ms, string expected)
        {
            Assert.Equal(expected, DisplayCopy.LifetimeLadderSuffix(kind, ms));
        }

        [Theory]
        [InlineData(LifetimeKind.WhileTrue, 0, "While the condition is true")]
        [InlineData(LifetimeKind.ForDuration, 6000, "For a duration (6 s)")]
        [InlineData(LifetimeKind.UntilDismissed, 0, "Until dismissed")]
        [InlineData(LifetimeKind.OnChange, 0, "When the value changes")]
        public void LifetimeFormLabel_RuledPairs(
            LifetimeKind kind, int ms, string expected)
        {
            Assert.Equal(expected, DisplayCopy.LifetimeFormLabel(kind, ms));
        }

        [Fact]
        public void ConditionDetail_AppendsLifetimeSuffix()
        {
            var doc = DocWithSummon(
                "tyreTemps", "s1", "sum1",
                LifetimeKind.ForDuration, 3000);
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            Assert.Contains(" · for 3 s", model.Rows[0].Detail);
        }

        [Fact]
        public void CycleRow_UsesGlossaryForm_AndPeriodSuffix()
        {
            var doc = MinimalDoc();
            doc.Cycles = new List<CycleEntry>
            {
                new CycleEntry
                {
                    Id = "c1",
                    PeriodMs = 5000,
                    Members = new List<PageRef>
                    {
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "tyreTemps" },
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "fuelErsDrs" },
                    },
                },
            };
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "cyc",
                Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c1" },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "s",
                        Name = "in the pit box",
                        Enabled = true,
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });

            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);
            var detail = model.Rows[0].Detail;
            Assert.Contains(DisplayCopy.CycleDefinition, detail);
            Assert.Contains("every 5 s while it's true", detail);
        }

        [Fact]
        public void TwoCycles_InOneProjection_FirstMentionOnce_ThenShortForm()
        {
            var doc = MinimalDoc();
            doc.Cycles = new List<CycleEntry>
            {
                new CycleEntry
                {
                    Id = "c1",
                    PeriodMs = 5000,
                    Members = new List<PageRef>
                    {
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "tyreTemps" },
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "fuelErsDrs" },
                    },
                },
                new CycleEntry
                {
                    Id = "c2",
                    PeriodMs = 2000,
                    Members = new List<PageRef>
                    {
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapTimes" },
                    },
                },
            };
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "cyc1",
                Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c1" },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "s1",
                        Name = "first cycle",
                        Enabled = true,
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "cyc2",
                Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c2" },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "s2",
                        Name = "second cycle",
                        Enabled = true,
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });

            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);
            var d1 = model.Rows.First(r => r.RowId == "cyc1").Detail;
            var d2 = model.Rows.First(r => r.RowId == "cyc2").Detail;
            Assert.Contains(DisplayCopy.CycleDefinition, d1);
            Assert.DoesNotContain(DisplayCopy.CycleDefinition, d2);
            Assert.Contains(DisplayCopy.Cycle, d2);
            Assert.Contains("every 5 s while it's true", d1);
            Assert.Contains("every 2 s while it's true", d2);
        }

        [Fact]
        public void Cycle_ForDuration_ComposesPeriodWithSummonLifetime()
        {
            var doc = MinimalDoc();
            doc.Cycles = new List<CycleEntry>
            {
                new CycleEntry
                {
                    Id = "c-dur",
                    PeriodMs = 4000,
                    Members = new List<PageRef>
                    {
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "tyreTemps" },
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "fuelErsDrs" },
                    },
                },
            };
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "cyc-dur",
                Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c-dur" },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "s",
                        Name = "timed cycle",
                        Enabled = true,
                        Lifetime = new Lifetime
                        {
                            Kind = LifetimeKind.ForDuration,
                            DurationMs = 3000,
                        },
                    },
                },
            });

            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);
            var detail = model.Rows.First(r => r.RowId == "cyc-dur").Detail;
            Assert.Contains("every 4 s for 3 s", detail);
        }

        [Fact]
        public void ExpandedManual_RememberedSeconds_SurviveWhenUnchecked()
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Manual,
                ReturnToRestAfterMs = null,
            });
            var expanded = new HashSet<string> { PriorityRowModel.ManualExpandKey };
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm,
                expandedRowIds: expanded,
                rememberedManualSeconds: 45);

            var manual = model.Rows.First(r => r.IsManual);
            Assert.False(manual.ManualOptions.ReturnEnabled);
            Assert.Equal(45, manual.ManualOptions.ShownSeconds);
        }

        [Fact]
        public void ResolvePageControlMapping_ReadsRealInputActionMappingTargets()
        {
            var settings = new PluginManagerSettings
            {
                InputActionMapping = new ObservableCollection<SimHubInputMapping>
                {
                    new SimHubInputMapping
                    {
                        Target = "FanatecPlugin.DisplayNextPage",
                    },
                    new SimHubInputMapping
                    {
                        Target = "SomeOtherPlugin.SomeAction",
                    },
                },
            };

            DisplayPriorityV2Model.ResolvePageControlMapping(
                InputActionMappingReader.Read(settings),
                out bool next,
                out bool prev);
            Assert.True(next);
            Assert.False(prev);

            settings.InputActionMapping = new ObservableCollection<SimHubInputMapping>
            {
                new SimHubInputMapping
                {
                    Target = "FanatecPlugin.DisplayPreviousPage",
                },
            };
            DisplayPriorityV2Model.ResolvePageControlMapping(
                InputActionMappingReader.Read(settings), out next, out prev);
            Assert.False(next);
            Assert.True(prev);
        }

        [Fact]
        public void InputActionMappingReader_ReadsRound5PersistedStoreShape()
        {
            // Exact relevant shape written by SimHub at 11:18:11 during the live
            // F12 mapping creation (extra members must not affect target projection).
            var settings = JsonConvert.DeserializeObject<PluginManagerSettings>(@"
{
  ""InputActionMapping"": [
    {
      ""Target"": ""FanatecPlugin.DisplayNextPage"",
      ""PressType"": 1,
      ""GameRestriction"": { ""SupportedGames"": [] },
      ""Trigger"": ""KeyboardReaderPlugin.F12""
    }
  ]
}")!;

            var surface = new LivePluginManagerSurface { Settings = settings };
            var targets = InputActionMappingReader.Read(surface);
            Assert.Equal(
                new[] { "FanatecPlugin.DisplayNextPage" },
                targets);
        }

        // ── Aggregate (Q7/P4) ────────────────────────────────────────────

        [Fact]
        public void Aggregate_UsesFiringLine_NotMembershipSentence()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var resolution = ResolutionWithAggregate("s1", active: 2, total: 4);
            var model = DisplayPriorityV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            Assert.Equal(
                DisplayCopy.EntrypointsFiringLine(2, 4) + DisplayCopy.LifetimeWhileOneActive,
                model.Rows[0].Detail);
            Assert.DoesNotContain("act as entrypoints", model.Rows[0].Detail);
        }

        // ── Expansion / count summary (Q9) ───────────────────────────────

        [Fact]
        public void ExpandedSeat_ShowsCountSummary()
        {
            var doc = DocWithTwoSummons("fuelErsDrs", "s1");
            var expanded = new HashSet<string> { "s1" };
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm,
                expandedRowIds: expanded);

            var row = model.Rows[0];
            Assert.True(row.IsExpanded);
            Assert.True(row.ShowDisclosure);
            Assert.Equal(DisplayCopy.SeatCountSummary(2, 0), row.Detail);
            Assert.Equal(2, row.Entrypoints.Count);
        }

        [Fact]
        public void CollapsedSeat_NoDisclosureGlyph()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            Assert.False(model.Rows[0].IsExpanded);
            Assert.False(model.Rows[0].ShowDisclosure);
        }

        // ── Manual options (Q10) ─────────────────────────────────────────

        [Fact]
        public void ExpandedManual_UncheckedShowsGreyed30_WhenNeverSet()
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Manual,
                ReturnToRestAfterMs = null,
            });
            var expanded = new HashSet<string> { PriorityRowModel.ManualExpandKey };
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm,
                expandedRowIds: expanded);

            var manual = model.Rows.First(r => r.IsManual);
            Assert.NotNull(manual.ManualOptions);
            Assert.False(manual.ManualOptions.ReturnEnabled);
            Assert.Equal(30, manual.ManualOptions.ShownSeconds);
        }

        [Fact]
        public void ExpandedManual_CheckedReflectsAuthoredMs()
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Manual,
                ReturnToRestAfterMs = 15000,
            });
            var expanded = new HashSet<string> { PriorityRowModel.ManualExpandKey };
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm,
                expandedRowIds: expanded);

            Assert.True(model.Rows.First(r => r.IsManual).ManualOptions.ReturnEnabled);
            Assert.Equal(15, model.Rows.First(r => r.IsManual).ManualOptions.ShownSeconds);
        }

        // ── Add page (Surface B — live plain door) ───────────────────────

        [Fact]
        public void AddPage_Enabled_Live()
        {
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Itm);

            Assert.True(model.AddPageEnabled);
            Assert.True(string.IsNullOrEmpty(model.AddPageTooltip));
        }

        // ── Explainers ───────────────────────────────────────────────────

        [Fact]
        public void Explainers_Itm_TwoCards()
        {
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Itm);
            Assert.Equal(2, model.Explainers.Count);
            Assert.Equal(DisplayCopy.TwoPinnedRows, model.Explainers[0].Label);
            Assert.Equal(DisplayCopy.Dismissing, model.Explainers[1].Label);
        }

        [Fact]
        public void Explainers_Segment_OneLaw()
        {
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Basic);
            Assert.Single(model.Explainers);
            Assert.Equal(DisplayCopy.OneLaw, model.Explainers[0].Label);
        }

        // ── Idle picker (5n) ─────────────────────────────────────────────

        [Fact]
        public void IdlePicker_HasPagesAndScreens_PlaylistsGroupEmpty()
        {
            var doc = DocWithPages();
            var catalog = TinyCatalog();
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog);

            Assert.NotNull(model.IdlePicker);
            Assert.True(model.IdlePicker.IncludeScreens);
            Assert.Contains(model.IdlePicker.Groups,
                g => g.Header == DisplayCopy.PagesOnThisWheel && g.Items.Count > 0);
            Assert.Contains(model.IdlePicker.Groups,
                g => g.Header == DisplayCopy.BuiltInScreens && g.Items.Count > 0);
            // task #22: group present via DisplayCopy, empty items.
            var pl = model.IdlePicker.Groups
                .FirstOrDefault(g => g.Header == DisplayCopy.PlaylistsGroup);
            Assert.NotNull(pl);
            Assert.Empty(pl.Items);
            Assert.Equal(DisplayCopy.NoPlaylistsYet, pl.EmptyState);
            // P5: no "Keep the last page shown"
            Assert.DoesNotContain(
                model.IdlePicker.Groups.SelectMany(g => g.Items),
                i => i.Name != null && i.Name.Contains("last page"));
        }

        [Fact]
        public void IdlePicker_NullCapability_IsUntested()
        {
            var catalog = new WheelCatalog
            {
                ScreenCommands = new ScreenCommandsCapability
                {
                    Logo = null,
                    Blank = null,
                },
            };
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Itm, catalog);

            var screens = model.IdlePicker.Groups
                .First(g => g.Header == DisplayCopy.BuiltInScreens).Items;
            Assert.All(screens.Where(s => s.IdleKind == IdleKind.Screen || s.Screen == WheelScreenCommand.Logo),
                s => Assert.Equal(DisplayCopy.UntestedOnThisWheel,
                    s.CapabilityNote ?? s.TrailingNote));
        }

        // ── Base picker (UNBOARDED) ──────────────────────────────────────

        [Fact]
        public void BasePagePicker_PagesOnly_NoScreensOrPlaylists()
        {
            var doc = DocWithPages();
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, TinyCatalog());

            Assert.NotNull(model.BasePagePicker);
            Assert.False(model.BasePagePicker.IncludeScreens);
            Assert.False(model.BasePagePicker.IncludePlaylists);
            Assert.DoesNotContain(model.BasePagePicker.Groups,
                g => g.Header == DisplayCopy.BuiltInScreens);
            Assert.DoesNotContain(model.BasePagePicker.Groups,
                g => g.Header == DisplayCopy.PlaylistsGroup);
            Assert.Contains(model.BasePagePicker.Groups,
                g => g.Header == DisplayCopy.PagesOnThisWheel);
        }

        [Fact]
        public void BaseRow_HasOverflowMenu()
        {
            var model = DisplayPriorityV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Itm);
            var bas = model.Rows.First(r => r.IsBaseRow);
            Assert.True(bas.ShowOverflowMenu);
            Assert.False(bas.ShowGrip);
        }

        // ── Layers vs overrides click (Q6) ───────────────────────────────

        [Fact]
        public void LayerChildren_AreClickable_OverrideChildren_AreNot()
        {
            var doc = HostedSeatWithLayer();
            var expanded = new HashSet<string> { "seat-h" };
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Basic,
                expandedRowIds: expanded);

            var seat = model.Rows.First(r => r.RowId == "seat-h");
            Assert.NotEmpty(seat.Layers);
            Assert.All(seat.Layers, l => Assert.True(l.IsClickable));
        }

        // ── IdleFromPickerItem ───────────────────────────────────────────

        [Fact]
        public void IdleFromPickerItem_PageAndScreen()
        {
            var pageItem = new PriorityPickerItemModel(
                "page:itm:x", null, "X", null, false, true, null,
                IdleKind.Page,
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "x" },
                WheelScreenCommand.Unknown);
            var idle = DisplayPriorityV2Model.IdleFromPickerItem(pageItem);
            Assert.Equal(IdleKind.Page, idle.Kind);
            Assert.Equal("x", idle.Page.CatalogPageId);

            var screenItem = new PriorityPickerItemModel(
                "screen:logo", null, DisplayCopy.TheWheelsLogo, null, false, true, null,
                IdleKind.Screen, null, WheelScreenCommand.Logo);
            idle = DisplayPriorityV2Model.IdleFromPickerItem(screenItem);
            Assert.Equal(IdleKind.Screen, idle.Kind);
            Assert.Equal(WheelScreenCommand.Logo, idle.Screen);
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static DisplayConfigV2 MinimalDoc()
        {
            return new DisplayConfigV2
            {
                SchemaVersion = 2,
                Settings = new SettingsBlock { Mode = SettingsMode.On },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>(),
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec { Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo },
                    },
                },
            };
        }

        private static DisplayConfigV2 DocWithSeat(string catalogPageId, string seatId)
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = seatId,
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = catalogPageId },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "sum-a",
                        Name = "a tire is hot",
                        Enabled = true,
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            return doc;
        }

        private static DisplayConfigV2 DocWithTwoSummons(string catalogPageId, string seatId)
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = seatId,
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = catalogPageId },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "s1", Name = "fuel low", Enabled = true,
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                    new Summon
                    {
                        Id = "s2", Name = "lap done", Enabled = true,
                        Lifetime = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 6000 },
                    },
                },
            });
            return doc;
        }

        private static DisplayConfigV2 DocWithSummon(
            string catalogPageId, string seatId, string summonId,
            LifetimeKind kind, int durationMs)
        {
            var doc = MinimalDoc();
            var life = new Lifetime { Kind = kind };
            if (kind == LifetimeKind.ForDuration)
                life.DurationMs = durationMs;
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = seatId,
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = catalogPageId },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = summonId,
                        Name = "brake bias changes",
                        Enabled = true,
                        Lifetime = life,
                    },
                },
            });
            return doc;
        }

        private static DisplayConfigV2 DocWithPages()
        {
            var doc = MinimalDoc();
            doc.Pages = new List<PageEntry>
            {
                new PageEntry
                {
                    Kind = PageEntryKind.ItmPage,
                    CatalogPageId = "tyreTemps",
                    NameOverride = "Tire Temps",
                },
                new PageEntry
                {
                    Kind = PageEntryKind.HostedPage,
                    Id = "speed",
                    Name = "Speed",
                },
            };
            doc.Priority.Rest.InSessionPage = new PageRef
            {
                Kind = PageRefKind.ItmPage,
                CatalogPageId = "tyreTemps",
            };
            return doc;
        }

        private static DisplayConfigV2 HostedSeatWithLayer()
        {
            var doc = MinimalDoc();
            doc.Pages = new List<PageEntry>
            {
                new PageEntry
                {
                    Kind = PageEntryKind.HostedPage,
                    Id = "alerts",
                    Name = "Alerts",
                    Layers = new List<LayerEntry>
                    {
                        new LayerEntry
                        {
                            Id = "l-pit",
                            Name = "PIT",
                            ActsAsEntrypoint = true,
                            Condition = new Condition
                            {
                                Source = new ValueSource
                                {
                                    Kind = ValueSourceKind.BuiltIn,
                                    Name = "PitLimiter",
                                },
                                Operator = ConditionOperator.IsTrue,
                            },
                            Lifetime = new Lifetime { Kind = LifetimeKind.UntilDismissed },
                        },
                    },
                },
            };
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "seat-h",
                Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "alerts" },
                Summons = new List<Summon>(),
            });
            return doc;
        }

        private static WheelCatalog TinyCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition { Id = "fl", ParamId = 10, ShortCode = "FL" },
                        new CatalogFieldDefinition { Id = "fuel", ParamId = 5, ShortCode = "FUEL" },
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
                                new CatalogFieldPlacement { Field = "fl" },
                            },
                        },
                        new CatalogPage
                        {
                            Id = "fuelErsDrs",
                            Index = 2,
                            Name = "Fuel / ERS / DRS",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement { Field = "fuel" },
                            },
                        },
                    },
                },
                ScreenCommands = new ScreenCommandsCapability
                {
                    Logo = null,
                    Blank = null,
                },
            };
        }

        private static DisplayResolutionSnapshotModel EmptyConnected()
            => DisplayResolutionSnapshotModel.From(
                null, inGame: true, isConnected: true, aggregates: null, manual: null);

        private static DisplayResolutionSnapshotModel ResolutionWithWinner(
            string carrierId, string destinationId)
        {
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(SeatArbiter.DisplaySurfaceId, carrierId, destinationId),
            };
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    carrierId, SeatArbiter.DisplaySurfaceId, destinationId,
                    CarrierPresence.OnScreen, null, CarrierRowLabels.None),
            };
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "test",
                surfaceWinners: winners,
                carrierStatuses: statuses,
                carrierSnapshots: new List<CarrierTickSnapshot>());
            return DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: null, manual: null);
        }

        private static DisplayResolutionSnapshotModel ResolutionWithAggregate(
            string seatId, int active, int total)
        {
            var aggregates = new List<AggregateMembership>
            {
                new AggregateMembership
                {
                    SeatId = seatId,
                    DestinationId = "itm:tyreTemps",
                    DerivedCarrierId = seatId + ":agg",
                    ActiveCount = active,
                    TotalCount = total,
                    MemberCarrierIds = Array.Empty<string>(),
                    MembershipDegraded = false,
                },
            };
            return DisplayResolutionSnapshotModel.From(
                null, inGame: true, isConnected: true, aggregates: aggregates, manual: null);
        }
    }
}
