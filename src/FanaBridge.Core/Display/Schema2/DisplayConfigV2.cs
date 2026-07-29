using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// The per-wheel display customization document (schema v2): one pages list, one
    /// priority ladder, field content by param, wheel-screen plane, and settings. Pure
    /// data — no validation or runtime wiring in this phase. Serialization goes through
    /// <see cref="DisplayConfigV2Serializer"/>.
    /// </summary>
    public class DisplayConfigV2
    {
        /// <summary>Current schema version for new v2 documents.</summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>Document format version. Higher versions load leniently (unknown
        /// members preserved); a loaded document keeps its version on save.</summary>
        [JsonProperty("schemaVersion", Order = -2)]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Profile hook, carried from v1 — unused until profiles ship.</summary>
        [JsonProperty("profileId")]
        public string ProfileId { get; set; }

        /// <summary>ITM catalog pages and hosted pages, one list; <c>kind</c> discriminates.</summary>
        [JsonProperty("pages")]
        public List<PageEntry> Pages { get; set; } = new List<PageEntry>();

        /// <summary>Named page cycles (2+ members each).</summary>
        [JsonProperty("cycles")]
        public List<CycleEntry> Cycles { get; set; } = new List<CycleEntry>();

        /// <summary>
        /// Idle programs (ordered destination · duration steps). Absent (<c>null</c>) ≡
        /// no playlists (not emitted); explicit empty list is rare but legal. Idle-slot
        /// only in v1 (amendment A1 / spec §16).
        /// </summary>
        [JsonProperty("playlists")]
        public List<PlaylistEntry> Playlists { get; set; }

        /// <summary>Priority ladder: ranked rows + fixed rest floor.</summary>
        [JsonProperty("priority")]
        public PriorityLadder Priority { get; set; } = new PriorityLadder();

        /// <summary>
        /// Walk order (itmPage / hostedPage refs). Absent (<c>null</c>) = compiled default;
        /// explicit empty list = empty walk (no members). These are different states.
        /// </summary>
        [JsonProperty("pageOrder")]
        public List<PageRef> PageOrder { get; set; }

        /// <summary>Per-parameter field content, keyed by param id (numeric string keys).</summary>
        [JsonProperty("fields")]
        public Dictionary<ushort, FieldEntry> Fields { get; set; }
            = new Dictionary<ushort, FieldEntry>();

        /// <summary>
        /// Shared field content, keyed by stable logical field id (catalog <c>fieldId</c>
        /// token: speed, gear, …). One config per logical field — gear and speed are
        /// separate keys. Absent when empty. Reuses <see cref="FieldEntry"/> verbatim.
        /// Reach is derived from catalog placements (not stored here).
        /// </summary>
        [JsonProperty("sharedFields")]
        public Dictionary<string, FieldEntry> SharedFields { get; set; }

        /// <summary>Wheel-screen plane (special-command rules).</summary>
        [JsonProperty("wheelScreen")]
        public WheelScreenPlane WheelScreen { get; set; } = new WheelScreenPlane();

        /// <summary>Document-level settings (mode, uncommanded-change policy).</summary>
        [JsonProperty("settings")]
        public SettingsBlock Settings { get; set; } = new SettingsBlock();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips — a future version's fields must survive load → save.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Document settings: engine mode and uncommanded-change policy.</summary>
    public class SettingsBlock
    {
        private string _modeRaw;
        private SettingsMode? _mode;

        /// <summary>When true, reject firmware state the host did not command.
        /// Default false (adopt) — suppressed when default.</summary>
        [JsonProperty("rejectUncommandedChanges")]
        [DefaultValue(false)]
        public bool RejectUncommandedChanges { get; set; }

        /// <summary>Serialized form of <see cref="Mode"/>, preserved verbatim.</summary>
        [JsonProperty("mode")]
        [DefaultValue("on")]
        public string ModeRaw
        {
            get => _modeRaw;
            set { _modeRaw = value; _mode = null; }
        }

        /// <summary>Engine mode. Omitted/blank raw → <see cref="SettingsMode.On"/>;
        /// unrecognized → <see cref="SettingsMode.Unknown"/> (raw preserved).</summary>
        [JsonIgnore]
        public SettingsMode Mode
        {
            get
            {
                if (_mode.HasValue)
                    return _mode.Value;
                if (string.IsNullOrWhiteSpace(_modeRaw))
                    _mode = SettingsMode.On;
                else
                    _mode = FanaBridge.Display.Rules.EnumText.Parse(_modeRaw, SettingsMode.Unknown);
                return _mode.Value;
            }
            set
            {
                _mode = value;
                // Suppress the default spelling so absent ≡ on.
                _modeRaw = value == SettingsMode.On ? null : FanaBridge.Display.Rules.EnumText.Write(value);
            }
        }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when settings carry an unusable value (e.g. unrecognized mode).
        /// Runtime-only; raw spellings preserved.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }
    }

    /// <summary>Settings.mode value spellings.</summary>
    public enum SettingsMode
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        On,
        LegacyOnly,
        Off,
    }
}
