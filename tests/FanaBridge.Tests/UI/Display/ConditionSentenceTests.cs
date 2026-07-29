using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// O8: condition → sentence generator. One test per condition family
    /// (level, bool, onChange). Finite grammar via DisplayCopy only.
    /// </summary>
    public class ConditionSentenceTests
    {
        private static AliasTable TableWith(string kind, string @ref, string alias, string? unit = null)
        {
            return new AliasTable
            {
                Aliases =
                {
                    new AliasEntry
                    {
                        Kind = kind == "builtIn" ? AliasKind.BuiltIn : AliasKind.Property,
                        Ref = @ref,
                        Alias = alias,
                        Unit = unit,
                    },
                },
            };
        }

        [Fact]
        public void Level_LessThan_UsesBelowPhraseAndUnit()
        {
            var aliases = TableWith("builtIn", "Fuel", "Fuel remaining", "L");
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
                Operator = ConditionOperator.LessThan,
                Value = 4.0,
            };

            string sentence = ConditionSentence.From(condition, aliases: aliases);

            Assert.Equal(
                DisplayCopy.ConditionLevelSentence(
                    "Fuel remaining",
                    DisplayCopy.OpBelow,
                    DisplayCopy.ConditionValue(4.0, "L")),
                sentence);
        }

        [Fact]
        public void Level_GreaterOrEqual_UsesAtOrAbove()
        {
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Speed" },
                Operator = ConditionOperator.GreaterOrEqual,
                Value = 100,
            };

            string sentence = ConditionSentence.From(condition);

            Assert.Contains(DisplayCopy.OpAtOrAbove, sentence);
            Assert.Contains("Speed", sentence);
            Assert.Contains("100", sentence);
        }

        [Fact]
        public void Bool_IsTrue_UsesIsOn()
        {
            var aliases = TableWith("builtIn", "PitLimiterOn", "Pit limiter");
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "PitLimiterOn" },
                Operator = ConditionOperator.IsTrue,
            };

            string sentence = ConditionSentence.From(condition, aliases: aliases);

            Assert.Equal(
                DisplayCopy.ConditionBoolSentence("Pit limiter", DisplayCopy.OpIsOn),
                sentence);
        }

        [Fact]
        public void Bool_IsFalse_UsesIsOff()
        {
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "DrsEnabled" },
                Operator = ConditionOperator.IsFalse,
            };

            string sentence = ConditionSentence.From(condition);

            Assert.Equal(
                DisplayCopy.ConditionBoolSentence("DrsEnabled", DisplayCopy.OpIsOff),
                sentence);
        }

        [Fact]
        public void OnChange_Any_UsesChanges()
        {
            var aliases = TableWith("builtIn", "BrakeBias", "Brake bias", "%");
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "BrakeBias" },
                Operator = ConditionOperator.GreaterThan, // ignored for onChange
                Value = 0,
            };
            var lifetime = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = ChangeDirection.Any,
            };

            string sentence = ConditionSentence.From(condition, lifetime, aliases);

            Assert.Equal(
                DisplayCopy.ConditionChangeSentence("Brake bias", DisplayCopy.OpChanges),
                sentence);
        }

        [Fact]
        public void OnChange_Up_UsesIncreases()
        {
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Gear" },
            };
            var lifetime = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = ChangeDirection.Up,
            };

            string sentence = ConditionSentence.From(condition, lifetime);

            Assert.Equal(
                DisplayCopy.ConditionChangeSentence("Gear", DisplayCopy.OpIncreases),
                sentence);
        }

        [Fact]
        public void OnChange_Down_UsesDecreases()
        {
            var condition = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
            };
            var lifetime = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = ChangeDirection.Down,
            };

            string sentence = ConditionSentence.From(condition, lifetime);

            Assert.Contains(DisplayCopy.OpDecreases, sentence);
        }

        [Fact]
        public void NullCondition_IsEmpty()
            => Assert.Equal(string.Empty, ConditionSentence.From(null));

        [Fact]
        public void PatternRule_ExpandsCapture()
        {
            var table = new AliasTable
            {
                PatternRules =
                {
                    new AliasPatternRule
                    {
                        Match = @"^FNKey0*(\d+)Activated$",
                        AliasPattern = "FN layer $1",
                    },
                },
            };
            var condition = new Condition
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.SimHubProperty,
                    Name = "FNKey01Activated",
                },
                Operator = ConditionOperator.IsTrue,
            };

            string sentence = ConditionSentence.From(condition, aliases: table);

            Assert.Equal(
                DisplayCopy.ConditionBoolSentence("FN layer 1", DisplayCopy.OpIsOn),
                sentence);
        }
    }
}
