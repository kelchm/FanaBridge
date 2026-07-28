using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Catalog
{
    /// <summary>
    /// Global alias table: property paths and built-in names → human sentences for
    /// condition generation. Authored catalog data (US spelling on display labels).
    /// </summary>
    public class AliasTable
    {
        [JsonProperty("aliasTableVersion")]
        public int AliasTableVersion { get; set; }

        [JsonProperty("aliases")]
        public List<AliasEntry> Aliases { get; set; } = new List<AliasEntry>();

        [JsonProperty("patternRules")]
        public List<AliasPatternRule> PatternRules { get; set; } = new List<AliasPatternRule>();

        [JsonProperty("prefixRules")]
        public List<AliasPrefixRule> PrefixRules { get; set; } = new List<AliasPrefixRule>();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>One exact-ref alias (builtIn name or simHub property path).</summary>
    public class AliasEntry
    {
        private string _kindRaw;
        private AliasKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="AliasKind.Unknown"/> when
        /// missing or unrecognized (raw preserved).</summary>
        [JsonIgnore]
        public AliasKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, AliasKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary>Built-in name or full SimHub property path.</summary>
        [JsonProperty("ref")]
        public string Ref { get; set; }

        /// <summary>Human alias used in generated condition sentences.</summary>
        [JsonProperty("alias")]
        public string Alias { get; set; }

        /// <summary>Unit label for the sentence, or null when unitless.</summary>
        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Alias-entry kind discriminator: built-in name vs SimHub property path.</summary>
    public enum AliasKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        BuiltIn,
        Property,
    }

    /// <summary>Regex pattern → alias template (e.g. FN layer keys).</summary>
    public class AliasPatternRule
    {
        [JsonProperty("match")]
        public string Match { get; set; }

        [JsonProperty("aliasPattern")]
        public string AliasPattern { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Prefix strip → alias template (matched after pattern rules).</summary>
    public class AliasPrefixRule
    {
        [JsonProperty("prefix")]
        public string Prefix { get; set; }

        [JsonProperty("aliasPattern")]
        public string AliasPattern { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }
}
