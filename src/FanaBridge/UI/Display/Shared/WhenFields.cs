using FanaBridge.Display.Rules;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// A rule condition decomposed into the pieces the v9 row renders: the property (name +
    /// display kind, for <see cref="PropertyGrammar"/>), the operator glyph, and the comparison
    /// value text. Pure — shared by the Triggers editor rows and the Overview priority rows so
    /// their "WHEN" language cannot drift.
    /// </summary>
    public struct WhenFields
    {
        /// <summary>The condition's source property name (null → the row falls back to its
        /// plain label: a user-named, degraded, or base row).</summary>
        public string PropertyName;

        /// <summary>How the property namespaces for display.</summary>
        public PropertyDisplayKind DisplayKind;

        /// <summary>The operator glyph ("&lt;", "≥", "is on", "changes", "triggered"), or "".</summary>
        public string Operator;

        /// <summary>The comparison value text for value-taking operators, else "".</summary>
        public string ValueText;

        /// <summary>Decompose a rule's WHEN condition. A null condition or missing source
        /// yields empty fields (PropertyName null).</summary>
        public static WhenFields From(RuleCondition when)
        {
            var f = new WhenFields
            {
                DisplayKind = PropertyDisplayKind.SimHubProperty,
                Operator = "",
                ValueText = "",
            };
            if (when == null)
                return f;
            if (when.Source != null)
            {
                f.PropertyName = when.Source.Name;
                f.DisplayKind = PropertyGrammar.KindFor(when.Source.Kind);
            }
            f.Operator = DisplayRuleFormatter.OperatorText(when.Kind);
            f.ValueText = when.Kind.RequiresValue()
                ? DisplayRuleFormatter.FormatValue(when.Value)
                : "";
            return f;
        }
    }
}
