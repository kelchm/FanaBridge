using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using GameReaderCommon;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Controller for the Fanatec 3-digit 7-segment display. Delegates layer
    /// evaluation to a <see cref="LayerStackEvaluator"/> and handles the rendering
    /// pipeline (alignment, scrolling) and hardware output via <see cref="SegmentEncoder"/>.
    /// </summary>
    public class SegmentDisplayController
    {
        private readonly object _lock = new object();
        private readonly SegmentEncoder _display;
        private readonly LayerStackEvaluator _evaluator = new LayerStackEvaluator();
        private DisplaySettings _settings;

        // Published evaluation snapshots for UI reads (volatile for cross-thread safety).
        private volatile DisplayLayer _winningLayer;
        private volatile HashSet<DisplayLayer> _activeLayers = new HashSet<DisplayLayer>();

        // Scrolling state.
        private List<byte> _scrollFrames;
        private int _scrollPos;
        private int _scrollFrameCounter;
        private string _scrollSourceText;

        // Rate limiter.
        private string _lastSentKey;

        // Public state.
        private string _currentText = "";
        private string _activeLayerName = "";

        public SegmentDisplayController(SegmentEncoder display, DisplaySettings settings)
        {
            _display = display;
            _settings = settings ?? DisplaySettings.CreateDefault();
        }

        /// <summary>The evaluator instance, exposed for UI preview access.</summary>
        public LayerStackEvaluator Evaluator => _evaluator;

        /// <summary>The layer currently controlling the display, or null.</summary>
        public DisplayLayer WinningLayer => _winningLayer;

        /// <summary>Whether a layer is currently active (condition met), regardless of whether it won.</summary>
        public bool IsLayerActive(DisplayLayer layer) => _activeLayers.Contains(layer);

        /// <summary>Current displayed text.</summary>
        public string CurrentText => _currentText;

        /// <summary>Name of the currently active layer.</summary>
        public string ActiveScreenName => _activeLayerName;

        /// <summary>Cycle to the next constant base layer.</summary>
        public void NextScreen() => _evaluator.NextScreen();

        /// <summary>Cycle to the previous constant base layer.</summary>
        public void PreviousScreen() => _evaluator.PreviousScreen();

        public void UpdateSettings(DisplaySettings settings)
        {
            lock (_lock)
            {
                _settings = settings ?? DisplaySettings.CreateDefault();
                _evaluator.Reset();
                _winningLayer = null;
                _activeLayers = new HashSet<DisplayLayer>();
                _lastSentKey = null;
                _currentText = "";
                _activeLayerName = "";
                ResetScroll();
            }
        }

        /// <summary>
        /// Evaluates the layer stack and updates the display. Called once per frame.
        /// </summary>
        public void Update(PluginManager pluginManager, GameData data)
        {
            lock (_lock)
            {
                bool gameRunning = data.GameRunning && data.NewData != null;
                var result = _evaluator.Evaluate(pluginManager, gameRunning, _settings);

                // Publish snapshots so UI reads see consistent state.
                _activeLayers = result.ActiveLayers;
                _winningLayer = result.Winner;

                if (result.Winner == null)
                {
                    SendText("   ");
                    _activeLayerName = "";
                    return;
                }

                var winner = result.Winner;
                _activeLayerName = winner.Name ?? "";

                if (winner.IsGearFormat)
                {
                    ResetScroll();
                    SendGear(result.Text ?? "N");
                }
                else
                {
                    var overflow = SegmentRendering.ResolveOverflow(winner.Overflow, winner.DisplayFormat);
                    string text = SegmentRendering.ApplyOverflow(result.Text ?? "   ", overflow);
                    string aligned = SegmentRendering.AlignText(text, winner.DisplayFormat);
                    if (overflow != OverflowStrategy.Scroll)
                    {
                        ResetScroll();
                        SendText(aligned);
                    }
                    else
                    {
                        SendTextOrScroll(aligned, winner.ScrollSpeedMs);
                    }
                }
            }
        }

        /// <summary>Blanks the display and resets all state.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _display.ClearDisplay();
                _winningLayer = null;
                _activeLayers = new HashSet<DisplayLayer>();
                _currentText = "";
                _activeLayerName = "";
                _evaluator.Reset();
                _lastSentKey = null;
                ResetScroll();
            }
        }

        // ── Display output ───────────────────────────────────────────

        private void SendGear(string gearText)
        {
            string key = "G:" + gearText;
            if (key == _lastSentKey) return;
            if (_display.DisplayGear(ParseGearInt(gearText)))
            {
                _lastSentKey = key;
                _currentText = gearText;
            }
            else
            {
                _lastSentKey = null;
            }
        }

        private void SendText(string text)
        {
            string key = "T:" + text;
            if (key == _lastSentKey) return;
            if (_display.DisplayText(text))
            {
                _lastSentKey = key;
                _currentText = text;
            }
            else
            {
                _lastSentKey = null;
            }
        }

        private void SendTextOrScroll(string text, int layerScrollSpeedMs = 0)
        {
            var encoded = SegmentRendering.EncodeText(text);
            if (encoded.Count <= 3) { ResetScroll(); SendText(text); return; }

            if (text != _scrollSourceText)
            {
                _scrollSourceText = text;
                _scrollFrames = new List<byte> { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank };
                _scrollFrames.AddRange(encoded);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollPos = 0;
                _scrollFrameCounter = 0;
            }

            int speedMs = layerScrollSpeedMs > 0 ? layerScrollSpeedMs : _settings.ScrollSpeedMs;
            int step = Math.Max(1, speedMs / 16);
            if (++_scrollFrameCounter >= step)
            {
                _scrollFrameCounter = 0;
                if (_scrollPos > _scrollFrames.Count - 3) _scrollPos = 0;
                if (_display.SetDisplay(_scrollFrames[_scrollPos], _scrollFrames[_scrollPos + 1], _scrollFrames[_scrollPos + 2]))
                {
                    _lastSentKey = null;
                    _currentText = text;
                    _scrollPos++;
                }
            }
        }

        private void ResetScroll()
        {
            _scrollFrames = null;
            _scrollSourceText = null;
            _scrollPos = 0;
            _scrollFrameCounter = 0;
        }

        private static int ParseGearInt(string g)
        {
            if (string.IsNullOrEmpty(g)) return 0;
            g = g.Trim().ToUpperInvariant();
            if (g == "R") return -1;
            if (g == "N") return 0;
            int r; if (int.TryParse(g, out r)) return r;
            return 0;
        }
    }
}
