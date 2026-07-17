using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// A digital twin of the ITM OLED: a fixed-aspect (4:1) black panel that renders a
    /// <see cref="DisplayValuesSnapshot"/> the way the hardware shows it — three zones
    /// split by thin dividers at x≈400/600, two stacked field slots per side (labels
    /// over values, left zone left-aligned, right zone right-aligned), and a center
    /// zone with a real segmented gear glyph over the speed. Geometry, typography, and
    /// the per-page slot shapes (dual TC/ABS side by side, the DRS zone/active dots)
    /// follow the official quick guide's page renders; the render decisions live in
    /// <see cref="ItmDisplayMirrorRender"/> (unit-tested), this control only draws.
    ///
    /// Reuse: the Overview card uses it read-only; the Pages &amp; fields editor will
    /// set <see cref="IsInteractive"/> and listen to <see cref="SlotClicked"/> (the hit
    /// regions exist now, dormant, with no selection visuals).
    /// </summary>
    public partial class ItmDisplayMirror : UserControl
    {
        // ── Virtual-canvas geometry (1000×250 units, from the guide's renders) ──
        //
        // Zones: left 0–400, center 400–600, right 600–1000; 50 units of edge padding.
        private const double ZonePad = 50;
        private const double LeftZoneRight = 400;
        private const double RightZoneLeft = 600;
        private const double CanvasWidth = 1000;
        private const double CanvasHeight = 250;

        // Slot rows: labels at the top of each half, values beneath (the guide's
        // vertical rhythm — top slot ≈15–115, bottom ≈125–225).
        private const double TopLabelY = 15;
        private const double TopValueY = 40;
        private const double BottomLabelY = 125;
        private const double BottomValueY = 150;

        // Typography: cap heights ≈26 (labels) / ≈62 (values) / ≈55 (speed) per the
        // guide — Arial's cap height is ~0.72 em, so these em sizes land there. The
        // hardware's face is visibly condensed relative to Arial (its lap times span
        // ~333 units where Arial needs ~392), so text is horizontally compressed to
        // match — otherwise long values would cross the dividers.
        private const double LabelFontSize = 36;
        private const double ValueFontSize = 88;
        private const double SpeedFontSize = 78;
        private const double TextScaleX = 0.84;

        // Dual TC/ABS-style slots: the second field starts 135 units in (guide render).
        private const double DualFieldOffset = 135;
        private const double DualFieldWidth = 130;

        // Center zone: segmented gear glyph over the speed.
        private const double GearGlyphWidth = 80;
        private const double GearGlyphHeight = 130;
        private const double GearGlyphTop = 15;
        private const double GearSegmentThickness = 18;
        private const double SpeedTop = 145;

        // DRS dots (page 2 top-right): ~55–60 diameter, side by side under the label.
        private const double DotDiameter = 60;
        private const double DotSpacing = 118;   // center to center
        private const double DotCenterYTop = 92;
        private const double DotCenterYBottom = 202;

        private static readonly FontFamily PanelFont = new FontFamily("Arial");

        /// <summary>When true, the slot hit regions are live and clicks raise
        /// <see cref="SlotClicked"/>. Default false (the Overview's read-only twin);
        /// the Pages &amp; fields editor turns it on.</summary>
        public bool IsInteractive { get; set; }

        /// <summary>Raised with the clicked field's parameter id while
        /// <see cref="IsInteractive"/> is true. Dormant hook for the pages editor.</summary>
        public event Action<ushort> SlotClicked;

        // Everything built per snapshot (slots, glyph, speed, hit regions) — removed
        // and rebuilt on each Render. The dividers and legacy caption are static XAML.
        private readonly List<UIElement> _dynamic = new List<UIElement>();

        public ItmDisplayMirror()
        {
            InitializeComponent();
            Render((DisplayValuesSnapshot)null);   // start as the dimmed empty panel
        }

        /// <summary>Renders a values snapshot (null = not live). Call on the UI thread;
        /// the caller re-renders only on snapshot reference change (≤ ~4 Hz).</summary>
        internal void Render(DisplayValuesSnapshot snapshot)
            => Render(ItmDisplayMirrorRender.Build(snapshot));

        internal void Render(MirrorModel model)
        {
            foreach (var element in _dynamic)
                canvas.Children.Remove(element);
            _dynamic.Clear();

            bool live = model.PanelState == MirrorPanelState.Live;
            // Off/unsynced: a dimmed empty panel — clean like the powered-down
            // hardware; the state caption lives in the host card's header.
            panelRoot.Opacity = model.PanelState == MirrorPanelState.Empty ? 0.45 : 1.0;
            dividerLeft.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            dividerRight.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            txtLegacy.Visibility = model.PanelState == MirrorPanelState.Legacy
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!live)
                return;

            foreach (var slot in model.Slots)
                DrawSlot(slot);
            DrawGear(model.GearText);
            AddText(model.SpeedText, LeftZoneRight + 10, SpeedTop, SpeedFontSize,
                RightZoneLeft - LeftZoneRight - 20, TextAlignment.Center);
        }

        // ── Slots ────────────────────────────────────────────────────────

        private void DrawSlot(MirrorSlotModel slot)
        {
            bool isRight = slot.Position == ItmSlotPosition.RightTop
                || slot.Position == ItmSlotPosition.RightBottom;
            bool isTop = slot.Position == ItmSlotPosition.LeftTop
                || slot.Position == ItmSlotPosition.RightTop;
            double labelY = isTop ? TopLabelY : BottomLabelY;
            double valueY = isTop ? TopValueY : BottomValueY;
            var align = isRight ? TextAlignment.Right : TextAlignment.Left;
            double zoneX = isRight ? RightZoneLeft + 10 : ZonePad;
            double zoneWidth = isRight
                ? CanvasWidth - ZonePad - (RightZoneLeft + 10)
                : LeftZoneRight - ZonePad - 5;

            if (!slot.IsDual)
            {
                var field = slot.Fields[0];
                AddText(slot.Label ?? field.Label, zoneX, labelY, LabelFontSize, zoneWidth, align);
                AddText(field.Value, zoneX, valueY, ValueFontSize, zoneWidth, align);
            }
            else if (slot.Fields[0].IsDot && slot.Fields[1].IsDot)
            {
                // The DRS shape: one shared label over the two dots (zone left,
                // active right).
                AddText(slot.Label, zoneX, labelY, LabelFontSize, zoneWidth, align);
                double cy = isTop ? DotCenterYTop : DotCenterYBottom;
                double first = isRight
                    ? CanvasWidth - ZonePad - DotDiameter / 2 - DotSpacing
                    : ZonePad + DotDiameter / 2;
                AddDot(first, cy, slot.Fields[0].DotFilled);
                AddDot(first + DotSpacing, cy, slot.Fields[1].DotFilled);
            }
            else
            {
                // The TC/ABS shape: two individually-labeled fields side by side
                // (a shared label, if any, sits above the first field).
                if (slot.Label != null)
                    AddText(slot.Label, zoneX, labelY, LabelFontSize, zoneWidth, align);
                for (int i = 0; i < 2; i++)
                {
                    double x = isRight
                        ? CanvasWidth - ZonePad - DualFieldWidth - (1 - i) * DualFieldOffset
                        : ZonePad + i * DualFieldOffset;
                    var field = slot.Fields[i];
                    if (field.Label != null)
                        AddText(field.Label, x, labelY, LabelFontSize, DualFieldWidth, align);
                    AddText(field.Value, x, valueY, ValueFontSize, DualFieldWidth, align);
                }
            }

            if (IsInteractive)
                AddHitRegions(slot, isRight, isTop);
        }

        // ── Center zone: the segmented gear glyph ────────────────────────

        // Draws the classic seven-segment digit as mitred hexagon Polygons — segment
        // n lit when bit n of the shared segment encoding is set (top, top-right,
        // bottom-right, bottom, bottom-left, top-left, middle).
        private void DrawGear(string gearText)
        {
            byte bits = ItmDisplayMirrorRender.GearSegmentBits(gearText);
            double x0 = (LeftZoneRight + RightZoneLeft) / 2 - GearGlyphWidth / 2;
            double y0 = GearGlyphTop;
            for (int segment = 0; segment < 7; segment++)
            {
                if ((bits & (1 << segment)) == 0)
                    continue;
                var polygon = new Polygon
                {
                    Points = SegmentPoints(segment, x0, y0,
                        GearGlyphWidth, GearGlyphHeight, GearSegmentThickness),
                    Fill = Brushes.White,
                };
                canvas.Children.Add(polygon);
                _dynamic.Add(polygon);
            }
        }

        private static PointCollection SegmentPoints(int segment, double x0, double y0,
            double w, double h, double t)
        {
            double half = t / 2;
            const double gap = 3;   // small notch between segments, like the hardware
            double yTop = y0 + half, yMid = y0 + h / 2, yBottom = y0 + h - half;
            double xLeft = x0 + half, xRight = x0 + w - half;
            switch (segment)
            {
                case 0: return Horizontal(yTop, xLeft, xRight, half, gap);
                case 1: return Vertical(xRight, yTop, yMid, half, gap);
                case 2: return Vertical(xRight, yMid, yBottom, half, gap);
                case 3: return Horizontal(yBottom, xLeft, xRight, half, gap);
                case 4: return Vertical(xLeft, yMid, yBottom, half, gap);
                case 5: return Vertical(xLeft, yTop, yMid, half, gap);
                default: return Horizontal(yMid, xLeft, xRight, half, gap);
            }
        }

        // A horizontal segment: a hexagon with mitred (pointed) ends, centered on cy,
        // spanning between the vertical segments' center lines minus the notch gap.
        private static PointCollection Horizontal(double cy, double x1, double x2,
            double half, double gap)
        {
            x1 += gap;
            x2 -= gap;
            return new PointCollection
            {
                new Point(x1, cy),
                new Point(x1 + half, cy - half),
                new Point(x2 - half, cy - half),
                new Point(x2, cy),
                new Point(x2 - half, cy + half),
                new Point(x1 + half, cy + half),
            };
        }

        // A vertical segment between two row center lines, mitred the same way.
        private static PointCollection Vertical(double cx, double y1, double y2,
            double half, double gap)
        {
            y1 += gap;
            y2 -= gap;
            return new PointCollection
            {
                new Point(cx, y1),
                new Point(cx + half, y1 + half),
                new Point(cx + half, y2 - half),
                new Point(cx, y2),
                new Point(cx - half, y2 - half),
                new Point(cx - half, y1 + half),
            };
        }

        // ── Element helpers ──────────────────────────────────────────────

        // Panel text at a virtual-canvas position. width/alignment give the zone box
        // (right zone right-aligns); the horizontal compression is applied as a layout
        // transform, so the box and alignment are in effective (post-scale) units.
        // Text clips at its box: an oversized value (a 100+ minute lap time, a huge
        // fuel capacity) must not paint across the zone dividers — the hardware keeps
        // its fields inside their zones too.
        private void AddText(string text, double x, double y, double fontSize,
            double effectiveWidth, TextAlignment alignment)
        {
            if (string.IsNullOrEmpty(text))
                return;
            var block = new TextBlock
            {
                Text = text,
                FontFamily = PanelFont,
                FontSize = fontSize,
                Foreground = Brushes.White,
                Width = effectiveWidth / TextScaleX,
                TextAlignment = alignment,
                LayoutTransform = new ScaleTransform(TextScaleX, 1.0),
                ClipToBounds = true,
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            canvas.Children.Add(block);
            _dynamic.Add(block);
        }

        // A DRS indicator dot: filled = on, hollow outline = off.
        private void AddDot(double cx, double cy, bool filled)
        {
            var dot = new Ellipse { Width = DotDiameter, Height = DotDiameter };
            if (filled)
                dot.Fill = Brushes.White;
            else
            {
                dot.Stroke = Brushes.White;
                dot.StrokeThickness = 3;
            }
            Canvas.SetLeft(dot, cx - DotDiameter / 2);
            Canvas.SetTop(dot, cy - DotDiameter / 2);
            canvas.Children.Add(dot);
            _dynamic.Add(dot);
        }

        // Transparent per-field hit regions over the slot's quadrant. A dual slot's
        // quadrant splits at the boundary between its two fields AS DRAWN (an equal
        // split would land inside the first field's artwork — the right-aligned DRS
        // ZONE dot is drawn past the quadrant's midpoint, so its clicks would report
        // the ACTIVE field). Built only when interactive; no visuals.
        private void AddHitRegions(MirrorSlotModel slot, bool isRight, bool isTop)
        {
            double zoneLeft = isRight ? RightZoneLeft : 0;
            double zoneRight = isRight ? CanvasWidth : LeftZoneRight;
            double top = isTop ? 0 : CanvasHeight / 2;
            int count = slot.Fields.Count;
            double split = count == 2 ? DualHitSplit(slot, isRight) : zoneRight;
            for (int i = 0; i < count; i++)
            {
                double left = i == 0 ? zoneLeft : split;
                double right = i == 0 ? split : zoneRight;
                ushort paramId = slot.Fields[i].ParamId;
                var region = new Rectangle
                {
                    Width = right - left,
                    Height = CanvasHeight / 2,
                    Fill = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                };
                region.MouseLeftButtonUp += (s, e) => SlotClicked?.Invoke(paramId);
                Canvas.SetLeft(region, left);
                Canvas.SetTop(region, top);
                canvas.Children.Add(region);
                _dynamic.Add(region);
            }
        }

        // The x boundary between a dual slot's two hit regions — midway between the
        // two fields at the positions DrawSlot draws them (dot centers for the DRS
        // shape, the label/value columns for the TC/ABS shape).
        internal static double DualHitSplit(MirrorSlotModel slot, bool isRight)
        {
            if (slot.Fields[0].IsDot && slot.Fields[1].IsDot)
            {
                // Midway between the two dot centers (see DrawSlot's dot placement).
                double firstCenter = isRight
                    ? CanvasWidth - ZonePad - DotDiameter / 2 - DotSpacing
                    : ZonePad + DotDiameter / 2;
                return firstCenter + DotSpacing / 2;
            }
            // TC/ABS style: midway between field 0's right edge (x0 + DualFieldWidth)
            // and field 1's left edge (x0 + DualFieldOffset).
            double x0 = isRight
                ? CanvasWidth - ZonePad - DualFieldWidth - DualFieldOffset
                : ZonePad;
            return x0 + (DualFieldWidth + DualFieldOffset) / 2;
        }
    }
}
