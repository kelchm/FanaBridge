using System;
using System.Collections.Generic;
using SimHub.Plugins;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Result of evaluating the display layer stack.
    /// </summary>
    public class LayerStackResult
    {
        /// <summary>The layer that won evaluation, or null if no layer is active.</summary>
        public DisplayLayer Winner { get; }

        /// <summary>Raw formatted text (not aligned or truncated). Empty string if no winner.</summary>
        public string Text { get; }

        /// <summary>All layers whose condition is currently met, regardless of whether they won.</summary>
        public HashSet<DisplayLayer> ActiveLayers { get; }

        public LayerStackResult(DisplayLayer winner, string text, HashSet<DisplayLayer> activeLayers)
        {
            Winner = winner;
            Text = text ?? "";
            ActiveLayers = activeLayers ?? new HashSet<DisplayLayer>();
        }

        /// <summary>Empty result for when no layers are active.</summary>
        public static readonly LayerStackResult Empty =
            new LayerStackResult(null, "", new HashSet<DisplayLayer>());
    }

    /// <summary>
    /// Stateful evaluator for the display layer stack. Owns per-layer runtime state
    /// (OnChange timers, WhileTrue tracking) and constant-layer cycling.
    ///
    /// No hardware dependencies — pure evaluation and formatting. Each consumer
    /// (hardware controller, UI preview, future ITM legacy mode) owns its own instance.
    /// </summary>
    public class LayerStackEvaluator
    {
        private Dictionary<DisplayLayer, LayerState> _layerStates =
            new Dictionary<DisplayLayer, LayerState>();

        private int _constantCycleIndex;

        /// <summary>Resets all per-layer state and cycling position.
        /// Swaps a fresh dictionary rather than clearing in-place to avoid
        /// racing with a concurrent Evaluate() call iterating the old one.</summary>
        public void Reset()
        {
            _layerStates = new Dictionary<DisplayLayer, LayerState>();
            _constantCycleIndex = 0;
        }

        /// <summary>Cycle to the next constant base layer.</summary>
        public void NextScreen() =>
            System.Threading.Interlocked.Increment(ref _constantCycleIndex);

        /// <summary>Cycle to the previous constant base layer.</summary>
        public void PreviousScreen() =>
            System.Threading.Interlocked.Decrement(ref _constantCycleIndex);

        /// <summary>
        /// Evaluates the full layer stack and returns the winning layer with its
        /// formatted (but not aligned) display text. Called once per frame.
        /// </summary>
        public LayerStackResult Evaluate(PluginManager pm, bool gameRunning,
                                         DisplaySettings settings)
        {
            if (settings == null) return LayerStackResult.Empty;

            DisplayLayer winner = null;
            string winnerText = null;
            var active = new HashSet<DisplayLayer>();
            var activeConstants = new List<DisplayLayer>();

            foreach (var layer in settings.Layers)
            {
                if (!layer.IsEnabled) continue;

                var state = GetState(layer);

                switch (layer.Mode)
                {
                    case DisplayLayerMode.WhileTrue:
                        if (gameRunning && EvalWhileTrue(pm, layer, state))
                        {
                            active.Add(layer);
                            if (winner == null)
                            {
                                winner = layer;
                                winnerText = GetDisplayText(pm, layer, state);
                            }
                        }
                        break;

                    case DisplayLayerMode.OnChange:
                        if (gameRunning && EvalOnChange(pm, layer, state))
                        {
                            active.Add(layer);
                            if (winner == null)
                            {
                                winner = layer;
                                winnerText = GetDisplayText(pm, layer, state);
                            }
                        }
                        break;

                    case DisplayLayerMode.Expression:
                        if (gameRunning)
                        {
                            string exprText = EvaluateExpression(layer);
                            if (!string.IsNullOrEmpty(exprText))
                            {
                                active.Add(layer);
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
                active.Add(c);

            // If no overlay won, use the cycled constant layer.
            if (winner == null && activeConstants.Count > 0)
            {
                int idx = _constantCycleIndex;
                idx = ((idx % activeConstants.Count) + activeConstants.Count)
                      % activeConstants.Count;
                System.Threading.Interlocked.Exchange(ref _constantCycleIndex, idx);

                winner = activeConstants[idx];
                var state = GetState(winner);
                EvalPropertyValue(pm, winner, state);
                winnerText = GetDisplayText(pm, winner, state);
            }

            return new LayerStackResult(winner, winnerText, active);
        }

        /// <summary>
        /// Evaluates a single layer in isolation and returns its formatted display text.
        /// Used by the UI for per-card preview. Does not affect internal state.
        /// </summary>
        public string EvaluateLayer(PluginManager pm, DisplayLayer layer)
        {
            if (layer == null) return "";

            if (layer.Source == DisplaySource.FixedText)
                return layer.FixedText ?? "";

            if (layer.Source == DisplaySource.Expression)
                return EvaluateExpression(layer);

            // Prefer PropertyName when Source is Property; fall back to WatchProperty
            // for trigger-only layers (WhileTrue/OnChange without a separate display prop).
            string prop = layer.Mode == DisplayLayerMode.Constant
                ? layer.PropertyName : layer.WatchProperty;

            if (layer.Source == DisplaySource.Property
                && !string.IsNullOrEmpty(layer.PropertyName))
                prop = layer.PropertyName;

            if (string.IsNullOrEmpty(prop)) return "";

            object val = SafeGetProperty(pm, prop);
            if (val == null) return "";
            return FormatValue(val, layer.DisplayFormat);
        }

        // ── Layer condition evaluation ──────────────────────────────

        private bool EvalWhileTrue(PluginManager pm, DisplayLayer layer, LayerState state)
        {
            object val = SafeGetProperty(pm, layer.WatchProperty);
            state.CurrentValue = val;
            return IsTruthy(val);
        }

        // The null check on LastValue deliberately suppresses the first-frame trigger:
        // on initialization LastValue is null, so the first observed value is recorded
        // without activating the overlay. Only subsequent changes fire the timer.
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
            if (layer.Source == DisplaySource.Property
                && !string.IsNullOrEmpty(layer.PropertyName))
                state.CurrentValue = SafeGetProperty(pm, layer.PropertyName);
        }

        private string GetDisplayText(PluginManager pm, DisplayLayer layer, LayerState state)
        {
            if (layer.Source == DisplaySource.FixedText)
                return layer.FixedText ?? "";

            if (layer.Source == DisplaySource.Expression)
                return EvaluateExpression(layer);

            object val;
            if (layer.Source == DisplaySource.Property
                && !string.IsNullOrEmpty(layer.PropertyName))
                val = SafeGetProperty(pm, layer.PropertyName);
            else
            {
                val = state.CurrentValue;
                if (val == null && !string.IsNullOrEmpty(layer.PropertyName))
                    val = SafeGetProperty(pm, layer.PropertyName);
            }

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

        // ── Static helpers ──────────────────────────────────────────

        internal static object SafeGetProperty(PluginManager pm, string name)
        {
            if (pm == null || string.IsNullOrEmpty(name)) return null;
            try { return pm.GetPropertyValue(name); }
            catch { return null; }
        }

        internal static bool IsTruthy(object value)
        {
            if (value == null) return false;
            if (value is bool b) return b;
            if (value is int i) return i != 0;
            if (value is long l) return l != 0;
            if (value is double d) return d != 0;
            if (value is float f) return f != 0;
            if (value is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return false;
                if (bool.TryParse(s, out var parsedBool)) return parsedBool;
                if (s == "0") return false;
                return true;
            }
            return true;
        }

        // ── Formatting ──────────────────────────────────────────────

        internal static string FormatValue(object value, DisplayFormat format)
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

        internal static string FormatNumeric(object value, string fmt)
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

        internal static string FormatGear(object value)
        {
            string g = value?.ToString()?.Trim().ToUpperInvariant() ?? "";
            if (g == "R" || g == "REVERSE" || g == "-1") return "R";
            if (g == "N" || g == "NEUTRAL" || g == "0") return "N";
            int r; if (int.TryParse(g, out r)) return r.ToString();
            return g.Length > 0 ? g.Substring(0, Math.Min(g.Length, 3)) : "N";
        }

        private class LayerState
        {
            public string LastValue;
            public object CurrentValue;
            public DateTime ActiveUntil = DateTime.MinValue;
        }
    }
}
