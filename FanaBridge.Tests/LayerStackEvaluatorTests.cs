using System;
using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class LayerStackEvaluatorTests
    {
        // ── Evaluate ───────────────────────────────────────────────────

        [Fact]
        public void Evaluate_NoLayers_ReturnsEmpty()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();
            var result = evaluator.Evaluate(null, false, settings);
            Assert.Null(result.Winner);
            Assert.Equal("", result.Text);
            Assert.Empty(result.ActiveLayers);
        }

        [Fact]
        public void Evaluate_SingleConstantLayer_ShowWhenIdle()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();
            var layer = new DisplayLayer
            {
                Name = "Test", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "TST",
                ShowWhenIdle = true,
            };
            settings.Layers.Add(layer);

            var result = evaluator.Evaluate(null, false, settings);
            Assert.Same(layer, result.Winner);
            Assert.Equal("TST", result.Text);
            Assert.Contains(layer, result.ActiveLayers);
        }

        [Fact]
        public void Evaluate_DisabledLayer_IsSkipped()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();
            settings.Layers.Add(new DisplayLayer
            {
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "OFF",
                ShowWhenRunning = false, ShowWhenIdle = false,
            });

            var result = evaluator.Evaluate(null, false, settings);
            Assert.Null(result.Winner);
        }

        [Fact]
        public void Evaluate_ConstantLayer_RespectsShowWhenFlags()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();
            settings.Layers.Add(new DisplayLayer
            {
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "RUN",
                ShowWhenRunning = true, ShowWhenIdle = false,
            });

            // Not running — should not show
            var result = evaluator.Evaluate(null, false, settings);
            Assert.Null(result.Winner);
        }

        [Fact]
        public void EvaluateLayer_FixedText_ReturnsText()
        {
            var evaluator = new LayerStackEvaluator();
            var layer = new DisplayLayer
            {
                Source = DisplaySource.FixedText, FixedText = "PIT",
            };
            Assert.Equal("PIT", evaluator.EvaluateLayer(null, layer));
        }

        [Fact]
        public void EvaluateLayer_NullLayer_ReturnsEmpty()
        {
            var evaluator = new LayerStackEvaluator();
            Assert.Equal("", evaluator.EvaluateLayer(null, null));
        }

        // ── IsTruthy ───────────────────────────────────────────────────

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(1, true)]
        [InlineData(0, false)]
        [InlineData(1.5, true)]
        [InlineData(0.0, false)]
        [InlineData("yes", true)]
        [InlineData("", false)]
        [InlineData("0", false)]
        [InlineData("false", false)]
        [InlineData("False", false)]
        [InlineData("FALSE", false)]
        [InlineData("true", true)]
        [InlineData(null, false)]
        public void IsTruthy_CoversAllTypes(object value, bool expected)
        {
            Assert.Equal(expected, LayerStackEvaluator.IsTruthy(value));
        }

        // ── FormatValue ────────────────────────────────────────────────

        [Fact]
        public void FormatValue_Gear_MapsCorrectly()
        {
            Assert.Equal("R", LayerStackEvaluator.FormatValue(-1, DisplayFormat.Gear));
            Assert.Equal("N", LayerStackEvaluator.FormatValue(0, DisplayFormat.Gear));
            Assert.Equal("3", LayerStackEvaluator.FormatValue(3, DisplayFormat.Gear));
        }

        [Fact]
        public void FormatValue_Number_RoundsToInteger()
        {
            Assert.Equal("42", LayerStackEvaluator.FormatValue(42, DisplayFormat.Number));
            Assert.Equal("43", LayerStackEvaluator.FormatValue(42.7, DisplayFormat.Number));
        }

        [Fact]
        public void FormatValue_Decimal_OneDecimalPlace()
        {
            Assert.Equal("4.2", LayerStackEvaluator.FormatValue(4.2, DisplayFormat.Decimal));
            Assert.Equal("4.0", LayerStackEvaluator.FormatValue(4.0, DisplayFormat.Decimal));
        }

        [Fact]
        public void FormatValue_Time_DefaultFormat()
        {
            var ts = TimeSpan.FromSeconds(65.3);
            Assert.Equal("05.3", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time));
        }

        [Fact]
        public void FormatValue_Time_MinutesSeconds()
        {
            var ts = TimeSpan.FromSeconds(65.3);
            Assert.Equal("1.05", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time, @"m\.ss"));
        }

        [Fact]
        public void FormatValue_Time_MinutesSecondsTenths()
        {
            var ts = TimeSpan.FromSeconds(65.3);
            Assert.Equal("1.05.3", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time, @"m\.ss\.f"));
        }

        [Fact]
        public void FormatValue_Time_InvalidFormat_FallsBack()
        {
            var ts = TimeSpan.FromSeconds(5);
            // Invalid format should fall back to default ss.f
            Assert.Equal("05.0", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time, "zzz_invalid"));
        }

        [Fact]
        public void FormatValue_Time_NonTimeSpan_FormatsAsDecimal()
        {
            Assert.Equal("4.2", LayerStackEvaluator.FormatValue(4.2, DisplayFormat.Time));
        }

        // ── Priority ordering ──────────────────────────────────────────

        [Fact]
        public void Evaluate_ConstantBeforeOnChange_ConstantWins()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var constant = new DisplayLayer
            {
                Name = "Always", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "WIN",
                ShowWhenRunning = true,
            };
            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000,
            };
            settings.Layers.Add(constant);  // index 0 – highest priority
            settings.Layers.Add(onChange);   // index 1

            evaluator.ForceActiveUntil(onChange, DateTime.UtcNow.AddSeconds(10));
            var result = evaluator.Evaluate(null, true, settings);

            Assert.Same(constant, result.Winner);
            Assert.Equal("WIN", result.Text);
        }

        [Fact]
        public void Evaluate_OnChangeBeforeConstant_OnChangeWins()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000,
            };
            var constant = new DisplayLayer
            {
                Name = "Always", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BAS",
                ShowWhenRunning = true,
            };
            settings.Layers.Add(onChange);   // index 0 – highest priority
            settings.Layers.Add(constant);   // index 1

            evaluator.ForceActiveUntil(onChange, DateTime.UtcNow.AddSeconds(10));
            var result = evaluator.Evaluate(null, true, settings);

            Assert.Same(onChange, result.Winner);
            Assert.Equal("OVR", result.Text);
        }

        [Fact]
        public void Evaluate_OnChangeInactive_ConstantWinsRegardlessOfPosition()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000,
            };
            var constant = new DisplayLayer
            {
                Name = "Always", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BAS",
                ShowWhenRunning = true,
            };
            settings.Layers.Add(onChange);   // index 0 – but not active
            settings.Layers.Add(constant);   // index 1

            // Don't force OnChange active — it should be inactive
            var result = evaluator.Evaluate(null, true, settings);

            Assert.Same(constant, result.Winner);
            Assert.Equal("BAS", result.Text);
        }

        [Fact]
        public void Evaluate_TwoConstants_CycleSelectsSecond()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var first = new DisplayLayer
            {
                Name = "C1", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "AA",
                ShowWhenIdle = true,
            };
            var second = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BB",
                ShowWhenIdle = true,
            };
            settings.Layers.Add(first);
            settings.Layers.Add(second);

            // Default cycle index is 0 → first constant
            var result = evaluator.Evaluate(null, false, settings);
            Assert.Equal("AA", result.Text);

            // Advance to second
            evaluator.NextScreen();
            result = evaluator.Evaluate(null, false, settings);
            Assert.Equal("BB", result.Text);
        }

        [Fact]
        public void Evaluate_ConstantCycle_WrapsAround()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var first = new DisplayLayer
            {
                Name = "C1", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "AA",
                ShowWhenIdle = true,
            };
            var second = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BB",
                ShowWhenIdle = true,
            };
            settings.Layers.Add(first);
            settings.Layers.Add(second);

            // Advance past the end — should wrap back to first
            evaluator.NextScreen();
            evaluator.NextScreen();
            var result = evaluator.Evaluate(null, false, settings);
            Assert.Equal("AA", result.Text);
        }

        [Fact]
        public void Evaluate_HighPriorityConstant_BeatsLowerOverlay_WithCycling()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var c1 = new DisplayLayer
            {
                Name = "C1", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "C1",
                ShowWhenRunning = true,
            };
            var c2 = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "C2",
                ShowWhenRunning = true,
            };
            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000,
            };
            settings.Layers.Add(c1);       // index 0 – highest
            settings.Layers.Add(c2);       // index 1
            settings.Layers.Add(onChange);  // index 2 – lowest

            evaluator.ForceActiveUntil(onChange, DateTime.UtcNow.AddSeconds(10));

            // Cycle to C2 — constant still wins over lower-priority overlay
            evaluator.NextScreen();
            var result = evaluator.Evaluate(null, true, settings);
            Assert.Equal("C2", result.Text);
            Assert.Contains(onChange, result.ActiveLayers);
        }

        [Fact]
        public void Evaluate_OverlayBetweenConstants_OverlayWinsWhenActive()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var c1 = new DisplayLayer
            {
                Name = "C1", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "C1",
                ShowWhenRunning = true,
            };
            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000,
            };
            var c2 = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "C2",
                ShowWhenRunning = true,
            };
            settings.Layers.Add(c1);       // index 0
            settings.Layers.Add(onChange);  // index 1
            settings.Layers.Add(c2);       // index 2

            evaluator.ForceActiveUntil(onChange, DateTime.UtcNow.AddSeconds(10));

            // C1 is at index 0 (highest), so constant still wins
            var result = evaluator.Evaluate(null, true, settings);
            Assert.Equal("C1", result.Text);
        }
    }
}
