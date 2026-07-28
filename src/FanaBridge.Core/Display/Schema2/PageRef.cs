using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Discriminated page / cycle reference. <c>kind</c> is stored raw so an unrecognized
    /// value round-trips verbatim. Legal carriers vary by site (pageOrder forbids cycle;
    /// rest.inSessionPage forbids cycle; priority targets allow cycle).
    /// </summary>
    public class PageRef
    {
        private string _kindRaw;
        private PageRefKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim.</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>Parsed <see cref="KindRaw"/> — <see cref="PageRefKind.Unknown"/> when
        /// missing or unrecognized.</summary>
        [JsonIgnore]
        public PageRefKind Kind
        {
            get => _kind ?? (_kind = FanaBridge.Display.Rules.EnumText.Parse(_kindRaw, PageRefKind.Unknown)).Value;
            set { _kind = value; _kindRaw = FanaBridge.Display.Rules.EnumText.Write(value); }
        }

        /// <summary><see cref="PageRefKind.ItmPage"/>: catalog page identity token
        /// (e.g. <c>fuelErsDrs</c>), never a wire index.</summary>
        [JsonProperty("catalogPageId")]
        public string CatalogPageId { get; set; }

        /// <summary><see cref="PageRefKind.HostedPage"/> or <see cref="PageRefKind.Cycle"/>:
        /// the stable id of the referenced object.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set by load-time validation when this ref is unresolved, illegal
        /// at its carrier, or a duplicate — runtime-only, never serialized.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }
    }

    /// <summary>Page-reference discriminator spellings.</summary>
    public enum PageRefKind
    {
        /// <summary>Lenient-load fallback — raw text preserved.</summary>
        Unknown = 0,
        ItmPage,
        HostedPage,
        Cycle,
    }
}
