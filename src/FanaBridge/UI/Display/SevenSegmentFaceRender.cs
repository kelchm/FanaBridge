using System;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure helpers for the shared 3-char seven-segment face: segment/dot bit tests and
    /// the Virtual-pages editor LIVE preview (formatter → effect clock → byte[3]). No WPF
    /// — sibling of <see cref="ItmDisplayMirrorRender"/>. The thin face control only draws.
    /// </summary>
    internal static class SevenSegmentFaceRender
    {
        /// <summary>Demo values used when a dynamic content kind has no live telemetry —
        /// pure-model preview still shows a recognizable face (mock's "142" for Speed).</summary>
        public const double DemoSpeed = 142;
        public const string DemoGear = "3";
        public const double DemoRpm = 8000;
        public const double DemoPosition = 12;
        public const double DemoFuel = 42;
        public const double DemoProperty = 88;

        /// <summary>Whether bit <paramref name="segment"/> (0–6) is lit on a segment byte.</summary>
        public static bool IsSegmentLit(byte segs, int segment)
            => segment >= 0 && segment < 7 && (segs & (1 << segment)) != 0;

        /// <summary>Whether the decimal-point bit (bit 7) is lit.</summary>
        public static bool IsDotLit(byte segs)
            => (segs & SevenSegment.Dot) != 0;

        /// <summary>
        /// Pure-model LIVE preview for a virtual page: content → text → effect frame.
        /// Text/Message use the screen's text; dynamic kinds use demo values so the editor
        /// face is never blank solely for lack of a game session. Property without a source
        /// (or unreadable) blanks. Null screen → blank frame.
        /// </summary>
        public static byte[] PreviewSegments(LegacyScreen screen, long nowMs = 0)
        {
            if (screen == null)
                return BlankFrame();

            string text = PreviewText(screen);
            if (text == null)
                return BlankFrame();

            LegacyEffect effect = screen.Effect;
            if (effect == LegacyEffect.Unknown)
                effect = LegacyEffect.None;
            return LegacyEffectClock.Apply(text, effect, nowMs);
        }

        /// <summary>Content string for the preview path (null → blank frame).</summary>
        public static string PreviewText(LegacyScreen screen)
        {
            if (screen == null)
                return null;

            switch (screen.ContentKind)
            {
                case LegacyContentKind.Text:
                case LegacyContentKind.Message:
                    return LegacyValueFormatter.FormatText(screen.Text);

                case LegacyContentKind.Speed:
                    return LegacyValueFormatter.FormatSpeed(DemoSpeed);

                case LegacyContentKind.Gear:
                    return LegacyValueFormatter.FormatGear(DemoGear);

                case LegacyContentKind.GearAndSpeed:
                    // Outside the overlay window → speed (demo has no recent shift).
                    return LegacyValueFormatter.FormatGearAndSpeed(
                        DemoGear, DemoSpeed, gearChangedAtMs: 0, nowMs: LegacyValueFormatter.GearOverlayMs + 1);

                case LegacyContentKind.GearBrackets:
                    return LegacyValueFormatter.FormatGearBrackets(
                        DemoGear, rpms: DemoRpm, redLineReached: 0);

                case LegacyContentKind.Rpm:
                    return LegacyValueFormatter.FormatRpm(DemoRpm);

                case LegacyContentKind.Position:
                    return LegacyValueFormatter.FormatPosition(DemoPosition);

                case LegacyContentKind.Fuel:
                    return LegacyValueFormatter.FormatFuel(DemoFuel);

                case LegacyContentKind.Property:
                    // No live property reader in the pure preview — show the demo numeral
                    // only when a source is present (editor echo); blank otherwise.
                    if (screen.Source == null || string.IsNullOrEmpty(screen.Source.Name))
                        return null;
                    return LegacyValueFormatter.FormatProperty(
                        DemoPropertyReader.Instance, screen.Source);

                case LegacyContentKind.Unknown:
                default:
                    return null;
            }
        }

        public static byte[] BlankFrame()
            => new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank };

        /// <summary>Always returns <see cref="DemoProperty"/> for any named source —
        /// editor preview only; never used on the wire.</summary>
        private sealed class DemoPropertyReader : IPropertyReader
        {
            public static readonly DemoPropertyReader Instance = new DemoPropertyReader();

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                if (spec == null || string.IsNullOrEmpty(spec.Name))
                {
                    value = 0;
                    return false;
                }
                value = DemoProperty;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                return false;
            }
        }
    }
}
