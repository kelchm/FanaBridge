using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Pure model tests for the minimal diagnostics panel. No WPF.
    /// Pins presence-state row composition, device block, wheel-screen
    /// (incl. dismissal latch), empty-state, and DisplayCopy wiring.
    /// </summary>
    public class DisplayDiagnosticsModelTests
    {
        // ── Empty / no-resolution ────────────────────────────────────────

        [Fact]
        public void Project_Empty_NeverBlank_RuledEmptyState()
        {
            var model = DisplayDiagnosticsModel.Project(DisplayResolutionSnapshotModel.Empty);

            Assert.False(model.HasResolution);
            Assert.Equal(DisplayCopy.DiagnosticsEmptyState, model.EmptyStateLine);
            Assert.Empty(model.LadderRows);
            Assert.Empty(model.DeviceLines);
            Assert.Empty(model.WheelScreenLines);
            Assert.Empty(model.ManualLines);
            Assert.Empty(model.FloorLines);
        }

        [Fact]
        public void Project_Disconnected_EmptyState()
        {
            var model = DisplayDiagnosticsModel.Project(
                DisplayResolutionSnapshotModel.From(
                    null, inGame: false, isConnected: false, aggregates: null, manual: null));

            Assert.False(model.HasResolution);
            Assert.Equal(DisplayCopy.DiagnosticsEmptyState, model.EmptyStateLine);
        }

        // ── Ladder rows per presence state ───────────────────────────────

        [Theory]
        [InlineData(CarrierPresence.Waiting, "waiting")]
        [InlineData(CarrierPresence.Outranked, "outranked")]
        [InlineData(CarrierPresence.OffScreen, "off-screen")]
        [InlineData(CarrierPresence.OnScreen, "")]
        public void Ladder_PresenceStates_RuledCopy(CarrierPresence presence, string expectedCopy)
        {
            var resolution = ResolutionWithCarrier(
                "fuel", SeatArbiter.DisplaySurfaceId, "itm:fuel",
                presence, remainingMs: null, CarrierRowLabels.None);

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.True(model.HasResolution);
            Assert.Single(model.LadderRows);
            Assert.Equal(expectedCopy, model.LadderRows[0].PresenceCopy);
            Assert.Equal("fuel", model.LadderRows[0].CarrierId);
            Assert.Equal("itm:fuel", model.LadderRows[0].DestinationId);
        }

        [Fact]
        public void Ladder_RowLabels_JoinedWithDot()
        {
            var resolution = ResolutionWithCarrier(
                "pit", SeatArbiter.DisplaySurfaceId, "itm:pit",
                CarrierPresence.Waiting, remainingMs: null,
                CarrierRowLabels.Off | CarrierRowLabels.CantRunHere);

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Equal(
                DisplayCopy.Off + " · " + DisplayCopy.CantRunHere,
                model.LadderRows[0].RowLabelsCopy);
        }

        [Fact]
        public void Ladder_TimingDetail_FromRemainingMs()
        {
            var resolution = ResolutionWithCarrier(
                "fuel", SeatArbiter.DisplaySurfaceId, "itm:fuel",
                CarrierPresence.Outranked, remainingMs: 1500, CarrierRowLabels.None);

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Equal(DisplayCopy.DiagnosticsRemainingMs(1500), model.LadderRows[0].TimingDetail);
        }

        [Fact]
        public void Ladder_Dismissed_PresenceEmpty_LabelOnly()
        {
            // Snapshot semantics: Presence-Dismissed → empty presence; DISMISSED only as row label.
            var resolution = ResolutionWithCarrier(
                "msg", DestinationIds.WheelScreenSurfaceId, "screen:logo",
                CarrierPresence.Dismissed, remainingMs: null, CarrierRowLabels.Dismissed);

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Equal(string.Empty, model.LadderRows[0].PresenceCopy);
            Assert.Equal(DisplayCopy.Dismissed, model.LadderRows[0].RowLabelsCopy);
            Assert.DoesNotContain(
                DisplayCopy.Dismissed + " · " + DisplayCopy.Dismissed,
                model.LadderRows[0].PresenceCopy + " · " + model.LadderRows[0].RowLabelsCopy);
        }

        [Fact]
        public void Ladder_ConditionAndName_KeyedBySummonId()
        {
            // Production carriers use Summon.Id, not PriorityRow.Id.
            var doc = new DisplayConfigV2();
            doc.Priority = new PriorityLadder();
            doc.Priority.Rows.Add(new PriorityRow
            {
                Id = "fuel-row",
                Kind = PriorityRowKind.Seat,
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "fuel-s1",
                        Name = "Low fuel",
                        Enabled = true,
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.SimHubProperty,
                                Name = "DataCorePlugin.GameData.Fuel",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 4.0,
                        },
                    },
                },
            });

            var resolution = ResolutionWithCarrier(
                "fuel-s1", SeatArbiter.DisplaySurfaceId, "itm:fuel",
                CarrierPresence.Waiting, remainingMs: null, CarrierRowLabels.None);

            var model = DisplayDiagnosticsModel.Project(resolution, doc);

            Assert.Equal("Low fuel", model.LadderRows[0].Label);
            Assert.Contains(DisplayCopy.OpBelow, model.LadderRows[0].ConditionSentence);
            Assert.Contains("Fuel", model.LadderRows[0].ConditionSentence);
        }

        // ── Device block ─────────────────────────────────────────────────

        [Fact]
        public void DeviceBlock_WithDeviceBlock_SurfacesKeyPageAndEdges()
        {
            var page = CurrentPageKnowledge.Known(6, null);
            var record = new ComposedResolutionRecord(
                tickMs: 42_000,
                deviceKey: "PBME",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>(),
                pageKnowledge: page,
                revertedThisTick: true,
                adoptWarnedThisTick: false);
            var resolution = DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: null, manual: null);

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.True(model.HasResolution);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(DisplayCopy.DiagnosticsDeviceKey, "PBME"),
                model.DeviceLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsPageKnowledge,
                    DisplayCopy.DiagnosticsPageKnown(6, null)),
                model.DeviceLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsRevertedThisTick, DisplayCopy.DiagnosticsYes),
                model.DeviceLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsAdoptWarnedThisTick, DisplayCopy.DiagnosticsNo),
                model.DeviceLines);
        }

        [Fact]
        public void DeviceBlock_WithoutDeviceBlock_NotesAbsence()
        {
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "x",
                surfaceWinners: new List<SurfaceWinner>
                {
                    new SurfaceWinner(SeatArbiter.DisplaySurfaceId, null, DestinationIds.RestInSession),
                },
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>());
            var resolution = DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: null, manual: null);

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Contains(DisplayCopy.DiagnosticsNoDeviceBlock, model.DeviceLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(DisplayCopy.DiagnosticsDeviceKey, "x"),
                model.DeviceLines);
        }

        // ── Wheel-screen section ─────────────────────────────────────────

        [Fact]
        public void WheelScreen_Held_WithOwner()
        {
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    "logo-rule", DestinationIds.WheelScreenSurfaceId, "screen:logo",
                    CarrierPresence.OnScreen, remainingMs: null, CarrierRowLabels.None),
            };
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(DestinationIds.WheelScreenSurfaceId, "logo-rule", "screen:logo"),
            };
            var resolution = Snapshot(statuses, winners, deviceKey: "PBME");

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(DisplayCopy.DiagnosticsOwner, "logo-rule"),
                model.WheelScreenLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsHoldState, DisplayCopy.DiagnosticsHeld),
                model.WheelScreenLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsDismissalLatch, DisplayCopy.DiagnosticsLatchClear),
                model.WheelScreenLines);
        }

        [Fact]
        public void WheelScreen_Released_IdleFloor()
        {
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(DestinationIds.WheelScreenSurfaceId, null, DestinationIds.RestIdle),
            };
            var resolution = Snapshot(
                new List<CarrierResolutionStatus>(), winners, deviceKey: "PBME");

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsHoldState, DisplayCopy.DiagnosticsReleased),
                model.WheelScreenLines);
        }

        [Fact]
        public void WheelScreen_DismissalLatch_ActiveWhenDismissedLabelPresent()
        {
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    "logo-rule", DestinationIds.WheelScreenSurfaceId, "screen:logo",
                    CarrierPresence.Dismissed, remainingMs: null, CarrierRowLabels.Dismissed),
            };
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(DestinationIds.WheelScreenSurfaceId, null, DestinationIds.RestIdle),
            };
            var resolution = Snapshot(statuses, winners, deviceKey: "PBME");

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsDismissalLatch, DisplayCopy.DiagnosticsLatchActive),
                model.WheelScreenLines);
        }

        // ── Manual + floor ───────────────────────────────────────────────

        [Fact]
        public void Manual_WithRememberedTarget()
        {
            // Manual bookkeeping alone is not a live resolution — pair with a composed record.
            var record = new ComposedResolutionRecord(
                tickMs: 1000,
                deviceKey: "PBME",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>(),
                pageKnowledge: CurrentPageKnowledge.Unknown,
                revertedThisTick: false,
                adoptWarnedThisTick: false);
            var resolution = DisplayResolutionSnapshotModel.From(
                record,
                inGame: true,
                isConnected: true,
                aggregates: null,
                manual: new ManualRowState
                {
                    HasRememberedTarget = true,
                    RememberedDestinationId = "itm:lapInfo",
                    OwnsDisplay = true,
                    MsSinceLastPress = 800,
                });

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.True(model.HasResolution);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(DisplayCopy.ManualPaging, "itm:lapInfo"),
                model.ManualLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsOwnsDisplay, DisplayCopy.DiagnosticsYes),
                model.ManualLines);
            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsSinceLastPress, DisplayCopy.DiagnosticsMs(800)),
                model.ManualLines);
        }

        [Fact]
        public void HasResolution_NullComposed_ManualAlone_RuledEmptyState()
        {
            var resolution = DisplayResolutionSnapshotModel.From(
                null,
                inGame: true,
                isConnected: true,
                aggregates: null,
                manual: new ManualRowState
                {
                    HasRememberedTarget = true,
                    RememberedDestinationId = "itm:lapInfo",
                    OwnsDisplay = true,
                });

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.False(model.HasResolution);
            Assert.Equal(DisplayCopy.DiagnosticsEmptyState, model.EmptyStateLine);
            Assert.Empty(model.ManualLines);
            Assert.Empty(model.LadderRows);
        }

        [Fact]
        public void WheelScreen_Absence_PublishesNothing()
        {
            // No wheel-screen surface winner → no invented released/clear facts.
            var resolution = Snapshot(
                new List<CarrierResolutionStatus>(),
                new List<SurfaceWinner>
                {
                    new SurfaceWinner(
                        SeatArbiter.DisplaySurfaceId, SeatArbiter.RestCarrierId, DestinationIds.RestInSession),
                },
                deviceKey: "PBME");

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Empty(model.WheelScreenLines);
            foreach (var line in model.WheelScreenLines
                         .Concat(model.DeviceLines)
                         .Concat(model.FloorLines)
                         .Concat(model.ManualLines))
            {
                Assert.DoesNotContain(DisplayCopy.DiagnosticsReleased, line);
                Assert.DoesNotContain(
                    DisplayCopy.DiagnosticsFactLine(
                        DisplayCopy.DiagnosticsDismissalLatch, DisplayCopy.DiagnosticsLatchClear),
                    line);
            }
        }

        [Fact]
        public void Floor_BasePage_WhenRestWinsDisplay()
        {
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(
                    SeatArbiter.DisplaySurfaceId,
                    SeatArbiter.RestCarrierId,
                    DestinationIds.RestInSession),
            };
            var resolution = Snapshot(
                new List<CarrierResolutionStatus>(), winners, deviceKey: "PBME");

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Contains(
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.BasePage, DisplayCopy.WhenNothingAboveIsLive),
                model.FloorLines);
            Assert.DoesNotContain(DestinationIds.RestInSession, string.Join("\n", model.FloorLines));
            Assert.DoesNotContain(DestinationIds.RestIdle, string.Join("\n", model.FloorLines));
        }

        // ── Copy vocabulary smoke ────────────────────────────────────────

        [Fact]
        public void Copy_UsesRuledPresenceAndLabels_NeverGlobal()
        {
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    "a", SeatArbiter.DisplaySurfaceId, "itm:a",
                    CarrierPresence.Waiting, null, CarrierRowLabels.None),
                new CarrierResolutionStatus(
                    "b", SeatArbiter.DisplaySurfaceId, "itm:b",
                    CarrierPresence.Outranked, null, CarrierRowLabels.Off),
                new CarrierResolutionStatus(
                    "c", SeatArbiter.DisplaySurfaceId, "itm:c",
                    CarrierPresence.OffScreen, null, CarrierRowLabels.CantRunHere),
            };
            var resolution = Snapshot(statuses, new List<SurfaceWinner>(), deviceKey: "x");

            var model = DisplayDiagnosticsModel.Project(resolution);

            Assert.Equal(DisplayCopy.Waiting, model.LadderRows[0].PresenceCopy);
            Assert.Equal(DisplayCopy.Outranked, model.LadderRows[1].PresenceCopy);
            Assert.Equal(DisplayCopy.Off, model.LadderRows[1].RowLabelsCopy);
            Assert.Equal(DisplayCopy.OffScreen, model.LadderRows[2].PresenceCopy);
            Assert.Equal(DisplayCopy.CantRunHere, model.LadderRows[2].RowLabelsCopy);

            // No banned "global" anywhere in projected copy.
            foreach (var row in model.LadderRows)
            {
                Assert.DoesNotContain("global", row.Label, System.StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("global", row.PresenceCopy, System.StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("global", row.ConditionSentence, System.StringComparison.OrdinalIgnoreCase);
            }
            foreach (var line in model.DeviceLines.Concat(model.WheelScreenLines)
                         .Concat(model.ManualLines).Concat(model.FloorLines))
            {
                Assert.DoesNotContain("global", line, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static DisplayResolutionSnapshotModel ResolutionWithCarrier(
            string carrierId,
            string surfaceId,
            string destinationId,
            CarrierPresence presence,
            int? remainingMs,
            CarrierRowLabels labels)
        {
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    carrierId, surfaceId, destinationId, presence, remainingMs, labels),
            };
            return Snapshot(statuses, new List<SurfaceWinner>(), deviceKey: "dev");
        }

        private static DisplayResolutionSnapshotModel Snapshot(
            IReadOnlyList<CarrierResolutionStatus> statuses,
            IReadOnlyList<SurfaceWinner> winners,
            string deviceKey)
        {
            var record = new ComposedResolutionRecord(
                tickMs: 1000,
                deviceKey: deviceKey,
                surfaceWinners: winners ?? new List<SurfaceWinner>(),
                carrierStatuses: statuses ?? new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>(),
                pageKnowledge: CurrentPageKnowledge.Unknown,
                revertedThisTick: false,
                adoptWarnedThisTick: false);
            return DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: null, manual: null);
        }
    }
}
