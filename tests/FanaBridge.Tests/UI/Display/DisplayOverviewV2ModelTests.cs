using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Pure model tests for every Overview region. No WPF.
    /// </summary>
    public class DisplayOverviewV2ModelTests
    {
        // ── Mode round-trip + write-through mapping (O9) ─────────────────

        [Fact]
        public void WithMode_WritesSettingsMode_OnDocumentClone()
        {
            var doc = MinimalDoc();
            doc.Settings.Mode = SettingsMode.On;

            var next = DisplayOverviewV2Model.WithMode(doc, SettingsMode.LegacyOnly);

            Assert.Equal(SettingsMode.LegacyOnly, next.Settings.Mode);
            Assert.Equal(SettingsMode.On, doc.Settings.Mode); // original untouched
        }

        /// <summary>
        /// Closing MAJOR: Overview mode/reject publish via CAS against the projected
        /// document. Concurrent edit between projection and write → conflict, no overwrite.
        /// </summary>
        [Fact]
        public void ModeWrite_Cas_CompetingPublish_YieldsConflict_NeverOverwrite()
        {
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = WheelProfileStore.FindByWheelType("PSWBMW"),
                    Capabilities = new WheelCapabilities(
                        WheelProfileStore.FindByWheelType("PSWBMW")!),
                },
                itmClock: () => null,
                log: _ => { });

            var projected = MinimalDoc();
            projected.Settings.Mode = SettingsMode.On;
            projected.Settings.RejectUncommandedChanges = false;
            projected = DisplayConfigV2Validator.Normalize(projected, _ => { });
            runtime.SetConfigV2(projected);

            // Overview path: expected = the document the view projected from.
            var expected = runtime.CurrentConfigV2;
            var modeNext = DisplayOverviewV2Model.WithMode(expected, SettingsMode.Off);

            // Competing writer between projection and mode write (Priority / bake shape).
            var competitor = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(expected), _ => { });
            competitor.Settings.RejectUncommandedChanges = true;
            runtime.SetConfigV2(competitor);

            Assert.False(runtime.TryApplyDisplayConfigV2(expected, modeNext));
            // Competitor preserved — mode Off never landed.
            Assert.Same(competitor, runtime.CurrentConfigV2);
            Assert.True(runtime.CurrentConfigV2.Settings.RejectUncommandedChanges);
            Assert.Equal(SettingsMode.On, runtime.CurrentConfigV2.Settings.Mode);
        }

        [Fact]
        public void RejectWrite_Cas_CompetingPublish_YieldsConflict_NeverOverwrite()
        {
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = WheelProfileStore.FindByWheelType("PSWBMW"),
                    Capabilities = new WheelCapabilities(
                        WheelProfileStore.FindByWheelType("PSWBMW")!),
                },
                itmClock: () => null,
                log: _ => { });

            var projected = MinimalDoc();
            projected.Settings.Mode = SettingsMode.On;
            projected.Settings.RejectUncommandedChanges = false;
            projected = DisplayConfigV2Validator.Normalize(projected, _ => { });
            runtime.SetConfigV2(projected);

            var expected = runtime.CurrentConfigV2;
            var rejectNext = DisplayOverviewV2Model.WithRejectUncommanded(expected, true);

            var competitor = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(expected), _ => { });
            competitor.Settings.Mode = SettingsMode.LegacyOnly;
            runtime.SetConfigV2(competitor);

            Assert.False(runtime.TryApplyDisplayConfigV2(expected, rejectNext));
            Assert.Same(competitor, runtime.CurrentConfigV2);
            Assert.Equal(SettingsMode.LegacyOnly, runtime.CurrentConfigV2.Settings.Mode);
            Assert.False(runtime.CurrentConfigV2.Settings.RejectUncommandedChanges);
        }

        [Theory]
        [InlineData(SettingsMode.On, "Itm")]
        [InlineData(SettingsMode.LegacyOnly, "Legacy")]
        [InlineData(SettingsMode.Off, "Off")]
        public void DisplayControlForMode_WriteThroughMap(SettingsMode mode, string control)
        {
            // E9-exit: this mapping dies with the codec trim.
            Assert.Equal(control, DisplayOverviewV2Model.DisplayControlForMode(mode));
        }

        [Theory]
        [InlineData("Itm", SettingsMode.On)]
        [InlineData("Legacy", SettingsMode.LegacyOnly)]
        [InlineData("Off", SettingsMode.Off)]
        public void ModeForDisplayControl_InverseMap(string control, SettingsMode mode)
            => Assert.Equal(mode, DisplayOverviewV2Model.ModeForDisplayControl(control));

        [Fact]
        public void Project_ModeRoundTrip_ReflectsDocumentSettingsMode()
        {
            var doc = MinimalDoc();
            doc.Settings.Mode = SettingsMode.LegacyOnly;
            doc.Settings.RejectUncommandedChanges = true;

            var model = DisplayOverviewV2Model.Project(
                doc, DisplayResolutionSnapshotModel.Empty, null, DisplayType.Itm);

            Assert.Equal(SettingsMode.LegacyOnly, model.Mode);
            Assert.True(model.RejectUncommandedChanges);
            Assert.True(model.IsItmWheel);
            Assert.Equal(DisplayCopy.ModeHintItm, model.ModeHint);
        }

        // ── Ladder row composition ───────────────────────────────────────

        [Fact]
        public void Ladder_ComposesRankedRows_PlusBaseAndIdle()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var model = DisplayOverviewV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            Assert.True(model.ShowLadder);
            // 1 seat + base + idle
            Assert.Equal(3, model.PriorityRows.Count);
            Assert.Equal("1", model.PriorityRows[0].RankText);
            Assert.Equal(DisplayCopy.PriorityBaseRank, model.PriorityRows[1].RankText);
            Assert.Equal(string.Empty, model.PriorityRows[2].RankText); // idle rank empty
            Assert.Equal(DisplayCopy.OutsideASession, model.PriorityRows[2].Destination.Name);
            Assert.Equal(OverviewRowState.Pinned, model.PriorityRows[1].State);
            Assert.Equal(OverviewRowState.Pinned, model.PriorityRows[2].State);
        }

        [Fact]
        public void Ladder_Winner_IsHighlighted_StatusEmpty()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var resolution = ResolutionWithWinner("s1", "itm:tyreTemps");

            var model = DisplayOverviewV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            var winner = model.PriorityRows[0];
            Assert.Equal(OverviewRowState.Winner, winner.State);
            Assert.Equal(DisplayCopy.OnScreen, winner.StatusCopy); // C1: empty
        }

        [Fact]
        public void Ladder_LegacyOnly_DimsItmRows_WithCantRunHere()
        {
            // O1 PROVISIONAL (design-backlog)
            var doc = DocWithSeat("tyreTemps", "s1");
            doc.Settings.Mode = SettingsMode.LegacyOnly;

            var model = DisplayOverviewV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            var itmRow = model.PriorityRows[0];
            Assert.Equal(OverviewRowState.Off, itmRow.State);
            Assert.Equal(DisplayCopy.CantRunHere, itmRow.StatusCopy);
        }

        [Fact]
        public void Ladder_LegacyOnly_StaleWinnerSnapshot_StillCantRunHere()
        {
            // Immediate post-switch poll still carries the prior ITM winner — provisional
            // CAN'T RUN HERE must beat the stale navy winner highlight.
            var doc = DocWithSeat("tyreTemps", "s1");
            doc.Settings.Mode = SettingsMode.LegacyOnly;
            var staleWinner = ResolutionWithWinner("s1", "itm:tyreTemps");

            var model = DisplayOverviewV2Model.Project(
                doc, staleWinner, null, DisplayType.Itm);

            var itmRow = model.PriorityRows[0];
            Assert.Equal(OverviewRowState.Off, itmRow.State);
            Assert.NotEqual(OverviewRowState.Winner, itmRow.State);
            Assert.Equal(DisplayCopy.CantRunHere, itmRow.StatusCopy);
            Assert.NotEqual(DisplayCopy.OnScreen, itmRow.StatusCopy);
        }

        [Fact]
        public void Ladder_ModeOff_NoLadder_EmptyState()
        {
            // O1 PROVISIONAL (design-backlog)
            var doc = MinimalDoc();
            doc.Settings.Mode = SettingsMode.Off;

            var model = DisplayOverviewV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            Assert.False(model.ShowLadder);
            Assert.Empty(model.PriorityRows);
            Assert.Equal(DisplayCopy.ModeOffEmptyState, model.ModeOffEmptyState);
        }

        [Fact]
        public void Ladder_ManualRow_UsesManualPagingDetail()
        {
            var doc = MinimalDoc();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Manual,
            });
            var resolution = DisplayResolutionSnapshotModel.From(
                null,
                inGame: true,
                isConnected: true,
                aggregates: null,
                manual: new ManualRowState { HasRememberedTarget = false });

            var model = DisplayOverviewV2Model.Project(
                doc, resolution, null, DisplayType.Itm,
                nextPageMapped: false, prevPageMapped: false);

            var manual = model.PriorityRows.First(r =>
                r.Destination.Name == DisplayCopy.ManualPaging);
            Assert.Equal(
                DisplayCopy.ManualPagingDetail(false, false, false),
                manual.Detail);
        }

        [Fact]
        public void Ladder_Aggregate_UsesEntrypointsFiringLine()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var aggregates = new List<AggregateMembership>
            {
                new AggregateMembership
                {
                    SeatId = "s1",
                    ActiveCount = 2,
                    TotalCount = 4,
                },
            };
            var resolution = DisplayResolutionSnapshotModel.From(
                null, inGame: true, isConnected: true, aggregates: aggregates, manual: null);

            var model = DisplayOverviewV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            Assert.Equal(
                DisplayCopy.EntrypointsFiringLine(2, 4),
                model.PriorityRows[0].Detail);
        }

        [Fact]
        public void Ladder_Outranked_Aggregate_ProjectsChildOffScreenClause()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            AddFieldOverride(doc, paramId: 9, overrideId: "ov-fn1", text: "FN1");
            var aggregates = new List<AggregateMembership>
            {
                new AggregateMembership
                {
                    SeatId = "s1",
                    DestinationId = "itm:tyreTemps",
                    ActiveCount = 2,
                    TotalCount = 4,
                    MemberCarrierIds = new[] { "ov-fn1" },
                },
            };
            var resolution = ResolutionOutrankedWithChild(
                seatId: "s1",
                seatDest: "itm:tyreTemps",
                childId: "ov-fn1",
                childDest: "itm:tyreTemps",
                aggregates);

            var model = DisplayOverviewV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            string expectedClause = DisplayCopy.OutrankedOffScreenClause(
                DisplayCopy.OverrideChildLabel("FN1"));
            Assert.Equal(
                DisplayCopy.DetailWithClause(
                    DisplayCopy.EntrypointsFiringLine(2, 4), expectedClause),
                model.PriorityRows[0].Detail);
        }

        [Fact]
        public void Ladder_Outranked_Summon_ProjectsChildOffScreenClause()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            AddFieldOverride(doc, paramId: 9, overrideId: "ov-fn1", text: "FN1");
            var resolution = ResolutionOutrankedWithChild(
                seatId: "s1",
                seatDest: "itm:tyreTemps",
                childId: "ov-fn1",
                childDest: "itm:tyreTemps",
                aggregates: null);

            var model = DisplayOverviewV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            string expectedClause = DisplayCopy.OutrankedOffScreenClause(
                DisplayCopy.OverrideChildLabel("FN1"));
            Assert.Contains(expectedClause, model.PriorityRows[0].Detail);
            Assert.Contains(DisplayCopy.OffScreen, model.PriorityRows[0].Detail);
        }

        [Fact]
        public void Ladder_OffRow_IsOutlinedStatusChip()
        {
            var doc = DocWithSeat("tyreTemps", "s1");
            var resolution = ResolutionWithRowLabel("s1", "itm:tyreTemps", CarrierRowLabels.Off);

            var model = DisplayOverviewV2Model.Project(
                doc, resolution, null, DisplayType.Itm);

            var row = model.PriorityRows[0];
            Assert.Equal(DisplayCopy.Off, row.StatusCopy);
            Assert.True(row.IsOutlinedStatusChip);
            Assert.Equal(OverviewRowState.Off, row.State);
        }

        // ── Idle row IdleSpec variants (O2) ──────────────────────────────

        [Fact]
        public void Idle_Blank_RendersBlankDisplay()
        {
            var idle = new IdleSpec { Kind = IdleKind.Blank };
            Assert.Equal(
                DisplayCopy.IdleTargetLine(DisplayCopy.ABlankDisplay, null),
                DisplayOverviewV2Model.IdleDetail(idle));
        }

        [Fact]
        public void Idle_Screen_Logo_RendersLogoLabel()
        {
            var idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Logo,
            };
            Assert.Equal(
                DisplayCopy.IdleTargetLine(DisplayCopy.TheWheelsLogo, null),
                DisplayOverviewV2Model.IdleDetail(idle));
        }

        [Fact]
        public void Idle_Page_RendersPageTarget()
        {
            var doc = MinimalDoc();
            doc.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "speed",
                Name = "Speed",
            });
            var idle = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = new PageRef { Kind = PageRefKind.HostedPage, Id = "speed" },
            };

            string detail = DisplayOverviewV2Model.IdleDetail(idle, doc);
            Assert.Contains(DisplayCopy.LegacyBadge, detail);
            Assert.Contains("Speed", detail);
        }

        [Fact]
        public void Idle_Null_DefaultsToBlank()
        {
            Assert.Equal(
                DisplayCopy.IdleTargetLine(DisplayCopy.ABlankDisplay, null),
                DisplayOverviewV2Model.IdleDetail(null));
        }

        // ── Mirror / header / controls ───────────────────────────────────

        [Fact]
        public void Header_SurfaceWord_ItmVsSegment()
        {
            var doc = MinimalDoc();
            var itm = DisplayOverviewV2Model.Project(
                doc, EmptyConnected(inGame: true), null, DisplayType.Itm);
            var seg = DisplayOverviewV2Model.Project(
                doc, EmptyConnected(inGame: false), null, DisplayType.Basic);

            Assert.Equal(DisplayCopy.ItmDisplay, itm.SurfaceWord);
            Assert.Equal(DisplayCopy.InGame, itm.SituationCopy);
            Assert.Equal(DisplayCopy.SegmentDisplay, seg.SurfaceWord);
            Assert.Equal(DisplayCopy.SituationIdle, seg.SituationCopy);
        }

        [Fact]
        public void Controls_NothingMapped_AmberConsequence()
        {
            var model = DisplayOverviewV2Model.Project(
                MinimalDoc(), EmptyConnected(), null, DisplayType.Itm,
                nextPageMapped: false, prevPageMapped: false);

            Assert.True(model.ShowNothingMappedAmber);
            Assert.Equal(DisplayCopy.NotMapped, model.NextPageValue);
            Assert.Contains(DisplayCopy.ControlsConsequenceNothingMapped, model.ConsequenceLines);
        }

        [Fact]
        public void WithRejectUncommanded_ClonesSettings()
        {
            var doc = MinimalDoc();
            var next = DisplayOverviewV2Model.WithRejectUncommanded(doc, true);
            Assert.True(next.Settings.RejectUncommandedChanges);
            Assert.False(doc.Settings.RejectUncommandedChanges);
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static DisplayConfigV2 MinimalDoc()
        {
            return new DisplayConfigV2
            {
                Settings = new SettingsBlock { Mode = SettingsMode.On },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.ItmPage,
                            CatalogPageId = "lapInfo",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
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
                Target = new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = catalogPageId,
                },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "sum1",
                        Enabled = true,
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "PitLimiterOn",
                            },
                            Operator = ConditionOperator.IsTrue,
                        },
                    },
                },
            });
            return doc;
        }

        private static DisplayResolutionSnapshotModel EmptyConnected(bool inGame = true)
            => DisplayResolutionSnapshotModel.From(
                null, inGame: inGame, isConnected: true, aggregates: null, manual: null);

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

        private static DisplayResolutionSnapshotModel ResolutionOutrankedWithChild(
            string seatId,
            string seatDest,
            string childId,
            string childDest,
            List<AggregateMembership> aggregates)
        {
            // Another carrier is the display winner so the seat is Outranked.
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(SeatArbiter.DisplaySurfaceId, "other", "itm:other"),
            };
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    seatId, SeatArbiter.DisplaySurfaceId, seatDest,
                    CarrierPresence.Outranked, null, CarrierRowLabels.None),
                new CarrierResolutionStatus(
                    childId, "field:9", childDest,
                    CarrierPresence.OffScreen, null, CarrierRowLabels.None),
            };
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "test",
                surfaceWinners: winners,
                carrierStatuses: statuses,
                carrierSnapshots: new List<CarrierTickSnapshot>());
            return DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: aggregates, manual: null);
        }

        private static DisplayResolutionSnapshotModel ResolutionWithRowLabel(
            string carrierId, string destinationId, CarrierRowLabels labels)
        {
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    carrierId, SeatArbiter.DisplaySurfaceId, destinationId,
                    CarrierPresence.Waiting, null, labels),
            };
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "test",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: statuses,
                carrierSnapshots: new List<CarrierTickSnapshot>());
            return DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: null, manual: null);
        }

        private static void AddFieldOverride(
            DisplayConfigV2 doc, ushort paramId, string overrideId, string text)
        {
            if (doc.Fields == null)
                doc.Fields = new Dictionary<ushort, FieldEntry>();
            doc.Fields[paramId] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = overrideId,
                        Content = new ContentObject
                        {
                            Kind = ContentKind.Text,
                            Text = text,
                        },
                    },
                },
            };
        }
    }
}
