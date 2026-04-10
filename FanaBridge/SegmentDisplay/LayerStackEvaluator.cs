using System;
using System.Collections.Generic;
using System.Diagnostics;
using FanaBridge.Shared;
using FanaBridge.Shared.Conditions;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Stateful evaluator for the segment display layer stack. Owns per-layer
    /// activation state and screen cycling. No hardware dependencies — pure
    /// evaluation and content resolution.
    ///
    /// Each consumer (hardware controller, UI preview, future ITM legacy mode)
    /// owns its own instance.
    /// </summary>
    public class LayerStackEvaluator
    {
        private Dictionary<SegmentDisplayLayer, ActivationState> _states =
            new Dictionary<SegmentDisplayLayer, ActivationState>();

        private int _screenCycleIndex;
        private readonly Func<long> _clock;

        public LayerStackEvaluator() : this(DefaultClock) { }

        /// <summary>Creates an evaluator with an injectable clock (for testing).</summary>
        internal LayerStackEvaluator(Func<long> clock)
        {
            _clock = clock ?? DefaultClock;
        }

        /// <summary>
        /// True when the most recent evaluation had an active overlay as the winner.
        /// Used by the ITM legacy page bridge to detect when to switch pages.
        /// </summary>
        public bool HasActiveOverlay { get; private set; }

        /// <summary>Resets all per-layer state and cycling position.</summary>
        public void Reset()
        {
            _states = new Dictionary<SegmentDisplayLayer, ActivationState>();
            _screenCycleIndex = 0;
            HasActiveOverlay = false;
        }

        /// <summary>Cycle to the next screen.</summary>
        public void NextScreen()
        {
            System.Threading.Interlocked.Increment(ref _screenCycleIndex);
        }

        /// <summary>Cycle to the previous screen.</summary>
        public void PreviousScreen()
        {
            System.Threading.Interlocked.Decrement(ref _screenCycleIndex);
        }

        /// <summary>Jump to a specific screen index.</summary>
        public void SetActiveScreen(int index)
        {
            System.Threading.Interlocked.Exchange(ref _screenCycleIndex, index);
        }

        /// <summary>
        /// Evaluates the full layer stack and returns the winning layer with its
        /// raw content text. Called once per frame.
        /// </summary>
        public LayerStackResult Evaluate(IPropertyProvider props, INCalcEngine ncalc,
                                         bool gameRunning, SegmentDisplaySettings settings)
        {
            if (settings == null) return LayerStackResult.Empty;

            long nowMs = _clock();
            SegmentDisplayLayer overlayWinner = null;
            var active = new HashSet<SegmentDisplayLayer>();
            var eligibleScreens = new List<SegmentDisplayLayer>();

            foreach (var layer in settings.Layers)
            {
                if (!layer.IsEnabled) continue;

                bool visible = (gameRunning && layer.ShowWhenRunning)
                            || (!gameRunning && layer.ShowWhenIdle);
                if (!visible) continue;

                var state = GetState(layer);

                if (layer.Role == LayerRole.Overlay)
                {
                    bool isActive = layer.Condition != null
                        && layer.Condition.Evaluate(props, ncalc, state, nowMs);

                    if (isActive)
                    {
                        active.Add(layer);
                        if (overlayWinner == null)
                            overlayWinner = layer;
                    }
                }
                else // Screen
                {
                    active.Add(layer);
                    eligibleScreens.Add(layer);
                }
            }

            // Determine winner: first active overlay wins; otherwise pick cycled screen
            SegmentDisplayLayer winner;
            if (overlayWinner != null)
            {
                winner = overlayWinner;
                HasActiveOverlay = true;
            }
            else if (eligibleScreens.Count > 0)
            {
                int idx = _screenCycleIndex;
                idx = ((idx % eligibleScreens.Count) + eligibleScreens.Count)
                      % eligibleScreens.Count;
                System.Threading.Interlocked.Exchange(ref _screenCycleIndex, idx);
                winner = eligibleScreens[idx];
                HasActiveOverlay = false;
            }
            else
            {
                HasActiveOverlay = false;
                return new LayerStackResult(null, "", active);
            }

            string text = ResolveContent(winner.Content, props, ncalc, nowMs);
            return new LayerStackResult(winner, text, active);
        }

        // ── Content resolution ──────────────────────────────────────

        internal static string ResolveContent(ContentSource content,
                                              IPropertyProvider props, INCalcEngine ncalc,
                                              long nowMs)
        {
            if (content == null) return "";

            if (content is FixedTextContent ftc)
                return ftc.Text ?? "";

            if (content is PropertyContent pc)
            {
                if (string.IsNullOrEmpty(pc.PropertyName) || props == null) return "";
                object val = props.GetValue(pc.PropertyName);
                return val?.ToString() ?? "";
            }

            if (content is ExpressionContent ec)
            {
                if (string.IsNullOrEmpty(ec.Expression) || ncalc == null) return "";
                object result = ncalc.Evaluate(ec.Expression);
                return result?.ToString() ?? "";
            }

            if (content is SequenceContent sc)
            {
                if (sc.Items == null || sc.Items.Length == 0) return "";
                int interval = sc.IntervalMs > 0 ? sc.IntervalMs : 500;
                long totalCycle = (long)interval * sc.Items.Length;
                int itemIndex = (int)((nowMs % totalCycle) / interval);
                return ResolveContent(sc.Items[itemIndex], props, ncalc, nowMs);
            }

            if (content is DeviceCommandContent)
                return ""; // handled specially by the controller

            return "";
        }

        // ── State management ────────────────────────────────────────

        private ActivationState GetState(SegmentDisplayLayer layer)
        {
            ActivationState state;
            if (!_states.TryGetValue(layer, out state))
            {
                state = new ActivationState();
                _states[layer] = state;
            }
            return state;
        }

        private static long DefaultClock()
        {
            return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        }
    }
}
