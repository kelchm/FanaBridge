namespace FanaBridge.SegmentDisplay
{
    /// <summary>How text longer than 3 characters is handled on the display.</summary>
    public enum OverflowType
    {
        /// <summary>Resolved from content format: Text scrolls, Time truncates left, others truncate right.</summary>
        Auto,

        /// <summary>Text scrolls across the display with configurable speed.</summary>
        Scroll,

        /// <summary>Drop leftmost characters, keep rightmost.</summary>
        TruncateLeft,

        /// <summary>Drop rightmost characters, keep leftmost.</summary>
        TruncateRight,
    }
}
