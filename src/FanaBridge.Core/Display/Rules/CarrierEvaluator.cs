using System;
using System.Collections.Generic;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Trigger family for a carrier condition. Level tracks a comparison; Edge fires on a
    /// sample change; Event fires when a named action appears in the tick's action list;
    /// Derived consumes caller-supplied satisfied/fired inputs (condition-less carriers).
    /// </summary>
    public enum CarrierTriggerFamily
    {
        Level,
        Edge,
        Event,
        /// <summary>
        /// Caller-injected condition: <see cref="CarrierTickInput.DerivedSatisfiedNow"/>
        /// (pin / whileTrue) and <see cref="CarrierTickInput.DerivedFiredThisTick"/>
        /// (visit / forDuration per fire). Routes through the same <c>Fire()</c>/expiry path.
        /// </summary>
        Derived,
    }

    /// <summary>Edge direction (v1 Changes/Increases/Decreases; v2 onChange direction).</summary>
    public enum CarrierEdgeDirection
    {
        Any,
        Up,
        Down,
    }

    /// <summary>
    /// Activation lifetime after a fire. WhileTrue tracks a level condition; ForDuration
    /// runs a window from each (re)fire; UntilDismissed latches until external dismissal
    /// (or, for level triggers, the condition going false).
    /// </summary>
    public enum CarrierLifetimeKind
    {
        WhileTrue,
        ForDuration,
        UntilDismissed,
    }

    /// <summary>
    /// Abstract condition vocabulary — what a carrier tests each tick. Carrier-shaped:
    /// both v1 <see cref="RuleCondition"/> and v2 <see cref="Condition"/>
    /// + <see cref="Lifetime"/> adapt onto this.
    /// </summary>
    public sealed class CarrierTrigger
    {
        public CarrierTriggerFamily Family { get; set; }

        /// <summary>Property/action source for property reads and event matching.</summary>
        public PropertySpec Source { get; set; }

        /// <summary>Level family only: v1 <see cref="ConditionKind"/> level spelling.</summary>
        public ConditionKind LevelKind { get; set; }

        public double? Value { get; set; }
        public double? Hysteresis { get; set; }

        /// <summary>Edge family only.</summary>
        public CarrierEdgeDirection Direction { get; set; }
    }

    /// <summary>Abstract lifetime vocabulary after a condition fire.</summary>
    public sealed class CarrierLifetime
    {
        public CarrierLifetimeKind Kind { get; set; }

        /// <summary><see cref="CarrierLifetimeKind.ForDuration"/> window length.
        /// Default matches both <see cref="CarrierDefaults.DefaultDurationMs"/> and
        /// <see cref="Lifetime.DefaultDurationMs"/> (pinned equal by tests).</summary>
        public int DurationMs { get; set; } = CarrierDefaults.DefaultDurationMs;
    }

    /// <summary>
    /// One evaluable carrier: identity + trigger + lifetime + eligibility. Priority and
    /// target are NOT here — selection owns those.
    /// </summary>
    public sealed class CarrierSpec
    {
        public CarrierSpec(string id, CarrierTrigger trigger, CarrierLifetime lifetime,
            RuleEligibility eligibility)
        {
            Id = id ?? "";
            Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
            Lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            Eligibility = eligibility;
        }

        public string Id { get; }
        public CarrierTrigger Trigger { get; }
        public CarrierLifetime Lifetime { get; }
        public RuleEligibility Eligibility { get; internal set; }

        /// <summary>
        /// Adapt a v2 <see cref="Condition"/> + <see cref="Lifetime"/> onto the same
        /// evaluator machine. <b>Precondition:</b> the document has been through
        /// <c>DisplayConfigV2Validator.Normalize</c> (see contract §1). Dispatch is on
        /// lifetime kind: onChange → Edge; otherwise Level. (FA2: v2 Condition vocabulary
        /// no longer has an <c>action</c> source; Event family remains via the scaffolding
        /// rule adapter for the v9 actionTriggered path until E8b.)
        /// <paramref name="owningFieldParamId"/> bakes itmField <c>self</c> to the owning
        /// field's param id at spec-build time.
        /// </summary>
        public static CarrierSpec FromV2(string id, Condition condition, Lifetime lifetime,
            RunsWhen runs, string owningFieldParamId = null)
        {
            var trigger = new CarrierTrigger();
            var life = new CarrierLifetime();

            // Source: map ValueSource → PropertySpec for IPropertyReader.
            if (condition?.Source != null)
            {
                string name = condition.Source.Name;
                if (condition.Source.Kind == ValueSourceKind.ItmField
                    && string.Equals(name, "self", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(owningFieldParamId))
                {
                    name = owningFieldParamId;
                }
                trigger.Source = new PropertySpec
                {
                    Name = name,
                    Kind = MapSourceKind(condition.Source.Kind),
                };
            }

            LifetimeKind lifeKind = lifetime != null ? lifetime.Kind : LifetimeKind.WhileTrue;
            if (lifetime == null)
                lifeKind = LifetimeKind.WhileTrue;

            if (lifeKind == LifetimeKind.OnChange)
            {
                trigger.Family = CarrierTriggerFamily.Edge;
                // Direction: coerced-to-any uses Any; Unknown → Any (engine view).
                if (lifetime != null && lifetime.DirectionCoercedToAny)
                    trigger.Direction = CarrierEdgeDirection.Any;
                else if (lifetime != null)
                {
                    switch (lifetime.Direction)
                    {
                        case ChangeDirection.Up:
                            trigger.Direction = CarrierEdgeDirection.Up;
                            break;
                        case ChangeDirection.Down:
                            trigger.Direction = CarrierEdgeDirection.Down;
                            break;
                        default:
                            trigger.Direction = CarrierEdgeDirection.Any;
                            break;
                    }
                }
                else
                    trigger.Direction = CarrierEdgeDirection.Any;

                // then:untilDismissed → latch immediately, no timed phase.
                // then ignored / absent → durationMs visit (default 5000).
                bool thenUntil = lifetime != null
                    && !lifetime.ThenIgnored
                    && lifetime.Then == LifetimeThen.UntilDismissed;
                if (thenUntil)
                {
                    life.Kind = CarrierLifetimeKind.UntilDismissed;
                }
                else
                {
                    life.Kind = CarrierLifetimeKind.ForDuration;
                    life.DurationMs = lifetime != null && !lifetime.DurationMsIgnored
                        ? lifetime.DurationMs
                        : CarrierDefaults.DefaultDurationMs;
                }
            }
            else
            {
                trigger.Family = CarrierTriggerFamily.Level;
                trigger.LevelKind = MapOperator(condition?.Operator);
                trigger.Value = condition?.Value;
                trigger.Hysteresis = condition != null && !condition.HysteresisIgnored
                    ? condition.Hysteresis
                    : null;
                MapPostFireLifetime(life, lifeKind, lifetime);
            }

            // whileTrue is level-only; edge/event coerce to forDuration (v1 law).
            CoerceNonLevelWhileTrue(trigger, life);

            return new CarrierSpec(id, trigger, life, MapRuns(runs));
        }

        /// <summary>Build a condition-less derived carrier (bring-up aggregate, childRef
        /// satellite). Caller supplies satisfied/fired each tick via
        /// <see cref="CarrierTickInput"/>.</summary>
        public static CarrierSpec Derived(string id, CarrierLifetimeKind lifetimeKind,
            int durationMs = CarrierDefaults.DefaultDurationMs,
            RuleEligibility eligibility = RuleEligibility.Always)
        {
            var trigger = new CarrierTrigger { Family = CarrierTriggerFamily.Derived };
            var life = new CarrierLifetime { Kind = lifetimeKind, DurationMs = durationMs };
            if (lifetimeKind != CarrierLifetimeKind.ForDuration)
                life.DurationMs = durationMs;
            return new CarrierSpec(id, trigger, life, eligibility);
        }

        private static void MapPostFireLifetime(CarrierLifetime life, LifetimeKind lifeKind,
            Lifetime lifetime)
        {
            switch (lifeKind)
            {
                case LifetimeKind.OnChange:
                {
                    // onChange post-fire (used when not already dispatched as Edge above).
                    bool thenUntil = lifetime != null
                        && !lifetime.ThenIgnored
                        && lifetime.Then == LifetimeThen.UntilDismissed;
                    if (thenUntil)
                    {
                        life.Kind = CarrierLifetimeKind.UntilDismissed;
                    }
                    else
                    {
                        life.Kind = CarrierLifetimeKind.ForDuration;
                        life.DurationMs = lifetime != null && !lifetime.DurationMsIgnored
                            ? lifetime.DurationMs
                            : CarrierDefaults.DefaultDurationMs;
                    }
                    break;
                }
                case LifetimeKind.ForDuration:
                    life.Kind = CarrierLifetimeKind.ForDuration;
                    life.DurationMs = lifetime != null && !lifetime.DurationMsIgnored
                        ? lifetime.DurationMs
                        : CarrierDefaults.DefaultDurationMs;
                    break;
                case LifetimeKind.UntilDismissed:
                    life.Kind = CarrierLifetimeKind.UntilDismissed;
                    break;
                case LifetimeKind.WhileTrue:
                default:
                    life.Kind = CarrierLifetimeKind.WhileTrue;
                    break;
            }
        }

        /// <summary>whileTrue is a level-only lifetime; edge/event carriers coerce to
        /// forDuration (mirrors v1 DisplayConfigValidator non-level/WhileActive law).</summary>
        internal static void CoerceNonLevelWhileTrue(CarrierTrigger trigger, CarrierLifetime life)
        {
            if (life.Kind != CarrierLifetimeKind.WhileTrue)
                return;
            if (trigger.Family != CarrierTriggerFamily.Edge
                && trigger.Family != CarrierTriggerFamily.Event)
                return;
            life.Kind = CarrierLifetimeKind.ForDuration;
            if (life.DurationMs <= 0)
                life.DurationMs = CarrierDefaults.DefaultDurationMs;
        }

        private static PropertyKind MapSourceKind(ValueSourceKind kind)
        {
            switch (kind)
            {
                case ValueSourceKind.BuiltIn: return PropertyKind.BuiltIn;
                case ValueSourceKind.SimHubProperty: return PropertyKind.SimHubProperty;
                case ValueSourceKind.ItmField: return PropertyKind.ItmField;
                case ValueSourceKind.Script: return PropertyKind.Script;
                default: return PropertyKind.Unknown;
            }
        }

        private static ConditionKind MapOperator(ConditionOperator? op)
        {
            if (op == null)
                return ConditionKind.Unknown;
            switch (op.Value)
            {
                case ConditionOperator.LessThan: return ConditionKind.LessThan;
                case ConditionOperator.LessOrEqual: return ConditionKind.LessOrEqual;
                case ConditionOperator.GreaterThan: return ConditionKind.GreaterThan;
                case ConditionOperator.GreaterOrEqual: return ConditionKind.GreaterOrEqual;
                case ConditionOperator.Equals: return ConditionKind.Equals;
                case ConditionOperator.NotEquals: return ConditionKind.NotEquals;
                case ConditionOperator.IsTrue: return ConditionKind.IsTrue;
                case ConditionOperator.IsFalse: return ConditionKind.IsFalse;
                default: return ConditionKind.Unknown;
            }
        }

        private static RuleEligibility MapRuns(RunsWhen runs)
        {
            switch (runs)
            {
                case RunsWhen.Always: return RuleEligibility.Always;
                case RunsWhen.Idle: return RuleEligibility.Idle;
                case RunsWhen.InGame: return RuleEligibility.InGame;
                default: return RuleEligibility.InGame;
            }
        }
    }

    /// <summary>
    /// Test-only helper: build a <see cref="CarrierSpec"/> as if the condition/lifetime
    /// had already been through <c>DisplayConfigV2Validator.Normalize</c> (sets the five
    /// runtime flags FromV2 reads). Downstream phase fixtures should use this rather than
    /// hand-rolling post-validation semantics.
    /// </summary>
    public static class CarrierSpecFixture
    {
        /// <summary>
        /// Apply the Normalize-visible runtime flags that FromV2 consumes, then call
        /// <see cref="CarrierSpec.FromV2"/>. Flags: ThenIgnored, DurationMsIgnored,
        /// DirectionCoercedToAny, HysteresisIgnored, and CoerceKind side effects already
        /// applied by the caller or simulated here for common cases.
        /// </summary>
        public static CarrierSpec Normalized(string id, Condition condition, Lifetime lifetime,
            RunsWhen runs, string owningFieldParamId = null)
        {
            // Simulate the mutual-exclusivity / domain flags Normalize would set.
            if (lifetime != null)
            {
                bool thenPresent = lifetime.Then == LifetimeThen.UntilDismissed
                    || (!string.IsNullOrWhiteSpace(lifetime.ThenRaw)
                        && lifetime.Then != LifetimeThen.Unknown);
                bool thenUntil = !lifetime.ThenIgnored
                    && lifetime.Then == LifetimeThen.UntilDismissed;
                if (thenUntil)
                    lifetime.DurationMsIgnored = true;

                if (lifetime.Direction == ChangeDirection.Unknown
                    && !string.IsNullOrWhiteSpace(lifetime.DirectionRaw))
                    lifetime.DirectionCoercedToAny = true;
            }

            if (condition != null && condition.Hysteresis != null)
            {
                bool hasOp = condition.Operator != null
                    && condition.Operator != ConditionOperator.Unknown;
                LifetimeKind lk = lifetime != null ? lifetime.Kind : LifetimeKind.WhileTrue;
                if (!hasOp || lk == LifetimeKind.OnChange)
                    condition.HysteresisIgnored = true;
            }

            return CarrierSpec.FromV2(id, condition, lifetime, runs, owningFieldParamId);
        }
    }

    /// <summary>
    /// Mutable per-carrier runtime state between ticks. Evaluator-owned fields use
    /// internal setters (compiler-enforced ownership). Policy uses
    /// <see cref="MarkSuperseded"/> / <see cref="ClearActivation"/> (v9 path only for
    /// supersede; v2 never writes Active/Superseded — see contract §4).
    /// </summary>
    public sealed class CarrierRuntime
    {
        // Condition state (evaluator-owned).
        public bool Satisfied { get; internal set; }
        public bool HasPrev { get; internal set; }
        public double Prev { get; internal set; }

        // Activation / hold clock (evaluator-owned; v9 policy may ClearActivation).
        public bool Active { get; internal set; }
        public long ExpiresAt { get; internal set; }

        /// <summary>
        /// v9-path selection latch (displaced UntilDismissed). Cleared on Fire and on
        /// ineligible wipe. v2 SeatArbiter must NOT use this — destination-scoped latches
        /// replace it. Prefer <see cref="MarkSuperseded"/> over writing the setter.
        /// </summary>
        public bool Superseded { get; internal set; }

        public bool EligibleNow { get; internal set; }
        public bool WarnedMissing { get; internal set; }

        /// <summary>
        /// True when <c>Fire()</c> ran this tick (including ForDuration window restarts and
        /// re-fires on an already-active latched carrier). Policy-neutral primitive for
        /// destination-scoped re-arm; set on every Fire, cleared at Evaluate start.
        /// </summary>
        public bool FiredThisTick { get; internal set; }

        /// <summary>
        /// Derived convenience: true when this tick's Fire created a new claim
        /// (<c>!Active || Superseded</c> before Fire). Window restarts while already
        /// active are NOT fresh.
        /// </summary>
        public bool FreshFireThisTick { get; internal set; }

        /// <summary>v9 selection policy: mark a displaced UntilDismissed activation.</summary>
        public void MarkSuperseded() => Superseded = true;

        /// <summary>v9 selection / manual-nav: drop the live activation (and clear supersede).</summary>
        public void ClearActivation()
        {
            Active = false;
            Superseded = false;
        }
    }

    /// <summary>One tick's external inputs for carrier evaluation.</summary>
    public struct CarrierTickInput
    {
        public long NowMs;
        public bool InGame;
        public IPropertyReader Properties;
        public IReadOnlyList<string> TriggeredActions;

        /// <summary>
        /// Caller-injected game identity (e.g. "IRacing"). The evaluator does not apply
        /// game-change policy; E4 owns the reset law. Empty/null means unspecified.
        /// </summary>
        public string GameId;

        /// <summary>
        /// Caller-injected edge: game identity changed since the previous tick.
        /// No evaluator policy — E4 keys the manual-row reset on this.
        /// </summary>
        public bool GameChanged;

        /// <summary>Derived family: pin/whileTrue satisfied input for this tick.</summary>
        public bool DerivedSatisfiedNow;

        /// <summary>
        /// Derived family: visit fire this tick. Restarts a ForDuration window via
        /// the same Fire path as edge/event (law 5: for X s each time one fires).
        /// </summary>
        public bool DerivedFiredThisTick;
    }

    /// <summary>
    /// Pure carrier evaluator: condition evaluation, hysteresis, hold clocks, eligibility
    /// gating, and fire/lifetime semantics. No selection, dwell, activity ring, or
    /// dismissal policy — those stay with the arbiter / v9 engine.
    ///
    /// Structural choice: one type rather than ConditionEvaluator + HoldClock. Fire and
    /// lifetime are one state machine (rising edge starts a ForDuration window; WhileTrue
    /// tracks satisfied; Superseded interacts with Fire's fresh flag). Splitting the clock
    /// would force awkward shared mutability without a cleaner seam.
    /// </summary>
    public static class CarrierEvaluator
    {
        /// <summary>Comparison tolerance for Equals/NotEquals and edge change detection.</summary>
        public const double Epsilon = 1e-9;

        /// <summary>
        /// Evaluate one carrier for this tick. Mutates <paramref name="runtime"/>.
        /// Returns whether a fresh fire occurred (for activity logging).
        /// </summary>
        public static bool Evaluate(CarrierSpec spec, CarrierRuntime runtime,
            in CarrierTickInput input, Action warnMissing)
        {
            runtime.FreshFireThisTick = false;
            runtime.FiredThisTick = false;

            runtime.EligibleNow = spec.Eligibility == RuleEligibility.Always
                || (spec.Eligibility == RuleEligibility.InGame ? input.InGame : !input.InGame);
            if (!runtime.EligibleNow)
            {
                // Ineligible clears everything: the activation, the level latch, and the
                // edge prev-value — re-entering eligibility starts from a clean slate
                // (an "edge" spanning a game restart is not a real change). v9-parity wipe
                // (shipped behaviour; D19 planes still evaluate — entry is reset, not paused).
                runtime.Active = false;
                runtime.Superseded = false;
                runtime.Satisfied = false;
                runtime.HasPrev = false;
                return false;
            }

            switch (spec.Trigger.Family)
            {
                case CarrierTriggerFamily.Level:
                    EvaluateLevel(spec, runtime, input, warnMissing);
                    break;
                case CarrierTriggerFamily.Edge:
                    EvaluateEdge(spec, runtime, input, warnMissing);
                    break;
                case CarrierTriggerFamily.Event:
                    EvaluateEvent(spec, runtime, input);
                    break;
                case CarrierTriggerFamily.Derived:
                    EvaluateDerived(spec, runtime, input);
                    break;
            }

            // ForDuration expiry (the window runs from the last fire, condition-independent).
            if (runtime.Active && spec.Lifetime.Kind == CarrierLifetimeKind.ForDuration
                && input.NowMs >= runtime.ExpiresAt)
                runtime.Active = false;

            return runtime.FreshFireThisTick;
        }

        private static void EvaluateLevel(CarrierSpec spec, CarrierRuntime runtime,
            in CarrierTickInput input, Action warnMissing)
        {
            var c = spec.Trigger;
            bool wasSatisfied = runtime.Satisfied;
            bool satisfied = false;

            if (c.LevelKind == ConditionKind.IsTrue || c.LevelKind == ConditionKind.IsFalse)
            {
                if (TryReadBool(c.Source, runtime, input, warnMissing, out bool b))
                    satisfied = c.LevelKind == ConditionKind.IsTrue ? b : !b;
                // Missing property → not satisfied (rule stays armed / releases).
            }
            else if (TryReadNumber(c.Source, runtime, input, warnMissing, out double x))
            {
                double v = c.Value ?? 0;
                double h = c.Hysteresis ?? 0;
                // Hysteresis acts on release only: once satisfied, the condition lets go
                // only past the threshold by the margin, in the releasing direction.
                satisfied = wasSatisfied
                    ? StillHolds(c.LevelKind, x, v, h)
                    : SatisfiedNow(c.LevelKind, x, v);
            }

            runtime.Satisfied = satisfied;
            bool rising = satisfied && !wasSatisfied;

            switch (spec.Lifetime.Kind)
            {
                case CarrierLifetimeKind.WhileTrue:
                    // Active exactly while satisfied — except a dismissed activation stays
                    // down until a fresh rising edge (satisfied never re-fires by itself).
                    if (rising)
                        Fire(spec, runtime, input.NowMs);
                    else if (!satisfied)
                        runtime.Active = false;
                    break;
                case CarrierLifetimeKind.ForDuration:
                    if (rising)
                        Fire(spec, runtime, input.NowMs);   // the window starts at the rising edge
                    break;
                case CarrierLifetimeKind.UntilDismissed:
                    if (rising)
                        Fire(spec, runtime, input.NowMs);
                    else if (!satisfied)
                        runtime.Active = false;   // level Indefinite: condition going false dismisses
                    break;
            }
        }

        private static void EvaluateEdge(CarrierSpec spec, CarrierRuntime runtime,
            in CarrierTickInput input, Action warnMissing)
        {
            // Missing sample: keep the previous value — a brief gap must neither fire nor
            // reset the edge baseline (eligibility loss is what resets it).
            if (!TryReadNumber(spec.Trigger.Source, runtime, input, warnMissing, out double x))
                return;
            // A non-finite sample is a gap, not a value (gap/delta properties emit NaN
            // when there is no reference car): same rule — no fire, baseline kept.
            if (double.IsNaN(x) || double.IsInfinity(x))
                return;
            if (!runtime.HasPrev)
            {
                // First sample never fires — there is nothing to have changed FROM.
                runtime.HasPrev = true;
                runtime.Prev = x;
                return;
            }

            bool fired;
            switch (spec.Trigger.Direction)
            {
                case CarrierEdgeDirection.Up: fired = x > runtime.Prev + Epsilon; break;
                case CarrierEdgeDirection.Down: fired = x < runtime.Prev - Epsilon; break;
                default: fired = Math.Abs(x - runtime.Prev) > Epsilon; break;   // Any
            }
            runtime.Prev = x;
            if (fired)
                Fire(spec, runtime, input.NowMs);
        }

        private static void EvaluateEvent(CarrierSpec spec, CarrierRuntime runtime,
            in CarrierTickInput input)
        {
            var actions = input.TriggeredActions;
            if (actions == null)
                return;
            string name = spec.Trigger.Source?.Name;
            if (name == null)
                return;
            for (int i = 0; i < actions.Count; i++)
            {
                if (string.Equals(actions[i], name, StringComparison.Ordinal))
                {
                    Fire(spec, runtime, input.NowMs);
                    return;
                }
            }
        }

        /// <summary>
        /// Derived carriers (bring-up aggregate, childRef satellite): caller supplies
        /// SatisfiedNow and/or FiredThisTick. Visit = restart window on every
        /// DerivedFiredThisTick; pin tracks DerivedSatisfiedNow like level WhileTrue.
        /// </summary>
        private static void EvaluateDerived(CarrierSpec spec, CarrierRuntime runtime,
            in CarrierTickInput input)
        {
            bool wasSatisfied = runtime.Satisfied;
            bool satisfied = input.DerivedSatisfiedNow;
            runtime.Satisfied = satisfied;
            bool rising = satisfied && !wasSatisfied;

            // Visit / any explicit fire this tick → same Fire path (restarts ForDuration).
            if (input.DerivedFiredThisTick)
            {
                Fire(spec, runtime, input.NowMs);
                // Still apply release for pin when not satisfied after a visit fire? No —
                // a fire this tick owns the activation; release paths run only when not fired.
                return;
            }

            switch (spec.Lifetime.Kind)
            {
                case CarrierLifetimeKind.WhileTrue:
                    if (rising)
                        Fire(spec, runtime, input.NowMs);
                    else if (!satisfied)
                        runtime.Active = false;
                    break;
                case CarrierLifetimeKind.ForDuration:
                    if (rising)
                        Fire(spec, runtime, input.NowMs);
                    break;
                case CarrierLifetimeKind.UntilDismissed:
                    if (rising)
                        Fire(spec, runtime, input.NowMs);
                    else if (!satisfied)
                        runtime.Active = false;
                    break;
            }
        }

        // A fire creates an activation, or restarts a ForDuration window / re-enters a
        // superseded activation. FiredThisTick is set on EVERY Fire (policy-neutral).
        // Only a genuinely new claim is a fresh fire — window restarts while already
        // active would drown the activity feed.
        private static void Fire(CarrierSpec spec, CarrierRuntime runtime, long now)
        {
            bool fresh = !runtime.Active || runtime.Superseded;
            runtime.Active = true;
            runtime.Superseded = false;
            runtime.FiredThisTick = true;
            if (spec.Lifetime.Kind == CarrierLifetimeKind.ForDuration)
                runtime.ExpiresAt = now + spec.Lifetime.DurationMs;
            if (fresh)
                runtime.FreshFireThisTick = true;
        }

        internal static bool SatisfiedNow(ConditionKind kind, double x, double v)
        {
            switch (kind)
            {
                case ConditionKind.LessThan: return x < v;
                case ConditionKind.LessOrEqual: return x <= v;
                case ConditionKind.GreaterThan: return x > v;
                case ConditionKind.GreaterOrEqual: return x >= v;
                case ConditionKind.Equals: return Math.Abs(x - v) <= Epsilon;
                case ConditionKind.NotEquals: return Math.Abs(x - v) > Epsilon;
                default: return false;
            }
        }

        internal static bool StillHolds(ConditionKind kind, double x, double v, double h)
        {
            switch (kind)
            {
                case ConditionKind.LessThan: return x < v + h;
                case ConditionKind.LessOrEqual: return x <= v + h;
                case ConditionKind.GreaterThan: return x > v - h;
                case ConditionKind.GreaterOrEqual: return x >= v - h;
                case ConditionKind.Equals: return Math.Abs(x - v) <= Epsilon + h;
                // NotEquals has no releasing direction past "equal" — hysteresis is inert.
                case ConditionKind.NotEquals: return Math.Abs(x - v) > Epsilon;
                default: return false;
            }
        }

        private static bool TryReadNumber(PropertySpec source, CarrierRuntime runtime,
            in CarrierTickInput input, Action warnMissing, out double value)
        {
            value = 0;
            // Script is parse-inert until the DSL lands — never reads a value.
            if (source != null && source.Kind == PropertyKind.Script)
                return false;
            if (input.Properties != null
                && source != null
                && input.Properties.TryGetNumber(source, out value))
                return true;
            WarnMissingOnce(runtime, warnMissing);
            return false;
        }

        private static bool TryReadBool(PropertySpec source, CarrierRuntime runtime,
            in CarrierTickInput input, Action warnMissing, out bool value)
        {
            value = false;
            if (source != null && source.Kind == PropertyKind.Script)
                return false;
            if (input.Properties != null
                && source != null
                && input.Properties.TryGetBool(source, out value))
                return true;
            WarnMissingOnce(runtime, warnMissing);
            return false;
        }

        private static void WarnMissingOnce(CarrierRuntime runtime, Action warnMissing)
        {
            if (runtime.WarnedMissing)
                return;
            runtime.WarnedMissing = true;
            warnMissing?.Invoke();
        }

        /// <summary>ForDuration remaining ms when active; null otherwise.</summary>
        public static int? RemainingMs(CarrierSpec spec, CarrierRuntime runtime, long now)
        {
            if (!runtime.Active || spec.Lifetime.Kind != CarrierLifetimeKind.ForDuration)
                return null;
            return (int)Math.Max(0, runtime.ExpiresAt - now);
        }
    }
}
