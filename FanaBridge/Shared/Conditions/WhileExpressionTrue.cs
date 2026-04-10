using Newtonsoft.Json;

namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Active while an NCalc expression evaluates to a truthy value.
    /// Requires an <see cref="INCalcEngine"/> implementation (Phase 2).
    /// In Phase 1, this condition always returns false.
    /// </summary>
    public class WhileExpressionTrue : ActivationCondition
    {
        public override string Type { get { return "WhileExpressionTrue"; } }

        /// <summary>NCalc expression to evaluate.</summary>
        [JsonProperty("Expression")]
        public string Expression { get; set; }

        public override bool Evaluate(IPropertyProvider props, ActivationState state, long nowMs)
        {
            // NCalc engine injection is deferred to Phase 2.
            return false;
        }
    }
}
