namespace FanaBridge.Core
{
    /// <summary>
    /// Hardware communication channel for a single LED.
    /// Determines which col03 sub-command and encoding is used.
    /// </summary>
    public enum LedChannel
    {
        /// <summary>subcmd 0x00 — full RGB565 (Rev/RPM LEDs).</summary>
        Rev,
        /// <summary>subcmd 0x01 — full RGB565 (Flag/status LEDs).</summary>
        Flag,
        /// <summary>subcmd 0x02 — full RGB565 (button-area color LEDs).</summary>
        Color,
        /// <summary>subcmd 0x03 — 3-bit intensity only (monochrome LEDs).</summary>
        Mono,
    }

    /// <summary>
    /// Semantic role of a single LED.  Drives SimHub categorization
    /// and can be used for future ASTR auto-generation.
    /// </summary>
    public enum LedRole
    {
        /// <summary>RPM/shift indicator.</summary>
        Rev,
        /// <summary>Status/warning flag indicator.</summary>
        Flag,
        /// <summary>Button back-light.</summary>
        Button,
        /// <summary>Encoder knob indicator.</summary>
        Encoder,
        /// <summary>General-purpose indicator (not tied to an input).</summary>
        Indicator,
    }
}
