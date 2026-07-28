using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Per-param field content: a resting base plus an ordered override ladder.
    /// Bound by param id (not page) so a child follows its param across host pages.
    /// </summary>
    public class FieldEntry
    {
        /// <summary>Resting source/format/suffix for the param.</summary>
        [JsonProperty("base")]
        public FieldBase Base { get; set; }

        /// <summary>Override ladder, array order = rank, top-first. One ladder per field.</summary>
        [JsonProperty("overrides")]
        public List<FieldOverride> Overrides { get; set; } = new List<FieldOverride>();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Field resting state: source, format, optional base suffix.</summary>
    public class FieldBase
    {
        [JsonProperty("source")]
        public ValueSource Source { get; set; }

        /// <summary>Format key (e.g. bare / withTotal / unit), or null for default.</summary>
        [JsonProperty("format")]
        public string Format { get; set; }

        /// <summary>Field's resting suffix text, nullable.</summary>
        [JsonProperty("baseSuffix")]
        public string BaseSuffix { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>
    /// One override on a field's ladder: which regions it writes, content, alignment,
    /// effect, condition/lifetime/runs, and the bring-up flag.
    /// </summary>
    public class FieldOverride
    {
        private string _writesRaw;
        private FieldWrites? _writes;
        private string _alignmentRaw;
        private FieldAlignment? _alignment;
        private string _effectRaw;
        private ContentEffect? _effect;
        private string _runsRaw;
        private RunsWhen? _runs;

        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Serialized form of <see cref="Writes"/>, preserved verbatim.</summary>
        [JsonProperty("writes")]
        public string WritesRaw
        {
            get => _writesRaw;
            set { _writesRaw = value; _writes = null; }
        }

        /// <summary>Which regions this override paints. Unrecognized → Unknown.</summary>
        [JsonIgnore]
        public FieldWrites Writes
        {
            get => _writes ?? (_writes = FanaBridge.Display.Rules.EnumText.Parse(_writesRaw, FieldWrites.Unknown)).Value;
            set { _writes = value; _writesRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        [JsonProperty("content")]
        public ContentObject Content { get; set; }

        /// <summary>Serialized form of <see cref="Alignment"/>. Default <c>left</c> suppressed.</summary>
        [JsonProperty("alignment")]
        [DefaultValue("left")]
        public string AlignmentRaw
        {
            get => _alignmentRaw;
            set { _alignmentRaw = value; _alignment = null; }
        }

        /// <summary>Multi-char suffix alignment. Omitted → <see cref="FieldAlignment.Left"/>.</summary>
        [JsonIgnore]
        public FieldAlignment Alignment
        {
            get
            {
                if (_alignment.HasValue)
                    return _alignment.Value;
                if (string.IsNullOrWhiteSpace(_alignmentRaw))
                    _alignment = FieldAlignment.Left;
                else
                    _alignment = FanaBridge.Display.Rules.EnumText.Parse(_alignmentRaw, FieldAlignment.Unknown);
                return _alignment.Value;
            }
            set
            {
                _alignment = value;
                _alignmentRaw = value == FieldAlignment.Left ? null : FanaBridge.Display.Rules.EnumText.Write(value);
            }
        }

        /// <summary>Serialized form of <see cref="Effect"/>, preserved verbatim.</summary>
        [JsonProperty("effect")]
        public string EffectRaw
        {
            get => _effectRaw;
            set { _effectRaw = value; _effect = null; }
        }

        /// <summary>Presentation effect. Omitted/blank → <see cref="ContentEffect.None"/>.</summary>
        [JsonIgnore]
        public ContentEffect Effect
        {
            get
            {
                if (_effect.HasValue)
                    return _effect.Value;
                if (string.IsNullOrWhiteSpace(_effectRaw))
                    _effect = ContentEffect.None;
                else
                    _effect = FanaBridge.Display.Rules.EnumText.Parse(_effectRaw, ContentEffect.Unknown);
                return _effect.Value;
            }
            set { _effect = value; _effectRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        [JsonProperty("condition")]
        public Condition Condition { get; set; }

        [JsonProperty("lifetime")]
        public Lifetime Lifetime { get; set; }

        /// <summary>Serialized form of <see cref="Runs"/>. Default <c>inGame</c> suppressed.</summary>
        [JsonProperty("runs")]
        [DefaultValue("inGame")]
        public string RunsRaw
        {
            get => _runsRaw;
            set { _runsRaw = value; _runs = null; }
        }

        /// <summary>Eligibility. Omitted → <see cref="RunsWhen.InGame"/>.</summary>
        [JsonIgnore]
        public RunsWhen Runs
        {
            get
            {
                if (_runs.HasValue)
                    return _runs.Value;
                if (string.IsNullOrWhiteSpace(_runsRaw))
                    _runs = RunsWhen.InGame;
                else
                    _runs = FanaBridge.Display.Rules.EnumText.Parse(_runsRaw, RunsWhen.Unknown);
                return _runs.Value;
            }
            set
            {
                _runs = value;
                _runsRaw = value == RunsWhen.InGame ? null : FanaBridge.Display.Rules.EnumText.Write(value);
            }
        }

        /// <summary>User enable switch. Default true (suppressed).</summary>
        [JsonProperty("enabled")]
        [DefaultValue(true)]
        public bool Enabled { get; set; } = true;

        /// <summary>Bring-up flag. Default false (suppressed). Aggregate lifetime lives
        /// on the home seat's bringUpLifetime, not here.</summary>
        [JsonProperty("actsAsEntrypoint")]
        [DefaultValue(false)]
        public bool ActsAsEntrypoint { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Which field regions an override paints.</summary>
    public enum FieldWrites
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Suffix,
        Value,
        Both,
    }

    /// <summary>Multi-char suffix alignment.</summary>
    public enum FieldAlignment
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Left,
        Right,
    }
}
