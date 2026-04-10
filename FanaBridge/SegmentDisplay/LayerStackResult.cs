using System.Collections.Generic;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Result of evaluating the display layer stack for a single frame.
    /// </summary>
    public class LayerStackResult
    {
        /// <summary>The layer that won evaluation, or null if no layer is active.</summary>
        public SegmentDisplayLayer Winner { get; }

        /// <summary>Raw content text (not formatted/aligned). Empty string if no winner.</summary>
        public string Text { get; }

        /// <summary>All layers whose condition is currently met, regardless of whether they won.</summary>
        public HashSet<SegmentDisplayLayer> ActiveLayers { get; }

        public LayerStackResult(SegmentDisplayLayer winner, string text,
                                HashSet<SegmentDisplayLayer> activeLayers)
        {
            Winner = winner;
            Text = text ?? "";
            ActiveLayers = activeLayers ?? new HashSet<SegmentDisplayLayer>();
        }

        /// <summary>Empty result for when no layers are active.</summary>
        public static readonly LayerStackResult Empty =
            new LayerStackResult(null, "", new HashSet<SegmentDisplayLayer>());
    }
}
