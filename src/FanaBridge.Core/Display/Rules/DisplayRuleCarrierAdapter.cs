using System;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// v9 scaffolding — deleted at E8b. Adapts DisplayRule condition/hold/eligibility
    /// onto the v2-owned carrier machine so the v9 engine path can share CarrierEvaluator.
    /// </summary>
    public static class DisplayRuleCarrierAdapter
    {
        /// <summary>Adapt a v1 DisplayRule (condition + hold subset) onto the carrier machine.
        /// Edge whileActive is expected already coerced to ForDuration by the validator;
        /// this maps Kind as the engine sees it and coerces non-level WhileTrue → ForDuration
        /// (v1 law restored).</summary>
        public static CarrierSpec ToCarrierSpec(DisplayRule rule)
        {
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            var trigger = new CarrierTrigger();
            var life = new CarrierLifetime();
            ApplyDisplayRule(rule, trigger, life, out var elig);
            return new CarrierSpec(rule.Id, trigger, life, elig);
        }

        /// <summary>
        /// Re-read mutable DisplayRule condition/hold/eligibility into this spec.
        /// Preserves live-rule semantics: the engine adapts before each evaluate so
        /// post-construction mutations of When/Hold/Eligible are observed.
        /// </summary>
        public static void Refresh(CarrierSpec spec, DisplayRule rule)
        {
            if (spec == null)
                throw new ArgumentNullException(nameof(spec));
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));
            ApplyDisplayRule(rule, spec.Trigger, spec.Lifetime, out var elig);
            spec.Eligibility = elig;
        }

        private static void ApplyDisplayRule(DisplayRule rule, CarrierTrigger trigger,
            CarrierLifetime life, out RuleEligibility eligibility)
        {
            var when = rule.When;
            var hold = rule.Hold;
            trigger.Source = when?.Source;
            trigger.Value = when?.Value;
            trigger.Hysteresis = when?.Hysteresis;
            trigger.LevelKind = ConditionKind.Unknown;
            trigger.Direction = CarrierEdgeDirection.Any;

            if (when != null && when.Kind.IsLevel())
            {
                trigger.Family = CarrierTriggerFamily.Level;
                trigger.LevelKind = when.Kind;
            }
            else if (when != null && when.Kind.IsEdge())
            {
                trigger.Family = CarrierTriggerFamily.Edge;
                if (when.Kind == ConditionKind.Increases)
                    trigger.Direction = CarrierEdgeDirection.Up;
                else if (when.Kind == ConditionKind.Decreases)
                    trigger.Direction = CarrierEdgeDirection.Down;
                else
                    trigger.Direction = CarrierEdgeDirection.Any;
            }
            else if (when != null && when.Kind.IsEvent())
            {
                trigger.Family = CarrierTriggerFamily.Event;
            }
            else
            {
                // Unknown condition kind: never fires (level SatisfiedNow default false).
                trigger.Family = CarrierTriggerFamily.Level;
                trigger.LevelKind = ConditionKind.Unknown;
            }

            if (hold != null)
            {
                switch (hold.Kind)
                {
                    case HoldKind.WhileActive:
                        life.Kind = CarrierLifetimeKind.WhileTrue;
                        break;
                    case HoldKind.ForDuration:
                        life.Kind = CarrierLifetimeKind.ForDuration;
                        life.DurationMs = hold.DurationMs;
                        break;
                    case HoldKind.UntilDismissed:
                        life.Kind = CarrierLifetimeKind.UntilDismissed;
                        break;
                    default:
                        life.Kind = CarrierLifetimeKind.ForDuration;
                        life.DurationMs = hold.DurationMs > 0
                            ? hold.DurationMs
                            : HoldSpec.DefaultDurationMs;
                        break;
                }
            }
            else
            {
                life.Kind = CarrierLifetimeKind.WhileTrue;
            }

            // v1 law: non-level + WhileActive → ForDuration (edge and event).
            CarrierSpec.CoerceNonLevelWhileTrue(trigger, life);

            eligibility = rule.Eligible;
        }
    }
}
