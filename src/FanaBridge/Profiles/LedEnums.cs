namespace FanaBridge.Profiles
{
    /// <summary>
    /// Hardware communication channel for a single LED.
    /// Determines which HID collection and sub-command is used.
    /// </summary>
    public enum LedChannel
    {
        /// <summary>col03 subcmd 0x00 — per-LED RGB565 rev/RPM LEDs.</summary>
        RevRgb,
        /// <summary>col03 subcmd 0x01 — per-LED RGB565 flag/status LEDs.</summary>
        FlagRgb,
        /// <summary>col03 subcmd 0x02 — per-LED RGB565 button LEDs.</summary>
        ButtonRgb,
        /// <summary>col03 subcmd 0x03 — intensity-only auxiliary slots in the button payload (no color counterpart).</summary>
        ButtonAuxIntensity,
        /// <summary>col01 subcmd 0x08 — per-LED on/off bitmask for non-RGB rev LEDs.</summary>
        LegacyRevOnOff,
        /// <summary>col01 subcmd 0x08 — single RGB333 color for the entire LED strip.</summary>
        LegacyRevStripe,
        /// <summary>col01 subcmd 0x0A — per-LED 3-bit color for rev LEDs (1 bit per R/G/B, 7 colors + off).</summary>
        LegacyRev3Bit,
        /// <summary>col01 subcmd 0x0B — per-LED 3-bit color for flag LEDs (1 bit per R/G/B, 7 colors + off).</summary>
        LegacyFlag3Bit,
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
