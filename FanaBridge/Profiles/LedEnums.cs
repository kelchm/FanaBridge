namespace FanaBridge.Profiles
{
    /// <summary>
    /// Hardware communication channel for a single LED.
    /// Determines which HID collection and sub-command is used.
    /// </summary>
    public enum LedChannel
    {
        /// <summary>col03 subcmd 0x00 — per-LED RGB565 (Rev/RPM LEDs).</summary>
        Rev,
        /// <summary>col03 subcmd 0x01 — per-LED RGB565 (Flag/status LEDs).</summary>
        Flag,
        /// <summary>col03 subcmd 0x02 — per-LED RGB565 (button-area color LEDs).</summary>
        Color,
        /// <summary>col03 subcmd 0x03 — 3-bit intensity only (monochrome LEDs).</summary>
        Mono,
        /// <summary>col01 subcmd 0x08 — 9-bit bitmask, per-LED on/off (non-RGB rev LEDs).</summary>
        LegacyRev,
        /// <summary>col01 subcmd 0x08 — single RGB333 color for the entire strip.</summary>
        RevStripe,
        /// <summary>col01 subcmd 0x0A — per-LED RGB boolean (8 colors per LED).</summary>
        LegacyRevRgb,
        /// <summary>col01 subcmd 0x08 — global RGB333 color + per-LED on/off bitmask.</summary>
        LegacyRevGlobal,
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
