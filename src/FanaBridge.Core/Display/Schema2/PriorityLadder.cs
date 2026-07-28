using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Priority ladder: ranked rows (array order = rank, top-first) plus the fixed rest
    /// floor. The derived flagged-children summon is not stored — computed at runtime.
    /// </summary>
    public class PriorityLadder
    {
        /// <summary>
        /// Storage-only ranked rows as authored (seat / satellite / manual). Array order
        /// is rank. Never rewritten by load-time validation — restored manuals and
        /// materialized home seats live only on the runtime projection
        /// (<see cref="EffectiveRows"/>).
        /// </summary>
        [JsonProperty("rows")]
        public List<PriorityRow> Rows { get; set; } = new List<PriorityRow>();

        /// <summary>Fixed floor: in-session page, landing page, idle. Not a row.</summary>
        [JsonProperty("rest")]
        public RestBlock Rest { get; set; } = new RestBlock();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>
        /// Runtime projection built by <see cref="DisplayConfigV2Validator.Normalize"/>:
        /// document rows (with degrade marks) plus any restored manual row and
        /// materialized home seats. Never serialized. Prefer
        /// <see cref="EffectiveRows"/> for consumers.
        /// </summary>
        [JsonIgnore]
        public List<PriorityRow> RuntimeRows { get; internal set; }

        /// <summary>
        /// Authoritative ranked-row view for runtime consumers. After Normalize this is
        /// the full projection (document + restored manual + materializations). Before
        /// Normalize (or when the projection is unset), falls back deterministically to
        /// the stored <see cref="Rows"/> list (empty when null).
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<PriorityRow> EffectiveRows
        {
            get
            {
                if (RuntimeRows != null)
                    return RuntimeRows;
                if (Rows != null)
                    return Rows;
                return Array.Empty<PriorityRow>();
            }
        }
    }

    /// <summary>
    /// One priority row. Three kinds: seat (home + summons + bringUpLifetime),
    /// satellite (summons-XOR-childRef), manual (remembered-page entrypoint).
    /// </summary>
    public class PriorityRow
    {
        private string _kindRaw;
        private PriorityRowKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="PriorityRowKind.Unknown"/> when
        /// missing or unrecognized.</summary>
        [JsonIgnore]
        public PriorityRowKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, PriorityRowKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary>Stable identity (seat / satellite). Manual has no id.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Seat and summon-satellite: destination page/cycle ref.
        /// ChildRef satellites store no target (derived from the child per device).</summary>
        [JsonProperty("target")]
        public PageRef Target { get; set; }

        /// <summary>Seat / summon-satellite: stored summons (0+). Exclusive with
        /// <see cref="ChildRef"/> on a satellite (childRef wins when both present).</summary>
        [JsonProperty("summons")]
        public List<Summon> Summons { get; set; }

        /// <summary>Seat only: lifetime of the derived flagged-children summon.
        /// Absent ≡ whileTrue (pin).</summary>
        [JsonProperty("bringUpLifetime")]
        public Lifetime BringUpLifetime { get; set; }

        /// <summary>ChildRef-satellite: reference to a flagged child (field override or
        /// layer). No stored target — destination is derived per device.</summary>
        [JsonProperty("childRef")]
        public ChildRef ChildRef { get; set; }

        /// <summary>ChildRef-satellite optional: its own bring-up lifetime.</summary>
        [JsonProperty("lifetime")]
        public Lifetime Lifetime { get; set; }

        /// <summary>Manual row: return-to-rest timer. Absent/null = off (default).</summary>
        [JsonProperty("returnToRestAfterMs")]
        public int? ReturnToRestAfterMs { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when the row is inert on this build (duplicate id/home, unresolved
        /// target/childRef, satellite neither shape, …). Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>True when this row was synthesized into
        /// <see cref="PriorityLadder.EffectiveRows"/> (restored manual or materialized
        /// home seat) and is not present in the authored document.</summary>
        [JsonIgnore]
        public bool MaterializedAtLoad { get; internal set; }

        /// <summary>Satellite with both summons and childRef: childRef wins; summons are
        /// ignored at runtime. Document preserves both.</summary>
        [JsonIgnore]
        public bool SummonsIgnored { get; internal set; }

        /// <summary>ChildRef satellite with a stored target: target is derived-only and
        /// ignored at runtime. Document value preserved.</summary>
        [JsonIgnore]
        public bool TargetIgnored { get; internal set; }

        /// <summary>ChildRef carrying both field and layer shapes: ambiguous, degraded.
        /// Document preserves both; runtime does not silently prefer either shape.</summary>
        [JsonIgnore]
        public bool ChildRefAmbiguous { get; internal set; }
    }

    /// <summary>Priority row discriminator.</summary>
    public enum PriorityRowKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Seat,
        Satellite,
        Manual,
    }

    /// <summary>
    /// Reference form of a child's derived summon: either a field override
    /// (<c>field</c> + <c>overrideId</c>) or a layer (<c>pageId</c> + <c>layerId</c>).
    /// </summary>
    public class ChildRef
    {
        /// <summary>Param id string for a field-override child.</summary>
        [JsonProperty("field")]
        public string Field { get; set; }

        /// <summary>Override id on that field.</summary>
        [JsonProperty("overrideId")]
        public string OverrideId { get; set; }

        /// <summary>Hosted page id for a layer child.</summary>
        [JsonProperty("pageId")]
        public string PageId { get; set; }

        /// <summary>Layer id on that page.</summary>
        [JsonProperty("layerId")]
        public string LayerId { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>
    /// A stored summon: condition + lifetime + runs. The derived flagged-children
    /// summon is not stored — only authored summons live here.
    /// </summary>
    public class Summon
    {
        private string _runsRaw;
        private RunsWhen? _runs;

        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Optional user label; null = generated from alias table + operator + value.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

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

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when this summon is unusable (duplicate id, bad condition, …).
        /// Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>Whether the summon may fire: user-enabled and honored by this build.</summary>
        [JsonIgnore]
        public bool EffectivelyEnabled => Enabled && !DegradedAtLoad;
    }

    /// <summary>Fixed rest floor: in-session page, landing page, idle.</summary>
    public class RestBlock
    {
        /// <summary>Page shown when nothing is active in-session (itmPage | hostedPage only).</summary>
        [JsonProperty("inSessionPage")]
        public PageRef InSessionPage { get; set; }

        /// <summary>ITM wheels: hosted page a bare native-button Legacy arrival shows
        /// before any remembered page exists. Absent on segment-only wheels.</summary>
        [JsonProperty("landingPage")]
        public PageRef LandingPage { get; set; }

        /// <summary>Idle presentation. Absent ≡ blank.</summary>
        [JsonProperty("idle")]
        public IdleSpec Idle { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>When <see cref="InSessionPage"/> is degraded, runtime falls back to
        /// the compiled default walk's first member (engine resolves further).</summary>
        [JsonIgnore]
        public bool InSessionPageUseDefaultWalk { get; internal set; }

        /// <summary>When <see cref="LandingPage"/> is degraded, runtime falls back to
        /// in-session when hosted, else the first hosted page.</summary>
        [JsonIgnore]
        public bool LandingPageUseFallback { get; internal set; }
    }

    /// <summary>
    /// Idle presentation: a special screen, blank, or a page. Compiled per device
    /// capability at runtime (not schema-forked).
    /// </summary>
    public class IdleSpec
    {
        private string _kindRaw;
        private IdleKind? _kind;
        private string _screenRaw;
        private WheelScreenCommand? _screen;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="IdleKind.Unknown"/> when
        /// missing or unrecognized.</summary>
        [JsonIgnore]
        public IdleKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, IdleKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary>Serialized form of <see cref="Screen"/> for kind <c>screen</c>.</summary>
        [JsonProperty("screen")]
        public string ScreenRaw
        {
            get => _screenRaw;
            set { _screenRaw = value; _screen = null; }
        }

        /// <summary>Special-command screen. Unrecognized → Unknown (raw preserved).</summary>
        [JsonIgnore]
        public WheelScreenCommand Screen
        {
            get => _screen ?? (_screen = FanaBridge.Display.Rules.EnumText.Parse(_screenRaw, WheelScreenCommand.Unknown)).Value;
            set { _screen = value; _screenRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary>Kind <c>page</c>: the page to show while idle.</summary>
        [JsonProperty("page")]
        public PageRef Page { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when idle is unusable (unsupported screen, unresolved page).
        /// Runtime-only. Fallback for page kind is <see cref="IdleKind.Blank"/>.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>When true, blank idle on a command-less ITM wheel uses the
        /// park-on-Legacy compile policy (§5 / §14).</summary>
        [JsonIgnore]
        public bool ParkOnLegacyForBlank { get; internal set; }

        /// <summary>When true, authored screen is unsupported and idle is inert.</summary>
        [JsonIgnore]
        public bool ScreenIgnored { get; internal set; }
    }

    /// <summary>Idle kind discriminator.</summary>
    public enum IdleKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Screen,
        Blank,
        Page,
    }

    /// <summary>SpecialCommands vocabulary for wheel-screen rules and idle screen kind.
    /// Spelling <c>logoInverted</c> (not "inverted").</summary>
    public enum WheelScreenCommand
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        Logo,
        Blank,
        White,
        LogoInverted,
    }
}
