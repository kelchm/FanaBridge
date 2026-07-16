using System.Collections.Generic;
using System.Linq;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The presentation catalog (<see cref="ItmDisplayLayout"/>): every page's four-slot
    /// map matches the official quick guide's renders — params in the right slots, the
    /// firmware-exact label strings (colon quirks included), the two dual-slot shapes —
    /// and every standard-catalog parameter has a slot, a label, and a renderer case.
    /// </summary>
    public class ItmDisplayLayoutTests
    {
        private static readonly ItmSlotPosition[] AllPositions =
        {
            ItmSlotPosition.LeftTop, ItmSlotPosition.LeftBottom,
            ItmSlotPosition.RightTop, ItmSlotPosition.RightBottom,
        };

        private static IEnumerable<ItmSlotField> AllFields(ItmPageLayout layout)
            => AllPositions.SelectMany(p => layout.SlotAt(p).Fields);

        // ── Per-page slot maps (labels pinned firmware-exact) ────────────

        [Fact]
        public void LapInfo_SlotsAndLabels()
        {
            var l = ItmDisplayLayout.For(ItmPage.LapInfo);
            Assert.True(l.HasSlots);
            Assert.Equal("LAPS:", l.LeftTop.Label);
            Assert.Equal(ItmParam.Lap, Assert.Single(l.LeftTop.Fields).ParamId);
            Assert.Equal("POSITION:", l.LeftBottom.Label);
            Assert.Equal(ItmParam.Position, Assert.Single(l.LeftBottom.Fields).ParamId);
            Assert.Equal("CURRENT LAP:", l.RightTop.Label);
            Assert.Equal(ItmParam.LapTime, Assert.Single(l.RightTop.Fields).ParamId);
            // Page 1's LAST LAP carries a colon (page 4's does not).
            Assert.Equal("LAST LAP:", l.RightBottom.Label);
            Assert.Equal(ItmParam.LastLapTime, Assert.Single(l.RightBottom.Fields).ParamId);
        }

        [Fact]
        public void FuelErsDrs_SlotsAndLabels_IncludingTheDualDrsAndTitleCaseDelta()
        {
            var l = ItmDisplayLayout.For(ItmPage.FuelErsDrs);
            Assert.Equal("FUEL:", l.LeftTop.Label);
            Assert.Equal(ItmParam.Fuel, Assert.Single(l.LeftTop.Fields).ParamId);
            Assert.Equal("ERS:", l.LeftBottom.Label);
            Assert.Equal(ItmParam.ErsLevel, Assert.Single(l.LeftBottom.Fields).ParamId);

            // Dual: one shared label over the two DRS dots — zone left, active right.
            Assert.True(l.RightTop.IsDual);
            Assert.Equal("DRS: ZONE / ACTIVE", l.RightTop.Label);
            Assert.Equal(ItmParam.DrsZone, l.RightTop.Fields[0].ParamId);
            Assert.Equal(ItmParam.DrsActive, l.RightTop.Fields[1].ParamId);
            Assert.All(l.RightTop.Fields, f => Assert.Null(f.Label));

            // The one title-case label on the display.
            Assert.Equal("Delta:", l.RightBottom.Label);
            Assert.Equal(ItmParam.DeltaOwnBest, Assert.Single(l.RightBottom.Fields).ParamId);
        }

        [Fact]
        public void CarSettings_SlotsAndLabels_IncludingTheDualTcAbs()
        {
            var l = ItmDisplayLayout.For(ItmPage.CarSettings);

            // Dual: TC and ABS side by side, individually labeled, no colons.
            Assert.True(l.LeftTop.IsDual);
            Assert.Null(l.LeftTop.Label);
            Assert.Equal(ItmParam.TcSetting, l.LeftTop.Fields[0].ParamId);
            Assert.Equal("TC", l.LeftTop.Fields[0].Label);
            Assert.Equal(ItmParam.AbsSetting, l.LeftTop.Fields[1].ParamId);
            Assert.Equal("ABS", l.LeftTop.Fields[1].Label);

            Assert.Equal("ENGINE MAP:", l.LeftBottom.Label);
            Assert.Equal(ItmParam.EngineMapping, Assert.Single(l.LeftBottom.Fields).ParamId);
            Assert.Equal("OIL TEMP:", l.RightTop.Label);
            Assert.Equal(ItmParam.OilTemp, Assert.Single(l.RightTop.Fields).ParamId);
            Assert.Equal("BRAKE BIAS:", l.RightBottom.Label);
            Assert.Equal(ItmParam.BrakeBias, Assert.Single(l.RightBottom.Fields).ParamId);
        }

        [Fact]
        public void LapTimes_SlotsAndLabels_TopLeftHasNoColon()
        {
            var l = ItmDisplayLayout.For(ItmPage.LapTimes);
            // The quirk: page 4's top-left LAST LAP has NO trailing colon.
            Assert.Equal("LAST LAP", l.LeftTop.Label);
            Assert.Equal(ItmParam.LastLapTime, Assert.Single(l.LeftTop.Fields).ParamId);
            Assert.Equal("BEST LAP:", l.LeftBottom.Label);
            Assert.Equal(ItmParam.BestLapTime, Assert.Single(l.LeftBottom.Fields).ParamId);
            Assert.Equal("CAR AHEAD:", l.RightTop.Label);
            Assert.Equal(ItmParam.CarAhead, Assert.Single(l.RightTop.Fields).ParamId);
            Assert.Equal("CAR BEHIND:", l.RightBottom.Label);
            Assert.Equal(ItmParam.CarBehind, Assert.Single(l.RightBottom.Fields).ParamId);
        }

        [Fact]
        public void TyreTemps_SlotsAndLabels()
        {
            var l = ItmDisplayLayout.For(ItmPage.TyreTemps);
            Assert.Equal("FL TIRE TEMP:", l.LeftTop.Label);
            Assert.Equal(ItmParam.TyreFlTemp, Assert.Single(l.LeftTop.Fields).ParamId);
            Assert.Equal("RL TIRE TEMP:", l.LeftBottom.Label);
            Assert.Equal(ItmParam.TyreRlTemp, Assert.Single(l.LeftBottom.Fields).ParamId);
            Assert.Equal("FR TIRE TEMP:", l.RightTop.Label);
            Assert.Equal(ItmParam.TyreFrTemp, Assert.Single(l.RightTop.Fields).ParamId);
            Assert.Equal("RR TIRE TEMP:", l.RightBottom.Label);
            Assert.Equal(ItmParam.TyreRrTemp, Assert.Single(l.RightBottom.Fields).ParamId);
        }

        [Fact]
        public void Legacy_HasNoSlots()
        {
            var l = ItmDisplayLayout.For(ItmPage.Legacy);
            Assert.False(l.HasSlots);
            Assert.Null(l.LeftTop);
            Assert.Null(l.LeftBottom);
            Assert.Null(l.RightTop);
            Assert.Null(l.RightBottom);
        }

        // ── Catalog coverage ─────────────────────────────────────────────

        [Fact]
        public void EveryStandardCatalogParam_HasExactlyOneSlot_WithALabel_AndARendererCase()
        {
            foreach (var info in ItmDeviceCatalog.PagesFor(ItmEncoder.DefaultDeviceId))
            {
                var layout = ItmDisplayLayout.For(info.Page);
                if (info.IsLegacy)
                {
                    Assert.False(layout.HasSlots);
                    continue;
                }

                Assert.True(layout.HasSlots);
                var fieldParams = AllFields(layout).Select(f => f.ParamId).ToList();

                // The slots carry exactly the page's params, minus the persistent
                // SPEED/GEAR center zone — each exactly once.
                var expected = info.Params
                    .Where(p => p != ItmParam.Speed && p != ItmParam.Gear).ToList();
                Assert.Equal(expected.OrderBy(p => p), fieldParams.OrderBy(p => p));
                Assert.Equal(fieldParams.Count, fieldParams.Distinct().Count());

                foreach (var pos in AllPositions)
                {
                    var slot = layout.SlotAt(pos);
                    Assert.NotNull(slot);
                    Assert.InRange(slot.Fields.Count, 1, 2);
                    foreach (var field in slot.Fields)
                    {
                        // Every field is labeled — on the slot or on the field itself.
                        Assert.False(string.IsNullOrEmpty(slot.Label ?? field.Label));
                        // And every field renders: a value and a placeholder.
                        Assert.False(string.IsNullOrEmpty(
                            ItmValueRenderer.Render(field.ParamId, ItmValue.UInt8(0, field.ParamId, 1))));
                        Assert.False(string.IsNullOrEmpty(ItmValueRenderer.Placeholder(field.ParamId)));
                    }
                }

                // SPEED/GEAR render too (the center zone).
                Assert.False(string.IsNullOrEmpty(
                    ItmValueRenderer.Render(ItmParam.Speed, ItmValue.Int16(0, ItmParam.Speed, 1))));
                Assert.False(string.IsNullOrEmpty(
                    ItmValueRenderer.Render(ItmParam.Gear, ItmValue.UInt8(1, ItmParam.Gear, 1))));
            }
        }

        [Fact]
        public void BentleyPageSet_ResolvesToTheSameLayouts_WithoutCarSettings()
        {
            var pages = ItmDeviceCatalog.PagesFor(4);   // Bentley GT3
            Assert.DoesNotContain(pages, p => p.Page == ItmPage.CarSettings);

            foreach (var info in pages)
            {
                var layout = ItmDisplayLayout.For(info.Page);
                Assert.Equal(info.Page, layout.Page);
                Assert.Equal(!info.IsLegacy, layout.HasSlots);
                // Layouts are keyed by content identity, so the renumbered wire pages
                // resolve to exactly the standard layouts.
                Assert.Same(layout, ItmDisplayLayout.For(info.Page));
            }
        }
    }
}
