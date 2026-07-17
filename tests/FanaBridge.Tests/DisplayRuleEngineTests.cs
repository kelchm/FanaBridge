using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Exercises <see cref="DisplayRuleEngine"/>: every condition family and hold kind,
    /// hysteresis, priority and the Waiting/OnScreen split, the dwell floor and its
    /// higher-priority preempt exception, alternate flipping, the manual-override policy,
    /// eligibility gating, the bounded activity ring, and determinism. The engine is a pure
    /// clock-injected state machine, so every scenario is a scripted tick sequence.
    /// </summary>
    public class DisplayRuleEngineTests
    {
        // ── Test doubles ─────────────────────────────────────────────────

        private sealed class Clock { public long T; public long Now() => T; }

        /// <summary>Dictionary-backed <see cref="IPropertyReader"/>: absent names read as
        /// missing; numbers and bools cross-convert the way the adapter will (non-zero is
        /// true, true is 1).</summary>
        private sealed class FakePropertyReader : IPropertyReader
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public void Set(string name, double value) => _values[name] = value;
            public void Set(string name, bool value) => _values[name] = value;
            public void Clear(string name) => _values.Remove(name);

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = raw is bool b ? (b ? 1 : 0) : (double)raw;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = raw is bool b ? b : Math.Abs((double)raw) > 1e-9;
                return true;
            }
        }

        private sealed class Harness
        {
            public readonly Clock Clock = new Clock();
            public readonly FakePropertyReader Props = new FakePropertyReader();
            public readonly List<string> Log = new List<string>();
            public DisplayRuleEngine Engine = null!;

            public RuleEngineResult Tick(long advance = 0, bool inGame = true,
                string[]? actions = null, ItmPage? manual = null)
            {
                Clock.T += advance;
                return Engine.Tick(new RuleEngineInput
                {
                    InGame = inGame,
                    Properties = Props,
                    TriggeredActions = actions,
                    Manual = manual.HasValue ? new ManualNavigation(manual.Value) : (ManualNavigation?)null,
                });
            }
        }

        private static Harness Itm(params DisplayRule[] rules)
            => Itm(ItmPage.LapInfo, null, rules);

        private static Harness Itm(ItmPage basePage, ISet<ItmPage>? available, params DisplayRule[] rules)
        {
            var h = new Harness();
            h.Engine = DisplayRuleEngine.ForItm(rules, basePage, available, h.Clock.Now, h.Log.Add);
            return h;
        }

        private static Harness Legacy(string? baseScreenId, params DisplayRule[] rules)
        {
            var h = new Harness();
            h.Engine = DisplayRuleEngine.ForLegacy(rules, baseScreenId, h.Clock.Now, h.Log.Add);
            return h;
        }

        // ── Rule builders ────────────────────────────────────────────────

        private static DisplayRule Rule(string id, RuleCondition when, RuleTarget show,
            HoldSpec hold, RuleEligibility eligible = RuleEligibility.InGame)
            => new DisplayRule { Id = id, When = when, Show = show, Hold = hold, Eligible = eligible };

        private static RuleCondition Level(ConditionKind kind, string name, double? value = null,
            double? hysteresis = null)
            => new RuleCondition
            {
                Kind = kind,
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name },
                Value = value,
                Hysteresis = hysteresis,
            };

        private static RuleCondition Edge(ConditionKind kind, string name)
            => new RuleCondition
            {
                Kind = kind,
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name },
            };

        private static RuleCondition Action(string name)
            => new RuleCondition
            {
                Kind = ConditionKind.ActionTriggered,
                Source = new PropertySpec { Kind = PropertyKind.FanaBridgeAction, Name = name },
            };

        private static RuleTarget Page(ItmPage page)
            => new RuleTarget { Kind = TargetKind.Page, Page = page };

        private static RuleTarget Screen(string id)
            => new RuleTarget { Kind = TargetKind.LegacyScreen, ScreenId = id };

        private static RuleTarget Alt(ItmPage a, ItmPage b, int periodMs)
            => new RuleTarget { Kind = TargetKind.Alternate, PageA = a, PageB = b, PeriodMs = periodMs };

        private static HoldSpec While() => new HoldSpec { Kind = HoldKind.WhileActive };
        private static HoldSpec For(int ms) => new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = ms };
        private static HoldSpec Indef() => new HoldSpec { Kind = HoldKind.Indefinite };

        // ── Assertion helpers ────────────────────────────────────────────

        private static void AssertPage(RuleEngineResult r, ItmPage page, string? ruleId)
        {
            Assert.Equal(TargetKind.Page, r.Intent.Kind);
            Assert.Equal(page, r.Intent.Page);
            Assert.Equal(ruleId, r.Intent.SourceRuleId);
        }

        private static RuleLiveState StateOf(RuleEngineResult r, string ruleId)
            => r.RuleStates.Single(s => s.RuleId == ruleId);

        private static RuleStatus StatusOf(RuleEngineResult r, string ruleId)
            => StateOf(r, ruleId).Status;

        // A dwell-safe advance: longer than MinDwellMs, so intent assertions are about
        // rule logic, not residency timers.
        private const int Settle = DisplayRuleEngine.MinDwellMs + 500;

        // ── Level conditions ─────────────────────────────────────────────

        [Fact]
        public void Level_WhileActive_TracksCondition()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 50);

            var r = h.Tick();
            AssertPage(r, ItmPage.LapInfo, null);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));

            h.Props.Set(BuiltInProperties.Fuel, 5);
            r = h.Tick(advance: 100);
            AssertPage(r, ItmPage.FuelErsDrs, "r");
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "r"));

            h.Props.Set(BuiltInProperties.Fuel, 50);
            r = h.Tick(advance: Settle);
            AssertPage(r, ItmPage.LapInfo, null);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));
        }

        [Theory]
        [InlineData(ConditionKind.LessThan, 9.0, 10.5, 12.0)]      // releases only at value+hyst
        [InlineData(ConditionKind.GreaterThan, 11.0, 9.5, 8.0)]    // releases only at value-hyst
        public void Level_Hysteresis_HoldsThroughReleaseBand(ConditionKind kind,
            double activate, double inBand, double release)
        {
            var h = Itm(Rule("r", Level(kind, BuiltInProperties.Fuel, 10, hysteresis: 2),
                Page(ItmPage.FuelErsDrs), While()));

            h.Props.Set(BuiltInProperties.Fuel, activate);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(), "r"));

            // Past the threshold but inside the hysteresis band: still holding.
            h.Props.Set(BuiltInProperties.Fuel, inBand);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));

            h.Props.Set(BuiltInProperties.Fuel, release);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));

            // The band never ACTIVATES — only the raw threshold does.
            h.Props.Set(BuiltInProperties.Fuel, inBand);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));

            h.Props.Set(BuiltInProperties.Fuel, activate);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));
        }

        [Fact]
        public void Level_EqualsAndNotEquals_UseEpsilon()
        {
            var h = Itm(
                Rule("eq", Level(ConditionKind.Equals, BuiltInProperties.TcLevel, 5),
                    Page(ItmPage.CarSettings), While()),
                Rule("ne", Level(ConditionKind.NotEquals, BuiltInProperties.TcLevel, 5),
                    Page(ItmPage.TyreTemps), While()));

            h.Props.Set(BuiltInProperties.TcLevel, 5 + 1e-10);   // inside epsilon: equal
            var r = h.Tick();
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "eq"));
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "ne"));

            h.Props.Set(BuiltInProperties.TcLevel, 6);
            r = h.Tick(advance: 100);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "eq"));
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "ne"));
        }

        [Fact]
        public void Level_IsTrueIsFalse_ReadBools_AndNumbersAsNonZero()
        {
            var h = Itm(
                Rule("on", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                    Page(ItmPage.FuelErsDrs), While()),
                Rule("off", Level(ConditionKind.IsFalse, BuiltInProperties.DrsAvailable),
                    Page(ItmPage.TyreTemps), While()));

            h.Props.Set(BuiltInProperties.DrsEnabled, true);
            h.Props.Set(BuiltInProperties.DrsAvailable, 1.0);   // SimHub-style 0/1 int
            var r = h.Tick();
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "on"));
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "off"));

            h.Props.Set(BuiltInProperties.DrsEnabled, false);
            h.Props.Set(BuiltInProperties.DrsAvailable, 0.0);
            r = h.Tick(advance: 100);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "on"));
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "off"));
        }

        // ── Missing properties ───────────────────────────────────────────

        [Fact]
        public void MissingProperty_RuleStaysArmed_WarnsExactlyOnce()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));

            for (int i = 0; i < 5; i++)
                Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));

            Assert.Equal(1, h.Log.Count(m => m.Contains("unavailable")));
        }

        [Fact]
        public void MissingProperty_WhileActive_ReleasesTheActivation()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));

            h.Props.Set(BuiltInProperties.Fuel, 5);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(), "r"));

            h.Props.Clear(BuiltInProperties.Fuel);   // missing = not satisfied
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));
        }

        // ── Edge conditions ──────────────────────────────────────────────

        [Fact]
        public void Edge_FirstSampleNeverFires()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), For(5000)));

            h.Props.Set(BuiltInProperties.BrakeBias, 52);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(), "r"));

            h.Props.Set(BuiltInProperties.BrakeBias, 53);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));
        }

        [Theory]
        [InlineData(ConditionKind.Changes, 51.0, true, 49.0, true)]
        [InlineData(ConditionKind.Increases, 51.0, true, 49.0, false)]
        [InlineData(ConditionKind.Decreases, 51.0, false, 49.0, true)]
        public void Edge_KindsFireDirectionally(ConditionKind kind,
            double up, bool firesUp, double down, bool firesDown)
        {
            // Two runs from the same baseline so each direction is a first transition.
            foreach (var (next, fires) in new[] { (up, firesUp), (down, firesDown) })
            {
                var h = Itm(Rule("r", Edge(kind, BuiltInProperties.BrakeBias),
                    Page(ItmPage.CarSettings), For(5000)));
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                h.Tick();
                h.Props.Set(BuiltInProperties.BrakeBias, next);
                Assert.Equal(fires ? RuleStatus.OnScreen : RuleStatus.Armed,
                    StatusOf(h.Tick(advance: 100), "r"));
            }
        }

        [Fact]
        public void Edge_MissingSample_KeepsBaseline_NoFire()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), For(5000)));

            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Clear(BuiltInProperties.BrakeBias);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));

            // The baseline survives the gap: 50 → 51 is still a change.
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Edge_NonFiniteSample_TreatedAsGap_KeepsBaseline(double gap)
        {
            // Gap/delta properties legitimately emit NaN for a frame (no reference car);
            // such a frame must neither fire nor become the new baseline.
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), For(5000)));

            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, gap);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));

            // The baseline survived the non-finite frame: 50 → 51 is still a change.
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));
        }

        // ── ForDuration holds ────────────────────────────────────────────

        [Fact]
        public void ForDuration_ExpiresAfterWindow_WithRemainingCountdown()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), For(5000)));
            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);

            var r = h.Tick(advance: 100);   // fires at t=100, window ends t=5100
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "r"));
            Assert.Equal(5000, StateOf(r, "r").RemainingMs);

            r = h.Tick(advance: 4999);      // t=5099
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "r"));
            Assert.Equal(1, StateOf(r, "r").RemainingMs);

            r = h.Tick(advance: 1);         // t=5100 — window over
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));
            Assert.Null(StateOf(r, "r").RemainingMs);
            AssertPage(r, ItmPage.LapInfo, null);
        }

        [Fact]
        public void ForDuration_RefireRestartsWindow()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), For(5000)));
            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            h.Tick(advance: 100);           // fires; would expire at 5100

            h.Props.Set(BuiltInProperties.BrakeBias, 52);
            h.Tick(advance: 2900);          // refire at t=3000 → new window to 8000

            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 4900), "r"));   // t=7900
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));       // t=8000
        }

        [Fact]
        public void ForDuration_LevelCondition_WindowStartsAtRisingEdge()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), For(5000)));

            h.Props.Set(BuiltInProperties.Fuel, 5);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(), "r"));

            // The condition stays true, but the window is what counts — and once it
            // expires, "still true" is not a rising edge: no re-fire.
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 4999), "r"));
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 1), "r"));
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 1000), "r"));

            // A genuine falling+rising edge re-fires.
            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Tick(advance: 100);
            h.Props.Set(BuiltInProperties.Fuel, 5);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));
        }

        // ── Indefinite holds and every dismissal path ────────────────────

        [Fact]
        public void Indefinite_LatchesLongPastAnyWindow()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), Indef()));
            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            h.Tick(advance: 100);

            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 3_600_000), "r"));
        }

        [Fact]
        public void Indefinite_LevelCondition_DismissedWhenConditionGoesFalse()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), Indef()));

            h.Props.Set(BuiltInProperties.Fuel, 5);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(), "r"));

            h.Props.Set(BuiltInProperties.Fuel, 50);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));
        }

        [Fact]
        public void Indefinite_DismissedByEligibilityLoss()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), Indef()));
            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));

            Assert.Equal(RuleStatus.Ineligible, StatusOf(h.Tick(advance: 100, inGame: false), "r"));
            // Back in game: the latch did not survive — armed, not on screen.
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));
        }

        [Fact]
        public void Indefinite_PreemptedByHigherPriority_DoesNotResumeWhenPreemptorEnds()
        {
            var h = Itm(
                Rule("hi", Action("show"), Page(ItmPage.TyreTemps), For(2000)),
                Rule("lo", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                    Page(ItmPage.CarSettings), Indef()));

            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            var r = h.Tick(advance: 100);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "lo"));

            // Higher-priority rule takes the screen; the indefinite waits...
            r = h.Tick(advance: Settle, actions: new[] { "show" });
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "hi"));
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "lo"));

            // ...and when the preemptor's window ends, it was superseded — not resumed.
            r = h.Tick(advance: 2000);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "hi"));
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "lo"));
            AssertPage(h.Tick(advance: Settle), ItmPage.LapInfo, null);
        }

        [Fact]
        public void Indefinite_DismissedByManualNavigation_NoRelatchWithoutFreshFire()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), Indef()));

            h.Props.Set(BuiltInProperties.Fuel, 5);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(), "r"));

            // Wheel-button navigation dismisses the latch immediately...
            var r = h.Tick(advance: 100, manual: ItmPage.TyreTemps);
            AssertPage(r, ItmPage.TyreTemps, null);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));

            // ...and the still-true condition never re-latches it, however long we wait.
            for (int i = 0; i < 5; i++)
            {
                r = h.Tick(advance: 1000);
                AssertPage(r, ItmPage.TyreTemps, null);
                Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));
            }

            // A genuine falling+rising edge is a NEW fire — the indefinite re-latches.
            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Tick(advance: 100);
            h.Props.Set(BuiltInProperties.Fuel, 5);
            AssertPage(h.Tick(advance: 100), ItmPage.FuelErsDrs, "r");
        }

        [Fact]
        public void Indefinite_FiredWhileOutranked_TakesOverWhenIncumbentEnds()
        {
            // Firing under a higher-priority incumbent is not a preemption: the
            // indefinite never had the screen, so it waits — and gets it when the
            // incumbent's window ends (the Waiting contract).
            var h = Itm(
                Rule("hi", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                    Page(ItmPage.CarSettings), For(2000)),
                Rule("lo", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), Indef()));

            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "hi"));

            h.Props.Set(BuiltInProperties.Fuel, 5);   // fresh rising edge while outranked
            var r = h.Tick(advance: 100);
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "lo"));

            r = h.Tick(advance: 2000);   // hi's window over — lo takes the screen
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "lo"));
            AssertPage(r, ItmPage.FuelErsDrs, "lo");

            // And it latches like any indefinite that won on its own.
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 60_000), "lo"));
        }

        [Fact]
        public void Indefinite_NotDismissedByLowerPriorityActivation()
        {
            var h = Itm(
                Rule("hi", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                    Page(ItmPage.CarSettings), Indef()),
                Rule("lo", Action("show"), Page(ItmPage.TyreTemps), For(2000)));

            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "hi"));

            // A lower-priority rule fires, waits, and expires — it never won, so the
            // indefinite was never preempted.
            var r = h.Tick(advance: 100, actions: new[] { "show" });
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "hi"));
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "lo"));

            r = h.Tick(advance: 2000);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "hi"));
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "lo"));
        }

        [Fact]
        public void ForDuration_PreemptedActivation_ResumesWhenPreemptorEnds()
        {
            // Unlike Indefinite, a timed hold resumes if its window is still open.
            var h = Itm(
                Rule("hi", Action("show"), Page(ItmPage.TyreTemps), For(2000)),
                Rule("lo", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                    Page(ItmPage.CarSettings), For(10000)));

            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Props.Set(BuiltInProperties.BrakeBias, 51);
            h.Tick(advance: 100);   // lo fires, window to 10100

            var r = h.Tick(advance: 1000, actions: new[] { "show" });   // hi wins at t=1100
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "lo"));

            r = h.Tick(advance: 2000);   // t=3100: hi expired, lo still inside its window
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "lo"));
            AssertPage(r, ItmPage.CarSettings, "lo");
        }

        // ── Eligibility ──────────────────────────────────────────────────

        [Fact]
        public void Eligibility_GatesInGameIdleAndAny()
        {
            var h = Itm(
                Rule("game", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), While(), RuleEligibility.InGame),
                Rule("idle", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.TyreTemps), While(), RuleEligibility.Idle),
                Rule("any", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.CarSettings), While(), RuleEligibility.Any));
            h.Props.Set(BuiltInProperties.Fuel, 5);

            var r = h.Tick(inGame: true);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "game"));
            Assert.Equal(RuleStatus.Ineligible, StatusOf(r, "idle"));
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "any"));

            r = h.Tick(advance: Settle, inGame: false);
            Assert.Equal(RuleStatus.Ineligible, StatusOf(r, "game"));
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "idle"));
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "any"));
        }

        [Fact]
        public void Eligibility_Loss_ResetsEdgeBaseline()
        {
            var h = Itm(Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                Page(ItmPage.CarSettings), For(5000)));

            h.Props.Set(BuiltInProperties.BrakeBias, 50);
            h.Tick();
            h.Tick(advance: 100, inGame: false);   // ineligible: baseline dropped

            // Re-entering eligibility re-records the first sample — 50 → 60 across the
            // gap is NOT a fire (a game restart is not a bias change).
            h.Props.Set(BuiltInProperties.BrakeBias, 60);
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 100), "r"));

            h.Props.Set(BuiltInProperties.BrakeBias, 61);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 100), "r"));
        }

        // ── Priority, waiting, base fallback ─────────────────────────────

        [Fact]
        public void Priority_LowestIndexWins_OthersWait()
        {
            var h = Itm(
                Rule("a", Action("one"), Page(ItmPage.FuelErsDrs), For(5000)),
                Rule("b", Action("two"), Page(ItmPage.TyreTemps), For(5000)));

            var r = h.Tick(actions: new[] { "one", "two" });
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "a"));
            Assert.Equal(RuleStatus.Waiting, StatusOf(r, "b"));
            Assert.Null(StateOf(r, "b").RemainingMs);   // countdown is the winner's only
            AssertPage(r, ItmPage.FuelErsDrs, "a");
        }

        [Fact]
        public void BaseFallback_NoWinner_EmitsBaseTarget()
        {
            var h = Itm(ItmPage.LapTimes, null,
                Rule("r", Action("x"), Page(ItmPage.TyreTemps), For(1000)));

            AssertPage(h.Tick(), ItmPage.LapTimes, null);
        }

        [Fact]
        public void LegacyEngine_BaseScreen_AndRuleTargets()
        {
            var h = Legacy("spd",
                Rule("pit", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                    Screen("pit"), While()));

            var r = h.Tick();
            Assert.Equal(TargetKind.LegacyScreen, r.Intent.Kind);
            Assert.Equal("spd", r.Intent.ScreenId);
            Assert.Null(r.Intent.SourceRuleId);

            h.Props.Set(BuiltInProperties.DrsEnabled, true);
            r = h.Tick(advance: 100);
            Assert.Equal("pit", r.Intent.ScreenId);
            Assert.Equal("pit", r.Intent.SourceRuleId);
        }

        [Fact]
        public void LegacyEngine_NullBaseScreen_MeansBlank()
        {
            var h = Legacy(null);
            var r = h.Tick();
            Assert.Equal(TargetKind.LegacyScreen, r.Intent.Kind);
            Assert.Null(r.Intent.ScreenId);
        }

        // ── Dwell floor / preemption ─────────────────────────────────────

        [Fact]
        public void DwellFloor_HoldsIntentThroughConditionFlapping()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Tick();

            h.Props.Set(BuiltInProperties.Fuel, 5);
            AssertPage(h.Tick(advance: 100), ItmPage.FuelErsDrs, "r");   // intent change at t=100

            // The value flaps every 100ms — the intent must not.
            for (int i = 0; i < 10; i++)
            {
                h.Props.Set(BuiltInProperties.Fuel, i % 2 == 0 ? 50 : 5);
                var r = h.Tick(advance: 100);
                Assert.Equal(ItmPage.FuelErsDrs, r.Intent.Page);
            }

            // Condition stays released: after MinDwellMs from the last change, base returns.
            h.Props.Set(BuiltInProperties.Fuel, 50);
            AssertPage(h.Tick(advance: DisplayRuleEngine.MinDwellMs), ItmPage.LapInfo, null);
        }

        [Fact]
        public void Preempt_HigherPriority_AllowedAfterPreemptFloor()
        {
            var h = Itm(
                Rule("hi", Action("show"), Page(ItmPage.TyreTemps), For(5000)),
                Rule("lo", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Tick();

            h.Props.Set(BuiltInProperties.Fuel, 5);
            h.Tick(advance: 100);   // lo takes the screen at t=100

            // Higher-priority fire 100ms later: blocked until PreemptFloorMs of residency.
            var r = h.Tick(advance: 100, actions: new[] { "show" });
            Assert.Equal(ItmPage.FuelErsDrs, r.Intent.Page);

            r = h.Tick(advance: DisplayRuleEngine.PreemptFloorMs - 200);   // t=100+400: still held
            Assert.Equal(ItmPage.FuelErsDrs, r.Intent.Page);

            r = h.Tick(advance: 100);   // t=100+500: preempt allowed
            AssertPage(r, ItmPage.TyreTemps, "hi");
        }

        [Fact]
        public void LowerPriority_CannotPreempt_WaitsFullDwell()
        {
            var h = Itm(
                Rule("hi", Action("show"), Page(ItmPage.TyreTemps), For(300)),
                Rule("lo", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), While()));

            h.Tick();                                    // t=0: base
            h.Tick(advance: 100, actions: new[] { "show" });   // t=100: hi wins (intent change)
            h.Props.Set(BuiltInProperties.Fuel, 5);
            h.Tick(advance: 100);                        // t=200: lo fires, waits

            // hi's window ends at t=400; lo is now the logical winner (OnScreen), but the
            // emitted intent keeps hi's page until MinDwellMs of residency has passed —
            // a lower-priority target never gets the preempt shortcut.
            var r = h.Tick(advance: 200);                // t=400
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "lo"));
            Assert.Equal(ItmPage.TyreTemps, r.Intent.Page);

            r = h.Tick(advance: 1100);                   // t=1500: held 1400ms — still blocked
            Assert.Equal(ItmPage.TyreTemps, r.Intent.Page);

            r = h.Tick(advance: 100);                    // t=1600 = MinDwellMs since the change
            AssertPage(r, ItmPage.FuelErsDrs, "lo");
        }

        // ── Alternate targets ────────────────────────────────────────────

        [Fact]
        public void Alternate_FlipsEachPeriod_UnimpededByDwell()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Alt(ItmPage.FuelErsDrs, ItmPage.TyreTemps, 1000), While()));
            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Tick();

            h.Props.Set(BuiltInProperties.Fuel, 5);
            AssertPage(h.Tick(advance: 100), ItmPage.FuelErsDrs, "r");     // win: A first

            Assert.Equal(ItmPage.FuelErsDrs, h.Tick(advance: 999).Intent.Page);   // t+999
            // The flip period (1000ms) is shorter than MinDwellMs — the internal
            // alternation is exempt from the dwell floor.
            Assert.Equal(ItmPage.TyreTemps, h.Tick(advance: 1).Intent.Page);      // t+1000 → B
            Assert.Equal(ItmPage.FuelErsDrs, h.Tick(advance: 1000).Intent.Page);  // t+2000 → A
            Assert.Equal(ItmPage.TyreTemps, h.Tick(advance: 1000).Intent.Page);   // t+3000 → B
        }

        // ── Manual override ──────────────────────────────────────────────

        [Fact]
        public void Manual_AdoptsImmediately_SupersedesWinner_LogsEvent()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 5);
            h.Tick();
            long versionBefore = h.Engine.ActivityVersion;

            // Wheel button page change 100ms after the rule took the screen — the dwell
            // floor never applies to manual adoption (the display has already moved).
            var r = h.Tick(advance: 100, manual: ItmPage.TyreTemps);
            AssertPage(r, ItmPage.TyreTemps, null);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));   // dismissed, not waiting

            var events = h.Engine.GetActivityEvents();
            Assert.True(h.Engine.ActivityVersion > versionBefore);
            Assert.Contains(events, e => e.Kind == ActivityKind.ManualNavigation);
        }

        [Fact]
        public void Manual_UncatalogedPage_RestsWithoutAPageIntent()
        {
            // Wheel navigation to a page outside the device's catalog is adopted with NO
            // page identity (ManualNavigation with a null page). The engine rests on
            // "wherever the wheel is" — a Page intent carrying no page, which the
            // director requests nothing for, so the unnamed page is never fought.
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 5);
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(), "r"));

            h.Clock.T += 100;
            var r = h.Engine.Tick(new RuleEngineInput
            {
                InGame = true,
                Properties = h.Props,
                Manual = new ManualNavigation(null),
            });
            Assert.Equal(TargetKind.Page, r.Intent.Kind);
            Assert.Null(r.Intent.Page);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));   // dismissed like any manual
            Assert.Contains(h.Engine.GetActivityEvents(),
                e => e.Kind == ActivityKind.ManualNavigation);
        }

        [Fact]
        public void Manual_LevelStillTrue_NeedsFreshEdgeToReclaim()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 5);
            h.Tick();
            h.Tick(advance: 100, manual: ItmPage.TyreTemps);

            // Condition remains true — no rising edge, no re-claim, however long we wait.
            for (int i = 0; i < 5; i++)
            {
                var r = h.Tick(advance: 1000);
                AssertPage(r, ItmPage.TyreTemps, null);
                Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));
            }

            // A genuine falling+rising edge re-enters competition and wins again.
            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Tick(advance: 100);
            h.Props.Set(BuiltInProperties.Fuel, 5);
            AssertPage(h.Tick(advance: 100), ItmPage.FuelErsDrs, "r");
        }

        [Fact]
        public void Manual_PageEqualToRuleTarget_RuleStillNeedsFreshEdge()
        {
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 5);
            h.Tick();

            // The driver pressed the button onto the very page the rule shows: the rule
            // does NOT keep OnScreen credit — the claim is now the driver's.
            var r = h.Tick(advance: 100, manual: ItmPage.FuelErsDrs);
            AssertPage(r, ItmPage.FuelErsDrs, null);
            Assert.Equal(RuleStatus.Armed, StatusOf(r, "r"));
        }

        [Fact]
        public void Manual_BecomesRestingTarget_UntilInGameEdge()
        {
            var h = Itm(Rule("r", Action("show"), Page(ItmPage.CarSettings), For(1000)));
            h.Tick();
            h.Tick(advance: 100, manual: ItmPage.TyreTemps);

            // A rule wins, then expires: the intent returns to the MANUAL page, not base.
            h.Tick(advance: 600, actions: new[] { "show" });   // preempt floor passed
            AssertPage(h.Tick(advance: Settle), ItmPage.TyreTemps, null);

            // Game exit → enter: the resting target reverts to base.
            h.Tick(advance: 100, inGame: false);
            AssertPage(h.Tick(advance: 100, inGame: true), ItmPage.LapInfo, null);
        }

        [Fact]
        public void Manual_RestingRevertOnInGameEdge_StampsDwellClock()
        {
            // The revert from a manual resting page to base is itself an emitted intent
            // change — a rule firing a tick later must wait out the preempt floor, not
            // switch off a stale dwell timestamp from the manual navigation.
            var h = Itm(Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 50);

            h.Tick(inGame: false);
            h.Tick(advance: 100, inGame: false, manual: ItmPage.TyreTemps);

            // Long idle, then the game starts: the resting target reverts to base.
            var r = h.Tick(advance: 50_000);   // in-game rising edge at t=50100
            AssertPage(r, ItmPage.LapInfo, null);

            h.Props.Set(BuiltInProperties.Fuel, 5);
            r = h.Tick(advance: 16);           // one frame later: floor still holds
            AssertPage(r, ItmPage.LapInfo, null);

            r = h.Tick(advance: DisplayRuleEngine.PreemptFloorMs);   // residency reached
            AssertPage(r, ItmPage.FuelErsDrs, "r");
        }

        [Fact]
        public void Manual_IgnoredByLegacyEngine()
        {
            var h = Legacy("spd");
            var r = h.Tick(manual: ItmPage.TyreTemps);
            Assert.Equal("spd", r.Intent.ScreenId);
            Assert.Equal(0, h.Engine.ActivityVersion);
        }

        // ── Action-triggered rules ───────────────────────────────────────

        [Fact]
        public void ActionTriggered_FiresOnExactName_IgnoresOthers()
        {
            var h = Itm(Rule("r", Action("ShowTyres"), Page(ItmPage.TyreTemps), For(2000)));

            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(actions: new[] { "OtherAction" }), "r"));

            var r = h.Tick(advance: 100, actions: new[] { "OtherAction", "ShowTyres" });
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "r"));
            AssertPage(r, ItmPage.TyreTemps, "r");

            // No action this tick: the hold window carries it, then it expires.
            Assert.Equal(RuleStatus.OnScreen, StatusOf(h.Tick(advance: 1000), "r"));
            Assert.Equal(RuleStatus.Armed, StatusOf(h.Tick(advance: 1000), "r"));
        }

        // ── Activity ring ────────────────────────────────────────────────

        [Fact]
        public void ActivityRing_RecordsEveryKind()
        {
            var h = Itm(Rule("r", Action("show"), Page(ItmPage.TyreTemps), For(1000)));
            h.Tick();
            h.Tick(advance: 100, actions: new[] { "show" });   // RuleFired
            h.Tick(advance: 2000);                             // RuleExpired + ReturnedToBase
            h.Tick(advance: 100, manual: ItmPage.CarSettings); // ManualNavigation

            var events = h.Engine.GetActivityEvents();
            Assert.Equal(4, events.Count);
            Assert.Equal(4, h.Engine.ActivityVersion);

            Assert.Equal(ActivityKind.RuleFired, events[0].Kind);
            Assert.Equal("r", events[0].RuleId);
            Assert.Equal(100, events[0].AtMs);
            Assert.Equal(ActivityKind.RuleExpired, events[1].Kind);
            Assert.Equal("r", events[1].RuleId);
            Assert.Equal(ActivityKind.ReturnedToBase, events[2].Kind);
            Assert.Null(events[2].RuleId);
            Assert.Equal(ActivityKind.ManualNavigation, events[3].Kind);
            Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Text)));
        }

        [Fact]
        public void ActivityRing_BoundedAtCapacity_DropsOldest()
        {
            var h = Itm(Rule("r", Action("show"), Page(ItmPage.TyreTemps), For(100)));
            h.Tick();

            // Each cycle: fire (RuleFired), then expiry past the dwell floor
            // (RuleExpired + ReturnedToBase) = 3 events × 20 = 60 > capacity 50.
            for (int i = 0; i < 20; i++)
            {
                h.Tick(advance: 2000, actions: new[] { "show" });
                h.Tick(advance: 2000);
            }

            var events = h.Engine.GetActivityEvents();
            Assert.Equal(DisplayRuleEngine.ActivityCapacity, events.Count);
            Assert.Equal(60, h.Engine.ActivityVersion);
            Assert.True(events[0].AtMs > 2000, "oldest events were dropped");
            // Ring order is chronological.
            for (int i = 1; i < events.Count; i++)
                Assert.True(events[i].AtMs >= events[i - 1].AtMs);
        }

        // ── Statuses: Disabled / Unavailable ─────────────────────────────

        [Fact]
        public void DisabledRule_NeverCompetes()
        {
            var rule = Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While());
            rule.Enabled = false;
            var h = Itm(rule);
            h.Props.Set(BuiltInProperties.Fuel, 5);

            var r = h.Tick();
            Assert.Equal(RuleStatus.Disabled, StatusOf(r, "r"));
            AssertPage(r, ItmPage.LapInfo, null);
        }

        [Fact]
        public void UnavailablePageTarget_PermanentStatus_NeverActivates()
        {
            var available = new HashSet<ItmPage> { ItmPage.LapInfo, ItmPage.FuelErsDrs };
            var h = Itm(ItmPage.LapInfo, available,
                Rule("gone", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.CarSettings), While()),
                Rule("alt", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Alt(ItmPage.FuelErsDrs, ItmPage.TyreTemps, 2000), While()),
                Rule("ok", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), While()));
            h.Props.Set(BuiltInProperties.Fuel, 5);

            var r = h.Tick();
            Assert.Equal(RuleStatus.Unavailable, StatusOf(r, "gone"));
            Assert.Equal(RuleStatus.Unavailable, StatusOf(r, "alt"));   // one missing page is enough
            Assert.Equal(RuleStatus.OnScreen, StatusOf(r, "ok"));
            AssertPage(r, ItmPage.FuelErsDrs, "ok");
            Assert.Equal(2, h.Log.Count(m => m.Contains("does not have")));
        }

        [Fact]
        public void UnavailableStatus_HoldsRegardlessOfEligibility()
        {
            var available = new HashSet<ItmPage> { ItmPage.LapInfo };
            var h = Itm(ItmPage.LapInfo, available,
                Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.CarSettings), While()));

            Assert.Equal(RuleStatus.Unavailable, StatusOf(h.Tick(inGame: true), "r"));
            Assert.Equal(RuleStatus.Unavailable, StatusOf(h.Tick(inGame: false), "r"));
        }

        // ── Determinism ──────────────────────────────────────────────────

        [Fact]
        public void Determinism_SameInputSequenceTwice_SameOutputs()
        {
            Assert.Equal(RunScriptedSequence(), RunScriptedSequence());
        }

        // A mixed script: level + edge + action + alternate rules, flapping values,
        // eligibility changes, manual navigation. Rendered per-tick to strings so any
        // divergence (intent, statuses, activity) fails loudly.
        private static List<string> RunScriptedSequence()
        {
            var h = Itm(
                Rule("act", Action("show"), Page(ItmPage.TyreTemps), For(1200)),
                Rule("edge", Edge(ConditionKind.Increases, BuiltInProperties.CurrentLap),
                    Page(ItmPage.LapTimes), Indef()),
                Rule("alt", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Alt(ItmPage.FuelErsDrs, ItmPage.CarSettings, 1000), While(), RuleEligibility.Any));

            var outputs = new List<string>();
            void Record(RuleEngineResult r) => outputs.Add(
                r.Intent.Kind + "/" + r.Intent.Page + "/" + r.Intent.SourceRuleId + "|"
                + string.Join(",", r.RuleStates.Select(s => s.RuleId + "=" + s.Status + ":" + s.RemainingMs))
                + "|v" + r.ActivityVersion);

            h.Props.Set(BuiltInProperties.Fuel, 50);
            h.Props.Set(BuiltInProperties.CurrentLap, 1);
            Record(h.Tick());
            h.Props.Set(BuiltInProperties.Fuel, 5);
            Record(h.Tick(advance: 100));
            Record(h.Tick(advance: 700, actions: new[] { "show" }));
            h.Props.Set(BuiltInProperties.CurrentLap, 2);
            Record(h.Tick(advance: 300));
            Record(h.Tick(advance: 900));
            Record(h.Tick(advance: 100, manual: ItmPage.CarSettings));
            h.Props.Set(BuiltInProperties.Fuel, 50);
            Record(h.Tick(advance: 400, inGame: false));
            h.Props.Set(BuiltInProperties.Fuel, 5);
            Record(h.Tick(advance: 600, inGame: true));
            Record(h.Tick(advance: 2000));

            foreach (var e in h.Engine.GetActivityEvents())
                outputs.Add(e.AtMs + "/" + e.Kind + "/" + e.Text + "/" + e.RuleId);
            return outputs;
        }

        // ── Formatter ────────────────────────────────────────────────────

        [Fact]
        public void Formatter_ProducesRowLanguage()
        {
            Assert.Equal("Fuel < 10 → Fuel / ERS / DRS", DisplayRuleFormatter.Describe(
                Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), While())));
            Assert.Equal("GapAhead ≤ 0.5 → Lap Times", DisplayRuleFormatter.Describe(
                Rule("r", Level(ConditionKind.LessOrEqual, BuiltInProperties.GapAhead, 0.5),
                    Page(ItmPage.LapTimes), While())));
            Assert.Equal("BrakeBias changes → Car Settings", DisplayRuleFormatter.Describe(
                Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                    Page(ItmPage.CarSettings), For(2000))));
            Assert.Equal("DrsEnabled is on → screen 'fn1'", DisplayRuleFormatter.Describe(
                Rule("r", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                    Screen("fn1"), While())));
            Assert.Equal("'ShowTyres' triggered → Fuel / ERS / DRS ⇄ Tire Temps",
                DisplayRuleFormatter.Describe(Rule("r", Action("ShowTyres"),
                    Alt(ItmPage.FuelErsDrs, ItmPage.TyreTemps, 3000), For(2000))));
        }

        [Fact]
        public void Formatter_Label_PrefersUserName()
        {
            var rule = Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), While());
            Assert.Equal("Fuel < 10 → Fuel / ERS / DRS", DisplayRuleFormatter.Label(rule));

            rule.Name = "Low fuel";
            Assert.Equal("Low fuel", DisplayRuleFormatter.Label(rule));
        }
    }
}
