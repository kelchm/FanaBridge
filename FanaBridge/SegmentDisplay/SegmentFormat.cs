namespace FanaBridge.SegmentDisplay
{
    /// <summary>How a content value is formatted for the 3-character 7-segment display.</summary>
    public enum SegmentFormat
    {
        /// <summary>Rounded integer, right-aligned. E.g. speed, lap, position.</summary>
        Number,

        /// <summary>One decimal place, right-aligned. E.g. fuel %.</summary>
        Decimal,

        /// <summary>Time as configurable TimeSpan format, right-aligned.</summary>
        Time,

        /// <summary>Gear mapping (R/N/1-9), centered.</summary>
        Gear,

        /// <summary>Raw text, left-aligned, scrolls if longer than 3 characters.</summary>
        Text,
    }
}
