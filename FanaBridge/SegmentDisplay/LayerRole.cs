namespace FanaBridge.SegmentDisplay
{
    /// <summary>Stack behavior of a display layer.</summary>
    public enum LayerRole
    {
        /// <summary>User-cycled default data page.</summary>
        Screen,

        /// <summary>Condition-triggered interrupt that overrides screens.</summary>
        Overlay,
    }
}
