namespace FanaBridge.Profiles
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
}
