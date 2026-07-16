using System;
using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>The four field slots of an ITM telemetry page, as the display lays them
    /// out: two stacked slots left of the gear/speed center zone, two right of it.</summary>
    public enum ItmSlotPosition
    {
        LeftTop,
        LeftBottom,
        RightTop,
        RightBottom,
    }

    /// <summary>
    /// One parameter within a display slot: the wire parameter id and, for dual slots
    /// whose fields are labeled individually (TC / ABS), that field's own label.
    /// Single-parameter slots carry their label on <see cref="ItmDisplaySlot.Label"/>
    /// and leave this null.
    /// </summary>
    public readonly struct ItmSlotField
    {
        /// <summary>The ITM wire parameter id shown in this field (see <see cref="ItmParam"/>).</summary>
        public ushort ParamId { get; }

        /// <summary>The field's own label (dual TC/ABS style), or null when the slot-level
        /// label covers every field in the slot.</summary>
        public string Label { get; }

        public ItmSlotField(ushort paramId, string label = null)
        {
            ParamId = paramId;
            Label = label;
        }
    }

    /// <summary>
    /// One field slot of an ITM page: a label line and one or two parameters rendered
    /// beneath it. Dual slots come in two shapes, both taken from the official quick
    /// guide renders: one shared label over two values (DRS zone + active dots), or two
    /// individually-labeled values side by side (TC + ABS).
    /// </summary>
    public sealed class ItmDisplaySlot
    {
        /// <summary>The slot-level label — the firmware-exact string, including its quirks
        /// (some labels carry no trailing colon; one is title-case). Null when each field
        /// carries its own label instead (<see cref="ItmSlotField.Label"/>).</summary>
        public string Label { get; }

        /// <summary>The parameters shown in this slot, left to right (one or two).</summary>
        public IReadOnlyList<ItmSlotField> Fields { get; }

        /// <summary>True when the slot shows two parameters side by side.</summary>
        public bool IsDual => Fields.Count == 2;

        internal ItmDisplaySlot(string label, params ItmSlotField[] fields)
        {
            Label = label;
            Fields = Array.AsReadOnly(fields);
        }
    }

    /// <summary>
    /// The full four-slot layout of one ITM page. The legacy page has no field slots
    /// (<see cref="HasSlots"/> is false — its 7-segment-style surface is rendered
    /// through a different path entirely).
    /// </summary>
    public sealed class ItmPageLayout
    {
        public ItmPage Page { get; }
        public ItmDisplaySlot LeftTop { get; }
        public ItmDisplaySlot LeftBottom { get; }
        public ItmDisplaySlot RightTop { get; }
        public ItmDisplaySlot RightBottom { get; }

        /// <summary>False for the legacy page, which carries no telemetry field slots.</summary>
        public bool HasSlots => LeftTop != null;

        /// <summary>The slot at a position, or null when the page has no slots.</summary>
        public ItmDisplaySlot SlotAt(ItmSlotPosition position)
        {
            switch (position)
            {
                case ItmSlotPosition.LeftTop: return LeftTop;
                case ItmSlotPosition.LeftBottom: return LeftBottom;
                case ItmSlotPosition.RightTop: return RightTop;
                default: return RightBottom;
            }
        }

        internal ItmPageLayout(ItmPage page, ItmDisplaySlot leftTop = null, ItmDisplaySlot leftBottom = null,
            ItmDisplaySlot rightTop = null, ItmDisplaySlot rightBottom = null)
        {
            Page = page;
            LeftTop = leftTop;
            LeftBottom = leftBottom;
            RightTop = rightTop;
            RightBottom = rightBottom;
        }
    }

    /// <summary>
    /// The presentation catalog for the ITM telemetry pages: which parameter sits in
    /// which of the four field slots, under which label — transcribed field-for-field
    /// from the official quick guide's page renders, label quirks included (page 4's
    /// top-left "LAST LAP" has no colon while page 1's does; page 2's "Delta:" is
    /// title case; TC / ABS carry no colons). This is presentation-layer knowledge
    /// only: the wire-truth parameter order per page stays in
    /// <see cref="ItmTelemetry.ParamsFor"/>, and which pages a device offers stays in
    /// <see cref="ItmDeviceCatalog"/> — layouts are keyed by <see cref="ItmPage"/>
    /// content identity, so a device that renumbers pages (Bentley) resolves through
    /// its catalog entries to the same layouts.
    /// </summary>
    public static class ItmDisplayLayout
    {
        // SPEED and GEAR head every page as the persistent center zone — they never
        // occupy a field slot.

        private static readonly ItmPageLayout LapInfo = new ItmPageLayout(ItmPage.LapInfo,
            leftTop: new ItmDisplaySlot("LAPS:", new ItmSlotField(ItmParam.Lap)),
            leftBottom: new ItmDisplaySlot("POSITION:", new ItmSlotField(ItmParam.Position)),
            rightTop: new ItmDisplaySlot("CURRENT LAP:", new ItmSlotField(ItmParam.LapTime)),
            rightBottom: new ItmDisplaySlot("LAST LAP:", new ItmSlotField(ItmParam.LastLapTime)));

        private static readonly ItmPageLayout FuelErsDrs = new ItmPageLayout(ItmPage.FuelErsDrs,
            leftTop: new ItmDisplaySlot("FUEL:", new ItmSlotField(ItmParam.Fuel)),
            leftBottom: new ItmDisplaySlot("ERS:", new ItmSlotField(ItmParam.ErsLevel)),
            // Dual: one shared label over the two DRS dots (zone left, active right).
            rightTop: new ItmDisplaySlot("DRS: ZONE / ACTIVE",
                new ItmSlotField(ItmParam.DrsZone), new ItmSlotField(ItmParam.DrsActive)),
            // Title case with colon — the one label that isn't ALL CAPS.
            rightBottom: new ItmDisplaySlot("Delta:", new ItmSlotField(ItmParam.DeltaOwnBest)));

        private static readonly ItmPageLayout CarSettings = new ItmPageLayout(ItmPage.CarSettings,
            // Dual: TC and ABS side by side, each with its own (colon-less) label.
            leftTop: new ItmDisplaySlot(null,
                new ItmSlotField(ItmParam.TcSetting, "TC"), new ItmSlotField(ItmParam.AbsSetting, "ABS")),
            leftBottom: new ItmDisplaySlot("ENGINE MAP:", new ItmSlotField(ItmParam.EngineMapping)),
            rightTop: new ItmDisplaySlot("OIL TEMP:", new ItmSlotField(ItmParam.OilTemp)),
            rightBottom: new ItmDisplaySlot("BRAKE BIAS:", new ItmSlotField(ItmParam.BrakeBias)));

        private static readonly ItmPageLayout LapTimes = new ItmPageLayout(ItmPage.LapTimes,
            // No trailing colon here — unlike page 1's "LAST LAP:". Firmware-exact.
            leftTop: new ItmDisplaySlot("LAST LAP", new ItmSlotField(ItmParam.LastLapTime)),
            leftBottom: new ItmDisplaySlot("BEST LAP:", new ItmSlotField(ItmParam.BestLapTime)),
            rightTop: new ItmDisplaySlot("CAR AHEAD:", new ItmSlotField(ItmParam.CarAhead)),
            rightBottom: new ItmDisplaySlot("CAR BEHIND:", new ItmSlotField(ItmParam.CarBehind)));

        private static readonly ItmPageLayout TyreTemps = new ItmPageLayout(ItmPage.TyreTemps,
            leftTop: new ItmDisplaySlot("FL TIRE TEMP:", new ItmSlotField(ItmParam.TyreFlTemp)),
            leftBottom: new ItmDisplaySlot("RL TIRE TEMP:", new ItmSlotField(ItmParam.TyreRlTemp)),
            rightTop: new ItmDisplaySlot("FR TIRE TEMP:", new ItmSlotField(ItmParam.TyreFrTemp)),
            rightBottom: new ItmDisplaySlot("RR TIRE TEMP:", new ItmSlotField(ItmParam.TyreRrTemp)));

        private static readonly ItmPageLayout Legacy = new ItmPageLayout(ItmPage.Legacy);

        /// <summary>
        /// The slot layout for a page's content identity. Every non-legacy catalog page
        /// has one; the legacy page (and any unknown identity) resolves to a layout with
        /// no slots.
        /// </summary>
        public static ItmPageLayout For(ItmPage page)
        {
            switch (page)
            {
                case ItmPage.LapInfo: return LapInfo;
                case ItmPage.FuelErsDrs: return FuelErsDrs;
                case ItmPage.CarSettings: return CarSettings;
                case ItmPage.LapTimes: return LapTimes;
                case ItmPage.TyreTemps: return TyreTemps;
                default: return Legacy;
            }
        }
    }
}
