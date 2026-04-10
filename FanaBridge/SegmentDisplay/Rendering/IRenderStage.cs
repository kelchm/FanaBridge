namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// A single stage in the segment display render pipeline.
    /// Stages are composed into a <see cref="RenderPipeline"/> at configuration
    /// time and applied sequentially each frame.
    /// </summary>
    public interface IRenderStage
    {
        /// <summary>Process a frame and return the (potentially modified) result.</summary>
        DisplayFrame Process(DisplayFrame input, RenderContext ctx);
    }
}
