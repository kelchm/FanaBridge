using System.ComponentModel;
using Newtonsoft.Json;
using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>
    /// How a rule's condition decides it is met. The kinds fall into three families with
    /// different activation semantics (the rule engine keys off the family, see
    /// <see cref="ConditionKindExtensions"/>): <b>level</b> kinds are satisfied while a
    /// comparison holds, <b>edge</b> kinds fire on a change between consecutive samples,
    /// and the <b>event</b> kind fires when a named FanaBridge action triggers.
    /// </summary>
    public enum ConditionKind
    {
        /// <summary>Lenient-load fallback for a kind this build does not recognize — the
        /// rule is degraded at load (never dropped), and the serialized text survives a
        /// round-trip untouched for the version that knows it.</summary>
        Unknown = 0,

        // Level kinds: satisfied while the comparison holds.
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        Equals,
        NotEquals,
        IsTrue,
        IsFalse,

        // Edge kinds: fire on a change between consecutive samples.
        Changes,
        Increases,
        Decreases,

        /// <summary>Event kind: fires when the named FanaBridge action triggers
        /// (the mapped-control path — the "property" is the action name).</summary>
        ActionTriggered,
    }

    /// <summary>
    /// Condition-kind family predicates, shared by the validator and the rule engine so
    /// the family split is defined exactly once.
    /// </summary>
    public static class ConditionKindExtensions
    {
        /// <summary>Satisfied while a comparison holds (threshold and boolean tests).</summary>
        public static bool IsLevel(this ConditionKind kind)
        {
            switch (kind)
            {
                case ConditionKind.LessThan:
                case ConditionKind.LessOrEqual:
                case ConditionKind.GreaterThan:
                case ConditionKind.GreaterOrEqual:
                case ConditionKind.Equals:
                case ConditionKind.NotEquals:
                case ConditionKind.IsTrue:
                case ConditionKind.IsFalse:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Fires on a change between consecutive samples.</summary>
        public static bool IsEdge(this ConditionKind kind)
            => kind == ConditionKind.Changes
            || kind == ConditionKind.Increases
            || kind == ConditionKind.Decreases;

        /// <summary>Fires when a named FanaBridge action triggers.</summary>
        public static bool IsEvent(this ConditionKind kind)
            => kind == ConditionKind.ActionTriggered;

        /// <summary>Level kinds that compare against <see cref="RuleCondition.Value"/>
        /// (IsTrue/IsFalse read a boolean and need none).</summary>
        public static bool RequiresValue(this ConditionKind kind)
        {
            switch (kind)
            {
                case ConditionKind.LessThan:
                case ConditionKind.LessOrEqual:
                case ConditionKind.GreaterThan:
                case ConditionKind.GreaterOrEqual:
                case ConditionKind.Equals:
                case ConditionKind.NotEquals:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// A rule's condition. One flat, kind-discriminated class rather than a hierarchy so
    /// the document serializes plainly and the UI edits a single shape; which fields
    /// apply follows from <see cref="Kind"/>'s family.
    /// </summary>
    public class RuleCondition
    {
        private string _kindRaw;
        private ConditionKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim: a kind
        /// this build does not recognize must survive a load/save round-trip unchanged
        /// for the version that knows it (see <see cref="EnumText"/>).</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="ConditionKind.Unknown"/>
        /// when missing or unrecognized (the validator degrades the rule).</summary>
        [JsonIgnore]
        public ConditionKind Kind
        {
            get => _kind ?? (_kind = EnumText.Parse(_kindRaw, ConditionKind.Unknown)).Value;
            set { _kind = value; _kindRaw = EnumText.Write(value); }
        }

        /// <summary>What the condition reads (or, for ActionTriggered, the action name).</summary>
        [JsonProperty("source")]
        public PropertySpec Source { get; set; }

        /// <summary>Comparison threshold for level kinds that take one; ignored otherwise.</summary>
        [JsonProperty("value")]
        public double? Value { get; set; }

        /// <summary>Level kinds only: once active, the condition deactivates only past
        /// <see cref="Value"/> ± this margin in the releasing direction, so a value
        /// hovering at the threshold cannot flap the display. Default 0 (none).</summary>
        [JsonProperty("hysteresis")]
        public double? Hysteresis { get; set; }
    }

    /// <summary>What a winning rule shows. Flat and kind-discriminated, like
    /// <see cref="RuleCondition"/>.</summary>
    public enum TargetKind
    {
        /// <summary>Lenient-load fallback — the rule is degraded at load; the serialized
        /// text is preserved (see <see cref="ConditionKind.Unknown"/>).</summary>
        Unknown = 0,
        /// <summary>A single ITM page (<see cref="RuleTarget.Page"/>).</summary>
        Page,
        /// <summary>A named legacy screen (<see cref="RuleTarget.ScreenId"/>). ITM rules may
        /// target this too: it resolves to the device's legacy ITM page plus that screen
        /// on the 7-segment surface.</summary>
        LegacyScreen,
        /// <summary>Two ITM pages alternating every <see cref="RuleTarget.PeriodMs"/>.</summary>
        Alternate,
    }

    /// <summary>
    /// A rule's target. Targets carry <see cref="ItmPage"/> content identities, never wire
    /// page numbers — wire numbering is per display device (a Bentley renumbers) and is
    /// resolved at the edge. Page accessors are nullable so an unrecognized page name from
    /// a future version degrades to "rule disabled" at load instead of silently
    /// retargeting; the raw name itself is preserved for the round-trip.
    /// </summary>
    public class RuleTarget
    {
        /// <summary>Factory default for <see cref="PeriodMs"/>.</summary>
        public const int DefaultAlternatePeriodMs = 3000;

        /// <summary>Floor for <see cref="PeriodMs"/> — alternation faster than this would
        /// flap the firmware's page switching.</summary>
        public const int MinAlternatePeriodMs = 1000;

        private string _kindRaw;
        private TargetKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim (see
        /// <see cref="RuleCondition.KindRaw"/>).</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="TargetKind.Unknown"/> when
        /// missing or unrecognized (the validator degrades the rule).</summary>
        [JsonIgnore]
        public TargetKind Kind
        {
            get => _kind ?? (_kind = EnumText.Parse(_kindRaw, TargetKind.Unknown)).Value;
            set { _kind = value; _kindRaw = EnumText.Write(value); }
        }

        /// <summary>Serialized page name for <see cref="Page"/>, preserved verbatim.</summary>
        [JsonProperty("page")]
        public string PageRaw { get; set; }

        /// <summary><see cref="TargetKind.Page"/>: the page to show — null when
        /// <see cref="PageRaw"/> is absent or names a page this build does not know.</summary>
        [JsonIgnore]
        public ItmPage? Page
        {
            get => EnumText.ParseNullable<ItmPage>(PageRaw);
            set => PageRaw = value == null ? null : EnumText.Write(value.Value);
        }

        /// <summary><see cref="TargetKind.LegacyScreen"/>: the <see cref="LegacyScreen.Id"/> to show.</summary>
        [JsonProperty("screenId")]
        public string ScreenId { get; set; }

        /// <summary>Serialized page name for <see cref="PageA"/>, preserved verbatim.</summary>
        [JsonProperty("pageA")]
        public string PageARaw { get; set; }

        /// <summary><see cref="TargetKind.Alternate"/>: the first of the two pages.</summary>
        [JsonIgnore]
        public ItmPage? PageA
        {
            get => EnumText.ParseNullable<ItmPage>(PageARaw);
            set => PageARaw = value == null ? null : EnumText.Write(value.Value);
        }

        /// <summary>Serialized page name for <see cref="PageB"/>, preserved verbatim.</summary>
        [JsonProperty("pageB")]
        public string PageBRaw { get; set; }

        /// <summary><see cref="TargetKind.Alternate"/>: the second of the two pages.</summary>
        [JsonIgnore]
        public ItmPage? PageB
        {
            get => EnumText.ParseNullable<ItmPage>(PageBRaw);
            set => PageBRaw = value == null ? null : EnumText.Write(value.Value);
        }

        /// <summary><see cref="TargetKind.Alternate"/>: flip period in milliseconds
        /// (clamped to ≥ <see cref="MinAlternatePeriodMs"/> at load).</summary>
        [JsonProperty("periodMs")]
        [DefaultValue(DefaultAlternatePeriodMs)]
        public int PeriodMs { get; set; } = DefaultAlternatePeriodMs;
    }

    /// <summary>How long an activation lives once its condition fires.</summary>
    public enum HoldKind
    {
        /// <summary>Lenient-load fallback — coerced to the condition family's default at
        /// load, at runtime only: the serialized text is preserved.</summary>
        Unknown = 0,
        /// <summary>Active exactly while the condition is satisfied. Level conditions only —
        /// an edge or event has no "still active" to track (coerced to ForDuration at load).</summary>
        WhileActive,
        /// <summary>Active for <see cref="HoldSpec.DurationMs"/> from each (re)fire.</summary>
        ForDuration,
        /// <summary>Active until dismissed (manual navigation, a preempting rule finishing,
        /// eligibility loss, or — for level conditions — the condition going false).</summary>
        Indefinite,
    }

    /// <summary>A rule's hold: activation lifetime after the condition fires.</summary>
    public class HoldSpec
    {
        /// <summary>Default <see cref="DurationMs"/>, also the coercion target when an
        /// edge/event condition arrives with an impossible WhileActive hold.</summary>
        public const int DefaultDurationMs = 5000;

        private string _kindRaw;
        private HoldKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim (see
        /// <see cref="RuleCondition.KindRaw"/>).</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="HoldKind.Unknown"/> when
        /// missing or unrecognized (the validator coerces to the family default).</summary>
        [JsonIgnore]
        public HoldKind Kind
        {
            get => _kind ?? (_kind = EnumText.Parse(_kindRaw, HoldKind.Unknown)).Value;
            set { _kind = value; _kindRaw = EnumText.Write(value); }
        }

        /// <summary>Load-time coercion that changes only what the engine sees — the
        /// serialized <see cref="KindRaw"/> stays untouched, so a future version's hold
        /// kind survives the round-trip.</summary>
        internal void CoerceKind(HoldKind kind) => _kind = kind;

        /// <summary><see cref="HoldKind.ForDuration"/> only: how long the activation lives.</summary>
        [JsonProperty("durationMs")]
        [DefaultValue(DefaultDurationMs)]
        public int DurationMs { get; set; } = DefaultDurationMs;
    }

    /// <summary>When a rule is allowed to compete: while telemetry flows (in-game), only
    /// while it doesn't (idle), or always.</summary>
    public enum RuleEligibility
    {
        /// <summary>Lenient-load fallback — coerced to <see cref="InGame"/> at load, at
        /// runtime only: the serialized text is preserved.</summary>
        Unknown = 0,
        InGame,
        Idle,
        Any,
    }

    /// <summary>
    /// One display rule: WHEN a condition holds/fires, SHOW a target, for HOLD long,
    /// ELIGIBLE in which session states. Priority is not stored on the rule — it is the
    /// rule's position in its list (index 0 wins). Post-hold behavior is deliberately not
    /// modeled: v1 has exactly one ("resume automatic"), so a field would be dead weight
    /// until a second behavior exists.
    /// </summary>
    public class DisplayRule
    {
        private string _eligibleRaw;
        private RuleEligibility? _eligible;

        /// <summary>Stable identity (GUID-ish string, generated by the UI; the loader
        /// assigns one if missing). Engine state and activity events key off it.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Optional user label; a UI can synthesize display text from the
        /// condition when absent.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Disabled rules keep their place in the priority list but never
        /// activate. This is the user's own switch — load-time degradation is tracked
        /// separately (<see cref="DegradedAtLoad"/>) and never overwrites it.</summary>
        [JsonProperty("enabled")]
        [DefaultValue(true)]
        public bool Enabled { get; set; } = true;

        /// <summary>Set by the load-time validator when this build cannot honor the rule
        /// (unrecognized kind, unusable source, missing pieces). Runtime-only, never
        /// serialized: the document keeps the rule exactly as written — including values
        /// only a future version understands — so it survives the round-trip intact.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>Whether the rule may compete: enabled by the user AND honored by
        /// this build.</summary>
        [JsonIgnore]
        public bool EffectivelyEnabled => Enabled && !DegradedAtLoad;

        [JsonProperty("when")]
        public RuleCondition When { get; set; }

        [JsonProperty("show")]
        public RuleTarget Show { get; set; }

        [JsonProperty("hold")]
        public HoldSpec Hold { get; set; }

        /// <summary>Serialized form of <see cref="Eligible"/>, preserved verbatim (see
        /// <see cref="RuleCondition.KindRaw"/>).</summary>
        [JsonProperty("eligible")]
        public string EligibleRaw
        {
            get => _eligibleRaw;
            set { _eligibleRaw = value; _eligible = null; }
        }

        /// <summary>Parsed <see cref="EligibleRaw"/> — <see cref="RuleEligibility.InGame"/>
        /// when omitted, <see cref="RuleEligibility.Unknown"/> when unrecognized (the
        /// validator coerces to InGame).</summary>
        [JsonIgnore]
        public RuleEligibility Eligible
        {
            get
            {
                if (_eligible == null)
                    _eligible = _eligibleRaw == null
                        ? RuleEligibility.InGame
                        : EnumText.Parse(_eligibleRaw, RuleEligibility.Unknown);
                return _eligible.Value;
            }
            set { _eligible = value; _eligibleRaw = EnumText.Write(value); }
        }

        /// <summary>Load-time coercion that changes only what the engine sees; see
        /// <see cref="HoldSpec.CoerceKind"/>.</summary>
        internal void CoerceEligible(RuleEligibility eligible) => _eligible = eligible;
    }
}
