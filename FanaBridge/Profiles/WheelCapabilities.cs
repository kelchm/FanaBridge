using System.Linq;

namespace FanaBridge.Profiles
{
    /// <summary>
    /// Runtime view of a wheel's hardware capabilities, computed from a
    /// <see cref="WheelProfile"/>.  Provides the derived counts and flags
    /// that the driver and SimHub integration layers need.
    ///
    /// This is NOT the source of truth — the <see cref="WheelProfile"/> JSON
    /// is.  This class exists so that downstream code doesn't need to
    /// repeatedly query/filter the LED list.
    /// </summary>
    public class WheelCapabilities
    {
        /// <summary>Full product name.</summary>
        public string Name { get; }

        /// <summary>Short display name for SimHub UI.</summary>
        public string ShortName { get; }

        /// <summary>Display type.</summary>
        public DisplayType Display { get; }

        /// <summary>The source profile this was built from (null for the None sentinel).</summary>
        public WheelProfile Profile { get; }

        /// <summary>Whether the active profile is built-in or user-created.</summary>
        public ProfileSource? ProfileSource { get; }

        /// <summary>
        /// For user profiles, the disk file path.  For built-in profiles,
        /// the embedded resource name.  Null for the None sentinel.
        /// </summary>
        public string ProfileSourcePath { get; }

        // ── Derived LED counts ───────────────────────────────────────────

        /// <summary>Number of RevRgb LEDs — col03 subcmd 0x00.</summary>
        public int RevRgbCount { get; }

        /// <summary>Number of FlagRgb LEDs — col03 subcmd 0x01.</summary>
        public int FlagRgbCount { get; }

        /// <summary>Number of ButtonRgb LEDs — col03 subcmd 0x02.</summary>
        public int ButtonRgbCount { get; }

        /// <summary>Number of ButtonAuxIntensity LEDs — col03 subcmd 0x03 overflow slots.</summary>
        public int ButtonAuxIntensityCount { get; }

        /// <summary>Number of LegacyRevOnOff LEDs — col01 bitmask.</summary>
        public int LegacyRevOnOffCount { get; }

        /// <summary>Number of LegacyRevStripe LEDs — col01 RGB333 (typically 1).</summary>
        public int LegacyRevStripeCount { get; }

        /// <summary>Number of LegacyRev3Bit LEDs — col01 subcmd 0x0A.</summary>
        public int LegacyRev3BitCount { get; }

        /// <summary>Number of LegacyFlag3Bit LEDs — col01 subcmd 0x0B.</summary>
        public int LegacyFlag3BitCount { get; }

        /// <summary>Rev + Flag count (all rev-like and flag-like channels) — used for LedModuleOptions.LedCount.</summary>
        public int RevFlagCount { get; }

        /// <summary>ButtonRgb + ButtonAuxIntensity count — "button" LEDs for SimHub.</summary>
        public int ButtonLedCount { get; }

        /// <summary>Total LED count across all channels.</summary>
        public int AllLedCount { get; }

        /// <summary>
        /// Pixel encoding for the ButtonRgb channel.
        /// See <see cref="WheelProfile.ColorFormat"/>.
        /// </summary>
        public ColorFormat ColorFormat { get; }

        // ── Convenience flags ────────────────────────────────────────────

        public bool HasRevRgb => RevRgbCount > 0;
        public bool HasFlagRgb => FlagRgbCount > 0;
        public bool HasLegacyRevOnOff => LegacyRevOnOffCount > 0;
        public bool HasLegacyRevStripe => LegacyRevStripeCount > 0;
        public bool HasLegacyRev3Bit => LegacyRev3BitCount > 0;
        public bool HasLegacyFlag3Bit => LegacyFlag3BitCount > 0;
        public bool HasLeds => AllLedCount > 0;
        public bool HasEncoders { get; }

        /// <summary>Whether this profile has been tested on physical hardware.</summary>
        public bool Verified { get; }

        /// <summary>
        /// Checks whether switching from <paramref name="other"/> to this
        /// capabilities set requires a SimHub restart to fully apply.
        /// Returns a human-readable reason string, or null if no restart is needed.
        /// </summary>
        public string GetRestartReason(WheelCapabilities other)
        {
            if (other == null)
                return null;

            if (RevFlagCount != other.RevFlagCount || ButtonLedCount != other.ButtonLedCount)
                return "LED count changed (" + other.AllLedCount + " → " + AllLedCount + ")";

            if (Display != other.Display)
                return "Display type changed (" + other.Display + " → " + Display + ")";

            return null;
        }

        // ── Constructors ─────────────────────────────────────────────────

        /// <summary>
        /// Builds a capabilities object from a loaded wheel profile.
        /// </summary>
        public WheelCapabilities(WheelProfile profile)
        {
            Profile = profile ?? throw new System.ArgumentNullException(nameof(profile));
            Name = profile.Name;
            ShortName = profile.ShortName;
            Display = profile.DisplayType;
            ProfileSource = profile.Source;
            ProfileSourcePath = profile.SourcePath;

            RevRgbCount = profile.RevRgbCount;
            FlagRgbCount = profile.FlagRgbCount;
            ButtonRgbCount = profile.ButtonRgbCount;
            ButtonAuxIntensityCount = profile.ButtonAuxIntensityCount;
            LegacyRevOnOffCount = profile.LegacyRevOnOffCount;
            LegacyRevStripeCount = profile.LegacyRevStripeCount;
            LegacyRev3BitCount = profile.LegacyRev3BitCount;
            LegacyFlag3BitCount = profile.LegacyFlag3BitCount;
            RevFlagCount = profile.RevFlagCount;
            ButtonLedCount = profile.ButtonLedCount;
            AllLedCount = profile.TotalLedCount;
            ColorFormat = profile.ColorFormat;
            HasEncoders = profile.Leds.Any(l => l.Role == LedRole.Encoder);
            Verified = profile.Verified;
        }

        /// <summary>Private constructor for the None sentinel.</summary>
        private WheelCapabilities()
        {
            Name = null;
            ShortName = null;
            Display = DisplayType.None;
            Verified = true;
        }

        // ── Null object ──────────────────────────────────────────────────

        /// <summary>
        /// Represents "no wheel connected" or "not yet identified".
        /// All capabilities are zeroed — this is not a real device config.
        /// </summary>
        public static WheelCapabilities None { get; } = new WheelCapabilities();
    }
}
