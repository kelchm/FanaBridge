using Newtonsoft.Json;

namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Active while a named SimHub property is truthy.
    /// Supports inversion for "while NOT true" without NCalc.
    /// </summary>
    public class WhilePropertyTrue : ActivationCondition
    {
        public override string Type { get { return "WhilePropertyTrue"; } }

        /// <summary>SimHub property name to evaluate.</summary>
        [JsonProperty("Property")]
        public string Property { get; set; }

        /// <summary>When true, the condition is active while the property is falsy.</summary>
        [JsonProperty("Invert")]
        public bool Invert { get; set; }

        public override bool Evaluate(IPropertyProvider props, INCalcEngine ncalc, ActivationState state, long nowMs)
        {
            if (string.IsNullOrEmpty(Property)) return false;

            object value = props.GetValue(Property);
            bool truthy = IsTruthy(value);
            return Invert ? !truthy : truthy;
        }

        /// <summary>
        /// Determines whether a value is "truthy" for condition evaluation.
        /// Null, false, zero, empty string, and "false"/"0" are falsy.
        /// </summary>
        internal static bool IsTruthy(object value)
        {
            if (value == null) return false;
            if (value is bool b) return b;
            if (value is int i) return i != 0;
            if (value is long l) return l != 0;
            if (value is double d) return d != 0;
            if (value is float f) return f != 0;
            if (value is decimal m) return m != 0;
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
    }
}
