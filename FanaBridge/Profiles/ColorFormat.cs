namespace FanaBridge.Profiles
{
    /// <summary>
    /// Pixel encoding for the Color LED channel (subcmd 0x02).
    /// Most Fanatec hardware uses standard RGB565 (5-6-5 bit layout),
    /// but some modules only read 5 green bits (RGB555).
    /// </summary>
    public enum ColorFormat
    {
        /// <summary>Standard 16-bit: 5 red, 6 green, 5 blue.</summary>
        Rgb565,
        /// <summary>5-5-5 green: only 5 green bits are read by the LED controller.
        /// The MSB of the 6-bit green field is ignored/misinterpreted.</summary>
        Rgb555,
    }
}
