using System.Collections.Generic;
using System.Linq;

namespace FanaBridge.Profiles
{
    /// <summary>
    /// A way in which a wheel's LEDs cannot show what SimHub's color picker offers.
    /// </summary>
    /// <remarks>
    /// SimHub presents a full 24-bit picker for every LED regardless of what the
    /// hardware can do. Most Fanatec LEDs are RGB565 and near enough, but several
    /// channels are far more limited — and the difference is invisible until the
    /// wheel does something unexpected. These descriptions drive the notice shown
    /// in the LEDs tab.
    /// </remarks>
    public sealed class LedColorLimitation
    {
        public LedColorLimitation(string text)
        {
            Text = text;
        }

        /// <summary>One plain sentence the user can act on. Shown verbatim.</summary>
        public string Text { get; }

        /// <summary>
        /// Describes every color limitation present on a wheel, or an empty list
        /// when its LEDs can render the picker's range faithfully.
        /// </summary>
        public static IReadOnlyList<LedColorLimitation> ForCapabilities(WheelCapabilities caps)
        {
            var result = new List<LedColorLimitation>();
            if (caps == null) return result;

            var leds = caps.Profile?.Leds;
            string device = DeviceNoun(caps.Profile);

            const string Palette = "red, green, blue, cyan, magenta, yellow and white";
            // Shared so the two palette messages can't drift apart.
            const string Matched = " Any other color is matched to the closest.";

            if (caps.HasLegacyRevOnOff)
            {
                result.Add(new LedColorLimitation(
                    "This " + device + "'s " + Subject(leds, "Rev LEDs", LedChannel.LegacyRevOnOff) +
                    " can only be switched on or off, so only brightness matters — the hue is ignored."));
            }

            // The RevStripe and the per-LED case say the same thing, so each leads
            // with what distinguishes it rather than repeating the other verbatim
            // when a device has both.
            if (caps.HasLegacyRevStripe)
            {
                result.Add(new LedColorLimitation(
                    // "RevStripe" is Fanatec's name for this part, so it stays literal
                    // rather than being described. See docs/terminology.md.
                    "This " + device + "'s RevStripe is a single light supporting a limited color " +
                    "palette: " + Palette + "." + Matched));
            }

            if (caps.HasLegacyRev3Bit || caps.HasLegacyFlag3Bit)
            {
                result.Add(new LedColorLimitation(
                    "This " + device + "'s " +
                    Subject(leds, "LEDs", LedChannel.LegacyRev3Bit, LedChannel.LegacyFlag3Bit) +
                    " only support a limited color palette: " + Palette + "." + Matched));
            }

            if (caps.ButtonAuxIntensityCount > 0)
            {
                result.Add(new LedColorLimitation(
                    "This " + device + "'s " + Subject(leds, "LEDs", LedChannel.ButtonAuxIntensity) +
                    " can't have their color controlled — only their brightness."));
            }

            return result;
        }

        /// <summary>
        /// What to call the thing the LEDs are on. A hub has no LEDs of its own —
        /// they belong to the button module attached to it — so a hub+module
        /// profile describes the module, and everything else describes the wheel.
        /// </summary>
        private static string DeviceNoun(WheelProfile profile)
        {
            if (profile == null) return "device";
            return string.IsNullOrEmpty(profile.Match?.ModuleType) ? "wheel" : "module";
        }

        /// <summary>
        /// Names the LEDs a limitation applies to, in the user's terms, by reading
        /// the roles the profile assigned them — "Rev LEDs", "Rev and Flag LEDs",
        /// "Encoder LEDs". A channel is not tied to one role, and custom profiles
        /// assign them freely, so this cannot be hardcoded per channel. Names follow
        /// docs/terminology.md.
        /// </summary>
        /// <param name="fallback">
        /// Used when no profile is available to read roles from.
        /// </param>
        private static string Subject(
            IReadOnlyList<LedDefinition> leds, string fallback, params LedChannel[] channels)
        {
            var roles = RolesFor(leds, channels);
            if (roles.Count == 0) return fallback;

            // Roles share the noun: "rev and flag LEDs", not "rev LEDs and flag LEDs".
            return Join(roles.Select(Adjective).ToList()) + " LEDs";
        }

        private static List<LedRole> RolesFor(
            IReadOnlyList<LedDefinition> leds, LedChannel[] channels)
        {
            if (leds == null) return new List<LedRole>();

            return leds
                .Where(l => channels.Contains(l.Channel))
                .Select(l => l.Role)
                .Distinct()
                .OrderBy(r => (int)r)
                .ToList();
        }

        // "a", "a and b", "a, b and c" — no Oxford comma, matching ordinary prose.
        private static string Join(IReadOnlyList<string> parts)
        {
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
        }

        private static string Adjective(LedRole role)
        {
            switch (role)
            {
                case LedRole.Rev: return "Rev";
                case LedRole.Flag: return "Flag";
                case LedRole.Button: return "Button";
                case LedRole.Encoder: return "Encoder";
                case LedRole.Indicator: return "Indicator";
                default: return "";
            }
        }
    }
}
