using Newtonsoft.Json;

namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Active for <see cref="HoldMs"/> milliseconds after a named property's
    /// value changes. Used for transient overlays like gear-change indicators.
    /// </summary>
    public class OnValueChange : ActivationCondition
    {
        public override string Type { get { return "OnValueChange"; } }

        /// <summary>SimHub property name to monitor for changes.</summary>
        [JsonProperty("Property")]
        public string Property { get; set; }

        /// <summary>How long (ms) to remain active after a change is detected.</summary>
        [JsonProperty("HoldMs")]
        public int HoldMs { get; set; }

        public override bool Evaluate(IPropertyProvider props, INCalcEngine ncalc, ActivationState state, long nowMs)
        {
            if (string.IsNullOrEmpty(Property)) return false;

            object current = props.GetValue(Property);
            string currentStr = current?.ToString() ?? "";

            // First evaluation: seed the last value, don't activate.
            if (state.LastValue == null && state.ActiveUntilMs == 0)
            {
                state.LastValue = currentStr;
                return false;
            }

            string lastStr = state.LastValue?.ToString() ?? "";

            // Detect change. Empty-string transitions are ignored to prevent
            // false triggers when a property hasn't been populated yet by the
            // game plugin (matching prototype behavior).
            if (currentStr != lastStr && currentStr.Length > 0)
            {
                state.ActiveUntilMs = nowMs + HoldMs;
            }

            state.LastValue = currentStr;
            return nowMs < state.ActiveUntilMs;
        }
    }
}
