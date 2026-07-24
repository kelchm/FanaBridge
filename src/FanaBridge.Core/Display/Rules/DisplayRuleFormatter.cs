using System.Globalization;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
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
                case TargetKind.SegmentScreen:
                    return "screen '" + (target.ScreenId ?? "?") + "'";
                case TargetKind.Special:
                    return SpecialCommands.Label(target.Command);
                case TargetKind.Cycle:
                {
                    var pages = target.CyclePages;
                    if (pages == null || pages.Count == 0)
                        return "?";
                    string text = PageName(pages[0]);
                    for (int i = 1; i < pages.Count; i++)
                        text += " ⇄ " + PageName(pages[i]);
                    return text;
                }
                default:
                    return "(unrecognized target)";
            }
        }

        /// <summary>A page's display name, tolerant of the nullable page fields.</summary>
        public static string PageName(ItmPage? page)
            => page == null ? "?" : ItmTelemetry.NameOf(page.Value);

        /// <summary>The operator glyph/word for a condition kind ("&lt;", "≥", "is on",
        /// "changes", "triggered") — the structured-row counterpart of the phrases baked into
        /// <see cref="DescribeCondition"/>, so the two share one operator vocabulary.</summary>
        public static string OperatorText(ConditionKind kind)
        {
            switch (kind)
            {
                case ConditionKind.LessThan: return "<";
                case ConditionKind.LessOrEqual: return "≤";
                case ConditionKind.GreaterThan: return ">";
                case ConditionKind.GreaterOrEqual: return "≥";
                case ConditionKind.Equals: return "=";
                case ConditionKind.NotEquals: return "≠";
                case ConditionKind.IsTrue: return "is on";
                case ConditionKind.IsFalse: return "is off";
                case ConditionKind.Changes: return "changes";
                case ConditionKind.Increases: return "increases";
                case ConditionKind.Decreases: return "decreases";
                case ConditionKind.ActionTriggered: return "triggered";
                default: return "";
            }
        }

        /// <summary>A comparison value formatted for display ("10", "0.5"), or "?" when null.</summary>
        public static string FormatValue(double? value) => Num(value);

        private static string Num(double? value)
            => value == null ? "?" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
