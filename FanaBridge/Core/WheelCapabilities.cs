using System.Collections.Generic;
using System.Linq;

namespace FanaBridge
{
    /// <summary>
    /// The type of display available on a wheel or button module.
    /// Describes the protocol/capability level, not the underlying
    /// technology (OLED, LCD, 7-seg LED are all possible for a given level).
    /// </summary>
    public enum DisplayType
    {
        /// <summary>No display.</summary>
        None,

        /// <summary>Simple 3-character display (7-seg LED, small OLED, etc.).</summary>
        Basic,

        /// <summary>Rich graphical display with ITM support (OLED or LCD).</summary>
        Itm,
    }

    /// <summary>
    /// Describes the hardware capabilities of a specific Fanatec steering wheel
    /// or hub + module configuration.  Kept intentionally minimal — only add
    /// properties when there is code that needs to branch on them.
    /// </summary>
    public class WheelCapabilities
    {
        /// <summary>Full product name (e.g. "Fanatec Podium Steering Wheel BMW M4 GT3").</summary>
        public string Name { get; set; }

        /// <summary>Short display name for the SimHub Devices UI (e.g. "Fanatec BMW M4 GT3").</summary>
        public string ShortName { get; set; }

        /// <summary>Number of individually-addressable RGB button LEDs (col03 protocol).</summary>
        public int ButtonLedCount { get; set; }

        /// <summary>Number of encoder-mounted LEDs exposed in SimHub's Encoders section.</summary>
        /// <remarks>
        /// The LED type depends on the wheel hardware:
        ///   • RGB encoder LEDs (e.g. BMR): share the button color protocol (subcmd 0x02)
        ///     but are interleaved with button LEDs at specific hardware indices.
        ///     Set <see cref="EncoderColorIndices"/> to declare their positions.
        ///   • Monochrome encoder LEDs (e.g. M4 GT3): intensity-only (0-7),
        ///     placed in the subcmd 0x03 intensity payload at <see cref="EncoderIntensityOffset"/>.
        /// </remarks>
        public int EncoderLedCount { get; set; }

        /// <summary>
        /// Hardware indices within the subcmd 0x02 color array where RGB encoder
        /// LEDs reside.  Length must match <see cref="EncoderLedCount"/> when set.
        /// Used when encoder LEDs share the button color protocol but are
        /// interleaved at non-contiguous positions (e.g. BMR: {1, 8, 11}).
        ///
        /// Null when encoders are monochrome (use <see cref="EncoderIntensityOffset"/>
        /// instead) or when the wheel has no encoder LEDs.
        /// </summary>
        public int[] EncoderColorIndices { get; set; }

        /// <summary>
        /// Starting byte index of encoder intensity values within the subcmd 0x03
        /// intensity payload (<see cref="FanatecDevice.INTENSITY_PAYLOAD_SIZE"/> bytes).
        /// Only meaningful when encoder LEDs are monochrome (intensity-only).
        ///
        /// When 0 (default): encoder LEDs are full RGB and share the button color
        /// protocol — their colors are appended to the button color array.
        /// When > 0: encoder LEDs are monochrome; luminance is extracted from the
        /// SimHub Color and placed at payload[offset .. offset+EncoderLedCount-1].
        ///
        /// Example: M4 GT3 has offset 12 (after 12 button intensity slots).
        /// </summary>
        public int EncoderIntensityOffset { get; set; }

        /// <summary>Total addressable col03 LEDs (buttons + encoders). Used for the raw/individual LED module.</summary>
        public int TotalLedCount => ButtonLedCount + EncoderLedCount;

        /// <summary>Total of all addressable LEDs across all types (rev + flag + button + encoder).</summary>
        public int AllLedCount => RevLedCount + FlagLedCount + ButtonLedCount + EncoderLedCount;

        /// <summary>
        /// Number of Rev (RPM indicator) LEDs controlled via col03 LED interface.
        /// Each LED has independent RGB565 color (subcmd 0x00 on col03).
        /// </summary>
        public int RevLedCount { get; set; }

        /// <summary>
        /// Number of Flag (status indicator) LEDs controlled via col03 LED interface.
        /// Each LED has independent RGB565 color (subcmd 0x01 on col03).
        /// </summary>
        public int FlagLedCount { get; set; }

        /// <summary>True if this device has Rev LEDs.</summary>
        public bool HasRevLeds => RevLedCount > 0;

        /// <summary>True if this device has Flag LEDs.</summary>
        public bool HasFlagLeds => FlagLedCount > 0;

        /// <summary>True if this device has encoder indicator LEDs (RGB or monochrome).</summary>
        public bool HasEncoderLeds => EncoderLedCount > 0;

        /// <summary>True if encoder LEDs are monochrome (intensity-only, not RGB).</summary>
        public bool HasMonochromeEncoders => EncoderLedCount > 0 && EncoderIntensityOffset > 0;

        /// <summary>True if encoder LEDs are RGB and share the button color protocol.</summary>
        public bool HasRgbEncoders => EncoderColorIndices != null && EncoderColorIndices.Length > 0;

        /// <summary>
        /// Total slots in the subcmd 0x02 color array.
        /// Includes both button and interleaved RGB encoder LEDs.
        /// </summary>
        public int ButtonColorLedCount => HasRgbEncoders
            ? ButtonLedCount + EncoderLedCount
            : ButtonLedCount;

        /// <summary>
        /// Builds an ordered array of hardware indices in the subcmd 0x02 color
        /// array that correspond to button (non-encoder) LEDs.
        /// Only meaningful when <see cref="HasRgbEncoders"/> is true.
        /// </summary>
        public int[] BuildButtonColorIndices()
        {
            if (!HasRgbEncoders)
                return null;
            var encoderSet = new HashSet<int>(EncoderColorIndices);
            return Enumerable.Range(0, ButtonColorLedCount)
                             .Where(i => !encoderSet.Contains(i))
                             .ToArray();
        }

        /// <summary>The type of display available on this wheel/module.</summary>
        public DisplayType Display { get; set; }

        // ── Null object ──────────────────────────────────────────────────

        /// <summary>
        /// Represents "no wheel connected" or "not yet identified".
        /// All capabilities are zeroed — this is not a real device config.
        /// </summary>
        public static WheelCapabilities None => new WheelCapabilities
        {
            Name = null,
            ShortName = null,
            ButtonLedCount = 0,
            EncoderLedCount = 0,
            RevLedCount = 0,
            FlagLedCount = 0,
            Display = DisplayType.None,
        };
    }
}
