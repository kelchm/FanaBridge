using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FanaBridge.Protocol;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Shared 3-character seven-segment face: green (#35E06A) segments on black, three
    /// positions plus the decimal-point bit. Geometry is the generalized gear-glyph
    /// builder (<see cref="SegmentPoints"/> is position-agnostic). Consumers: Virtual
    /// pages LIVE preview, legacy Overview mirror, Page-6 delegation mini face.
    /// </summary>
    internal sealed class SevenSegmentFace : UserControl
    {
        private const double DigitWidth = 48;
        private const double DigitHeight = 78;
        private const double DigitGap = 10;
        private const double SegmentThickness = 10;
        private const double DotSize = 10;
        private const double PadX = 14;
        private const double PadY = 10;

        private readonly Canvas _canvas;
        private readonly UIElement[] _dynamic = new UIElement[32];
        private int _dynamicCount;

        public SevenSegmentFace()
        {
            _canvas = new Canvas
            {
                Width = PadX * 2 + DigitWidth * 3 + DigitGap * 2,
                Height = PadY * 2 + DigitHeight + 4,
                Background = DisplayPalette.LegacyFaceBg,
            };
            Content = new Border
            {
                Background = DisplayPalette.LegacyFaceBg,
                CornerRadius = new CornerRadius(4),
                Child = _canvas,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Render(SevenSegmentFaceRender.BlankFrame());
        }

        /// <summary>Paints three segment bytes (null / short → blank positions). Dot bit
        /// (0x80) draws a small filled circle at the digit's lower-right.</summary>
        public void Render(byte[] segments)
        {
            ClearDynamic();
            if (segments == null)
                segments = SevenSegmentFaceRender.BlankFrame();

            for (int i = 0; i < 3; i++)
            {
                byte bits = i < segments.Length ? segments[i] : SevenSegment.Blank;
                double x0 = PadX + i * (DigitWidth + DigitGap);
                double y0 = PadY;
                DrawDigit(bits, x0, y0);
            }
        }

        private void DrawDigit(byte bits, double x0, double y0)
        {
            for (int segment = 0; segment < 7; segment++)
            {
                if (!SevenSegmentFaceRender.IsSegmentLit(bits, segment))
                    continue;
                var polygon = new Polygon
                {
                    Points = SegmentPoints(segment, x0, y0, DigitWidth, DigitHeight, SegmentThickness),
                    Fill = DisplayPalette.LegacyFaceGreen,
                    IsHitTestVisible = false,
                };
                _canvas.Children.Add(polygon);
                AddDynamic(polygon);
            }

            if (SevenSegmentFaceRender.IsDotLit(bits))
            {
                var dot = new Ellipse
                {
                    Width = DotSize,
                    Height = DotSize,
                    Fill = DisplayPalette.LegacyFaceGreen,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, x0 + DigitWidth - DotSize * 0.15);
                Canvas.SetTop(dot, y0 + DigitHeight - DotSize * 0.35);
                _canvas.Children.Add(dot);
                AddDynamic(dot);
            }
        }

        private void ClearDynamic()
        {
            for (int i = 0; i < _dynamicCount; i++)
                _canvas.Children.Remove(_dynamic[i]);
            _dynamicCount = 0;
        }

        private void AddDynamic(UIElement el)
        {
            if (_dynamicCount < _dynamic.Length)
                _dynamic[_dynamicCount++] = el;
        }

        // ── Shared geometry (also used by the ITM center gear glyph) ─────

        /// <summary>
        /// Classic seven-segment digit geometry as mitred hexagon polygons. Segment n is
        /// top, top-right, bottom-right, bottom, bottom-left, top-left, middle. Position-
        /// agnostic: the same builder draws the gear glyph and every face digit.
        /// </summary>
        internal static PointCollection SegmentPoints(int segment, double x0, double y0,
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
    }
}
