using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Keeper laws: pure v2 carrier evaluation pins (FromV2 / Derived / snapshots).
    /// </summary>
    public class CarrierEvaluatorContractTests
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



        // ── onChange lifetime ────────────────────────────────────────────

        [Fact]
        public void OnChange_Any_FiresOnChange_WithDuration()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                DurationMs = 2000,
            };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.Gear), life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.Gear, 2);
            Assert.False(Eval(spec, rt, props, now: 0));
            Assert.False(rt.Active);

            props.Set(BuiltInProperties.Gear, 3);
            Assert.True(Eval(spec, rt, props, now: 100));
            Assert.True(rt.Active);
            Assert.True(rt.FreshFireThisTick);
            Assert.True(rt.FiredThisTick);
            Assert.Equal(2100, rt.ExpiresAt);

            Assert.False(Eval(spec, rt, props, now: 2099));
            Assert.True(rt.Active);
            Assert.Equal(1, CarrierEvaluator.RemainingMs(spec, rt, 2099));

            Assert.False(Eval(spec, rt, props, now: 2100));
            Assert.False(rt.Active);
        }


        [Fact]
        public void OnChange_DirectionUp_IgnoresDown()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = ChangeDirection.Up,
                DurationMs = 1000,
            };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.BrakeBias), life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.BrakeBias, 50);
            Eval(spec, rt, props, 0);

            props.Set(BuiltInProperties.BrakeBias, 49);
            Assert.False(Eval(spec, rt, props, 50));
            Assert.False(rt.Active);

            props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.True(Eval(spec, rt, props, 100));
            Assert.True(rt.Active);
        }


        [Fact]
        public void OnChange_DirectionDown_IgnoresUp()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = ChangeDirection.Down,
                DurationMs = 1000,
            };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.BrakeBias), life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.BrakeBias, 50);
            Eval(spec, rt, props, 0);

            props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.False(Eval(spec, rt, props, 50));

            props.Set(BuiltInProperties.BrakeBias, 49);
            Assert.True(Eval(spec, rt, props, 100));
        }


        [Fact]
        public void OnChange_ThenUntilDismissed_LatchesImmediately_NoTimedPhase()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Then = LifetimeThen.UntilDismissed,
                DurationMs = 9999,
            };
            life.DurationMsIgnored = true;

            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.Gear), life, RunsWhen.Always);
            Assert.Equal(CarrierLifetimeKind.UntilDismissed, spec.Lifetime.Kind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.Gear, 1);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.Gear, 2);
            Assert.True(Eval(spec, rt, props, 100));
            Assert.True(rt.Active);

            Assert.False(Eval(spec, rt, props, 100_000));
            Assert.True(rt.Active);
            Assert.Null(CarrierEvaluator.RemainingMs(spec, rt, 100_000));
        }


        [Fact]
        public void OnChange_DefaultDuration_Is5000()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.Gear), life, RunsWhen.Always);
            Assert.Equal(CarrierLifetimeKind.ForDuration, spec.Lifetime.Kind);
            Assert.Equal(Lifetime.DefaultDurationMs, spec.Lifetime.DurationMs);
        }


        // ── E3-12 (1): operator present on onChange still pure edge ──────

        [Fact]
        public void OnChange_OperatorPresent_StillPureEdge_IgnoresOperator()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange, DurationMs = 1000 };
            var cond = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = BuiltInProperties.Gear },
                Operator = ConditionOperator.GreaterThan, // illegal authoring; FromV2 ignores on onChange path
                Value = 99,
            };
            var spec = CarrierSpec.FromV2("c", cond, life, RunsWhen.Always);
            Assert.Equal(CarrierTriggerFamily.Edge, spec.Trigger.Family);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.Gear, 1);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.Gear, 2);
            Assert.True(Eval(spec, rt, props, 50)); // pure edge fire, not level > 99
            Assert.True(rt.Active);
        }


        // ── E3-12 (2): ThenIgnored → onChange + durationMs ───────────────

        [Fact]
        public void OnChange_ThenIgnored_BehavesAsDurationVisit()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                ThenRaw = "untilSomethingElse",
                DurationMs = 1500,
            };
            life.ThenIgnored = true; // Normalize sets this for garbage then

            var spec = CarrierSpecFixture.Normalized("c", SourceOnly(BuiltInProperties.Gear),
                life, RunsWhen.Always);
            Assert.Equal(CarrierLifetimeKind.ForDuration, spec.Lifetime.Kind);
            Assert.Equal(1500, spec.Lifetime.DurationMs);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.Gear, 1);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.Gear, 2);
            Assert.True(Eval(spec, rt, props, 100));
            Assert.Equal(1600, rt.ExpiresAt);
            Eval(spec, rt, props, 1600);
            Assert.False(rt.Active);
        }


        // ── E3-12 (3): DirectionCoercedToAny ──────────────────────────────

        [Fact]
        public void OnChange_DirectionCoercedToAny_FiresOnAnyChange()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                DirectionRaw = "sideways",
                DurationMs = 1000,
            };
            life.DirectionCoercedToAny = true;

            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.BrakeBias), life, RunsWhen.Always);
            Assert.Equal(CarrierEdgeDirection.Any, spec.Trigger.Direction);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.BrakeBias, 50);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.BrakeBias, 49);
            Assert.True(Eval(spec, rt, props, 50)); // down counts as any
        }


        // ── E3-12 (4): whileTrue without operator never fires ────────────

        [Fact]
        public void WhileTrue_Operatorless_NeverFires()
        {
            var life = new Lifetime { Kind = LifetimeKind.WhileTrue };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.Fuel), life, RunsWhen.Always);
            Assert.Equal(CarrierTriggerFamily.Level, spec.Trigger.Family);
            Assert.Equal(ConditionOperator.Unknown, spec.Trigger.LevelKind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.Fuel, 5);
            Assert.False(Eval(spec, rt, props, 0));
            Assert.False(rt.Active);
            props.Set(BuiltInProperties.Fuel, 50);
            Assert.False(Eval(spec, rt, props, 10));
            Assert.False(rt.Active);
        }


        // ── E3-12 (5): untilDismissed coerced to forDuration behaves ─────

        [Fact]
        public void UntilDismissed_CoercedToForDuration_ExpiresOnWindow()
        {
            // Unflagged content child: validator coerces untilDismissed → forDuration.
            var life = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 800 };
            // Simulate post-coerce: Kind already ForDuration (CoerceKind side effect).
            var spec = CarrierSpec.FromV2("c",
                LevelOp(ConditionOperator.LessThan, BuiltInProperties.Fuel, 10),
                life, RunsWhen.Always);
            Assert.Equal(CarrierLifetimeKind.ForDuration, spec.Lifetime.Kind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.Fuel, 5);
            Assert.True(Eval(spec, rt, props, 0));
            Assert.Equal(800, rt.ExpiresAt);
            Eval(spec, rt, props, 800);
            Assert.False(rt.Active);
            // still true — no rising edge → no permanent latch
            Eval(spec, rt, props, 900);
            Assert.False(rt.Active);
        }


        // ── E3-12 (6): absolute ExpiresAt pins for edge restart ──────────

        [Fact]
        public void OnChange_EdgeInsideOpenWindow_RestartsExpiresAt_Absolute()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange, DurationMs = 5000 };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.BrakeBias), life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.BrakeBias, 50);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.True(Eval(spec, rt, props, 100));
            Assert.Equal(5100, rt.ExpiresAt);
            Assert.True(rt.FreshFireThisTick);
            Assert.True(rt.FiredThisTick);

            props.Set(BuiltInProperties.BrakeBias, 52);
            Assert.False(Eval(spec, rt, props, 1100)); // restart, not fresh
            Assert.True(rt.Active);
            Assert.False(rt.FreshFireThisTick);
            Assert.True(rt.FiredThisTick);
            Assert.Equal(6100, rt.ExpiresAt); // absolute pin: 1100 + 5000
        }


        // ── E3-12 (8) / E3-09: eligibility wipe on v2 path ────────────────

        [Fact]
        public void Eligibility_InGame_Wipe_ClearsActivationAndEdgeBaseline()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange, DurationMs = 30_000 };
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.BrakeBias),
                life, RunsWhen.InGame);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.BrakeBias, 50);
            Eval(spec, rt, props, 0, inGame: true);
            props.Set(BuiltInProperties.BrakeBias, 51);
            Assert.True(Eval(spec, rt, props, 100, inGame: true));
            Assert.True(rt.Active);
            Assert.True(rt.HasPrev);

            // Telemetry drop: ineligible wipe
            Assert.False(Eval(spec, rt, props, 200, inGame: false));
            Assert.False(rt.Active);
            Assert.False(rt.HasPrev);
            Assert.False(rt.Satisfied);

            // Re-entry: first sample never fires
            props.Set(BuiltInProperties.BrakeBias, 52);
            Assert.False(Eval(spec, rt, props, 300, inGame: true));
            Assert.False(rt.Active);
            Assert.True(rt.HasPrev);

            props.Set(BuiltInProperties.BrakeBias, 53);
            Assert.True(Eval(spec, rt, props, 400, inGame: true));
            Assert.True(rt.Active);
        }


        [Fact]
        public void Eligibility_LevelForDuration_Wipe_DestroysWindow()
        {
            var life = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 30_000 };
            var spec = CarrierSpec.FromV2("c",
                LevelOp(ConditionOperator.LessThan, BuiltInProperties.Fuel, 10),
                life, RunsWhen.InGame);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.Fuel, 5);
            Assert.True(Eval(spec, rt, props, 0, inGame: true));
            Assert.Equal(30_000, rt.ExpiresAt);

            Eval(spec, rt, props, 5000, inGame: false);
            Assert.False(rt.Active);

            // still true on re-entry but wiped → no rising edge from clean Satisfied
            Eval(spec, rt, props, 7000, inGame: true);
            Assert.True(rt.Active); // rising from Satisfied false→true after wipe
            // Note: wipe clears Satisfied, so re-entry while still true IS a rising edge.
            // That is the shipped v9-parity wipe behaviour (not pause).
        }


        // ── Level lifetimes ──────────────────────────────────────────────

        [Fact]
        public void WhileTrue_TracksLevel()
        {
            var life = new Lifetime { Kind = LifetimeKind.WhileTrue };
            var spec = CarrierSpec.FromV2("c",
                LevelOp(ConditionOperator.LessThan, BuiltInProperties.Fuel, 10),
                life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.Fuel, 50);
            Eval(spec, rt, props, 0);
            Assert.False(rt.Active);

            props.Set(BuiltInProperties.Fuel, 5);
            Assert.True(Eval(spec, rt, props, 10));
            Assert.True(rt.Active);

            props.Set(BuiltInProperties.Fuel, 50);
            Eval(spec, rt, props, 20);
            Assert.False(rt.Active);
        }


        [Fact]
        public void Level_ForDuration_NoRefireWhileStillTrue()
        {
            var life = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 500 };
            var spec = CarrierSpec.FromV2("c",
                LevelOp(ConditionOperator.LessThan, BuiltInProperties.Fuel, 10),
                life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.Fuel, 5);
            Assert.True(Eval(spec, rt, props, 0));
            Eval(spec, rt, props, 500);
            Assert.False(rt.Active);
            Eval(spec, rt, props, 600);
            Assert.False(rt.Active);
        }


        // ── E3-01: Derived family ────────────────────────────────────────

        [Fact]
        public void Derived_Visit_RestartsWindowOnEveryFiredThisTick()
        {
            var spec = CarrierSpec.Derived("bringUp", CarrierLifetimeKind.ForDuration, durationMs: 4000);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            Assert.True(Eval(spec, rt, props, 0, derivedFired: true));
            Assert.Equal(4000, rt.ExpiresAt);
            Assert.True(rt.FreshFireThisTick);
            Assert.True(rt.FiredThisTick);

            // Child B fires at t=1500 while aggregate still active — window restarts.
            Assert.False(Eval(spec, rt, props, 1500, derivedFired: true, derivedSatisfied: true));
            Assert.True(rt.Active);
            Assert.False(rt.FreshFireThisTick);
            Assert.True(rt.FiredThisTick);
            Assert.Equal(5500, rt.ExpiresAt);

            Eval(spec, rt, props, 5500);
            Assert.False(rt.Active);
        }


        [Fact]
        public void Derived_Pin_WhileTrue_TracksSatisfiedNow()
        {
            var spec = CarrierSpec.Derived("pin", CarrierLifetimeKind.WhileTrue);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            Assert.False(Eval(spec, rt, props, 0, derivedSatisfied: false));
            Assert.True(Eval(spec, rt, props, 10, derivedSatisfied: true));
            Assert.True(rt.Active);
            Assert.False(Eval(spec, rt, props, 20, derivedSatisfied: true));
            Assert.True(rt.Active);
            Assert.False(Eval(spec, rt, props, 30, derivedSatisfied: false));
            Assert.False(rt.Active);
        }


        // ── E3-04: itmField + self ───────────────────────────────────────

        [Fact]
        public void ItmField_Level_ReadsParamId()
        {
            var life = new Lifetime { Kind = LifetimeKind.WhileTrue };
            var spec = CarrierSpec.FromV2("f",
                ItmField("0x42", ConditionOperator.GreaterThan, 5),
                life, RunsWhen.Always);
            Assert.Equal(PropertyKind.ItmField, spec.Trigger.Source.Kind);
            Assert.Equal("0x42", spec.Trigger.Source.Name);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set("0x42", 10);
            Assert.True(Eval(spec, rt, props, 0));
            Assert.True(rt.Active);
        }


        [Fact]
        public void ItmField_Self_BakesOwningFieldParamId()
        {
            var life = new Lifetime { Kind = LifetimeKind.WhileTrue };
            var cond = ItmField("self", ConditionOperator.GreaterThan, 5);
            var spec = CarrierSpec.FromV2("f", cond, life, RunsWhen.Always,
                owningFieldParamId: "0xAB");
            Assert.Equal(PropertyKind.ItmField, spec.Trigger.Source.Kind);
            Assert.Equal("0xAB", spec.Trigger.Source.Name);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set("0xAB", 10);
            Assert.True(Eval(spec, rt, props, 0));
        }


        [Fact]
        public void ItmField_OnChange_FiresOnEdge()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange, DurationMs = 1000 };
            var cond = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.ItmField, Name = "0x10" },
            };
            var spec = CarrierSpec.FromV2("f", cond, life, RunsWhen.Always);
            Assert.Equal(CarrierTriggerFamily.Edge, spec.Trigger.Family);
            Assert.Equal(PropertyKind.ItmField, spec.Trigger.Source.Kind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set("0x10", 1);
            Eval(spec, rt, props, 0);
            props.Set("0x10", 2);
            Assert.True(Eval(spec, rt, props, 50));
        }


        [Fact]
        public void Script_Source_IsParseInert_NeverReads()
        {
            var life = new Lifetime { Kind = LifetimeKind.WhileTrue };
            var cond = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.Script, Name = "x > 1" },
                Operator = ConditionOperator.GreaterThan,
                Value = 1,
            };
            var spec = CarrierSpec.FromV2("s", cond, life, RunsWhen.Always);
            Assert.Equal(PropertyKind.Script, spec.Trigger.Source.Kind);

            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set("x > 1", 99);
            // Parse-inert: evaluator refuses Script even if a reader would resolve the name.
            Assert.False(Eval(spec, rt, props, 0));
            Assert.False(rt.Active);
            Assert.Equal(PropertyKind.Script, spec.Trigger.Source.Kind);
        }


        // ── FiredThisTick on latch re-fire ───────────────────────────────

        [Fact]
        public void FiredThisTick_SetOnWindowRestart_AndLatchedRefire()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Then = LifetimeThen.UntilDismissed,
            };
            life.DurationMsIgnored = true;
            var spec = CarrierSpec.FromV2("c", SourceOnly(BuiltInProperties.Gear), life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();

            props.Set(BuiltInProperties.Gear, 1);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.Gear, 2);
            Assert.True(Eval(spec, rt, props, 100));
            Assert.True(rt.FiredThisTick);
            Assert.True(rt.FreshFireThisTick);

            props.Set(BuiltInProperties.Gear, 3);
            Assert.False(Eval(spec, rt, props, 200)); // already active, not fresh
            Assert.True(rt.Active);
            Assert.True(rt.FiredThisTick);
            Assert.False(rt.FreshFireThisTick);
        }


        // ── Snapshot shape ───────────────────────────────────────────────

        [Fact]
        public void Snapshot_CapturesFreshFireFiredThisTickAndClocks()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange, DurationMs = 1000 };
            var spec = CarrierSpec.FromV2("s", SourceOnly(BuiltInProperties.Gear), life, RunsWhen.Always);
            var rt = new CarrierRuntime();
            var props = new FakeProps();
            props.Set(BuiltInProperties.Gear, 1);
            Eval(spec, rt, props, 0);
            props.Set(BuiltInProperties.Gear, 2);
            Eval(spec, rt, props, 50);

            var snap = CarrierTickSnapshot.From(spec, rt, 50);
            Assert.Equal("s", snap.CarrierId);
            Assert.True(snap.Active);
            Assert.True(snap.FreshFire);
            Assert.True(snap.FiredThisTick);
            Assert.Equal(1050, snap.ExpiresAtMs);
            Assert.Equal(1000, snap.RemainingMs);
            Assert.True(snap.Eligible);
        }

    }
}
