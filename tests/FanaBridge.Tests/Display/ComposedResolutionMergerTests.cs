using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>Phase E7 G: composed-resolution merge (contract §6.1) + one-tick golden.</summary>
    public class ComposedResolutionMergerTests
    {
        private static CarrierTickSnapshot Snap(string id, bool active = true)
            => new CarrierTickSnapshot(
                id, active, active, false, false, false, true, 0, null);

        private static ComposedResolutionRecord SeatSlice()
            => new ComposedResolutionRecord(
                tickMs: 1000,
                deviceKey: "pbme",
                surfaceWinners: new[]
                {
                    new SurfaceWinner("display", "e-pit", "hosted:p-pit"),
                },
                carrierStatuses: new[]
                {
                    new CarrierResolutionStatus(
                        "e-pit", "display", "hosted:p-pit",
                        CarrierPresence.OnScreen, remainingMs: 4000, CarrierRowLabels.None),
                    new CarrierResolutionStatus(
                        "l-pit", "page:p-alerts", "hosted:p-pit",
                        presence: null, remainingMs: null, CarrierRowLabels.Dismissed),
                },
                carrierSnapshots: new[] { Snap("e-pit"), Snap("l-pit") });

        private static ComposedResolutionRecord FrameSlice()
            => new ComposedResolutionRecord(
                tickMs: 1000,
                deviceKey: "pbme",
                surfaceWinners: new[]
                {
                    new SurfaceWinner("page:p-alerts", "l-pit", "hosted:p-pit"),
                    new SurfaceWinner("field:42", "o-fl", "itm:tyreTemps"),
                },
                carrierStatuses: new[]
                {
                    new CarrierResolutionStatus(
                        "l-pit", "page:p-alerts", "hosted:p-pit",
                        CarrierPresence.OnScreen, remainingMs: null, CarrierRowLabels.None),
                    new CarrierResolutionStatus(
                        "o-fl", "field:42", "itm:tyreTemps",
                        CarrierPresence.OnScreen, remainingMs: null, CarrierRowLabels.None),
                },
                carrierSnapshots: new[] { Snap("l-pit"), Snap("o-fl") });

        private static ComposedResolutionRecord WheelSlice()
            => new ComposedResolutionRecord(
                tickMs: 1000,
                deviceKey: "pbme",
                surfaceWinners: new[]
                {
                    new SurfaceWinner(DestinationIds.WheelScreenSurfaceId, "ws-logo", "screen:logo"),
                },
                carrierStatuses: new[]
                {
                    new CarrierResolutionStatus(
                        "ws-logo", DestinationIds.WheelScreenSurfaceId, "screen:logo",
                        CarrierPresence.OnScreen, remainingMs: null, CarrierRowLabels.None),
                },
                carrierSnapshots: new[] { Snap("ws-logo") });

        [Fact]
        public void OneTickGolden_E4E5E6_SameTickMerge()
        {
            var conflicts = new List<string>();
            var merged = ComposedResolutionMerger.Merge(
                SeatSlice(), FrameSlice(), WheelSlice(), conflicts.Add);

            Assert.Equal(1000, merged.TickMs);
            Assert.Equal("pbme", merged.DeviceKey);
            Assert.Empty(conflicts);

            Assert.Equal(4, merged.SurfaceWinners.Count);
            Assert.Contains(merged.SurfaceWinners, w => w.SurfaceId == "display");
            Assert.Contains(merged.SurfaceWinners, w => w.SurfaceId == "page:p-alerts");
            Assert.Contains(merged.SurfaceWinners, w => w.SurfaceId == "field:42");
            Assert.Contains(merged.SurfaceWinners,
                w => w.SurfaceId == DestinationIds.WheelScreenSurfaceId);

            var lPit = merged.CarrierStatuses.Single(
                s => s.CarrierId == "l-pit" && s.SurfaceId == "page:p-alerts");
            Assert.Equal(CarrierPresence.OnScreen, lPit.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed, lPit.RowLabels);

            Assert.Equal(4, merged.CarrierSnapshots.Count);
        }

        [Fact]
        public void PresenceConflict_FailsDeterministically()
        {
            var a = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "x", "p") },
                new[]
                {
                    new CarrierResolutionStatus(
                        "x", "display", "p", CarrierPresence.OnScreen, null, CarrierRowLabels.None),
                },
                new[] { Snap("x") });
            var b = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "x", "p") },
                new[]
                {
                    new CarrierResolutionStatus(
                        "x", "display", "p", CarrierPresence.OffScreen, null, CarrierRowLabels.None),
                },
                new[] { Snap("x") });
            var conflicts = new List<string>();
            var ex = Assert.Throws<ComposedResolutionMerger.MergeConflictException>(
                () => ComposedResolutionMerger.Merge(a, b, null, conflicts.Add));
            Assert.Contains("presence conflict", ex.Message);
            Assert.NotEmpty(conflicts);
        }

        [Fact]
        public void DestinationId_OwnerByPrefix_FrameWinsOnFieldSurface()
        {
            // E4 stamps field: row with hosted:wrong; E5 owns field: and supplies itm:tyreTemps.
            var seat = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "rest", "rest:inSession") },
                new[]
                {
                    new CarrierResolutionStatus(
                        "child", "field:42", "hosted:wrong",
                        presence: null, null, CarrierRowLabels.Dismissed),
                },
                new[] { Snap("child") });
            var frame = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("field:42", "child", "itm:tyreTemps") },
                new[]
                {
                    new CarrierResolutionStatus(
                        "child", "field:42", "itm:tyreTemps",
                        CarrierPresence.OnScreen, null, CarrierRowLabels.None),
                },
                new[] { Snap("child") });
            var conflicts = new List<string>();
            var merged = ComposedResolutionMerger.Merge(seat, frame, null, conflicts.Add);
            var row = merged.CarrierStatuses.Single();
            Assert.Equal("itm:tyreTemps", row.DestinationId);
            Assert.Contains(conflicts, c => c.Contains("DestinationId ownership conflict"));
        }

        [Fact]
        public void DuplicateSurfaceWinner_FailsDeterministically()
        {
            var a = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "a", "p1") },
                Array.Empty<CarrierResolutionStatus>(),
                Array.Empty<CarrierTickSnapshot>());
            // Second seat-owned surface is impossible via normal emit; force two seat slices
            // by merging seat with a fake that also claims display with different winner.
            // Use two records both claiming display as seat-owned — second ingest from
            // a "seat" path isn't available; instead inject via wheelScreen wrongly claiming
            // display is soft-skipped. Force via two frames? Frame is not owner of display.
            // Build two seat-like records by calling Merge with seat + a second seat as frame
            // won't work. Directly construct conflict by having frame claim display with
            // different winner — non-owner is skipped. Need same owner twice.
            // Workaround: merge seat with another seat-shaped record as "seat" only once.
            // Use two SurfaceWinners on same surface inside one record? Ingest skips duplicates
            // from same emitter. So we need two emitters both owner of same surface — only
            // possible if OwnerForSurface returns same for two emitters incorrectly.
            // Alternative: same surface from seat twice via two Merge calls isn't the API.
            // Spec: "duplicate SurfaceWinners assert like presence". Emit seat + a fabricated
            // record as wheel that wrongly uses surface "display" is skipped (wrong owner).
            // Emit seat + frame both with surface "field:1" different winners — frame is owner,
            // seat winner for field: is skipped (non-owner). No conflict.
            // To trigger: two frame-owned winners for same field from frame + wheel both
            // claiming field: — wheel is non-owner, skipped.
            // The only way is same owner appearing twice. Seat ingested once. Unless we
            // put display winners in seat and also pass seat as wheelScreen after changing
            // owner map... Owner for display is seat only.
            // Practical fixture: use reflection-free approach — two records both as seat isn't
            // possible. Make OwnerForSurface return null for unknown → first wins silently.
            // Looking at code: if owner is null, first wins without conflict.
            // For known surface with same owner: only one ingest path per emitter name.
            // I'll test OwnerForSurface + a synthetic path where seat and a second seat-like
            // record... Actually re-read Ingest: winnerOwner tracks emitter string. If I
            // could ingest seat twice... I can't via Merge.
            // Fix production: also fail when existing winner differs even from same surface
            // when second is also claimed by owner. Currently second seat never arrives.
            //
            // Alternative fixture: frame has two SurfaceWinners with same SurfaceId in one list.
            // First wins, second is same emitter — SurfaceWinnerEqual check: if different
            // carrier, Fail only when different emitters. Same emitter continues.
            //
            // Update production to also assert when same surface appears twice in one list
            // with disagreeing winners? Or accept that cross-emitter is the wiring bug.
            //
            // Cross-emitter same surface: seat claims "display", and we also have a second
            // "seat" if we pass seat as frame... frame owner for display is not seat so
            // skipped.
            //
            // I'll add a package-internal test via two wheelScreen and frame both for
            // wheelScreen surface with different winners — wheel is owner, frame is not
            // → frame skipped.
            //
            // Only path: Merge(seat1, null, seat2) where both claim display — but
            // wheelScreen emitter name is "wheelScreen", and display owner is "seat",
            // so seat2 as wheelScreen is skipped as non-owner.
            //
            // Change: when non-owner supplies a SurfaceWinner for a surface that already
            // has an owner entry with DIFFERENT value — that is the conflict for winners.
            // Currently non-owner is skipped before existing check when !ContainsKey...
            // When ContainsKey, different emitter + !Equal → Fail. So:
            // seat claims display/a/p1. Then wheelScreen also claims display/b/p2 —
            // wheel is non-owner so continue at "if owner != emitter continue" BEFORE
            // the ContainsKey path... wait, order is:
            // if ContainsKey → conflict check
            // else if owner != emitter → skip
            // else add
            // So non-owner when key already present hits ContainsKey first → Fail if unequal.
            var seat = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "a", "p1") },
                Array.Empty<CarrierResolutionStatus>(),
                Array.Empty<CarrierTickSnapshot>());
            var wheel = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "b", "p2") },
                Array.Empty<CarrierResolutionStatus>(),
                Array.Empty<CarrierTickSnapshot>());
            var conflicts = new List<string>();
            var ex = Assert.Throws<ComposedResolutionMerger.MergeConflictException>(
                () => ComposedResolutionMerger.Merge(seat, null, wheel, conflicts.Add));
            Assert.Contains("duplicate SurfaceWinner", ex.Message);
        }

        [Fact]
        public void NullSlices_Skipped()
        {
            var merged = ComposedResolutionMerger.Merge(SeatSlice(), null, null);
            Assert.Equal(1000, merged.TickMs);
            Assert.Single(merged.SurfaceWinners);
        }

        [Fact]
        public void LabelsUnion_WithoutPresenceSteal()
        {
            var seat = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("display", "rest", "rest:inSession") },
                new[]
                {
                    new CarrierResolutionStatus(
                        "child", "field:1", "itm:x",
                        presence: null, null, CarrierRowLabels.Dismissed),
                },
                new[] { Snap("child") });
            var frame = new ComposedResolutionRecord(
                1, "d",
                new[] { new SurfaceWinner("field:1", "child", "itm:x") },
                new[]
                {
                    new CarrierResolutionStatus(
                        "child", "field:1", "itm:x",
                        CarrierPresence.OffScreen, null, CarrierRowLabels.None),
                },
                new[] { Snap("child") });
            var merged = ComposedResolutionMerger.Merge(seat, frame, null);
            var row = merged.CarrierStatuses.Single();
            Assert.Equal(CarrierPresence.OffScreen, row.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed, row.RowLabels);
            Assert.Equal("itm:x", row.DestinationId); // frame owner
        }

        [Fact]
        public void DeviceBlock_ConnectBeforeAnnouncement_HonestlyUnknown()
        {
            var seat = new ComposedResolutionRecord(
                1, "d",
                Array.Empty<SurfaceWinner>(),
                Array.Empty<CarrierResolutionStatus>(),
                Array.Empty<CarrierTickSnapshot>(),
                CurrentPageKnowledge.Unknown,
                revertedThisTick: false,
                adoptWarnedThisTick: false);
            var merged = ComposedResolutionMerger.Merge(seat, null, null);
            Assert.True(merged.HasDeviceBlock);
            Assert.False(merged.PageKnowledge.IsKnown);
            Assert.False(merged.RevertedThisTick);
            Assert.False(merged.AdoptWarnedThisTick);
        }
    }
}
