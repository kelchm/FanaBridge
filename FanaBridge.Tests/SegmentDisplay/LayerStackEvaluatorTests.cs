using System.Collections.Generic;
using System.Linq;
using FanaBridge.SegmentDisplay;
using FanaBridge.Shared;
using FanaBridge.Shared.Conditions;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class LayerStackEvaluatorTests
    {
        // ── Empty / null ────────────────────────────────────────────

        [Fact]
        public void Evaluate_NoLayers_ReturnsEmpty()
        {
            var result = Eval(new SegmentDisplaySettings());
            Assert.Null(result.Winner);
            Assert.Equal("", result.Text);
        }

        [Fact]
        public void Evaluate_NullSettings_ReturnsEmpty()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var result = evaluator.Evaluate(new StubProps(), null, false, null);
            Assert.Null(result.Winner);
        }

        // ── Single screen ───────────────────────────────────────────

        [Fact]
        public void Evaluate_SingleScreen_ShowWhenIdle()
        {
            var layer = MakeScreen("Gear", "3", showIdle: true);
            var result = Eval(Settings(layer), gameRunning: false);
            Assert.Same(layer, result.Winner);
            Assert.Equal("3", result.Text);
        }

        [Fact]
        public void Evaluate_SingleScreen_ShowWhenRunning()
        {
            var layer = MakeScreen("Speed", "120", showRunning: true);
            var result = Eval(Settings(layer), gameRunning: true);
            Assert.Same(layer, result.Winner);
        }

        [Fact]
        public void Evaluate_DisabledLayer_Skipped()
        {
            var layer = MakeScreen("Gear", "3", showRunning: false, showIdle: false);
            var result = Eval(Settings(layer));
            Assert.Null(result.Winner);
        }

        [Fact]
        public void Evaluate_RunningOnlyLayer_NotVisibleWhenIdle()
        {
            var layer = MakeScreen("Speed", "100", showRunning: true, showIdle: false);
            var result = Eval(Settings(layer), gameRunning: false);
            Assert.Null(result.Winner);
        }

        // ── Overlays ────────────────────────────────────────────────

        [Fact]
        public void Evaluate_ActiveOverlay_WinsOverScreen()
        {
            var screen = MakeScreen("Gear", "3");
            var overlay = MakeOverlay("PIT", alwaysActive: true);
            var result = Eval(Settings(overlay, screen));

            Assert.Same(overlay, result.Winner);
            Assert.Equal("PIT", result.Text);
            Assert.Contains(screen, result.ActiveLayers);
            Assert.Contains(overlay, result.ActiveLayers);
        }

        [Fact]
        public void Evaluate_InactiveOverlay_ScreenWins()
        {
            var screen = MakeScreen("Gear", "3");
            var overlay = MakeOverlay("PIT", alwaysActive: false);
            var result = Eval(Settings(overlay, screen));

            Assert.Same(screen, result.Winner);
        }

        [Fact]
        public void Evaluate_MultipleOverlays_FirstActiveWins()
        {
            var screen = MakeScreen("Gear", "3");
            var overlay1 = MakeOverlay("YEL", alwaysActive: false);
            var overlay2 = MakeOverlay("PIT", alwaysActive: true);
            var overlay3 = MakeOverlay("DRS", alwaysActive: true);

            var result = Eval(Settings(overlay1, overlay2, overlay3, screen));
            Assert.Same(overlay2, result.Winner);
            Assert.Equal("PIT", result.Text);
        }

        [Fact]
        public void Evaluate_HasActiveOverlay_SetCorrectly()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var screen = MakeScreen("Gear", "3");
            var overlay = MakeOverlay("PIT", alwaysActive: true);

            evaluator.Evaluate(new StubProps(), null, false, Settings(overlay, screen));
            Assert.True(evaluator.HasActiveOverlay);

            // Now evaluate without active overlay
            var overlay2 = MakeOverlay("PIT", alwaysActive: false);
            evaluator.Evaluate(new StubProps(), null, false, Settings(overlay2, screen));
            Assert.False(evaluator.HasActiveOverlay);
        }

        // ── Screen cycling ──────────────────────────────────────────

        [Fact]
        public void Evaluate_TwoScreens_DefaultsToFirst()
        {
            var screen1 = MakeScreen("Gear", "3");
            var screen2 = MakeScreen("Speed", "120");

            var result = Eval(Settings(screen1, screen2));
            Assert.Same(screen1, result.Winner);
        }

        [Fact]
        public void Evaluate_TwoScreens_NextCyclesToSecond()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var screen1 = MakeScreen("Gear", "3");
            var screen2 = MakeScreen("Speed", "120");
            var settings = Settings(screen1, screen2);

            evaluator.NextScreen();
            var result = evaluator.Evaluate(new StubProps(), null, false, settings);
            Assert.Same(screen2, result.Winner);
        }

        [Fact]
        public void Evaluate_ScreenCycle_WrapsAround()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var screen1 = MakeScreen("Gear", "3");
            var screen2 = MakeScreen("Speed", "120");
            var settings = Settings(screen1, screen2);

            evaluator.NextScreen();
            evaluator.NextScreen(); // past end
            var result = evaluator.Evaluate(new StubProps(), null, false, settings);
            Assert.Same(screen1, result.Winner); // wrapped back to 0
        }

        [Fact]
        public void Evaluate_PreviousScreen_WrapsNegative()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var screen1 = MakeScreen("Gear", "3");
            var screen2 = MakeScreen("Speed", "120");
            var settings = Settings(screen1, screen2);

            evaluator.PreviousScreen(); // -1 wraps to last
            var result = evaluator.Evaluate(new StubProps(), null, false, settings);
            Assert.Same(screen2, result.Winner);
        }

        [Fact]
        public void SetActiveScreen_JumpsToIndex()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var screen1 = MakeScreen("Gear", "3");
            var screen2 = MakeScreen("Speed", "120");
            var screen3 = MakeScreen("Fuel", "87");
            var settings = Settings(screen1, screen2, screen3);

            evaluator.SetActiveScreen(2);
            var result = evaluator.Evaluate(new StubProps(), null, false, settings);
            Assert.Same(screen3, result.Winner);
        }

        // ── Content resolution ──────────────────────────────────────

        [Fact]
        public void Evaluate_FixedTextContent_ReturnsText()
        {
            var layer = new SegmentDisplayLayer
            {
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = "HI" },
                ShowWhenIdle = true,
            };
            var result = Eval(Settings(layer));
            Assert.Equal("HI", result.Text);
        }

        [Fact]
        public void Evaluate_PropertyContent_ResolvesValue()
        {
            var props = new StubProps();
            props.Set("DataCorePlugin.GameData.Gear", 4);

            var layer = new SegmentDisplayLayer
            {
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
                ShowWhenIdle = true,
            };

            var evaluator = new LayerStackEvaluator(() => 0);
            var result = evaluator.Evaluate(props, null, false, Settings(layer));
            Assert.Equal("4", result.Text);
        }

        [Fact]
        public void Evaluate_ExpressionContent_ResolvesExpression()
        {
            var ncalc = new StubNCalc();
            ncalc.Result = 42;

            var layer = new SegmentDisplayLayer
            {
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new ExpressionContent
                {
                    Expression = "[Speed] * 2",
                    Format = SegmentFormat.Number,
                },
                ShowWhenIdle = true,
            };

            var evaluator = new LayerStackEvaluator(() => 0);
            var result = evaluator.Evaluate(new StubProps(), ncalc, false, Settings(layer));
            Assert.Equal("42", result.Text);
        }

        [Fact]
        public void Evaluate_SequenceContent_CyclesByTime()
        {
            var layer = new SegmentDisplayLayer
            {
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new SequenceContent
                {
                    Items = new ContentSource[]
                    {
                        new FixedTextContent { Text = "LOW" },
                        new FixedTextContent { Text = "FUL" },
                    },
                    IntervalMs = 500,
                },
                ShowWhenIdle = true,
            };

            // At t=0, first item (0 % 1000 / 500 = 0)
            var eval0 = new LayerStackEvaluator(() => 0);
            Assert.Equal("LOW", eval0.Evaluate(new StubProps(), null, false, Settings(layer)).Text);

            // At t=600, second item (600 % 1000 / 500 = 1)
            var eval600 = new LayerStackEvaluator(() => 600);
            Assert.Equal("FUL", eval600.Evaluate(new StubProps(), null, false, Settings(layer)).Text);
        }

        // ── OnValueChange overlay ───────────────────────────────────

        [Fact]
        public void Evaluate_OnChangeOverlay_ActivatesOnChange()
        {
            long nowMs = 0;
            var evaluator = new LayerStackEvaluator(() => nowMs);
            var props = new StubProps();
            props.Set("Gear", 3);

            var overlay = new SegmentDisplayLayer
            {
                Role = LayerRole.Overlay,
                Condition = new OnValueChange { Property = "Gear", HoldMs = 2000 },
                Content = new FixedTextContent { Text = "CHG" },
                ShowWhenIdle = true,
            };
            var screen = MakeScreen("Base", "---");
            var settings = Settings(overlay, screen);

            // First eval: seeds state, screen wins
            var r1 = evaluator.Evaluate(props, null, false, settings);
            Assert.Same(screen, r1.Winner);

            // Change gear
            nowMs = 1000;
            props.Set("Gear", 4);
            var r2 = evaluator.Evaluate(props, null, false, settings);
            Assert.Same(overlay, r2.Winner);
            Assert.Equal("CHG", r2.Text);

            // After hold expires
            nowMs = 3500;
            var r3 = evaluator.Evaluate(props, null, false, settings);
            Assert.Same(screen, r3.Winner);
        }

        // ── Reset ───────────────────────────────────────────────────

        [Fact]
        public void Reset_ClearsStateAndCycleIndex()
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            var screen1 = MakeScreen("Gear", "3");
            var screen2 = MakeScreen("Speed", "120");

            evaluator.NextScreen();
            evaluator.Reset();

            var result = evaluator.Evaluate(new StubProps(), null, false, Settings(screen1, screen2));
            Assert.Same(screen1, result.Winner); // reset back to index 0
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static LayerStackResult Eval(SegmentDisplaySettings settings,
                                             bool gameRunning = false)
        {
            var evaluator = new LayerStackEvaluator(() => 0);
            return evaluator.Evaluate(new StubProps(), null, gameRunning, settings);
        }

        private static SegmentDisplaySettings Settings(params SegmentDisplayLayer[] layers)
        {
            var s = new SegmentDisplaySettings();
            foreach (var l in layers) s.Layers.Add(l);
            return s;
        }

        private static SegmentDisplayLayer MakeScreen(string name, string text,
            bool showRunning = false, bool showIdle = true)
        {
            return new SegmentDisplayLayer
            {
                Name = name,
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = text },
                ShowWhenRunning = showRunning,
                ShowWhenIdle = showIdle,
            };
        }

        private static SegmentDisplayLayer MakeOverlay(string text, bool alwaysActive,
            bool showRunning = false, bool showIdle = true)
        {
            return new SegmentDisplayLayer
            {
                Role = LayerRole.Overlay,
                Condition = alwaysActive
                    ? (ActivationCondition)new AlwaysActive()
                    : new WhilePropertyTrue { Property = "__never_set__" },
                Content = new FixedTextContent { Text = text },
                ShowWhenRunning = showRunning,
                ShowWhenIdle = showIdle,
            };
        }

        private class StubProps : IPropertyProvider
        {
            private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
            public void Set(string name, object value) { _values[name] = value; }
            public object GetValue(string name)
            {
                object v;
                return _values.TryGetValue(name, out v) ? v : null;
            }
        }

        private class StubNCalc : INCalcEngine
        {
            public object Result { get; set; }
            public object Evaluate(string expression) { return Result; }
        }
    }
}
