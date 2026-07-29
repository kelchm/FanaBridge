using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Condition sentence shape used everywhere (summons, layers, overrides, wheel-screen
    /// rules): source + level operator + value + optional hysteresis. Edge-ness lives on
    /// <see cref="Lifetime"/> (<c>onChange</c>), not here.
    /// </summary>
    public class Condition
    {
        private string _operatorRaw;
        private ConditionOperator? _operator;

        [JsonProperty("source")]
        public ValueSource Source { get; set; }

        /// <summary>Serialized form of <see cref="Operator"/>, preserved verbatim.
        /// Absent on onChange summons (no level test).</summary>
        [JsonProperty("operator")]
        public string OperatorRaw
        {
            get => _operatorRaw;
            set { _operatorRaw = value; _operator = null; }
        }

        /// <summary>Level operator. Null when raw is absent; Unknown when unrecognized
        /// (raw preserved).</summary>
        [JsonIgnore]
        public ConditionOperator? Operator
        {
            get
            {
                if (_operator.HasValue)
                    return _operator;
                if (string.IsNullOrWhiteSpace(_operatorRaw))
                    return null;
                _operator = FanaBridge.Display.Rules.EnumText.Parse(_operatorRaw, ConditionOperator.Unknown);
                return _operator;
            }
            set
            {
                _operator = value;
                _operatorRaw = value == null ? null : FanaBridge.Display.Rules.EnumText.Write(value.Value);
            }
        }

        /// <summary>Comparison threshold for operators that take one.</summary>
        [JsonProperty("value")]
        public double? Value { get; set; }

        /// <summary>Level operators only: release-side margin. Optional.</summary>
        [JsonProperty("hysteresis")]
        public double? Hysteresis { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>When true, <see cref="Hysteresis"/> is present on a non-level
        /// condition and must be ignored at runtime. Document value preserved.</summary>
        [JsonIgnore]
        public bool HysteresisIgnored { get; internal set; }
    }

    /// <summary>
    /// A (kind, name) value source for conditions, field bases, and property content.
    /// Kind spellings: <c>simHubProperty</c> / <c>builtIn</c> / <c>itmField</c> /
    /// <c>script</c> (parsed-but-inert). FA2: <c>action</c> is no longer a recognized
    /// kind (unknown → degraded).
    /// </summary>
    public class ValueSource
    {
        private string _kindRaw;
        private ValueSourceKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="ValueSourceKind.Unknown"/> when
        /// missing or unrecognized.</summary>
        [JsonIgnore]
        public ValueSourceKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, ValueSourceKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary>Name within the kind's namespace (property path, built-in name,
        /// param id, or <c>self</c> for itmField on a field override).</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when the source is unusable on this build (unknown built-in,
        /// malformed param id, illegal <c>self</c>). Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }
    }

    /// <summary>Condition / content source kind spellings.</summary>
    public enum ValueSourceKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        SimHubProperty,
        BuiltIn,
        ItmField,
        /// <summary>Reserved until the script DSL is sequenced — parse-and-preserve.</summary>
        Script,
    }

    /// <summary>Level operators.
    /// Edge kinds relocated to <see cref="LifetimeKind.OnChange"/>.</summary>
    public enum ConditionOperator
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        Equals,
        NotEquals,
        IsTrue,
        IsFalse,
    }

    /// <summary>
    /// Activation lifetime after a condition fires. One encoding on every carrier
    /// (summons, overrides, layers, wheel-screen rules). <c>whileTrue</c> is the absent
    /// default; edge-ness is <c>onChange</c> (+ optional direction / then).
    /// </summary>
    public class Lifetime
    {
        /// <summary>Default <see cref="DurationMs"/> for forDuration and onChange-without-then.</summary>
        public const int DefaultDurationMs = 5000;

        private string _kindRaw;
        private LifetimeKind? _kind;
        private int? _durationMs;
        private string _directionRaw;
        private ChangeDirection? _direction;
        private string _thenRaw;
        private LifetimeThen? _then;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="LifetimeKind.Unknown"/> when
        /// missing or unrecognized. Absent lifetime object ≡ whileTrue at runtime.</summary>
        [JsonIgnore]
        public LifetimeKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, LifetimeKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary>
        /// <see cref="LifetimeKind.ForDuration"/> / onChange-without-then: visit length.
        /// Default 5000 when absent. Authored presence is tracked separately from the
        /// value so <c>durationMs:5000</c> round-trips and participates in then+durationMs
        /// mutual-exclusivity degrade rules (never rewritten on save).
        /// </summary>
        [JsonProperty("durationMs")]
        public int DurationMs
        {
            get => _durationMs ?? DefaultDurationMs;
            set => _durationMs = value;
        }

        /// <summary>True when <c>durationMs</c> was present in JSON or explicitly assigned.</summary>
        [JsonIgnore]
        public bool DurationMsPresent => _durationMs.HasValue;

        /// <summary>Serialize only when authored/assigned — absent stays absent (runtime default).</summary>
        public bool ShouldSerializeDurationMs() => _durationMs.HasValue;

        /// <summary>Serialized form of <see cref="Direction"/> for onChange.
        /// Default <c>any</c> is suppressed on write.</summary>
        [JsonProperty("direction")]
        [DefaultValue("any")]
        public string DirectionRaw
        {
            get => _directionRaw;
            set { _directionRaw = value; _direction = null; }
        }

        /// <summary>onChange direction. Omitted → <see cref="ChangeDirection.Any"/>;
        /// unrecognized → <see cref="ChangeDirection.Unknown"/>.</summary>
        [JsonIgnore]
        public ChangeDirection Direction
        {
            get
            {
                if (_direction.HasValue)
                    return _direction.Value;
                if (string.IsNullOrWhiteSpace(_directionRaw))
                    _direction = ChangeDirection.Any;
                else
                    _direction = FanaBridge.Display.Rules.EnumText.Parse(_directionRaw, ChangeDirection.Unknown);
                return _direction.Value;
            }
            set
            {
                _direction = value;
                _directionRaw = value == ChangeDirection.Any ? null : FanaBridge.Display.Rules.EnumText.Write(value);
            }
        }

        /// <summary>Serialized form of <see cref="Then"/>. Domain closed to
        /// <c>untilDismissed</c> in v2.0; mutually exclusive with durationMs.</summary>
        [JsonProperty("then")]
        public string ThenRaw
        {
            get => _thenRaw;
            set { _thenRaw = value; _then = null; }
        }

        /// <summary>Edge-then-stick: latch immediately on the edge (no timed phase).
        /// Null when absent. When <see cref="ThenIgnored"/>, the engine treats then as
        /// absent; the parsed value (incl. <see cref="LifetimeThen.Unknown"/>) is unchanged
        /// so unknown-spelling round-trip tests still observe the fallback.</summary>
        [JsonIgnore]
        public LifetimeThen? Then
        {
            get
            {
                if (_then.HasValue)
                    return _then;
                if (string.IsNullOrWhiteSpace(_thenRaw))
                    return null;
                _then = FanaBridge.Display.Rules.EnumText.Parse(_thenRaw, LifetimeThen.Unknown);
                return _then;
            }
            set
            {
                _then = value;
                _thenRaw = value == null ? null : FanaBridge.Display.Rules.EnumText.Write(value.Value);
            }
        }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>When true, ignore authored <see cref="DurationMs"/> (e.g. mutually
        /// exclusive with <c>then</c>). Document value preserved.</summary>
        [JsonIgnore]
        public bool DurationMsIgnored { get; internal set; }

        /// <summary>When true, ignore authored <c>then</c> (illegal domain or coerced
        /// away). Parsed <see cref="Then"/> stays as-read (Unknown for bad spellings);
        /// the engine consults this flag.</summary>
        [JsonIgnore]
        public bool ThenIgnored { get; internal set; }

        /// <summary>When true, authored direction was outside any/up/down — engine uses
        /// <see cref="ChangeDirection.Any"/>. Parsed <see cref="Direction"/> stays Unknown.</summary>
        [JsonIgnore]
        public bool DirectionCoercedToAny { get; internal set; }

        /// <summary>Load-time coercion that changes only what the engine sees — the
        /// serialized <see cref="KindRaw"/> stays untouched.</summary>
        internal void CoerceKind(LifetimeKind kind) => _kind = kind;
    }

    /// <summary>Lifetime kind spellings.</summary>
    public enum LifetimeKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        WhileTrue,
        ForDuration,
        UntilDismissed,
        OnChange,
    }

    /// <summary>onChange direction.</summary>
    public enum ChangeDirection
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Any,
        Up,
        Down,
    }

    /// <summary><c>then</c> value domain — closed to untilDismissed in v2.0.</summary>
    public enum LifetimeThen
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        UntilDismissed,
    }

    /// <summary>Eligibility: while telemetry flows, only while it doesn't, or always.
    /// Spelling <c>runs</c> on the wire.</summary>
    public enum RunsWhen
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        InGame,
        Idle,
        Always,
    }
}
