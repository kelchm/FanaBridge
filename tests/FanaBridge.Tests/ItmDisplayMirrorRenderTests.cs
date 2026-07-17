using System;
using System.Linq;
using FanaBridge.Adapters;
using FanaBridge.Display.Session;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;
using FanaBridge.UI;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The mirror control's render model (<see cref="ItmDisplayMirrorRender"/>): slot
    /// view-model building from a values snapshot, the state fallbacks (live twin /
    /// legacy caption / dimmed empty panel), the header state caption, the page caption
    /// with its status-line fallback, and the gear glyph's segment bits. The WPF control
    /// itself only draws this model; visual QA is out of scope here.
    /// </summary>
    public class ItmDisplayMirrorRenderTests
    {
        // ── Snapshot builders ────────────────────────────────────────────

        private static DisplayValueSlot Slot(string? label, params DisplayValueField[] fields)
            => new DisplayValueSlot(label!, fields);

        private static DisplayValueField Field(ushort paramId, string value, string? label = null)
            => new DisplayValueField(paramId, label!, value);

        private static DisplayValuesSnapshot Snapshot(
            ItmPage? page = ItmPage.LapInfo, byte wirePage = 1, string? pageName = "Lap Info",
            ItmLifecycleState state = ItmLifecycleState.Synced, bool placeholders = false,
            DisplayValueSlot? leftTop = null, DisplayValueSlot? leftBottom = null,
            DisplayValueSlot? rightTop = null, DisplayValueSlot? rightBottom = null,
            string? gear = "6", string? speed = "268")
            => new DisplayValuesSnapshot(page, wirePage, pageName, state, placeholders,
                leftTop, leftBottom, rightTop, rightBottom, gear, speed,
                composedAtMs: 1000, composedAtUtc: DateTime.UtcNow);

        // The quick guide's page-1 render as a snapshot.
        private static DisplayValuesSnapshot LapInfoSnapshot()
            => Snapshot(
                leftTop: Slot("LAPS:", Field(ItmParam.Lap, "15 /73")),
                leftBottom: Slot("POSITION:", Field(ItmParam.Position, "02 /20")),
                rightTop: Slot("CURRENT LAP:", Field(ItmParam.LapTime, "01:36.911")),
                rightBottom: Slot("LAST LAP:", Field(ItmParam.LastLapTime, "02:14.169")));

        // ── Build: the live twin ─────────────────────────────────────────

        [Fact]
        public void Build_SyncedWithValues_IsTheLiveTwin()
        {
            var model = ItmDisplayMirrorRender.Build(LapInfoSnapshot());

            Assert.Equal(MirrorPanelState.Live, model.PanelState);
            Assert.Equal(4, model.Slots.Count);
            Assert.Equal("6", model.GearText);
            Assert.Equal("268", model.SpeedText);

            var leftTop = model.Slots.Single(s => s.Position == ItmSlotPosition.LeftTop);
            Assert.Equal("LAPS:", leftTop.Label);
            Assert.False(leftTop.IsDual);
            Assert.Equal("15 /73", leftTop.Fields[0].Value);
            Assert.Equal(ItmParam.Lap, leftTop.Fields[0].ParamId);
            Assert.False(leftTop.Fields[0].IsDot);

            var rightBottom = model.Slots.Single(s => s.Position == ItmSlotPosition.RightBottom);
            Assert.Equal("LAST LAP:", rightBottom.Label);
            Assert.Equal("02:14.169", rightBottom.Fields[0].Value);
        }

        [Fact]
        public void Build_PlaceholderValues_PassThroughUnchanged()
        {
            // Post-reset/idle: the snapshot's fields already carry the dash
            // placeholders; the model shows them as-is (same live layout).
            var snapshot = Snapshot(placeholders: true,
                leftTop: Slot("LAPS:", Field(ItmParam.Lap, "--- / -")),
                rightTop: Slot("CURRENT LAP:", Field(ItmParam.LapTime, "--:--.-")),
                gear: "-", speed: "---");
            var model = ItmDisplayMirrorRender.Build(snapshot);

            Assert.Equal(MirrorPanelState.Live, model.PanelState);
            Assert.Equal("--- / -",
                model.Slots.Single(s => s.Position == ItmSlotPosition.LeftTop).Fields[0].Value);
            Assert.Equal("-", model.GearText);
            Assert.Equal("---", model.SpeedText);
        }

        [Fact]
        public void Build_DrsDualSlot_BecomesDots()
        {
            var snapshot = Snapshot(page: ItmPage.FuelErsDrs, wirePage: 2, pageName: "Fuel / ERS / DRS",
                rightTop: Slot("DRS: ZONE / ACTIVE",
                    Field(ItmParam.DrsZone, ItmValueRenderer.DrsDotOn),
                    Field(ItmParam.DrsActive, ItmValueRenderer.DrsDotOff)));
            var model = ItmDisplayMirrorRender.Build(snapshot);

            var slot = model.Slots.Single(s => s.Position == ItmSlotPosition.RightTop);
            Assert.Equal("DRS: ZONE / ACTIVE", slot.Label);
            Assert.True(slot.IsDual);
            Assert.True(slot.Fields[0].IsDot);
            Assert.True(slot.Fields[0].DotFilled);    // zone: filled
            Assert.True(slot.Fields[1].IsDot);
            Assert.False(slot.Fields[1].DotFilled);   // active: hollow
        }

        [Fact]
        public void Build_TcAbsDualSlot_KeepsPerFieldLabels()
        {
            var snapshot = Snapshot(page: ItmPage.CarSettings, wirePage: 3, pageName: "Car Settings",
                leftTop: Slot(null,
                    Field(ItmParam.TcSetting, "08", "TC"),
                    Field(ItmParam.AbsSetting, "12", "ABS")));
            var model = ItmDisplayMirrorRender.Build(snapshot);

            var slot = model.Slots.Single(s => s.Position == ItmSlotPosition.LeftTop);
            Assert.Null(slot.Label);
            Assert.True(slot.IsDual);
            Assert.Equal("TC", slot.Fields[0].Label);
            Assert.Equal("08", slot.Fields[0].Value);
            Assert.Equal("ABS", slot.Fields[1].Label);
            Assert.Equal("12", slot.Fields[1].Value);
            Assert.All(slot.Fields, f => Assert.False(f.IsDot));
        }

        // ── Build: the fallback states ───────────────────────────────────

        [Fact]
        public void Build_LegacyPage_ShowsTheLegacyCaption()
        {
            var model = ItmDisplayMirrorRender.Build(Snapshot(
                page: ItmPage.Legacy, wirePage: 6, pageName: "Legacy",
                gear: null, speed: null));
            Assert.Equal(MirrorPanelState.Legacy, model.PanelState);
            Assert.Empty(model.Slots);
        }

        [Fact]
        public void Build_NullSnapshot_IsTheEmptyPanel()
        {
            var model = ItmDisplayMirrorRender.Build(null);
            Assert.Equal(MirrorPanelState.Empty, model.PanelState);
            Assert.Empty(model.Slots);
            Assert.Null(model.GearText);
        }

        [Theory]
        [InlineData(ItmLifecycleState.Idle)]
        [InlineData(ItmLifecycleState.Disabled)]
        [InlineData(ItmLifecycleState.BringUp)]
        [InlineData(ItmLifecycleState.AwaitPush)]
        [InlineData(ItmLifecycleState.Switching)]
        [InlineData(ItmLifecycleState.Recovery)]
        [InlineData(ItmLifecycleState.Unavailable)]
        public void Build_NotSynced_IsTheEmptyPanel_EvenWithSlotData(ItmLifecycleState state)
        {
            // Suspended/off states keep the panel clean; the header caption explains.
            var snapshot = Snapshot(state: state,
                leftTop: Slot("LAPS:", Field(ItmParam.Lap, "15 /73")));
            Assert.Equal(MirrorPanelState.Empty,
                ItmDisplayMirrorRender.Build(snapshot).PanelState);
        }

        [Fact]
        public void Build_SyncedButNoPageAdopted_IsTheEmptyPanel()
        {
            var model = ItmDisplayMirrorRender.Build(Snapshot(
                page: null, wirePage: 0, pageName: null, gear: null, speed: null));
            Assert.Equal(MirrorPanelState.Empty, model.PanelState);
        }

        // ── State caption (card header, outside the panel) ───────────────

        [Theory]
        [InlineData(ItmLifecycleState.Idle, "ITM idle")]
        [InlineData(ItmLifecycleState.Disabled, "ITM off")]
        [InlineData(ItmLifecycleState.BringUp, "Bringing up…")]
        [InlineData(ItmLifecycleState.AwaitPush, "Bringing up…")]
        [InlineData(ItmLifecycleState.Switching, "Switching page…")]
        [InlineData(ItmLifecycleState.Recovery, "Recovering…")]
        [InlineData(ItmLifecycleState.Unavailable, "Display unavailable")]
        public void StateCaption_MapsTheLifecycleStates(ItmLifecycleState state, string expected)
        {
            Assert.Equal(expected,
                ItmDisplayMirrorRender.StateCaption(Snapshot(state: state)));
        }

        [Fact]
        public void StateCaption_SyncedIsSilent_NullSnapshotReadsOff()
        {
            Assert.Null(ItmDisplayMirrorRender.StateCaption(LapInfoSnapshot()));
            Assert.Equal("ITM off", ItmDisplayMirrorRender.StateCaption(null));
        }

        [Fact]
        public void StateCaption_SyncedOnUncatalogedPage_ExplainsTheEmptyPanel()
        {
            // Synced on a page outside the catalog (the firmware knows pages we
            // don't): the hardware shows values but the twin has no layout, so the
            // panel renders Empty — the header caption must say why rather than
            // sit blank next to the LIVE dot.
            var uncataloged = Snapshot(page: null, wirePage: 9, pageName: "Page 9",
                gear: null, speed: null);
            Assert.Equal(MirrorPanelState.Empty,
                ItmDisplayMirrorRender.Build(uncataloged).PanelState);
            Assert.Equal("Unrecognized page",
                ItmDisplayMirrorRender.StateCaption(uncataloged));

            // Same when not even a wire page is known.
            Assert.Equal("Unrecognized page",
                ItmDisplayMirrorRender.StateCaption(
                    Snapshot(page: null, wirePage: 0, pageName: null,
                        gear: null, speed: null)));
        }

        // ── Page caption (below the panel) ───────────────────────────────

        [Fact]
        public void PageCaption_ComesFromTheValuesSnapshot()
        {
            Assert.Equal("Page 1 · Lap Info",
                ItmDisplayMirrorRender.PageCaption(LapInfoSnapshot(), null, itmDeviceId: 3));
            Assert.Equal("Page 6 · Legacy",
                ItmDisplayMirrorRender.PageCaption(
                    Snapshot(page: ItmPage.Legacy, wirePage: 6, pageName: "Legacy"),
                    null, itmDeviceId: 3));
        }

        [Fact]
        public void PageCaption_UncatalogedPage_ReadsHonestlyWithoutDoubling()
        {
            Assert.Equal("Page 9",
                ItmDisplayMirrorRender.PageCaption(
                    Snapshot(page: null, wirePage: 9, pageName: "Page 9"),
                    null, itmDeviceId: 3));
        }

        [Fact]
        public void PageCaption_NoValuesSnapshot_FallsBackToTheStatusLine()
        {
            // The pre-mirror status-line path keeps working when the driver publishes
            // no values snapshot (and when it exists but knows no page yet).
            Assert.Equal("Page 4 · Lap Times",
                ItmDisplayMirrorRender.PageCaption(null, "Synced — page 4, 6 params", 3));
            Assert.Equal("ITM off",
                ItmDisplayMirrorRender.PageCaption(null, null, 3));
            Assert.Equal("Bringing up…",
                ItmDisplayMirrorRender.PageCaption(
                    Snapshot(page: null, wirePage: 0, pageName: null,
                        state: ItmLifecycleState.BringUp),
                    "BringUp", 3));
        }

        // ── Dual-slot hit-region split (the piece-5 click hook) ─────────
        //
        // The boundary between a dual slot's two hit regions must sit between the
        // fields AS DRAWN, not at the quadrant midpoint — the right-aligned DRS ZONE
        // dot (center x=802) is drawn past the right quadrant's midpoint (x=800), so
        // an equal split would report DrsActive for clicks on the ZONE dot.

        private static MirrorSlotModel DotSlot() => new MirrorSlotModel
        {
            Position = ItmSlotPosition.RightTop,
            Fields =
            {
                new MirrorFieldModel { ParamId = ItmParam.DrsZone, IsDot = true },
                new MirrorFieldModel { ParamId = ItmParam.DrsActive, IsDot = true },
            },
        };

        private static MirrorSlotModel TcAbsSlot() => new MirrorSlotModel
        {
            Position = ItmSlotPosition.LeftTop,
            Fields =
            {
                new MirrorFieldModel { ParamId = ItmParam.TcSetting, Label = "TC" },
                new MirrorFieldModel { ParamId = ItmParam.AbsSetting, Label = "ABS" },
            },
        };

        [Fact]
        public void DualHitSplit_DrsDots_FallsBetweenTheDrawnDotCenters()
        {
            // Right zone (page 2 RT): dots drawn at centers x=802 and x=920 — the
            // split is midway (861), so both dots hit their own field.
            Assert.Equal(861, ItmDisplayMirror.DualHitSplit(DotSlot(), isRight: true));
            // Left-aligned variant: centers x=80 and x=198 → split at 139.
            Assert.Equal(139, ItmDisplayMirror.DualHitSplit(DotSlot(), isRight: false));
        }

        [Fact]
        public void DualHitSplit_TcAbs_FallsBetweenTheDrawnColumns()
        {
            // Left zone (page 3 LT): TC drawn at x 50–180, ABS at x 185–315 — the
            // split (182.5) sits in the gap, so the ABS label's left edge stays ABS.
            Assert.Equal(182.5, ItmDisplayMirror.DualHitSplit(TcAbsSlot(), isRight: false));
            // Right-aligned variant: columns at x 685–815 and 820–950 → split 817.5.
            Assert.Equal(817.5, ItmDisplayMirror.DualHitSplit(TcAbsSlot(), isRight: true));
        }

        // ── Gear glyph segment bits ──────────────────────────────────────

        [Theory]
        [InlineData("6", SevenSegment.Digit6)]
        [InlineData("2", SevenSegment.Digit2)]
        [InlineData("N", SevenSegment.N)]
        [InlineData("R", SevenSegment.R)]
        [InlineData("-", SevenSegment.Dash)]
        [InlineData("", SevenSegment.Blank)]
        [InlineData(null, SevenSegment.Blank)]
        public void GearSegmentBits_LightTheSharedPatterns(string? gear, byte expected)
        {
            Assert.Equal(expected, ItmDisplayMirrorRender.GearSegmentBits(gear));
        }
    }
}
