using System.Globalization;
using FanaBridge.Protocol;

namespace FanaBridge.Customization
{
    /// <summary>
    /// Builds the design's rule row language ("Fuel &lt; 10 → Fuel / ERS / DRS") from a
    /// rule's condition and target. Used by the engine for activity-event text and by the
    /// UI for rules the user hasn't named. Pages are described by their content names only
    /// — wire page numbers are per display device and unknown at this layer.
    /// </summary>
    public static class DisplayRuleFormatter
    {
        /// <summary>The rule's display text: its user label when set, else
        /// <see cref="Describe"/>.</summary>
        public static string Label(DisplayRule rule)
            => !string.IsNullOrWhiteSpace(rule.Name) ? rule.Name : Describe(rule);

        /// <summary>The full "condition → target" row for a rule.</summary>
        public static string Describe(DisplayRule rule)
            => DescribeCondition(rule.When) + " → " + DescribeTarget(rule.Show);

        /// <summary>The condition half of the row, e.g. "Fuel &lt; 10", "BrakeBias changes",
        /// "'ShowTyres' triggered".</summary>
        public static string DescribeCondition(RuleCondition condition)
        {
            if (condition == null || condition.Source == null)
                return "(no condition)";

            string name = condition.Source.Name ?? "?";
            switch (condition.Kind)
            {
                case ConditionKind.LessThan: return name + " < " + Num(condition.Value);
                case ConditionKind.LessOrEqual: return name + " ≤ " + Num(condition.Value);
                case ConditionKind.GreaterThan: return name + " > " + Num(condition.Value);
                case ConditionKind.GreaterOrEqual: return name + " ≥ " + Num(condition.Value);
                case ConditionKind.Equals: return name + " = " + Num(condition.Value);
                case ConditionKind.NotEquals: return name + " ≠ " + Num(condition.Value);
                case ConditionKind.IsTrue: return name + " is on";
                case ConditionKind.IsFalse: return name + " is off";
                case ConditionKind.Changes: return name + " changes";
                case ConditionKind.Increases: return name + " increases";
                case ConditionKind.Decreases: return name + " decreases";
                case ConditionKind.ActionTriggered: return "'" + name + "' triggered";
                default: return "(unrecognized condition)";
            }
        }

        /// <summary>The target half of the row, e.g. "Car Settings",
        /// "Fuel / ERS / DRS ⇄ Tire Temps", "screen 'FN1'".</summary>
        public static string DescribeTarget(RuleTarget target)
        {
            if (target == null)
                return "(no target)";
            switch (target.Kind)
            {
                case TargetKind.Page:
                    return PageName(target.Page);
                case TargetKind.LegacyScreen:
                    return "screen '" + (target.ScreenId ?? "?") + "'";
                case TargetKind.Alternate:
                    return PageName(target.PageA) + " ⇄ " + PageName(target.PageB);
                default:
                    return "(unrecognized target)";
            }
        }

        /// <summary>A page's display name, tolerant of the nullable page fields.</summary>
        public static string PageName(ItmPage? page)
            => page == null ? "?" : ItmTelemetry.NameOf(page.Value);

        private static string Num(double? value)
            => value == null ? "?" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
