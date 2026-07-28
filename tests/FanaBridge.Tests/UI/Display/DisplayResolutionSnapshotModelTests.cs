using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Mapping pins for <see cref="DisplayResolutionSnapshotModel"/>: every
    /// <see cref="CarrierPresence"/> and single-bit <see cref="CarrierRowLabels"/>
    /// value maps to its ruled <see cref="DisplayCopy"/> string; device block
    /// passthrough; null record → empty model.
    /// </summary>
    public class DisplayResolutionSnapshotModelTests
    {
        // ── Presence → ruled status ──────────────────────────────────────

        [Theory]
        [InlineData(CarrierPresence.Waiting, DisplayCopy.Waiting)]
        [InlineData(CarrierPresence.Outranked, DisplayCopy.Outranked)]
        [InlineData(CarrierPresence.OffScreen, DisplayCopy.OffScreen)]
        [InlineData(CarrierPresence.OnScreen, DisplayCopy.OnScreen)]
        [InlineData(CarrierPresence.Dismissed, "")]
        public void PresenceCopy_MapsEachCarrierPresence(CarrierPresence presence, string expected)
            => Assert.Equal(expected, DisplayResolutionSnapshotModel.PresenceCopy(presence));

        [Fact]
        public void PresenceCopy_Null_IsEmpty()
            => Assert.Equal(string.Empty, DisplayResolutionSnapshotModel.PresenceCopy(null));

        // ── Row labels → ruled / diagnostics strings ─────────────────────

        [Theory]
        [InlineData(CarrierRowLabels.Off, DisplayCopy.Off)]
        [InlineData(CarrierRowLabels.Dismissed, DisplayCopy.Dismissed)]
        [InlineData(CarrierRowLabels.CantRunHere, DisplayCopy.CantRunHere)]
        [InlineData(CarrierRowLabels.NoWheel, DisplayCopy.NoWheel)]
        [InlineData(CarrierRowLabels.Paused, DisplayCopy.Paused)]
        [InlineData(CarrierRowLabels.KeptAsIs, DisplayCopy.KeptAsIs)]
        [InlineData(CarrierRowLabels.OutOfSessionScope, DisplayCopy.OutOfSessionScope)]
        [InlineData(CarrierRowLabels.Untested, DisplayCopy.Untested)]
        public void RowLabelCopy_MapsEachFlag(CarrierRowLabels label, string expected)
            => Assert.Equal(expected, DisplayResolutionSnapshotModel.RowLabelCopy(label));

        [Fact]
        public void RowLabelCopy_None_IsNull()
            => Assert.Null(DisplayResolutionSnapshotModel.RowLabelCopy(CarrierRowLabels.None));

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
    }
}
