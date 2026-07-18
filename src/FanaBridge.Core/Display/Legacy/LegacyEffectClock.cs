using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Legacy
{
    /// <summary>
    /// Pure, clock-injected presentation effects for legacy virtual pages. Takes an
    /// already-rendered content string (from <see cref="LegacyValueFormatter"/>) plus an
    /// effect and <c>nowMs</c>, and produces the 3-byte segment frame for that instant.
    /// Scroll steps every <see cref="ScrollStepMs"/>; blink toggles every
    /// <see cref="BlinkHalfPeriodMs"/>. <see cref="LegacyEffect.Flash"/> is treated as
    /// Blink (the validator coerces it at load; this is the defensive runtime path).
    /// </summary>
    public static class LegacyEffectClock
    {
        /// <summary>Milliseconds between scroll window advances.</summary>
        public const int ScrollStepMs = 400;

        /// <summary>Milliseconds for one blink half-cycle (on or off).</summary>
        public const int BlinkHalfPeriodMs = 500;

        /// <summary>
        /// Applies <paramref name="effect"/> to <paramref name="renderedText"/> at
        /// <paramref name="nowMs"/> and returns a 3-byte segment frame.
        /// </summary>
        public static byte[] Apply(string renderedText, LegacyEffect effect, long nowMs)
        {
            switch (effect)
            {
                case LegacyEffect.Scroll:
                    return ScrollFrame(renderedText, nowMs);

                case LegacyEffect.Blink:
                case LegacyEffect.Flash:
                    return BlinkFrame(renderedText, nowMs);

                case LegacyEffect.None:
                case LegacyEffect.Unknown:
                default:
                    return LegacyValueFormatter.Render(renderedText);
            }
        }

        // ── Scroll ───────────────────────────────────────────────────────

        /// <summary>
        /// Sliding 3-position window over the encoded text. Pads with three trailing
        /// blanks so the message fully clears before wrapping. Inert when the text
        /// already fits in 3 positions (returns the static render).
        /// </summary>
        private static byte[] ScrollFrame(string text, long nowMs)
        {
            List<byte> encoded = SevenSegment.EncodeWithDots(text ?? "");
            if (encoded.Count <= 3)
                return LegacyValueFormatter.Render(text);

            // Three blank pads so the last character scrolls fully off before wrap.
            encoded.Add(SevenSegment.Blank);
            encoded.Add(SevenSegment.Blank);
            encoded.Add(SevenSegment.Blank);

            int step = (int)((nowMs / ScrollStepMs) % encoded.Count);
            var frame = new byte[3];
            for (int i = 0; i < 3; i++)
                frame[i] = encoded[(step + i) % encoded.Count];
            return frame;
        }

        // ── Blink ────────────────────────────────────────────────────────

        /// <summary>500 ms on / 500 ms off. Off phase is a blank frame.</summary>
        private static byte[] BlinkFrame(string text, long nowMs)
        {
            // Phase 0 = on, phase 1 = off.
            long phase = (nowMs / BlinkHalfPeriodMs) % 2;
            if (phase != 0)
            {
                return new byte[]
                {
                    SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank,
                };
            }
            return LegacyValueFormatter.Render(text);
        }
    }
}
