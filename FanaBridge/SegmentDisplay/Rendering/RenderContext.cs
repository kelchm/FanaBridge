using FanaBridge.Shared;

namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// Contextual data available to all pipeline stages during a single frame.
    /// </summary>
    public class RenderContext
    {
        /// <summary>Milliseconds since the winning layer became active.</summary>
        public long ElapsedMs { get; set; }

        /// <summary>Milliseconds since the last frame (~16ms at 60fps).</summary>
        public long FrameMs { get; set; }

        /// <summary>Property provider for resolving SimHub values.</summary>
        public IPropertyProvider Props { get; set; }

        /// <summary>NCalc expression engine.</summary>
        public INCalcEngine NCalc { get; set; }
    }
}
