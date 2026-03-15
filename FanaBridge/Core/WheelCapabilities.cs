using System.Linq;

namespace FanaBridge.Core
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

        /// <summary>Number of Rev (RPM) LEDs — subcmd 0x00.</summary>
        public int RevLedCount { get; }

        /// <summary>Number of Flag (status) LEDs — subcmd 0x01.</summary>
        public int FlagLedCount { get; }

        /// <summary>Number of RGB color LEDs — subcmd 0x02.</summary>
        public int ColorLedCount { get; }

        /// <summary>Number of monochrome intensity LEDs — subcmd 0x03.</summary>
        public int MonoLedCount { get; }

        /// <summary>Rev + Flag count — used for LedModuleOptions.LedCount.</summary>
        public int RevFlagCount { get; }

        /// <summary>Color + Mono count — "button" LEDs for SimHub.</summary>
        public int ButtonLedCount { get; }

        /// <summary>Total LED count across all channels.</summary>
        public int AllLedCount { get; }

        /// <summary>
        /// Pixel encoding for the Color LED channel.
        /// See <see cref="WheelProfile.ColorFormat"/>.
        /// </summary>
        public ColorFormat ColorFormat { get; }

        // ── Convenience flags ────────────────────────────────────────────

        public bool HasRevLeds => RevLedCount > 0;
        public bool HasFlagLeds => FlagLedCount > 0;
        public bool HasLeds => AllLedCount > 0;
        public bool HasEncoders { get; }

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

            RevLedCount = profile.RevLedCount;
            FlagLedCount = profile.FlagLedCount;
            ColorLedCount = profile.ColorLedCount;
            MonoLedCount = profile.MonoLedCount;
            RevFlagCount = profile.RevFlagCount;
            ButtonLedCount = profile.ButtonLedCount;
            AllLedCount = profile.TotalLedCount;
            ColorFormat = profile.ColorFormat;
            HasEncoders = profile.Leds.Any(l => l.Role == LedRole.Encoder);
        }

        /// <summary>Private constructor for the None sentinel.</summary>
        private WheelCapabilities()
        {
            Name = null;
            ShortName = null;
            Display = DisplayType.None;
        }

        // ── Null object ──────────────────────────────────────────────────

        /// <summary>
        /// Represents "no wheel connected" or "not yet identified".
        /// All capabilities are zeroed — this is not a real device config.
        /// </summary>
        public static WheelCapabilities None { get; } = new WheelCapabilities();
    }
}
