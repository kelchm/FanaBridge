using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using GameReaderCommon;
using SimHub.Plugins;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Unified display manager for the Fanatec 3-digit 7-segment display.
    /// Evaluates a priority-ordered stack of <see cref="DisplayLayer"/> entries.
    /// The first active layer (top-to-bottom) wins. Constant layers cycle
    /// among themselves via NextScreen/PreviousScreen.
    /// </summary>
    public class FanatecDisplayManager
    {
        private readonly DisplayEncoder _display;
        private DisplaySettings _settings;

        // Per-layer runtime state (keyed by layer reference).
        private Dictionary<DisplayLayer, LayerState> _layerStates = new Dictionary<DisplayLayer, LayerState>();

        // Active layer tracking for UI indicators.
        private DisplayLayer _winningLayer;
        private HashSet<DisplayLayer> _activeLayers = new HashSet<DisplayLayer>();

        /// <summary>The layer currently controlling the display, or null.</summary>
        public DisplayLayer WinningLayer => _winningLayer;

        /// <summary>Whether a layer is currently active (condition met), regardless of whether it won.</summary>
        public bool IsLayerActive(DisplayLayer layer) => _activeLayers.Contains(layer);

        // Cycling among Constant layers.
        private int _constantCycleIndex;

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

        public FanatecDisplayManager(DisplayEncoder display, DisplaySettings settings)
        {
            _display = display;
            _settings = settings ?? DisplaySettings.CreateDefault();
        }

        public void UpdateSettings(DisplaySettings settings)
        {
            _settings = settings ?? DisplaySettings.CreateDefault();
            _layerStates.Clear();
        }

        /// <summary>Current displayed text.</summary>
        public string CurrentText => _currentText;

        /// <summary>Name of the currently active layer.</summary>
        public string ActiveScreenName => _activeLayerName;

        /// <summary>Cycle to the next constant base layer.</summary>
        public void NextScreen() => _constantCycleIndex++;

        /// <summary>Cycle to the previous constant base layer.</summary>
        public void PreviousScreen() => _constantCycleIndex--;

        /// <summary>
        /// Evaluates the layer stack and updates the display. Called once per frame.
        /// </summary>
        public void Update(PluginManager pluginManager, GameData data)
        {
            bool gameRunning = data.GameRunning && data.NewData != null;

            // Scan layers top-to-bottom for the first active one.
            DisplayLayer winner = null;
            string winnerText = null;
            _activeLayers.Clear();

            // Track constant layers for cycling.
            var activeConstants = new List<DisplayLayer>();

            foreach (var layer in _settings.Layers)
            {
                if (!layer.IsEnabled) continue;

                var state = GetState(layer);

                switch (layer.Mode)
                {
                    case DisplayLayerMode.WhileTrue:
                        if (gameRunning && EvalWhileTrue(pluginManager, layer, state))
                        {
                            _activeLayers.Add(layer);
                            if (winner == null)
                            {
                                winner = layer;
                                winnerText = GetDisplayText(pluginManager, layer, state);
                            }
                        }
                        break;

                    case DisplayLayerMode.OnChange:
                        if (gameRunning && EvalOnChange(pluginManager, layer, state))
                        {
                            _activeLayers.Add(layer);
                            if (winner == null)
                            {
                                winner = layer;
                                winnerText = GetDisplayText(pluginManager, layer, state);
                            }
                        }
                        break;

                    case DisplayLayerMode.Expression:
                        if (gameRunning)
                        {
                            string exprText = EvaluateExpression(layer);
                            if (!string.IsNullOrEmpty(exprText))
                            {
                                _activeLayers.Add(layer);
                                if (winner == null)
                                {
                                    winner = layer;
                                    winnerText = exprText;
                                }
                            }
                        }
                        break;

                    case DisplayLayerMode.Constant:
                        bool visible = (gameRunning && layer.ShowWhenRunning)
                                    || (!gameRunning && layer.ShowWhenIdle);
                        if (visible)
                            activeConstants.Add(layer);
                        break;
                }
            }

            // Mark all visible constants as active.
            foreach (var c in activeConstants)
                _activeLayers.Add(c);

            // If no overlay won, use the cycled constant layer.
            if (winner == null && activeConstants.Count > 0)
            {
                _constantCycleIndex = ((_constantCycleIndex % activeConstants.Count) + activeConstants.Count) % activeConstants.Count;
                winner = activeConstants[_constantCycleIndex];
                var state = GetState(winner);
                EvalPropertyValue(pluginManager, winner, state);
                winnerText = GetDisplayText(pluginManager, winner, state);
            }

            _winningLayer = winner;

            // Display the winner.
            if (winner == null)
            {
                SendText("   ");
                _activeLayerName = "";
                return;
            }

            _activeLayerName = winner.Name ?? "";
            string aligned = AlignText(winnerText ?? "   ", winner.DisplayFormat);
            if (winner.IsGearFormat)
            {
                ResetScroll();
                SendGear(winnerText ?? "N");
            }
            else
            {
                SendTextOrScroll(aligned);
            }
        }

        /// <summary>Blanks the display and resets all state.</summary>
        public void Clear()
        {
            _display.ClearDisplay();
            _currentText = "";
            _activeLayerName = "";
            _layerStates.Clear();
            _lastSentKey = null;
            ResetScroll();
        }

        // ── Layer evaluation ─────────────────────────────────────────

        private bool EvalWhileTrue(PluginManager pm, DisplayLayer layer, LayerState state)
        {
            object val = SafeGetProperty(pm, layer.WatchProperty);
            state.CurrentValue = val;
            return IsTruthy(val);
        }

        private bool EvalOnChange(PluginManager pm, DisplayLayer layer, LayerState state)
        {
            object val = SafeGetProperty(pm, layer.WatchProperty);
            string current = val?.ToString() ?? "";
            state.CurrentValue = val;

            if (state.LastValue != null && current != state.LastValue && current.Length > 0)
                state.ActiveUntil = DateTime.UtcNow.AddMilliseconds(layer.DurationMs);

            state.LastValue = current;
            return DateTime.UtcNow < state.ActiveUntil;
        }

        private void EvalPropertyValue(PluginManager pm, DisplayLayer layer, LayerState state)
        {
            if (layer.Source == DisplaySource.Property && !string.IsNullOrEmpty(layer.PropertyName))
                state.CurrentValue = SafeGetProperty(pm, layer.PropertyName);
        }

        private string GetDisplayText(PluginManager pm, DisplayLayer layer, LayerState state)
        {
            if (layer.Source == DisplaySource.FixedText)
                return layer.FixedText ?? "";

            if (layer.Source == DisplaySource.Expression)
                return EvaluateExpression(layer);

            object val = state.CurrentValue;
            if (val == null && !string.IsNullOrEmpty(layer.PropertyName))
                val = SafeGetProperty(pm, layer.PropertyName);

            if (val == null) return "";
            return FormatValue(val, layer.DisplayFormat);
        }

        /// <summary>
        /// Evaluates a single layer and returns what it would display right now.
        /// Used by the UI for per-layer preview.
        /// </summary>
        public string EvaluateLayerPreview(PluginManager pm, DisplayLayer layer)
        {
            if (layer.Source == DisplaySource.FixedText)
                return layer.FixedText ?? "";

            if (layer.Source == DisplaySource.Expression)
                return EvaluateExpression(layer);

            string prop = layer.Mode == DisplayLayerMode.Constant
                ? layer.PropertyName : layer.WatchProperty;

            if (layer.Source == DisplaySource.Property && !string.IsNullOrEmpty(layer.PropertyName))
                prop = layer.PropertyName;

            if (string.IsNullOrEmpty(prop)) return "";

            object val = SafeGetProperty(pm, prop);
            if (val == null) return "";
            return FormatValue(val, layer.DisplayFormat);
        }

        private string EvaluateExpression(DisplayLayer layer)
        {
            if (string.IsNullOrEmpty(layer.Expression)) return "";
            var engine = FanatecPlugin.Instance?.NCalcEngine;
            if (engine == null) return "";
            try
            {
                var expr = new ExpressionValue { Expression = layer.Expression };
                var result = engine.ParseValue(expr);
                if (result == null) return "";
                return FormatValue(result, layer.DisplayFormat);
            }
            catch { return "ERR"; }
        }

        private LayerState GetState(DisplayLayer layer)
        {
            if (!_layerStates.TryGetValue(layer, out var state))
            {
                state = new LayerState();
                _layerStates[layer] = state;
            }
            return state;
        }

        private static object SafeGetProperty(PluginManager pm, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { return pm.GetPropertyValue(name); }
            catch { return null; }
        }

        private static bool IsTruthy(object value)
        {
            if (value == null) return false;
            if (value is bool b) return b;
            if (value is int i) return i != 0;
            if (value is long l) return l != 0;
            if (value is double d) return d != 0;
            if (value is float f) return f != 0;
            if (value is string s) return s.Length > 0 && s != "0" && s != "False";
            return true;
        }

        private class LayerState
        {
            public string LastValue;
            public object CurrentValue;
            public DateTime ActiveUntil = DateTime.MinValue;
        }

        // ── Formatting ───────────────────────────────────────────────

        private static string FormatValue(object value, DisplayFormat format)
        {
            switch (format)
            {
                case DisplayFormat.Gear:
                    return FormatGear(value);
                case DisplayFormat.Number:
                    return FormatNumeric(value, "0");
                case DisplayFormat.Decimal:
                    return FormatNumeric(value, "0.0");
                case DisplayFormat.Time:
                    if (value is TimeSpan ts) return ts.ToString("ss\\.f");
                    return FormatNumeric(value, "0.0");
                case DisplayFormat.Text:
                default:
                    return value?.ToString() ?? "";
            }
        }

        private static string FormatNumeric(object value, string fmt)
        {
            try
            {
                if (value is double d) return d.ToString(fmt);
                if (value is float f) return f.ToString(fmt);
                if (value is int i) return i.ToString(fmt);
                if (value is long l) return l.ToString(fmt);
                return value.ToString();
            }
            catch { return value.ToString(); }
        }

        /// <summary>
        /// Aligns a formatted string for the 3-character display.
        /// Gear = centered, Number/Decimal/Time = right-aligned, Text = left-aligned.
        /// </summary>
        internal static string AlignText(string text, DisplayFormat format)
        {
            if (string.IsNullOrEmpty(text) || text.Length >= 3) return text;
            switch (format)
            {
                case DisplayFormat.Gear:
                    // Center: pad equally on both sides
                    return text.Length == 1 ? " " + text + " " :
                           text.Length == 2 ? " " + text : text;
                case DisplayFormat.Number:
                case DisplayFormat.Decimal:
                case DisplayFormat.Time:
                    // Right-align
                    return text.PadLeft(3);
                case DisplayFormat.Text:
                default:
                    return text;
            }
        }

        private static string FormatGear(object value)
        {
            string g = value?.ToString()?.Trim().ToUpperInvariant() ?? "";
            if (g == "R" || g == "REVERSE" || g == "-1") return "R";
            if (g == "N" || g == "NEUTRAL" || g == "0") return "N";
            int r; if (int.TryParse(g, out r)) return r.ToString();
            return g.Length > 0 ? g.Substring(0, Math.Min(g.Length, 3)) : "N";
        }

        internal static string TruncateTo3(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int chars = 0, cutoff = text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                if ((text[i] == '.' || text[i] == ',') && chars > 0) continue;
                chars++;
                if (chars > 3) { cutoff = i; break; }
            }
            return text.Substring(0, cutoff);
        }

        // ── Display output ───────────────────────────────────────────

        private void SendGear(string gearText)
        {
            string key = "G:" + gearText;
            if (key == _lastSentKey) return;
            _lastSentKey = key;
            _display.DisplayGear(ParseGearInt(gearText));
            _currentText = gearText;
        }

        private void SendText(string text)
        {
            string key = "T:" + text;
            if (key == _lastSentKey) return;
            _lastSentKey = key;
            _currentText = text;
            _display.DisplayText(text);
        }

        private void SendTextOrScroll(string text)
        {
            var encoded = EncodeText(text);
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

            int step = Math.Max(1, _settings.ScrollSpeedMs / 16);
            if (++_scrollFrameCounter >= step)
            {
                _scrollFrameCounter = 0;
                if (_scrollPos > _scrollFrames.Count - 3) _scrollPos = 0;
                _display.SetDisplay(_scrollFrames[_scrollPos], _scrollFrames[_scrollPos + 1], _scrollFrames[_scrollPos + 2]);
                _lastSentKey = null;
                _currentText = text;
                _scrollPos++;
            }
        }

        private void ResetScroll()
        {
            _scrollFrames = null;
            _scrollSourceText = null;
            _scrollPos = 0;
            _scrollFrameCounter = 0;
        }

        internal static List<byte> EncodeText(string text)
        {
            var encoded = new List<byte>();
            if (string.IsNullOrEmpty(text)) return encoded;
            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && encoded.Count > 0)
                    encoded[encoded.Count - 1] |= SevenSegment.Dot;
                else
                    encoded.Add(SevenSegment.CharToSegment(ch));
            }
            return encoded;
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
