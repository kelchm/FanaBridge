using Newtonsoft.Json;
using FanaBridge.Protocol;

namespace FanaBridge.Customization
{
    /// <summary>
    /// A named screen for the legacy 7-segment surface: up to three characters shown
    /// verbatim. Screens form the library that legacy rules (and ITM rules targeting
    /// <see cref="TargetKind.LegacyScreen"/>) pick from.
    /// </summary>
    public class LegacyScreen
    {
        /// <summary>Stable identity, referenced by <see cref="RuleTarget.ScreenId"/> and
        /// <see cref="LegacyRuleSet.BaseScreenId"/>.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Human-readable label for the UI.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>What the display shows: 1–3 display positions; '.'/',' fold onto the
        /// previous character's dot segment. Validated at load with
        /// <see cref="IsRenderableText"/> — an unrenderable screen is skipped with a warning.</summary>
        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary>
        /// Whether <paramref name="text"/> renders on the 7-segment display: 1–3 display
        /// positions, each covered by <see cref="SevenSegment.CharToSegment"/>. Positions
        /// are counted the way the encoder folds ("-1.5" is three positions, the dot rides
        /// the '1' — see <see cref="SevenSegment.EncodeWithDots"/>). Space is a deliberate
        /// blank; any other character the segment table cannot draw (it would fall back to
        /// blank) fails, so a screen never silently shows empty positions.
        /// </summary>
        public static bool IsRenderableText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            int positions = 0;
            foreach (char ch in text)
            {
                if (ch == '.' || ch == ',')
                {
                    if (positions == 0)
                        positions++;   // nothing to fold onto — a leading dot takes a slot
                    continue;
                }
                if (ch != ' ' && SevenSegment.CharToSegment(ch) == SevenSegment.Blank)
                    return false;
                positions++;
            }
            return positions >= 1 && positions <= 3;
        }
    }
}
