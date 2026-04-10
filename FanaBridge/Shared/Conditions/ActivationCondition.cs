using Newtonsoft.Json;

namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Base class for polymorphic activation conditions.
    /// Subclasses answer "is this thing active right now?"
    /// JSON discriminated by the <see cref="Type"/> property.
    /// </summary>
    [JsonConverter(typeof(ActivationConditionConverter))]
    public abstract class ActivationCondition
    {
        /// <summary>
        /// JSON discriminator string. Each subclass returns a fixed value
        /// (e.g., "AlwaysActive", "WhilePropertyTrue").
        /// </summary>
        [JsonProperty("Type", Order = -10)]
        public abstract string Type { get; }

        /// <summary>
        /// Evaluates whether this condition is currently active.
        /// </summary>
        /// <param name="props">Property provider for reading SimHub values.</param>
        /// <param name="state">Per-condition mutable state (last value, timers).</param>
        /// <param name="nowMs">Current timestamp in milliseconds.</param>
        /// <returns>True if the condition is met.</returns>
        public abstract bool Evaluate(IPropertyProvider props, ActivationState state, long nowMs);
    }
}
