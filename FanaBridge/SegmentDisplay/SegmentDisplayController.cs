using System.Collections.Generic;
using FanaBridge.Shared;
using FanaBridge.SegmentDisplay.Rendering;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Orchestrates the segment display system: evaluates the layer stack,
    /// runs the winning layer through its render pipeline, and sends output
    /// to the display hardware. Thin — owns no scroll state, format logic,
    /// or gear branching; those live in pipeline stages.
    /// </summary>
    public class SegmentDisplayController
    {
        private readonly ISegmentDisplay _display;
        private readonly LayerStackEvaluator _evaluator;

        private SegmentDisplaySettings _settings;
        private Dictionary<SegmentDisplayLayer, RenderPipeline> _pipelines;
        private Dictionary<SegmentDisplayLayer, long> _activeSinceMs;

        private long _frameTimeMs;
        private long _lastFrameMs;

        // Published state for UI reads.
        private volatile SegmentDisplayLayer _winningLayer;
        private volatile HashSet<SegmentDisplayLayer> _activeLayers = new HashSet<SegmentDisplayLayer>();

        private string _currentText = "";
        private string _activeLayerName = "";

        public SegmentDisplayController(ISegmentDisplay display, SegmentDisplaySettings settings)
            : this(display, settings, new LayerStackEvaluator())
        {
        }

        internal SegmentDisplayController(ISegmentDisplay display, SegmentDisplaySettings settings,
                                          LayerStackEvaluator evaluator)
        {
            _display = display;
            _evaluator = evaluator;
            _settings = settings ?? SegmentDisplaySettings.CreateDefault();
            RebuildPipelines();
        }

        /// <summary>The evaluator instance, exposed for UI preview access.</summary>
        public LayerStackEvaluator Evaluator { get { return _evaluator; } }

        /// <summary>The layer currently controlling the display, or null.</summary>
        public SegmentDisplayLayer WinningLayer { get { return _winningLayer; } }

        /// <summary>Set of all layers whose condition is currently met.</summary>
        public HashSet<SegmentDisplayLayer> ActiveLayers { get { return _activeLayers; } }

        /// <summary>Current displayed text (raw, pre-pipeline).</summary>
        public string CurrentText { get { return _currentText; } }

        /// <summary>Name of the currently active layer.</summary>
        public string ActiveScreenName { get { return _activeLayerName; } }

        /// <summary>Cycle to the next screen.</summary>
        public void NextScreen() { _evaluator.NextScreen(); }

        /// <summary>Cycle to the previous screen.</summary>
        public void PreviousScreen() { _evaluator.PreviousScreen(); }

        /// <summary>Replace settings and rebuild pipelines.</summary>
        public void UpdateSettings(SegmentDisplaySettings settings)
        {
            _settings = settings ?? SegmentDisplaySettings.CreateDefault();
            RebuildPipelines();
            _evaluator.Reset();
            _winningLayer = null;
            _activeLayers = new HashSet<SegmentDisplayLayer>();
            _currentText = "";
            _activeLayerName = "";
        }

        /// <summary>Clear the display and reset state.</summary>
        public void Clear()
        {
            _display.Clear();
            _winningLayer = null;
            _currentText = "";
            _activeLayerName = "";
        }

        /// <summary>
        /// Called once per frame. Evaluates the layer stack, runs the render
        /// pipeline, and sends output to the display.
        /// </summary>
        /// <param name="props">SimHub property provider.</param>
        /// <param name="ncalc">NCalc expression engine, or null.</param>
        /// <param name="gameRunning">Whether a game session is active.</param>
        /// <param name="nowMs">Current timestamp in milliseconds.</param>
        public void Update(IPropertyProvider props, INCalcEngine ncalc,
                           bool gameRunning, long nowMs)
        {
            _frameTimeMs = nowMs - _lastFrameMs;
            _lastFrameMs = nowMs;

            _display.Keepalive();

            var result = _evaluator.Evaluate(props, ncalc, gameRunning, _settings);
            _winningLayer = result.Winner;
            _activeLayers = result.ActiveLayers;

            if (result.Winner == null)
            {
                _currentText = "";
                _activeLayerName = "";
                _display.Clear();
                return;
            }

            _activeLayerName = result.Winner.Name ?? "";

            // DeviceCommandContent bypasses the pipeline
            if (result.Winner.Content is DeviceCommandContent cmd)
            {
                _display.SendCommand(cmd.Command);
                _currentText = "";
                return;
            }

            _currentText = result.Text;

            // Track when each layer became active for effect timing
            long elapsedMs = TrackActiveSince(result.Winner, nowMs);

            // Run the pipeline
            RenderPipeline pipeline;
            if (!_pipelines.TryGetValue(result.Winner, out pipeline))
            {
                pipeline = RenderPipeline.ForLayer(result.Winner);
                _pipelines[result.Winner] = pipeline;
            }

            var ctx = new RenderContext
            {
                ElapsedMs = elapsedMs,
                FrameMs = _frameTimeMs,
                Props = props,
                NCalc = ncalc,
            };

            var frame = new SegmentDisplayFrame { Text = result.Text };
            frame = pipeline.Process(frame, ctx);

            if (frame.SuppressOutput)
            {
                _display.Clear();
            }
            else if (frame.Segments != null && frame.Segments.Length >= 3)
            {
                _display.Send(frame.Segments[0], frame.Segments[1], frame.Segments[2]);
            }
        }

        // ── Internals ───────────────────────────────────────────────

        private long TrackActiveSince(SegmentDisplayLayer layer, long nowMs)
        {
            long since;
            if (!_activeSinceMs.TryGetValue(layer, out since))
            {
                since = nowMs;
                _activeSinceMs[layer] = since;
            }

            // Clean up entries for layers that are no longer the winner
            // (keeps the dict from growing unbounded across settings changes)
            if (_activeSinceMs.Count > _settings.Layers.Count + 4)
            {
                var toRemove = new List<SegmentDisplayLayer>();
                foreach (var kvp in _activeSinceMs)
                {
                    if (kvp.Key != layer) toRemove.Add(kvp.Key);
                }
                foreach (var key in toRemove)
                    _activeSinceMs.Remove(key);
            }

            return nowMs - since;
        }

        private void RebuildPipelines()
        {
            _pipelines = new Dictionary<SegmentDisplayLayer, RenderPipeline>();
            _activeSinceMs = new Dictionary<SegmentDisplayLayer, long>();

            if (_settings == null) return;
            foreach (var layer in _settings.Layers)
            {
                _pipelines[layer] = RenderPipeline.ForLayer(layer);
            }
        }
    }
}
