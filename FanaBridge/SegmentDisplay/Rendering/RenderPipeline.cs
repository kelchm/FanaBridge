using System.Collections.Generic;

namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// Assembled render pipeline for a single display layer. Built once at
    /// configuration time, then applied each frame. Immutable after construction.
    /// </summary>
    public class RenderPipeline
    {
        private readonly IRenderStage[] _stages;

        public RenderPipeline(IRenderStage[] stages)
        {
            _stages = stages ?? new IRenderStage[0];
        }

        /// <summary>Run the frame through all stages sequentially.</summary>
        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            for (int i = 0; i < _stages.Length; i++)
            {
                input = _stages[i].Process(input, ctx);
            }
            return input;
        }

        /// <summary>
        /// Builds a pipeline for a given layer's configuration.
        /// </summary>
        public static RenderPipeline ForLayer(SegmentDisplayLayer layer)
        {
            if (layer.Content == null)
                throw new System.ArgumentException("Layer content must not be null.", "layer");

            var stages = new List<IRenderStage>();

            // 1. Formatter (from content format)
            stages.Add(ResolveFormatter(layer));

            // 2. Alignment
            stages.Add(new AlignStage(layer.Alignment, GetFormat(layer)));

            // 3. Overflow
            var overflow = ResolveOverflow(layer);
            if (overflow == OverflowType.Scroll)
                stages.Add(new ScrollStage(layer.ScrollSpeedMs));
            else
                stages.Add(new TruncateStage(overflow));

            // 4. Effects
            if (layer.Effects != null)
            {
                foreach (var effect in layer.Effects)
                {
                    if (effect is BlinkEffect blink)
                        stages.Add(new BlinkStage(blink.OnMs, blink.OffMs));
                    else if (effect is FlashEffect flash)
                        stages.Add(new FlashStage(flash.Count, flash.RateMs));
                }
            }

            // 5. Segment encoding
            stages.Add(new SegmentEncodeStage());

            return new RenderPipeline(stages.ToArray());
        }

        private static SegmentFormat GetFormat(SegmentDisplayLayer layer)
        {
            if (layer.Content is PropertyContent pc) return pc.Format;
            if (layer.Content is ExpressionContent ec) return ec.Format;
            return SegmentFormat.Text;
        }

        private static IRenderStage ResolveFormatter(SegmentDisplayLayer layer)
        {
            var format = GetFormat(layer);
            switch (format)
            {
                case SegmentFormat.Number:  return new NumberFormatter();
                case SegmentFormat.Decimal: return new DecimalFormatter();
                case SegmentFormat.Time:    return new TimeFormatter(GetTimeFormat(layer));
                case SegmentFormat.Gear:    return new GearFormatter();
                default:                    return new TextPassthrough();
            }
        }

        private static string GetTimeFormat(SegmentDisplayLayer layer)
        {
            if (layer.Content is PropertyContent pc) return pc.TimeFormat;
            return null;
        }

        private static OverflowType ResolveOverflow(SegmentDisplayLayer layer)
        {
            if (layer.Overflow != OverflowType.Auto) return layer.Overflow;
            var format = GetFormat(layer);
            switch (format)
            {
                case SegmentFormat.Text: return OverflowType.Scroll;
                case SegmentFormat.Time: return OverflowType.TruncateLeft;
                default:                 return OverflowType.TruncateRight;
            }
        }
    }
}
