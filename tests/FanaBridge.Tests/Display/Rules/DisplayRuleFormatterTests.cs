using FanaBridge.Display.Rules;
using Xunit;

namespace FanaBridge.Tests.Display.Rules
{
    /// <summary>
    /// The rule-row language (<see cref="DisplayRuleFormatter"/>). Pins the operator
    /// vocabulary the v9 structured rows route through (<see cref="DisplayRuleFormatter.OperatorText"/>
    /// / <see cref="DisplayRuleFormatter.FormatValue"/>) arm-by-arm, and cross-asserts it
    /// against the phrase <see cref="DisplayRuleFormatter.DescribeCondition"/> bakes in so
    /// the two parallel operator vocabularies cannot silently drift apart.
    /// </summary>
    public class DisplayRuleFormatterTests
    {
        private static RuleCondition Cond(ConditionKind kind, double? value = 10)
            => new RuleCondition
            {
                Kind = kind,
                Value = value,
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = "X" },
            };

        // ── OperatorText: every arm pinned ───────────────────────────────

        [Theory]
        [InlineData(ConditionKind.LessThan, "<")]
        [InlineData(ConditionKind.LessOrEqual, "≤")]
        [InlineData(ConditionKind.GreaterThan, ">")]
        [InlineData(ConditionKind.GreaterOrEqual, "≥")]
        [InlineData(ConditionKind.Equals, "=")]
        [InlineData(ConditionKind.NotEquals, "≠")]
        [InlineData(ConditionKind.IsTrue, "is on")]
        [InlineData(ConditionKind.IsFalse, "is off")]
        [InlineData(ConditionKind.Changes, "changes")]
        [InlineData(ConditionKind.Increases, "increases")]
        [InlineData(ConditionKind.Decreases, "decreases")]
        [InlineData(ConditionKind.ActionTriggered, "triggered")]
        [InlineData(ConditionKind.Unknown, "")]
        public void OperatorText_PinsEveryArm(ConditionKind kind, string expected)
            => Assert.Equal(expected, DisplayRuleFormatter.OperatorText(kind));

        // ── FormatValue: integer / fractional / null ─────────────────────

        [Theory]
        [InlineData(10, "10")]
        [InlineData(0.5, "0.5")]
        [InlineData(16.969, "16.969")]
        public void FormatValue_FormatsNumbers_InvariantThreeDecimals(double value, string expected)
            => Assert.Equal(expected, DisplayRuleFormatter.FormatValue(value));

        [Fact]
        public void FormatValue_Null_IsQuestionMark()
            => Assert.Equal("?", DisplayRuleFormatter.FormatValue(null));

        // ── Drift guard: OperatorText ⇔ DescribeCondition ────────────────

        [Theory]
        [InlineData(ConditionKind.LessThan)]
        [InlineData(ConditionKind.LessOrEqual)]
        [InlineData(ConditionKind.GreaterThan)]
        [InlineData(ConditionKind.GreaterOrEqual)]
        [InlineData(ConditionKind.Equals)]
        [InlineData(ConditionKind.NotEquals)]
        public void DescribeCondition_ValueKinds_ReconstructFromOperatorTextAndFormatValue(ConditionKind kind)
        {
            var cond = Cond(kind, 10);
            // "X <op> 10" must be exactly name + operator glyph + formatted value — the
            // structured row (OperatorText+FormatValue) and the sentence form share one
            // vocabulary; a swapped glyph or reformatted number breaks this equality.
            Assert.Equal(
                "X " + DisplayRuleFormatter.OperatorText(kind) + " " + DisplayRuleFormatter.FormatValue(10),
                DisplayRuleFormatter.DescribeCondition(cond));
        }

        [Theory]
        [InlineData(ConditionKind.IsTrue)]
        [InlineData(ConditionKind.IsFalse)]
        [InlineData(ConditionKind.Changes)]
        [InlineData(ConditionKind.Increases)]
        [InlineData(ConditionKind.Decreases)]
        public void DescribeCondition_ValuelessKinds_ReconstructFromOperatorText(ConditionKind kind)
            => Assert.Equal(
                "X " + DisplayRuleFormatter.OperatorText(kind),
                DisplayRuleFormatter.DescribeCondition(Cond(kind)));

        [Fact]
        public void DescribeCondition_ActionTriggered_KeepsQuotedFraming_NotThePlainOperator()
        {
            // ActionTriggered is the one arm where the sentence form and the operator token
            // diverge: DescribeCondition wraps the action name in quotes ("'X' triggered"),
            // so a structured row that used OperatorText would DROP the quotes. This asserts
            // the divergence (why ActionTriggered is excluded from structured rendering).
            Assert.Equal("'X' triggered", DisplayRuleFormatter.DescribeCondition(Cond(ConditionKind.ActionTriggered)));
            Assert.Equal("triggered", DisplayRuleFormatter.OperatorText(ConditionKind.ActionTriggered));
        }
    }
}
