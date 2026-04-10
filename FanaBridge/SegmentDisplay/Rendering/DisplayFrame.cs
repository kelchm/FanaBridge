namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// Data carried through the render pipeline for a single frame.
    /// Each stage reads and produces a new frame.
    /// </summary>
    public struct DisplayFrame
    {
        /// <summary>Current text content (modified by formatters, align, overflow stages).</summary>
        public string Text;

        /// <summary>Encoded 7-segment bytes (set by the final encoding stage). Null until then.</summary>
        public byte[] Segments;

        /// <summary>When true, effects have suppressed output (blink OFF phase). The controller should blank the display.</summary>
        public bool SuppressOutput;
    }
}
