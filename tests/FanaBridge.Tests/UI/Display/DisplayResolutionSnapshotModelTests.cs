using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Mapping pins for <see cref="DisplayResolutionSnapshotModel"/>: every
    /// <see cref="CarrierPresence"/> and single-bit <see cref="CarrierRowLabels"/>
    /// value maps to its ruled <see cref="DisplayCopy"/> string; device block
    /// passthrough; null record → empty model. Completeness is
    /// <see cref="Enum.GetValues"/>-driven so a new enum member fails until mapped.
    /// </summary>
    public class DisplayResolutionSnapshotModelTests
    {
        // ── Presence → ruled status (completeness) ───────────────────────

        public static IEnumerable<object[]> AllCarrierPresenceValues()
            => Enum.GetValues(typeof(CarrierPresence))
                .Cast<CarrierPresence>()
                .Select(p => new object[] { p });

        [Theory]
        [MemberData(nameof(AllCarrierPresenceValues))]
        public void PresenceCopy_MapsEveryCarrierPresence(CarrierPresence presence)
        {
            string copy = DisplayResolutionSnapshotModel.PresenceCopy(presence);
            Assert.NotNull(copy);

            switch (presence)
            {
                case CarrierPresence.Waiting:
                    Assert.Equal(DisplayCopy.Waiting, copy);
                    break;
                case CarrierPresence.Outranked:
                    Assert.Equal(DisplayCopy.Outranked, copy);
                    break;
                case CarrierPresence.OffScreen:
                    Assert.Equal(DisplayCopy.OffScreen, copy);
                    break;
                case CarrierPresence.OnScreen:
                    Assert.Equal(DisplayCopy.OnScreen, copy);
                    break;
                case CarrierPresence.Dismissed:
                    // Presence-Dismissed is a non-check state; DISMISSED is the row label.
                    Assert.Equal(string.Empty, copy);
                    break;
                default:
                    Assert.Fail(
                        "Unmapped CarrierPresence." + presence
                        + " — add a case in PresenceCopy and this test.");
                    break;
            }
        }

        [Fact]
        public void PresenceCopy_Null_IsEmpty()
            => Assert.Equal(string.Empty, DisplayResolutionSnapshotModel.PresenceCopy(null));

        // ── Row labels → ruled / diagnostics (completeness) ──────────────

        public static IEnumerable<object[]> AllSingleBitCarrierRowLabels()
            => Enum.GetValues(typeof(CarrierRowLabels))
                .Cast<CarrierRowLabels>()
                .Where(IsSingleBitOrNone)
                .Select(l => new object[] { l });

        [Theory]
        [MemberData(nameof(AllSingleBitCarrierRowLabels))]
        public void RowLabelCopy_MapsEveryDefinedFlag(CarrierRowLabels label)
        {
            string copy = DisplayResolutionSnapshotModel.RowLabelCopy(label);

            switch (label)
            {
                case CarrierRowLabels.None:
                    Assert.Null(copy);
                    break;
                case CarrierRowLabels.Off:
                    Assert.Equal(DisplayCopy.Off, copy);
                    break;
                case CarrierRowLabels.Dismissed:
                    Assert.Equal(DisplayCopy.Dismissed, copy);
                    break;
                case CarrierRowLabels.CantRunHere:
                    Assert.Equal(DisplayCopy.CantRunHere, copy);
                    break;
                case CarrierRowLabels.NoWheel:
                    Assert.Equal(DisplayCopy.NoWheel, copy);
                    break;
                case CarrierRowLabels.Paused:
                    Assert.Equal(DisplayCopy.Paused, copy);
                    break;
                case CarrierRowLabels.KeptAsIs:
                    Assert.Equal(DisplayCopy.KeptAsIs, copy);
                    break;
                case CarrierRowLabels.OutOfSessionScope:
                    Assert.Equal(DisplayCopy.OutOfSessionScope, copy);
                    break;
                case CarrierRowLabels.Untested:
                    Assert.Equal(DisplayCopy.Untested, copy);
                    break;
                default:
                    Assert.Fail(
                        "Unmapped CarrierRowLabels." + label
                        + " — add a case in RowLabelCopy and this test.");
                    break;
            }
        }

        [Fact]
        public void RowLabelCopies_CombinesFlags_InStableOrder()
        {
            var flags = CarrierRowLabels.Off
                | CarrierRowLabels.CantRunHere
                | CarrierRowLabels.Dismissed
                | CarrierRowLabels.Untested;

            var copies = DisplayResolutionSnapshotModel.RowLabelCopies(flags);

            Assert.Equal(
                new[]
                {
                    DisplayCopy.Off,
                    DisplayCopy.CantRunHere,
                    DisplayCopy.Dismissed,
                    DisplayCopy.Untested,
                },
                copies);
        }

        [Fact]
        public void RowLabelCopies_None_IsEmpty()
            => Assert.Empty(DisplayResolutionSnapshotModel.RowLabelCopies(CarrierRowLabels.None));

        // ── Null record → empty ──────────────────────────────────────────

        [Fact]
        public void From_NullRecord_ReturnsEmpty()
        {
            var model = DisplayResolutionSnapshotModel.From(null);

            Assert.Same(DisplayResolutionSnapshotModel.Empty, model);
            Assert.Equal(0, model.TickMs);
            Assert.Equal(string.Empty, model.DeviceKey);
            Assert.False(model.HasDeviceBlock);
            Assert.False(model.PageKnowledge.IsKnown);
            Assert.False(model.RevertedThisTick);
            Assert.False(model.AdoptWarnedThisTick);
            Assert.Empty(model.Carriers);
            // O12 defaults on empty
            Assert.False(model.InGame);
            Assert.False(model.IsConnected);
            Assert.Empty(model.SurfaceWinners);
            Assert.Empty(model.Aggregates);
            Assert.Null(model.Manual);
        }

        // ── Device block passthrough ─────────────────────────────────────

        [Fact]
        public void From_DeviceBlock_Passthrough()
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
                adoptWarnedThisTick: true);

            var model = DisplayResolutionSnapshotModel.From(record);

            Assert.Equal(42_000, model.TickMs);
            Assert.Equal("PBME", model.DeviceKey);
            Assert.True(model.HasDeviceBlock);
            Assert.True(model.PageKnowledge.IsKnown);
            Assert.Equal((byte)6, model.PageKnowledge.WirePage);
            Assert.True(model.RevertedThisTick);
            Assert.True(model.AdoptWarnedThisTick);
            Assert.Empty(model.Carriers);
        }

        [Fact]
        public void From_WithoutDeviceBlock_HasDeviceBlockFalse()
        {
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "dev",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>());

            var model = DisplayResolutionSnapshotModel.From(record);

            Assert.False(model.HasDeviceBlock);
            Assert.False(model.PageKnowledge.IsKnown);
            Assert.False(model.RevertedThisTick);
            Assert.False(model.AdoptWarnedThisTick);
        }

        // ── Full carrier row translation ─────────────────────────────────

        [Fact]
        public void From_CarrierStatuses_MapToRuledCopy()
        {
            var statuses = new List<CarrierResolutionStatus>
            {
                new CarrierResolutionStatus(
                    "pit", "seat", "page-1",
                    CarrierPresence.Waiting, remainingMs: null,
                    CarrierRowLabels.None),
                new CarrierResolutionStatus(
                    "fuel", "seat", "page-1",
                    CarrierPresence.Outranked, remainingMs: 1500,
                    CarrierRowLabels.Off),
                new CarrierResolutionStatus(
                    "msg", "wheelScreen", "blank",
                    CarrierPresence.OffScreen, remainingMs: null,
                    CarrierRowLabels.CantRunHere | CarrierRowLabels.Dismissed),
            };

            var record = new ComposedResolutionRecord(
                tickMs: 9,
                deviceKey: "x",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: statuses,
                carrierSnapshots: new List<CarrierTickSnapshot>());

            var model = DisplayResolutionSnapshotModel.From(record);

            Assert.Equal(3, model.Carriers.Count);

            Assert.Equal("pit", model.Carriers[0].CarrierId);
            Assert.Equal(DisplayCopy.Waiting, model.Carriers[0].PresenceCopy);
            Assert.Empty(model.Carriers[0].RowLabelCopies);

            Assert.Equal(DisplayCopy.Outranked, model.Carriers[1].PresenceCopy);
            Assert.Equal(new[] { DisplayCopy.Off }, model.Carriers[1].RowLabelCopies);
            Assert.Equal(1500, model.Carriers[1].RemainingMs);

            Assert.Equal(DisplayCopy.OffScreen, model.Carriers[2].PresenceCopy);
            Assert.Equal(
                new[] { DisplayCopy.CantRunHere, DisplayCopy.Dismissed },
                model.Carriers[2].RowLabelCopies);
        }

        // ── O12 published engine values ──────────────────────────────────

        [Fact]
        public void From_SurfaceWinners_Projected()
        {
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner("display", "s1", "itm:tyreTemps"),
                new SurfaceWinner("wheelScreen", null, null),
            };
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "x",
                surfaceWinners: winners,
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>());

            var model = DisplayResolutionSnapshotModel.From(record);

            Assert.Equal(2, model.SurfaceWinners.Count);
            Assert.Equal("display", model.SurfaceWinners[0].SurfaceId);
            Assert.Equal("s1", model.SurfaceWinners[0].WinnerCarrierId);
            Assert.Equal("itm:tyreTemps", model.SurfaceWinners[0].DestinationId);
        }

        [Fact]
        public void From_InGame_And_IsConnected_PassThrough()
        {
            var record = new ComposedResolutionRecord(
                tickMs: 1,
                deviceKey: "x",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: new List<CarrierTickSnapshot>());

            var model = DisplayResolutionSnapshotModel.From(
                record, inGame: true, isConnected: true, aggregates: null, manual: null);

            Assert.True(model.InGame);
            Assert.True(model.IsConnected);
        }

        [Fact]
        public void From_Aggregates_And_Manual_Projected()
        {
            var aggregates = new List<AggregateMembership>
            {
                new AggregateMembership
                {
                    SeatId = "s1",
                    DestinationId = "itm:tyreTemps",
                    DerivedCarrierId = "agg:s1",
                    ActiveCount = 2,
                    TotalCount = 4,
                    MemberCarrierIds = new[] { "c1", "c2" },
                    MembershipDegraded = false,
                },
            };
            var manual = new ManualRowState
            {
                HasRememberedTarget = true,
                RememberedDestinationId = "itm:lapInfo",
                OwnsDisplay = false,
            };

            var model = DisplayResolutionSnapshotModel.From(
                null, inGame: false, isConnected: true, aggregates: aggregates, manual: manual);

            Assert.True(model.IsConnected);
            Assert.False(model.InGame);
            Assert.Single(model.Aggregates);
            Assert.Equal("s1", model.Aggregates[0].SeatId);
            Assert.Equal(2, model.Aggregates[0].ActiveCount);
            Assert.Equal(4, model.Aggregates[0].TotalCount);
            Assert.NotNull(model.Manual);
            Assert.True(model.Manual.HasRememberedTarget);
            Assert.Equal("itm:lapInfo", model.Manual.RememberedDestinationId);
        }

        [Fact]
        public void From_Disconnected_IsEmpty()
        {
            var model = DisplayResolutionSnapshotModel.From(
                null, inGame: false, isConnected: false, aggregates: null, manual: null);
            Assert.Same(DisplayResolutionSnapshotModel.Empty, model);
        }

        // ── Record-gap diagnostics projection ────────────────────────────

        [Fact]
        public void From_RecordGapFields_Projected()
        {
            var env = new CapabilityEnvelopeSummary(
                fieldParamCount: 4,
                screenLogo: true,
                screenBlank: null,
                screenWhite: false,
                screenLogoInverted: true,
                provisional: null);
            var snaps = new List<CarrierTickSnapshot>
            {
                new CarrierTickSnapshot(
                    "c1", true, true, false, true, true, 9000, 400),
            };
            var record = new ComposedResolutionRecord(
                tickMs: 10,
                deviceKey: "PBME",
                surfaceWinners: new List<SurfaceWinner>(),
                carrierStatuses: new List<CarrierResolutionStatus>(),
                carrierSnapshots: snaps,
                pageKnowledge: CurrentPageKnowledge.Unknown,
                revertedThisTick: false,
                adoptWarnedThisTick: false,
                itmDeviceId: 4,
                surfaceHeld: true,
                releaseEdge: true,
                dismissedCarrierIds: new[] { "x", "y" },
                capabilityEnvelope: env);

            var model = DisplayResolutionSnapshotModel.From(record);

            Assert.Equal((byte)4, model.ItmDeviceId);
            Assert.True(model.SurfaceHeld);
            Assert.True(model.ReleaseEdge);
            Assert.Equal(new[] { "x", "y" }, model.DismissedCarrierIds);
            Assert.True(model.HasCapabilityEnvelope);
            Assert.Equal(4, model.CapabilityEnvelope.FieldParamCount);
            Assert.True(model.CapabilityEnvelope.ScreenLogo);
            Assert.Null(model.CapabilityEnvelope.ScreenBlank);
            Assert.Single(model.CarrierSnapshots);
            Assert.Equal("c1", model.CarrierSnapshots[0].CarrierId);
            Assert.True(model.CarrierSnapshots[0].Active);
            Assert.True(model.CarrierSnapshots[0].FiredThisTick);
            Assert.Equal(400, model.CarrierSnapshots[0].RemainingMs);
        }

        /// <summary>
        /// Flags enums include composite values when members are combined in the
        /// definition; we only require each defined single-bit (or None) name.
        /// </summary>
        private static bool IsSingleBitOrNone(CarrierRowLabels label)
        {
            if (label == CarrierRowLabels.None)
                return true;
            int v = (int)label;
            return v != 0 && (v & (v - 1)) == 0;
        }
    }
}
