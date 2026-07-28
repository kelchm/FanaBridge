using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Wheel-screen plane: ranked special-command rules above <c>priority.rest.idle</c>.
    /// When rest.idle is a page, this plane's floor is silence (no screen command).
    /// </summary>
    public class WheelScreenPlane
    {
        /// <summary>Ranked rules, array order = rank, top-first.</summary>
        [JsonProperty("rules")]
        public List<WheelScreenRule> Rules { get; set; } = new List<WheelScreenRule>();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>
    /// One wheel-screen rule: show a special screen when condition+lifetime fire.
    /// Same summon grammar as everything else.
    /// </summary>
    public class WheelScreenRule
    {
        private string _screenRaw;
        private WheelScreenCommand? _screen;
        private string _runsRaw;
        private RunsWhen? _runs;

        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Serialized form of <see cref="Screen"/>, preserved verbatim.
        /// Spelling <c>logoInverted</c> (not "inverted").</summary>
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
    }
}
