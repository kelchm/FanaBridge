using Newtonsoft.Json;

namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Active while an NCalc expression evaluates to a truthy value.
    /// </summary>
    public class WhileExpressionTrue : ActivationCondition
    {
        public override string Type { get { return "WhileExpressionTrue"; } }

        /// <summary>NCalc expression to evaluate.</summary>
        [JsonProperty("Expression")]
        public string Expression { get; set; }

        public override bool Evaluate(IPropertyProvider props, INCalcEngine ncalc, ActivationState state, long nowMs)
        {
            if (string.IsNullOrEmpty(Expression) || ncalc == null) return false;

            object result = ncalc.Evaluate(Expression);
            return WhilePropertyTrue.IsTruthy(result);
        }
    }
}
