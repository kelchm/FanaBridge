using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Customization
{
    /// <summary>
    /// What the display should be showing. An intent carries content identities only
    /// (<see cref="ItmPage"/> / screen id) — wire page numbers are per display device and
    /// are resolved by the page director at the edge. An <see cref="TargetKind.Alternate"/>
    /// rule never appears here as such: the engine resolves the alternation and emits the
    /// current flip page, so the consumer only ever sees a concrete page or screen.
    /// </summary>
    public struct RuleIntent : IEquatable<RuleIntent>
    {
        public RuleIntent(TargetKind kind, ItmPage? page, string screenId, string sourceRuleId)
        {
            Kind = kind;
            Page = page;
            ScreenId = screenId;
            SourceRuleId = sourceRuleId;
        }

        /// <summary><see cref="TargetKind.Page"/> or <see cref="TargetKind.LegacyScreen"/>.</summary>
        public TargetKind Kind { get; }

        /// <summary>The page to show, for <see cref="TargetKind.Page"/>.</summary>
        public ItmPage? Page { get; }

        /// <summary>The screen to show, for <see cref="TargetKind.LegacyScreen"/>. Null means
        /// a blank display (a legacy set with no base screen).</summary>
        public string ScreenId { get; }

        /// <summary>The rule whose target this is, or null for the resting/base target.</summary>
        public string SourceRuleId { get; }

        public bool Equals(RuleIntent other)
            => Kind == other.Kind && Page == other.Page
            && string.Equals(ScreenId, other.ScreenId, StringComparison.Ordinal)
            && string.Equals(SourceRuleId, other.SourceRuleId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is RuleIntent other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Kind;
                h = h * 397 ^ (Page?.GetHashCode() ?? 0);
                h = h * 397 ^ (ScreenId?.GetHashCode() ?? 0);
                h = h * 397 ^ (SourceRuleId?.GetHashCode() ?? 0);
                return h;
            }
        }
    }

    /// <summary>A rule's live state, as shown in the UI's priority list.</summary>
    public enum RuleStatus
    {
        /// <summary>Turned off in the config (or degraded at load); never competes.</summary>
        Disabled,
        /// <summary>Targets a page this display does not have; never competes.</summary>
        Unavailable,
        /// <summary>Not eligible in the current session state (in-game vs idle).</summary>
        Ineligible,
        /// <summary>Eligible, condition not currently activating it.</summary>
        Armed,
        /// <summary>Activation live, but a higher-priority rule holds the screen.</summary>
        Waiting,
        /// <summary>The winning rule — its target is the emitted intent.</summary>
        OnScreen,
    }

    /// <summary>One rule's status for this tick, in rule-list order.</summary>
    public struct RuleLiveState
    {
        public RuleLiveState(string ruleId, RuleStatus status, int? remainingMs)
        {
            RuleId = ruleId;
            Status = status;
            RemainingMs = remainingMs;
        }

        public string RuleId { get; }

        public RuleStatus Status { get; }

        /// <summary>Milliseconds left in the hold window — only while
        /// <see cref="RuleStatus.OnScreen"/> with a ForDuration hold (the UI's countdown ring).</summary>
        public int? RemainingMs { get; }
    }

    /// <summary>One tick's output.</summary>
    public sealed class RuleEngineResult
    {
        internal RuleEngineResult(RuleIntent intent, IReadOnlyList<RuleLiveState> ruleStates,
            long activityVersion)
        {
            Intent = intent;
            RuleStates = ruleStates;
            ActivityVersion = activityVersion;
        }

        /// <summary>What the display should show (the winner's target, or the resting/base
        /// target when no rule is winning), already dwell-filtered.</summary>
        public RuleIntent Intent { get; }

        /// <summary>Per-rule statuses, in rule-list (priority) order.</summary>
        public IReadOnlyList<RuleLiveState> RuleStates { get; }

        /// <summary>Increments once per activity event — a cheap "anything new?" check for
        /// the polling UI before it snapshots <see cref="DisplayRuleEngine.GetActivityEvents"/>.</summary>
        public long ActivityVersion { get; }
    }
}
