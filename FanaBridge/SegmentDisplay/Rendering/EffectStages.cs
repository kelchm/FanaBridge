namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// Toggles <see cref="SegmentDisplayFrame.SuppressOutput"/> on a repeating on/off interval.
    /// </summary>
    public class BlinkStage : IRenderStage
    {
        private readonly int _onMs;
        private readonly int _offMs;

        public BlinkStage(int onMs, int offMs)
        {
            _onMs = onMs > 0 ? onMs : 500;
            _offMs = offMs > 0 ? offMs : 500;
        }

        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            int cycle = _onMs + _offMs;
            if (cycle <= 0) return input;

            long phase = ctx.ElapsedMs % cycle;
            if (phase >= _onMs)
                input.SuppressOutput = true;

            return input;
        }
    }

    /// <summary>
    /// Rapid blink N times from activation, then solid. Count=0 means continuous.
    /// </summary>
    public class FlashStage : IRenderStage
    {
        private readonly int _count;
        private readonly int _rateMs;

        public FlashStage(int count, int rateMs)
        {
            _count = count;
            _rateMs = rateMs > 0 ? rateMs : 150;
        }

        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            int cycle = _rateMs * 2; // on + off
            if (cycle <= 0) return input;

            if (_count > 0)
            {
                // Finite flash: after count cycles, stay solid
                long totalFlashMs = (long)_count * cycle;
                if (ctx.ElapsedMs >= totalFlashMs)
                    return input; // solid — no suppression
            }

            // During flashing: off phase suppresses
            long phase = ctx.ElapsedMs % cycle;
            if (phase >= _rateMs)
                input.SuppressOutput = true;

            return input;
        }
    }
}
