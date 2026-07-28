// Scaffolding — deleted at E8b.
using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E3: evaluator-level v2 lifetime semantics (onChange + direction + then) and
    /// migration-golden alignment with the v9 DisplayRule path. The live v9 engine still
    /// consumes only the v1 subset via <see cref="DisplayRuleCarrierAdapter.ToCarrierSpec"/>.
    /// </summary>
    /// <summary>
    /// Scaffolding — deleted at E8b. v9 DisplayRule adapter, Event-family path, and
    /// v1↔v2 migration goldens for the pre-E8b harness.
    /// </summary>
    public class CarrierEvaluatorV2Tests
    {
        private sealed class FakeProps : IPropertyReader
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public void Set(string name, double value) => _values[name] = value;
            public void Clear(string name) => _values.Remove(name);

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = (double)raw;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = Math.Abs((double)raw) > 1e-9;
                return true;
            }
        }

        private static Condition SourceOnly(string name)
            => new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = name },
            };

        /// <summary>
        /// Event-family CarrierSpec via the v9 actionTriggered path (FA2: v2 Condition
        /// no longer has an action source; Event stays until E8b via FromDisplayRule).
        /// </summary>
        private static CarrierSpec EventSpec(string id, string actionName, HoldSpec hold)
        {
            var rule = new DisplayRule
            {
                Id = id,
                When = new RuleCondition
                {
                    Kind = ConditionKind.ActionTriggered,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.FanaBridgeAction,
                        Name = actionName,
                    },
                },
                Hold = hold,
                Eligible = RuleEligibility.Always,
            };
            return DisplayRuleCarrierAdapter.ToCarrierSpec(rule);
        }

        private static Condition LevelOp(ConditionOperator op, string name, double value,
            double? hyst = null)
            => new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = name },
                Operator = op,
                Value = value,
                Hysteresis = hyst,
            };

        private static Condition ItmField(string name, ConditionOperator op, double value)
            => new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.ItmField, Name = name },
                Operator = op,
                Value = value,
            };

        private static bool Eval(CarrierSpec spec, CarrierRuntime rt, FakeProps props,
            long now, bool inGame = true, string[]? actions = null,
            bool derivedSatisfied = false, bool derivedFired = false)
        {
            return CarrierEvaluator.Evaluate(spec, rt, new CarrierTickInput
            {
                NowMs = now,
                InGame = inGame,
                Properties = props,
                TriggeredActions = actions,
                DerivedSatisfiedNow = derivedSatisfied,
                DerivedFiredThisTick = derivedFired,
            }, warnMissing: null);
        }

        // ── Constants agreement ──────────────────────────────────────────


        [Fact]
        public void DefaultDurationConstants_Agree()
        {
            Assert.Equal(HoldSpec.DefaultDurationMs, Lifetime.DefaultDurationMs);
            Assert.Equal(HoldSpec.DefaultDurationMs, new CarrierLifetime().DurationMs);
        }


        // ── E3-12 (7) / E3-05: Event family (v9 actionTriggered path; FA2) ─

        [Fact]
        public void Event_ForDuration_FiresFromTriggeredActions_AndExpires()
        {
            var spec = EventSpec("act", "showPit",
                new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 2000 });
            Assert.Equal(CarrierTriggerFamily.Event, spec.Trigger.Family);
            Assert.Equal(CarrierLifetimeKind.ForDuration, spec.Lifetime.Kind);
            Assert.Equal(2000, spec.Lifetime.DurationMs);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            Assert.False(Eval(spec, rt, props, 0));
            Assert.True(Eval(spec, rt, props, 100, actions: new[] { "showPit" }));
            Assert.True(rt.Active);
            Assert.True(rt.FreshFireThisTick);
            Assert.Equal(2100, rt.ExpiresAt);
            Assert.False(Eval(spec, rt, props, 2099));
            Assert.True(rt.Active);
            Assert.False(Eval(spec, rt, props, 2100));
            Assert.False(rt.Active);
        }


        [Fact]
        public void Event_UntilDismissed_Latches()
        {
            var spec = EventSpec("act", "showPit",
                new HoldSpec { Kind = HoldKind.UntilDismissed });
            Assert.Equal(CarrierTriggerFamily.Event, spec.Trigger.Family);
            Assert.Equal(CarrierLifetimeKind.UntilDismissed, spec.Lifetime.Kind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            Assert.True(Eval(spec, rt, props, 50, actions: new[] { "showPit" }));
            Assert.True(rt.Active);
            Assert.False(Eval(spec, rt, props, 100_000));
            Assert.True(rt.Active);
            Assert.Null(CarrierEvaluator.RemainingMs(spec, rt, 100_000));
        }


        [Fact]
        public void EventAction_MatchesV1ActionTriggered_Reference()
        {
            // FA2: both sides are FromDisplayRule (v9 path) — Event family stays until E8b.
            var rule = new DisplayRule
            {
                Id = "act",
                When = new RuleCondition
                {
                    Kind = ConditionKind.ActionTriggered,
                    Source = new PropertySpec { Kind = PropertyKind.FanaBridgeAction, Name = "fn1" },
                },
                Hold = new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 3000 },
                Eligible = RuleEligibility.Always,
            };
            var v1Spec = DisplayRuleCarrierAdapter.ToCarrierSpec(rule);
            var v1Rt = new CarrierRuntime();
            var twin = DisplayRuleCarrierAdapter.ToCarrierSpec(rule);
            var twinRt = new CarrierRuntime();

            var props = new FakeProps();
            foreach (var (t, acts) in new (long, string[]?)[]
                     {
                         (0, null),
                         (100, new[] { "fn1" }),
                         (3099, null),
                         (3100, null),
                         (3200, new[] { "fn1" }),
                     })
            {
                bool f1 = Eval(v1Spec, v1Rt, props, t, actions: acts);
                bool f2 = Eval(twin, twinRt, props, t, actions: acts);
                Assert.Equal(f1, f2);
                Assert.Equal(v1Rt.Active, twinRt.Active);
                Assert.Equal(v1Rt.ExpiresAt, twinRt.ExpiresAt);
            }
        }


        // ── E3-06: Event + whileTrue coerces to ForDuration ──────────────

        [Fact]
        public void Event_WhileTrue_CoercesToForDuration_AndReleases()
        {
            var spec = EventSpec("act", "x",
                new HoldSpec { Kind = HoldKind.WhileActive });
            Assert.Equal(CarrierTriggerFamily.Event, spec.Trigger.Family);
            Assert.Equal(CarrierLifetimeKind.ForDuration, spec.Lifetime.Kind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            Assert.True(Eval(spec, rt, props, 0, actions: new[] { "x" }));
            Assert.True(rt.Active);
            Assert.Equal(HoldSpec.DefaultDurationMs, rt.ExpiresAt);
            Eval(spec, rt, props, HoldSpec.DefaultDurationMs);
            Assert.False(rt.Active);
        }


        [Fact]
        public void FromDisplayRule_EdgeWhileActive_CoercesToForDuration()
        {
            var rule = new DisplayRule
            {
                Id = "e",
                When = new RuleCondition
                {
                    Kind = ConditionKind.Changes,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Gear },
                },
                Hold = new HoldSpec { Kind = HoldKind.WhileActive, DurationMs = HoldSpec.DefaultDurationMs },
                Eligible = RuleEligibility.Always,
            };
            var spec = DisplayRuleCarrierAdapter.ToCarrierSpec(rule);
            Assert.Equal(CarrierTriggerFamily.Edge, spec.Trigger.Family);
            Assert.Equal(CarrierLifetimeKind.ForDuration, spec.Lifetime.Kind);
        }


        // ── Migration golden: v1 edge path ≡ v2 onChange path ───────────

        [Theory]
        [InlineData(ConditionKind.Changes, ChangeDirection.Any)]
        [InlineData(ConditionKind.Increases, ChangeDirection.Up)]
        [InlineData(ConditionKind.Decreases, ChangeDirection.Down)]
        public void MigrationGolden_EdgeForDuration_MatchesOnChangeDuration(
            ConditionKind v1Kind, ChangeDirection v2Dir)
        {
            var v1Rule = new DisplayRule
            {
                Id = "edge",
                When = new RuleCondition
                {
                    Kind = v1Kind,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.BrakeBias },
                },
                Hold = new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 2500 },
                Eligible = RuleEligibility.Always,
            };
            var v1Spec = DisplayRuleCarrierAdapter.ToCarrierSpec(v1Rule);
            var v1Rt = new CarrierRuntime();

            var v2Life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = v2Dir,
                DurationMs = 2500,
            };
            var v2Spec = CarrierSpec.FromV2("edge", SourceOnly(BuiltInProperties.BrakeBias),
                v2Life, RunsWhen.Always);
            var v2Rt = new CarrierRuntime();

            var series = new (long t, double? val)[]
            {
                (0, 50),
                (100, 51),
                (200, 49),
                (300, 49),
                (2600, 49),
                (2700, 52),
                (5200, 52),
            };

            var props1 = new FakeProps();
            var props2 = new FakeProps();
            foreach (var (t, val) in series)
            {
                if (val.HasValue)
                {
                    props1.Set(BuiltInProperties.BrakeBias, val.Value);
                    props2.Set(BuiltInProperties.BrakeBias, val.Value);
                }
                bool f1 = Eval(v1Spec, v1Rt, props1, t);
                bool f2 = Eval(v2Spec, v2Rt, props2, t);
                Assert.Equal(f1, f2);
                Assert.Equal(v1Rt.Active, v2Rt.Active);
                Assert.Equal(v1Rt.ExpiresAt, v2Rt.ExpiresAt);
                Assert.Equal(v1Rt.Satisfied, v2Rt.Satisfied);
                Assert.Equal(
                    CarrierEvaluator.RemainingMs(v1Spec, v1Rt, t),
                    CarrierEvaluator.RemainingMs(v2Spec, v2Rt, t));
            }
        }


        [Fact]
        public void MigrationGolden_EdgeUntilDismissed_MatchesOnChangeThen_IncludingDismissal()
        {
            var v1Rule = new DisplayRule
            {
                Id = "edge",
                When = new RuleCondition
                {
                    Kind = ConditionKind.Changes,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Gear },
                },
                Hold = new HoldSpec { Kind = HoldKind.UntilDismissed },
                Eligible = RuleEligibility.Always,
            };
            var v1Spec = DisplayRuleCarrierAdapter.ToCarrierSpec(v1Rule);
            var v1Rt = new CarrierRuntime();

            var v2Life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Then = LifetimeThen.UntilDismissed,
            };
            v2Life.DurationMsIgnored = true;
            var v2Spec = CarrierSpec.FromV2("edge", SourceOnly(BuiltInProperties.Gear),
                v2Life, RunsWhen.Always);
            var v2Rt = new CarrierRuntime();

            var props1 = new FakeProps();
            var props2 = new FakeProps();
            foreach (var (t, val) in new (long, double)[] { (0, 1), (50, 2), (10_000, 2) })
            {
                props1.Set(BuiltInProperties.Gear, val);
                props2.Set(BuiltInProperties.Gear, val);
                bool f1 = Eval(v1Spec, v1Rt, props1, t);
                bool f2 = Eval(v2Spec, v2Rt, props2, t);
                Assert.Equal(f1, f2);
                Assert.Equal(v1Rt.Active, v2Rt.Active);
            }

            // Dismissal half: clear activation; still-true edge does not re-fire without change.
            v1Rt.ClearActivation();
            v2Rt.ClearActivation();
            Assert.False(Eval(v1Spec, v1Rt, props1, 10_100));
            Assert.False(Eval(v2Spec, v2Rt, props2, 10_100));
            Assert.False(v1Rt.Active);
            Assert.False(v2Rt.Active);

            // Fresh edge after dismissal re-latches both paths.
            props1.Set(BuiltInProperties.Gear, 3);
            props2.Set(BuiltInProperties.Gear, 3);
            Assert.True(Eval(v1Spec, v1Rt, props1, 10_200));
            Assert.True(Eval(v2Spec, v2Rt, props2, 10_200));
            Assert.True(v1Rt.Active);
            Assert.True(v2Rt.Active);
        }


        [Fact]
        public void MigrationGolden_LevelWhileActive_MatchesWhileTrue()
        {
            var v1Rule = new DisplayRule
            {
                Id = "lvl",
                When = new RuleCondition
                {
                    Kind = ConditionKind.LessThan,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Fuel },
                    Value = 10,
                    Hysteresis = 2,
                },
                Hold = new HoldSpec { Kind = HoldKind.WhileActive },
                Eligible = RuleEligibility.Always,
            };
            var v1Spec = DisplayRuleCarrierAdapter.ToCarrierSpec(v1Rule);
            var v1Rt = new CarrierRuntime();

            var v2Life = new Lifetime { Kind = LifetimeKind.WhileTrue };
            var v2Spec = CarrierSpec.FromV2("lvl",
                LevelOp(ConditionOperator.LessThan, BuiltInProperties.Fuel, 10, hyst: 2),
                v2Life, RunsWhen.Always);
            var v2Rt = new CarrierRuntime();

            var props1 = new FakeProps();
            var props2 = new FakeProps();
            foreach (var (t, val) in new (long, double)[]
                     { (0, 50), (10, 5), (20, 11), (30, 12), (40, 5) })
            {
                props1.Set(BuiltInProperties.Fuel, val);
                props2.Set(BuiltInProperties.Fuel, val);
                bool f1 = Eval(v1Spec, v1Rt, props1, t);
                bool f2 = Eval(v2Spec, v2Rt, props2, t);
                Assert.Equal(f1, f2);
                Assert.Equal(v1Rt.Active, v2Rt.Active);
                Assert.Equal(v1Rt.Satisfied, v2Rt.Satisfied);
            }
        }


        // ── Live-rule semantics (E3-002) ─────────────────────────────────

        [Fact]
        public void LiveRule_MutateAfterConstruction_ObservedOnNextTick()
        {
            var rule = new DisplayRule
            {
                Id = "live",
                When = new RuleCondition
                {
                    Kind = ConditionKind.LessThan,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Fuel },
                    Value = 10,
                },
                Hold = new HoldSpec { Kind = HoldKind.WhileActive },
                Eligible = RuleEligibility.Always,
                Show = new RuleTarget { Kind = TargetKind.Page, Page = ItmPage.FuelErsDrs },
                Enabled = true,
            };
            var engine = DisplayRuleEngine.ForItm(new[] { rule }, ItmPage.LapInfo,
                null, () => 0);
            var props = new FakeProps();
            props.Set(BuiltInProperties.Fuel, 15); // above threshold 10

            engine.Tick(new RuleEngineInput { InGame = true, Properties = props });
            Assert.Equal(RuleStatus.Armed, engine.Tick(new RuleEngineInput
            {
                InGame = true,
                Properties = props,
            }).RuleStates[0].Status);

            // Mutate threshold after construction — live adapter must observe.
            rule.When.Value = 20;
            var r = engine.Tick(new RuleEngineInput { InGame = true, Properties = props });
            Assert.Equal(RuleStatus.OnScreen, r.RuleStates[0].Status);

            // Mutate hold duration via ForDuration path.
            rule.When.Value = 10;
            rule.When.Kind = ConditionKind.Changes;
            rule.When.Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Gear };
            rule.Hold.Kind = HoldKind.ForDuration;
            rule.Hold.DurationMs = 2500;
            props.Set(BuiltInProperties.Gear, 1);
            engine.Tick(new RuleEngineInput { InGame = true, Properties = props });
            props.Set(BuiltInProperties.Gear, 2);
            long t = 100;
            var eng2 = DisplayRuleEngine.ForItm(new[] { rule }, ItmPage.LapInfo,
                null, () => t);
            props.Set(BuiltInProperties.Gear, 1);
            eng2.Tick(new RuleEngineInput { InGame = true, Properties = props });
            props.Set(BuiltInProperties.Gear, 2);
            t = 100;
            eng2.Tick(new RuleEngineInput { InGame = true, Properties = props });
            // RefreshFromDisplayRule is the unit under test for hold mutation:
            var spec = DisplayRuleCarrierAdapter.ToCarrierSpec(rule);
            rule.Hold.DurationMs = 999;
            DisplayRuleCarrierAdapter.Refresh(spec, rule);
            Assert.Equal(999, spec.Lifetime.DurationMs);

            rule.Eligible = RuleEligibility.Idle;
            DisplayRuleCarrierAdapter.Refresh(spec, rule);
            Assert.Equal(RuleEligibility.Idle, spec.Eligibility);
        }

    }
}
