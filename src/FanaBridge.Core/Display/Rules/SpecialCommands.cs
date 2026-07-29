namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Firmware OLED screen selector (col01 subcmd 0x50). Wire spellings are short
    /// camelCase tokens owned here — not the enum member names — so the document
    /// stays compact and stable.
    /// </summary>
    public enum SpecialCommand
    {
        /// <summary>Lenient-load fallback — the rule is degraded at load; raw text is preserved.</summary>
        Unknown = 0,
        LogoScreen,
        LogoInvertedScreen,
        WhiteScreen,
        BlankScreen,
    }

    /// <summary>
    /// Single place for special-command wire spellings, pattern bytes, and display labels.
    /// UI and the stack never hard-code pattern bytes or user-facing screen names elsewhere.
    /// </summary>
    public static class SpecialCommands
    {
        /// <summary>Firmware pattern: blank OLED.</summary>
        public const byte PatternBlank = 0x00;
        /// <summary>Firmware pattern: Fanatec logo (white on black).</summary>
        public const byte PatternLogo = 0x01;
        /// <summary>Firmware pattern: logo inverted.</summary>
        public const byte PatternLogoInverted = 0x02;
        /// <summary>Firmware pattern: full white.</summary>
        public const byte PatternWhite = 0x03;

        /// <summary>col01 sub-command for the OLED screen selector.</summary>
        public const byte Subcommand = 0x50;

        /// <summary>
        /// Re-send interval for a HELD screen: the firmware reverts a selected screen
        /// after roughly 60 s without a refresh (hardware-observed), so a latched
        /// command re-sends this often — comfortably inside the revert window.
        /// </summary>
        public const int KeepaliveMs = 15000;

        /// <summary>Document spelling for <paramref name="command"/>, or null for Unknown.</summary>
        public static string Write(SpecialCommand command)
        {
            switch (command)
            {
                case SpecialCommand.LogoScreen: return "logo";
                case SpecialCommand.LogoInvertedScreen: return "logoInverted";
                case SpecialCommand.WhiteScreen: return "white";
                case SpecialCommand.BlankScreen: return "blank";
                default: return null;
            }
        }

        /// <summary>Parses a document spelling (case-insensitive), or
        /// <see cref="SpecialCommand.Unknown"/> when missing/unrecognized.</summary>
        public static SpecialCommand Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SpecialCommand.Unknown;
            if (string.Equals(text, "logo", System.StringComparison.OrdinalIgnoreCase))
                return SpecialCommand.LogoScreen;
            if (string.Equals(text, "logoInverted", System.StringComparison.OrdinalIgnoreCase))
                return SpecialCommand.LogoInvertedScreen;
            if (string.Equals(text, "white", System.StringComparison.OrdinalIgnoreCase))
                return SpecialCommand.WhiteScreen;
            if (string.Equals(text, "blank", System.StringComparison.OrdinalIgnoreCase))
                return SpecialCommand.BlankScreen;
            return SpecialCommand.Unknown;
        }

        /// <summary>True when <paramref name="text"/> is a known special-command spelling
        /// (used to strip special tokens from cycle page lists).</summary>
        public static bool IsKnownSpelling(string text)
            => Parse(text) != SpecialCommand.Unknown;

        /// <summary>Firmware pattern byte for a known command (0 for Unknown).</summary>
        public static byte PatternOf(SpecialCommand command)
        {
            switch (command)
            {
                case SpecialCommand.LogoScreen: return PatternLogo;
                case SpecialCommand.LogoInvertedScreen: return PatternLogoInverted;
                case SpecialCommand.WhiteScreen: return PatternWhite;
                case SpecialCommand.BlankScreen: return PatternBlank;
                default: return PatternBlank;
            }
        }

        /// <summary>User-facing screen label ("Fanatec logo", …), or "?" for Unknown.</summary>
        public static string Label(SpecialCommand command)
        {
            switch (command)
            {
                case SpecialCommand.LogoScreen: return "Fanatec logo";
                case SpecialCommand.LogoInvertedScreen: return "Fanatec logo (inverted)";
                case SpecialCommand.WhiteScreen: return "White screen";
                case SpecialCommand.BlankScreen: return "Blank screen";
                default: return "?";
            }
        }
    }
}
