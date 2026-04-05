using System.Collections.Generic;
using System.Linq;
using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class DisplayManagerTests
    {
        // ── SegmentRendering.TruncateTo3 ────────────────────────────────

        [Theory]
        [InlineData("123", "123")]
        [InlineData("12345", "123")]
        [InlineData("", "")]
        [InlineData("AB", "AB")]
        public void TruncateTo3_TruncatesAtThreeDisplayChars(string input, string expected)
        {
            Assert.Equal(expected, SegmentRendering.TruncateTo3(input));
        }

        [Fact]
        public void TruncateTo3_DotsDoNotCountAsDisplayChars()
        {
            Assert.Equal("1.23", SegmentRendering.TruncateTo3("1.234"));
        }

        [Fact]
        public void TruncateTo3_NullReturnsEmpty()
        {
            Assert.Equal("", SegmentRendering.TruncateTo3(null));
        }

        // ── MigrateFromLegacy ───────────────────────────────────────────

        [Fact]
        public void MigrateFromLegacy_Gear_HasGearConstantLayer()
        {
            var settings = DisplaySettings.MigrateFromLegacy("Gear");
            var gearLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Gear" && l.Mode == DisplayLayerMode.Constant && l.IsEnabled);
            Assert.NotNull(gearLayer);
        }

        [Fact]
        public void MigrateFromLegacy_Speed_HasSpeedConstantLayer()
        {
            var settings = DisplaySettings.MigrateFromLegacy("Speed");
            var speedLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Speed" && l.Mode == DisplayLayerMode.Constant && l.IsEnabled);
            Assert.NotNull(speedLayer);
        }

        [Fact]
        public void MigrateFromLegacy_GearAndSpeed_HasSpeedAndGearChangeOverlay()
        {
            var settings = DisplaySettings.MigrateFromLegacy("GearAndSpeed");
            var speedLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Speed" && l.IsEnabled);
            var gearOverlay = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "GearChange" && l.Mode == DisplayLayerMode.OnChange && l.IsEnabled);
            Assert.NotNull(speedLayer);
            Assert.NotNull(gearOverlay);
        }

        [Fact]
        public void MigrateFromLegacy_UnknownDefaultsToGear()
        {
            var settings = DisplaySettings.MigrateFromLegacy("SomethingWeird");
            var gearLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Gear" && l.IsEnabled);
            Assert.NotNull(gearLayer);
        }

        // ── CreateDefault ───────────────────────────────────────────────

        [Fact]
        public void CreateDefault_HasLayers()
        {
            var settings = DisplaySettings.CreateDefault();
            Assert.True(settings.Layers.Count > 0);
            Assert.Contains(settings.Layers, l => l.CatalogKey == "Gear");
        }

        // ── LayerCatalog ────────────────────────────────────────────────

        [Fact]
        public void LayerCatalog_HasBothConstantAndConditionalEntries()
        {
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.Constant);
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.OnChange);
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.WhileTrue);
        }

        [Fact]
        public void LayerCatalog_CreateFromCatalog_ReturnsCopy()
        {
            var a = LayerCatalog.CreateFromCatalog("Gear");
            var b = LayerCatalog.CreateFromCatalog("Gear");
            Assert.NotSame(a, b);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.CatalogKey, b.CatalogKey);
            Assert.True(a.IsEnabled);
            Assert.True(b.IsEnabled);
        }

        [Fact]
        public void LayerCatalog_FindByKey_ReturnsNull_ForUnknown()
        {
            Assert.Null(LayerCatalog.FindByKey("DoesNotExist"));
        }

        // ── DisplayLayer ────────────────────────────────────────────────

        [Fact]
        public void DisplayLayer_IsGearFormat_MatchesEnum()
        {
            var layer = new DisplayLayer { DisplayFormat = DisplayFormat.Gear };
            Assert.True(layer.IsGearFormat);
            layer.DisplayFormat = DisplayFormat.Number;
            Assert.False(layer.IsGearFormat);
        }

        [Fact]
        public void DisplayLayer_ModeLabel_ReturnsCorrectStrings()
        {
            var layer = new DisplayLayer { Mode = DisplayLayerMode.Constant };
            Assert.Equal("ALWAYS", layer.ModeLabel);
            layer.Mode = DisplayLayerMode.OnChange;
            Assert.Equal("ON CHANGE", layer.ModeLabel);
            layer.Mode = DisplayLayerMode.WhileTrue;
            Assert.Equal("WHILE TRUE", layer.ModeLabel);
        }

        [Fact]
        public void DisplayLayer_TimingLabel_OnlyForOnChange()
        {
            var layer = new DisplayLayer { Mode = DisplayLayerMode.OnChange, DurationMs = 2000 };
            Assert.Equal("2s", layer.TimingLabel);
            layer.Mode = DisplayLayerMode.Constant;
            Assert.Equal("", layer.TimingLabel);
        }

        [Fact]
        public void DisplayLayer_PropertyChanged_Fires()
        {
            var layer = new DisplayLayer();
            string changedProp = null;
            layer.PropertyChanged += (s, e) => changedProp = e.PropertyName;
            layer.Name = "Test";
            Assert.Equal("Name", changedProp);
        }

        // ── SegmentRendering.EncodeText ─────────────────────────────────

        [Fact]
        public void EncodeText_DotFoldsIntoPredecessor()
        {
            var encoded = SegmentRendering.EncodeText("1.2");
            Assert.Equal(2, encoded.Count);
            Assert.True((encoded[0] & 0x80) != 0); // dot bit set on first char
        }

        [Fact]
        public void EncodeText_EmptyReturnsEmpty()
        {
            Assert.Empty(SegmentRendering.EncodeText(""));
            Assert.Empty(SegmentRendering.EncodeText(null));
        }

        // ── LayerStackEvaluator ─────────────────────────────────────────

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
                ShowWhenIdle = true, IsEnabled = true,
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
                ShowWhenIdle = true, IsEnabled = false,
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
                ShowWhenRunning = true, ShowWhenIdle = false, IsEnabled = true,
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

        // ── LayerStackEvaluator static helpers ──────────────────────────

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
            var ts = System.TimeSpan.FromSeconds(65.3);
            Assert.Equal("05.3", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time));
        }

        [Fact]
        public void FormatValue_Time_MinutesSeconds()
        {
            var ts = System.TimeSpan.FromSeconds(65.3);
            Assert.Equal("1.05", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time, @"m\.ss"));
        }

        [Fact]
        public void FormatValue_Time_MinutesSecondsTenths()
        {
            var ts = System.TimeSpan.FromSeconds(65.3);
            Assert.Equal("1.05.3", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time, @"m\.ss\.f"));
        }

        [Fact]
        public void FormatValue_Time_InvalidFormat_FallsBack()
        {
            var ts = System.TimeSpan.FromSeconds(5);
            // Invalid format should fall back to default ss.f
            Assert.Equal("05.0", LayerStackEvaluator.FormatValue(ts, DisplayFormat.Time, "zzz_invalid"));
        }

        [Fact]
        public void FormatValue_Time_NonTimeSpan_FormatsAsDecimal()
        {
            Assert.Equal("4.2", LayerStackEvaluator.FormatValue(4.2, DisplayFormat.Time));
        }

        // ── LayerStackEvaluator – priority ordering ────────────────────

        [Fact]
        public void Evaluate_ConstantBeforeOnChange_ConstantWins()
        {
            var evaluator = new LayerStackEvaluator();
            var settings = new DisplaySettings();

            var constant = new DisplayLayer
            {
                Name = "Always", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "WIN",
                ShowWhenRunning = true, IsEnabled = true,
            };
            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000, IsEnabled = true,
            };
            settings.Layers.Add(constant);  // index 0 – highest priority
            settings.Layers.Add(onChange);   // index 1

            evaluator.ForceActiveUntil(onChange, System.DateTime.UtcNow.AddSeconds(10));
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
                WatchProperty = "Prop", DurationMs = 5000, IsEnabled = true,
            };
            var constant = new DisplayLayer
            {
                Name = "Always", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BAS",
                ShowWhenRunning = true, IsEnabled = true,
            };
            settings.Layers.Add(onChange);   // index 0 – highest priority
            settings.Layers.Add(constant);   // index 1

            evaluator.ForceActiveUntil(onChange, System.DateTime.UtcNow.AddSeconds(10));
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
                WatchProperty = "Prop", DurationMs = 5000, IsEnabled = true,
            };
            var constant = new DisplayLayer
            {
                Name = "Always", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BAS",
                ShowWhenRunning = true, IsEnabled = true,
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
                ShowWhenIdle = true, IsEnabled = true,
            };
            var second = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BB",
                ShowWhenIdle = true, IsEnabled = true,
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
                ShowWhenIdle = true, IsEnabled = true,
            };
            var second = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "BB",
                ShowWhenIdle = true, IsEnabled = true,
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
                ShowWhenRunning = true, IsEnabled = true,
            };
            var c2 = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "C2",
                ShowWhenRunning = true, IsEnabled = true,
            };
            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000, IsEnabled = true,
            };
            settings.Layers.Add(c1);       // index 0 – highest
            settings.Layers.Add(c2);       // index 1
            settings.Layers.Add(onChange);  // index 2 – lowest

            evaluator.ForceActiveUntil(onChange, System.DateTime.UtcNow.AddSeconds(10));

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
                ShowWhenRunning = true, IsEnabled = true,
            };
            var onChange = new DisplayLayer
            {
                Name = "Overlay", Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.FixedText, FixedText = "OVR",
                WatchProperty = "Prop", DurationMs = 5000, IsEnabled = true,
            };
            var c2 = new DisplayLayer
            {
                Name = "C2", Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.FixedText, FixedText = "C2",
                ShowWhenRunning = true, IsEnabled = true,
            };
            settings.Layers.Add(c1);       // index 0
            settings.Layers.Add(onChange);  // index 1
            settings.Layers.Add(c2);       // index 2

            evaluator.ForceActiveUntil(onChange, System.DateTime.UtcNow.AddSeconds(10));

            // C1 is at index 0 (highest), so constant still wins
            var result = evaluator.Evaluate(null, true, settings);
            Assert.Equal("C1", result.Text);
        }
    }
}
