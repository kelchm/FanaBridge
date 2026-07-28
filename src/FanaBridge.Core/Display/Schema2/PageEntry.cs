using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// One entry in <see cref="DisplayConfigV2.Pages"/>: an ITM catalog page (user state
    /// about a firmware page) or a hosted page (segment display / on-Legacy). Flat and
    /// kind-discriminated like v1 rule targets.
    /// </summary>
    public class PageEntry
    {
        private string _kindRaw;
        private PageEntryKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="PageEntryKind.Unknown"/> when
        /// missing or unrecognized.</summary>
        [JsonIgnore]
        public PageEntryKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, PageEntryKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        // ── itmPage ──────────────────────────────────────────────────────────

        /// <summary><see cref="PageEntryKind.ItmPage"/>: catalog page identity.</summary>
        [JsonProperty("catalogPageId")]
        public string CatalogPageId { get; set; }

        /// <summary><see cref="PageEntryKind.ItmPage"/>: optional display-name override.</summary>
        [JsonProperty("nameOverride")]
        public string NameOverride { get; set; }

        /// <summary><see cref="PageEntryKind.ItmPage"/>: authoring-time removal flag.
        /// Default false (suppressed). ITM pages are removed, never deleted.</summary>
        [JsonProperty("removed")]
        [DefaultValue(false)]
        public bool Removed { get; set; }

        // ── hostedPage ───────────────────────────────────────────────────────

        /// <summary><see cref="PageEntryKind.HostedPage"/>: stable identity.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary><see cref="PageEntryKind.HostedPage"/>: display name.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// <see cref="PageEntryKind.HostedPage"/> base content. Null and absent are the
        /// same state (blank base — legal for alert-style pages whose content is layers).
        /// </summary>
        [JsonProperty("base")]
        public ContentWithEffect Base { get; set; }

        /// <summary><see cref="PageEntryKind.HostedPage"/>: ordered layers, top-first.</summary>
        [JsonProperty("layers")]
        public List<LayerEntry> Layers { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when this entry loses the identity race (duplicate id /
        /// catalogPageId). Runtime-only; never serialized.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }
    }

    /// <summary>Pages[] entry discriminator.</summary>
    public enum PageEntryKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        ItmPage,
        HostedPage,
    }

    /// <summary>
    /// Base or layer presentation: a content object plus an optional effect.
    /// Shape is uniform for hosted-page <c>base</c> and for layers (layers also carry
    /// condition/lifetime/runs on the layer itself).
    /// </summary>
    public class ContentWithEffect
    {
        private string _effectRaw;
        private ContentEffect? _effect;

        [JsonProperty("content")]
        public ContentObject Content { get; set; }

        /// <summary>Serialized form of <see cref="Effect"/>, preserved verbatim.</summary>
        [JsonProperty("effect")]
        public string EffectRaw
        {
            get => _effectRaw;
            set { _effectRaw = value; _effect = null; }
        }

        /// <summary>Presentation effect. Omitted/blank → <see cref="ContentEffect.None"/>;
        /// unrecognized → <see cref="ContentEffect.Unknown"/> (raw preserved).</summary>
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

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when base presentation is unusable (e.g. unrecognized effect).
        /// Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>Runtime-only effect coercion (serialized <see cref="EffectRaw"/>
        /// preserved) — e.g. flash → blink.</summary>
        internal void CoerceEffect(ContentEffect effect) => _effect = effect;
    }

    /// <summary>
    /// Decomposed content kinds carried from v1 <c>LegacyContentKind</c>: text / speed /
    /// gear / gearBrackets / rpm / position / fuel / message / property.
    /// </summary>
    public class ContentObject
    {
        private string _kindRaw;
        private ContentKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="ContentKind.Unknown"/> when
        /// missing or unrecognized.</summary>
        [JsonIgnore]
        public ContentKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, ContentKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary><see cref="ContentKind.Text"/> / <see cref="ContentKind.Message"/> static text.</summary>
        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary><see cref="ContentKind.Property"/>: value source.</summary>
        [JsonProperty("source")]
        public ValueSource Source { get; set; }

        /// <summary><see cref="ContentKind.Property"/>: format key (uninterpreted here).</summary>
        [JsonProperty("format")]
        public string Format { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>When true, render the v1 no-data convention (property source unusable)
        /// or treat over-length text via <see cref="EffectiveText"/>. Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>Runtime-clamped text for over-length hand-authored content. Null means
        /// use <see cref="Text"/> as-authored. Document <see cref="Text"/> is never rewritten.</summary>
        [JsonIgnore]
        public string EffectiveText { get; internal set; }
    }

    /// <summary>Content-kind roster (v1 contentKind spellings, camelCase).</summary>
    public enum ContentKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Text,
        Speed,
        Gear,
        GearBrackets,
        Rpm,
        Position,
        Fuel,
        Message,
        Property,
    }

    /// <summary>Presentation effect roster (v1 effect spellings). <see cref="Flash"/>
    /// parses for survival; runtime coercion is out of scope for this phase.</summary>
    public enum ContentEffect
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        None,
        Scroll,
        Blink,
        Flash,
    }

    /// <summary>
    /// A layer on a hosted page: content + effect + condition/lifetime/runs, plus the
    /// bring-up flag. Array order on the parent is ladder rank (top-first).
    /// </summary>
    public class LayerEntry
    {
        private string _effectRaw;
        private ContentEffect? _effect;
        private string _runsRaw;
        private RunsWhen? _runs;

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("content")]
        public ContentObject Content { get; set; }

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

        /// <summary>Serialized form of <see cref="Runs"/>, preserved verbatim.
        /// Default <c>inGame</c> is suppressed on write.</summary>
        [JsonProperty("runs")]
        [DefaultValue("inGame")]
        public string RunsRaw
        {
            get => _runsRaw;
            set { _runsRaw = value; _runs = null; }
        }

        /// <summary>Eligibility. Omitted → <see cref="RunsWhen.InGame"/>; unrecognized →
        /// <see cref="RunsWhen.Unknown"/> (raw preserved).</summary>
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

        /// <summary>Bring-up flag: when true, a derived summon targets this layer's host.
        /// Default false (suppressed).</summary>
        [JsonProperty("actsAsEntrypoint")]
        [DefaultValue(false)]
        public bool ActsAsEntrypoint { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when this layer is unusable on this build (duplicate id, bad
        /// condition/source, capability miss, …). Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>When true, <see cref="ActsAsEntrypoint"/> is inert (capability /
        /// removed host). Document flag preserved.</summary>
        [JsonIgnore]
        public bool ActsAsEntrypointIgnored { get; internal set; }

        /// <summary>Whether the layer may compete: user-enabled and honored by this build.</summary>
        [JsonIgnore]
        public bool EffectivelyEnabled => Enabled && !DegradedAtLoad;

        /// <summary>Runtime-only effect coercion (serialized raw preserved).</summary>
        internal void CoerceEffect(ContentEffect effect) => _effect = effect;
    }

    /// <summary>A named cycle of pages (2+ members; ITM, hosted, or mixed).</summary>
    public class CycleEntry
    {
        /// <summary>Default <see cref="PeriodMs"/> (suppressed when default).</summary>
        public const int DefaultPeriodMs = 3000;

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Member page refs (itmPage / hostedPage — not cycles).</summary>
        [JsonProperty("members")]
        public List<PageRef> Members { get; set; } = new List<PageRef>();

        /// <summary>Flip period in milliseconds. Default 3000 (suppressed).</summary>
        [JsonProperty("periodMs")]
        [DefaultValue(DefaultPeriodMs)]
        public int PeriodMs { get; set; } = DefaultPeriodMs;

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when the cycle has fewer than two resolvable members, or loses
        /// the identity race. Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }
    }
}
