using System.Collections.Generic;
using FanaBridge.Shared;
using FanaBridge.Shared.Conditions;
using Xunit;

namespace FanaBridge.Tests.Conditions
{
    public class ActivationConditionTests
    {
        // ── AlwaysActive ────────────────────────────────────────────

        [Fact]
        public void AlwaysActive_ReturnsTrue()
        {
            var condition = new AlwaysActive();
            Assert.True(condition.Evaluate(new StubPropertyProvider(), null, new ActivationState(), 0));
        }

        // ── WhilePropertyTrue ───────────────────────────────────────

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(1, true)]
        [InlineData(0, false)]
        [InlineData(1.0, true)]
        [InlineData(0.0, false)]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("hello", true)]
        [InlineData("", false)]
        public void WhilePropertyTrue_EvaluatesTruthiness(object value, bool expected)
        {
            var props = new StubPropertyProvider();
            props.Set("test", value);

            var condition = new WhilePropertyTrue { Property = "test" };
            Assert.Equal(expected, condition.Evaluate(props, null, new ActivationState(), 0));
        }

        [Fact]
        public void WhilePropertyTrue_NullProperty_ReturnsFalse()
        {
            var props = new StubPropertyProvider();
            var condition = new WhilePropertyTrue { Property = "missing" };
            Assert.False(condition.Evaluate(props, null, new ActivationState(), 0));
        }

        [Fact]
        public void WhilePropertyTrue_EmptyPropertyName_ReturnsFalse()
        {
            var condition = new WhilePropertyTrue { Property = "" };
            Assert.False(condition.Evaluate(new StubPropertyProvider(), null, new ActivationState(), 0));
        }

        [Fact]
        public void WhilePropertyTrue_Inverted_FlipsResult()
        {
            var props = new StubPropertyProvider();
            props.Set("flag", true);

            var condition = new WhilePropertyTrue { Property = "flag", Invert = true };
            Assert.False(condition.Evaluate(props, null, new ActivationState(), 0));

            props.Set("flag", false);
            Assert.True(condition.Evaluate(props, null, new ActivationState(), 0));
        }

        // ── OnValueChange ───────────────────────────────────────────

        [Fact]
        public void OnValueChange_FirstEvaluation_ReturnsFalse()
        {
            var props = new StubPropertyProvider();
            props.Set("gear", 3);

            var condition = new OnValueChange { Property = "gear", HoldMs = 2000 };
            var state = new ActivationState();

            Assert.False(condition.Evaluate(props, null, state, 1000));
        }

        [Fact]
        public void OnValueChange_ValueChanges_ReturnsTrueForHoldDuration()
        {
            var props = new StubPropertyProvider();
            props.Set("gear", 3);

            var condition = new OnValueChange { Property = "gear", HoldMs = 2000 };
            var state = new ActivationState();

            // Seed
            condition.Evaluate(props, null, state, 1000);

            // Change value
            props.Set("gear", 4);
            Assert.True(condition.Evaluate(props, null, state, 2000));

            // Still within hold
            Assert.True(condition.Evaluate(props, null, state, 3500));

            // After hold expires
            Assert.False(condition.Evaluate(props, null, state, 4500));
        }

        [Fact]
        public void OnValueChange_NoChange_ReturnsFalse()
        {
            var props = new StubPropertyProvider();
            props.Set("gear", 3);

            var condition = new OnValueChange { Property = "gear", HoldMs = 2000 };
            var state = new ActivationState();

            // Seed
            condition.Evaluate(props, null, state, 1000);

            // Same value
            Assert.False(condition.Evaluate(props, null, state, 2000));
        }

        [Fact]
        public void OnValueChange_MultipleChanges_ExtendsHold()
        {
            var props = new StubPropertyProvider();
            props.Set("gear", 3);

            var condition = new OnValueChange { Property = "gear", HoldMs = 2000 };
            var state = new ActivationState();

            // Seed
            condition.Evaluate(props, null, state, 1000);

            // First change at t=2000, hold until t=4000
            props.Set("gear", 4);
            condition.Evaluate(props, null, state, 2000);

            // Second change at t=3000, hold extended until t=5000
            props.Set("gear", 5);
            condition.Evaluate(props, null, state, 3000);

            // Still active at t=4500 (would have expired from first change)
            Assert.True(condition.Evaluate(props, null, state, 4500));

            // Expired at t=5500
            Assert.False(condition.Evaluate(props, null, state, 5500));
        }

        [Fact]
        public void OnValueChange_EmptyPropertyName_ReturnsFalse()
        {
            var condition = new OnValueChange { Property = "" };
            Assert.False(condition.Evaluate(new StubPropertyProvider(), null, new ActivationState(), 0));
        }

        // ── WhileExpressionTrue ─────────────────────────────────────

        [Fact]
        public void WhileExpressionTrue_TruthyResult_ReturnsTrue()
        {
            var ncalc = new StubNCalcEngine();
            ncalc.Result = true;

            var condition = new WhileExpressionTrue { Expression = "1 == 1" };
            Assert.True(condition.Evaluate(new StubPropertyProvider(), ncalc, new ActivationState(), 0));
        }

        [Fact]
        public void WhileExpressionTrue_FalsyResult_ReturnsFalse()
        {
            var ncalc = new StubNCalcEngine();
            ncalc.Result = false;

            var condition = new WhileExpressionTrue { Expression = "1 == 0" };
            Assert.False(condition.Evaluate(new StubPropertyProvider(), ncalc, new ActivationState(), 0));
        }

        [Fact]
        public void WhileExpressionTrue_NullEngine_ReturnsFalse()
        {
            var condition = new WhileExpressionTrue { Expression = "1 == 1" };
            Assert.False(condition.Evaluate(new StubPropertyProvider(), null, new ActivationState(), 0));
        }

        [Fact]
        public void WhileExpressionTrue_EmptyExpression_ReturnsFalse()
        {
            var condition = new WhileExpressionTrue { Expression = "" };
            Assert.False(condition.Evaluate(new StubPropertyProvider(), new StubNCalcEngine(), new ActivationState(), 0));
        }

        [Fact]
        public void WhileExpressionTrue_NullResult_ReturnsFalse()
        {
            var ncalc = new StubNCalcEngine();
            ncalc.Result = null;

            var condition = new WhileExpressionTrue { Expression = "bad()" };
            Assert.False(condition.Evaluate(new StubPropertyProvider(), ncalc, new ActivationState(), 0));
        }

        // ── IsTruthy edge cases ─────────────────────────────────────

        [Fact]
        public void IsTruthy_Null_ReturnsFalse()
        {
            Assert.False(WhilePropertyTrue.IsTruthy(null));
        }

        [Fact]
        public void IsTruthy_UnknownNonNullObject_ReturnsTrue()
        {
            Assert.True(WhilePropertyTrue.IsTruthy(new object()));
        }

        [Theory]
        [InlineData(1L, true)]
        [InlineData(0L, false)]
        public void IsTruthy_Long(long value, bool expected)
        {
            Assert.Equal(expected, WhilePropertyTrue.IsTruthy(value));
        }

        [Theory]
        [InlineData(1f, true)]
        [InlineData(0f, false)]
        public void IsTruthy_Float(float value, bool expected)
        {
            Assert.Equal(expected, WhilePropertyTrue.IsTruthy(value));
        }

        // ── Test doubles ────────────────────────────────────────────

        private class StubPropertyProvider : IPropertyProvider
        {
            private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

            public void Set(string name, object value) { _values[name] = value; }

            public object GetValue(string propertyName)
            {
                object val;
                return _values.TryGetValue(propertyName, out val) ? val : null;
            }
        }

        private class StubNCalcEngine : INCalcEngine
        {
            public object Result { get; set; }

            public object Evaluate(string expression)
            {
                return Result;
            }
        }
    }
}
