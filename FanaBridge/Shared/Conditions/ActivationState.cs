namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Per-condition runtime state bag. Conditions read and write
    /// their own fields during <see cref="ActivationCondition.Evaluate"/>.
    /// Managed externally by the evaluator (one state per activatable thing).
    /// </summary>
    public class ActivationState
    {
        /// <summary>Last observed property value (for change detection).</summary>
        public object LastValue { get; set; }

        /// <summary>Timestamp (ms) until which this condition remains active.</summary>
        public long ActiveUntilMs { get; set; }
    }
}
