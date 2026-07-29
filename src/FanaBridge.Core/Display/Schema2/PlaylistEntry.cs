using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Ordered idle program (spec §16 / amendment A1): destination · duration steps with
    /// <c>terminal: hold|loop</c>. Idle-slot only in v1; authored by setups or hand JSON.
    /// </summary>
    public class PlaylistEntry
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Optional user label. Null = UI generates from steps.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Program order — array order is the sequence.</summary>
        [JsonProperty("steps")]
        public List<PlaylistStep> Steps { get; set; } = new List<PlaylistStep>();

        private string _terminalRaw;
        private PlaylistTerminal? _terminal;

        /// <summary>Serialized form of <see cref="Terminal"/>, preserved verbatim.
        /// Default <c>hold</c> is suppressed on write.</summary>
        [JsonProperty("terminal")]
        [DefaultValue("hold")]
        public string TerminalRaw
        {
            get => _terminalRaw;
            set { _terminalRaw = value; _terminal = null; }
        }

        /// <summary>
        /// End-of-program behavior. Omitted/blank raw → <see cref="PlaylistTerminal.Hold"/>;
        /// unrecognized → <see cref="PlaylistTerminal.Unknown"/> (raw preserved; runtime
        /// coerces to hold, degrade-visible).
        /// </summary>
        [JsonIgnore]
        public PlaylistTerminal Terminal
        {
            get
            {
                if (_terminal.HasValue)
                    return _terminal.Value;
                if (string.IsNullOrWhiteSpace(_terminalRaw))
                    _terminal = PlaylistTerminal.Hold;
                else
                    _terminal = FanaBridge.Display.Rules.EnumText.Parse(
                        _terminalRaw, PlaylistTerminal.Unknown);
                return _terminal.Value;
            }
            set
            {
                _terminal = value;
                // Suppress the default spelling so absent ≡ hold.
                _terminalRaw = value == PlaylistTerminal.Hold
                    ? null
                    : FanaBridge.Display.Rules.EnumText.Write(value);
            }
        }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when the playlist has no resolvable steps, loses the identity
        /// race, or has a reserved/missing id. Runtime-only.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>True when authored <see cref="Terminal"/> was outside hold/loop and
        /// runtime coerces to hold. Runtime-only; raw preserved.</summary>
        [JsonIgnore]
        public bool TerminalCoercedAtLoad { get; internal set; }
    }

    /// <summary>
    /// One playlist step: idle-compatible destination + optional duration.
    /// Carries destination and durationMs only — any other member is extension data.
    /// </summary>
    public class PlaylistStep
    {
        /// <summary>
        /// Step destination — same shape as <c>rest.idle</c> (screen / blank / page).
        /// Nested <c>kind: playlist</c> is illegal (step degraded).
        /// </summary>
        [JsonProperty("destination")]
        public IdleSpec Destination { get; set; }

        private int? _durationMs;

        /// <summary>
        /// Step length in ms. Optional: absent is legal (RULED OQ-P3). On the held final
        /// step under <c>terminal: hold</c> a present value is ignored + degrade-visible;
        /// on any other step absence degrades and skips the step at runtime.
        /// </summary>
        [JsonProperty("durationMs")]
        public int DurationMs
        {
            get => _durationMs ?? 0;
            set => _durationMs = value;
        }

        /// <summary>True when <c>durationMs</c> was present in JSON or explicitly assigned.</summary>
        [JsonIgnore]
        public bool DurationMsPresent => _durationMs.HasValue;

        /// <summary>Serialize only when authored/assigned — absent stays absent.</summary>
        public bool ShouldSerializeDurationMs() => _durationMs.HasValue;

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>Set when destination is unusable or duration is required-and-absent.
        /// Runtime-only; program SKIPS degraded steps.</summary>
        [JsonIgnore]
        public bool DegradedAtLoad { get; internal set; }

        /// <summary>
        /// True when a present duration on the held final step is ignored under
        /// <c>terminal: hold</c> (degrade-visible). Runtime-only.
        /// </summary>
        [JsonIgnore]
        public bool DurationMsIgnored { get; internal set; }

        /// <summary>
        /// True when effective runtime duration was raised to the destination dwell
        /// floor (SeatArbiter.MinDwellMs = 500). Authored value preserved; clamp is
        /// runtime-only. Runtime-only mark.
        /// </summary>
        [JsonIgnore]
        public bool DurationClampedAtRuntime { get; internal set; }
    }

    /// <summary>Playlist <c>terminal</c> discriminator spellings.</summary>
    public enum PlaylistTerminal
    {
        /// <summary>Lenient-load fallback — raw text preserved; runtime coerces to Hold.</summary>
        Unknown = 0,
        Hold,
        Loop,
    }
}
