using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// What a <see cref="LegacyScreen"/> shows on the 3-char surface. Absent/unrecognized
    /// text follows the EnumText contract: missing defaults to <see cref="Text"/> (today's
    /// static 1–3 char screens, byte-parity); unknown survives load/save and the screen is
    /// skipped as a survivor so rules targeting it degrade like a missing screen.
    /// </summary>
    public enum LegacyContentKind
    {
        /// <summary>Lenient-load fallback — the screen is kept in the document but excluded
        /// from the survivors set (rules targeting it degrade). Raw text is preserved.</summary>
        Unknown = 0,
        /// <summary>Static 1–3 character text (<see cref="LegacyScreen.Text"/>). Default
        /// when <c>contentKind</c> is omitted — today's screens.</summary>
        Text,
        /// <summary>SpeedLocal, rounded and clamped 0–999 (absorbed LegacyDisplayDriver mode).</summary>
        Speed,
        /// <summary>Parsed gear glyph, centered (absorbed LegacyDisplayDriver mode).</summary>
        Gear,
        /// <summary>Parsed gear glyph always drawn in brackets ("[3]") — pure render;
        /// redline membership is a trigger, not embedded here.</summary>
        GearBrackets,
        /// <summary>Rpms/10, clamped 0–999.</summary>
        Rpm,
        /// <summary>Race position, clamped 0–999.</summary>
        Position,
        /// <summary>Fuel remaining, rounded and clamped 0–999.</summary>
        Fuel,
        /// <summary>Free renderable text of any length (≥1 position); scroll makes it fit.</summary>
        Message,
        /// <summary>A <see cref="PropertySpec"/> read as a number, clamped 0–999.</summary>
        Property,
    }

    /// <summary>
    /// Presentation effect applied to a rendered legacy screen. <see cref="Flash"/> parses
    /// for EnumText survival but the validator coerces it to <see cref="Blink"/> at runtime
    /// (v1 implements only None / Scroll / Blink).
    /// </summary>
    public enum LegacyEffect
    {
        /// <summary>Lenient-load fallback — treated as <see cref="None"/> at runtime;
        /// raw text is preserved for the round-trip.</summary>
        Unknown = 0,
        /// <summary>No effect — the rendered text is shown as-is. Default when omitted.</summary>
        None,
        /// <summary>Marquee scroll when the rendered text exceeds 3 positions (~400 ms/step).</summary>
        Scroll,
        /// <summary>500 ms on / 500 ms off.</summary>
        Blink,
        /// <summary>Parses and survives round-trip; coerced to <see cref="Blink"/> at load
        /// (runtime-only) with a warning.</summary>
        Flash,
    }

    /// <summary>
    /// A named screen for the legacy 7-segment surface. Screens form the library that
    /// legacy rules (and ITM rules targeting <see cref="TargetKind.Screen"/>) pick
    /// from. Content is either static text (the original 1–3 char form) or a dynamic kind
    /// resolved at frame time by the legacy formatter — nothing consumes those kinds until
    /// the wire path is wired (Phase 7b).
    /// </summary>
    public class LegacyScreen
    {
        private string _contentKindRaw;
        private LegacyContentKind? _contentKind;
        private string _effectRaw;
        private LegacyEffect? _effect;

        /// <summary>Stable identity, referenced by <see cref="RuleTarget.ScreenId"/> and
        /// <see cref="LegacyRuleSet.BaseScreenId"/>.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Human-readable label for the UI.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// Static text for <see cref="LegacyContentKind.Text"/> (1–3 positions) and
        /// <see cref="LegacyContentKind.Message"/> (any length, every char renderable).
        /// Dynamic kinds ignore this field.
        /// </summary>
        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary>Serialized form of <see cref="ContentKind"/>, preserved verbatim
        /// (see <see cref="RuleCondition.KindRaw"/>).</summary>
        [JsonProperty("contentKind")]
        public string ContentKindRaw
        {
            get => _contentKindRaw;
            set { _contentKindRaw = value; _contentKind = null; }
        }

        /// <summary>
        /// What the screen shows. Omitted/blank raw → <see cref="LegacyContentKind.Text"/>
        /// (back-compat). Unrecognized raw → <see cref="LegacyContentKind.Unknown"/> (screen
        /// kept, excluded from survivors; raw survives the round-trip).
        /// </summary>
        [JsonIgnore]
        public LegacyContentKind ContentKind
        {
            get
            {
                if (_contentKind.HasValue)
                    return _contentKind.Value;
                if (string.IsNullOrWhiteSpace(_contentKindRaw))
                    _contentKind = LegacyContentKind.Text;
                else
                    _contentKind = EnumText.Parse(_contentKindRaw, LegacyContentKind.Unknown);
                return _contentKind.Value;
            }
            set { _contentKind = value; _contentKindRaw = EnumText.Write(value); }
        }

        /// <summary>Serialized form of <see cref="Effect"/>, preserved verbatim
        /// (see <see cref="RuleCondition.KindRaw"/>).</summary>
        [JsonProperty("effect")]
        public string EffectRaw
        {
            get => _effectRaw;
            set { _effectRaw = value; _effect = null; }
        }

        /// <summary>
        /// Presentation effect. Omitted/blank raw → <see cref="LegacyEffect.None"/>.
        /// Unrecognized → <see cref="LegacyEffect.Unknown"/> (treated as None at runtime;
        /// raw preserved). <see cref="LegacyEffect.Flash"/> is coerced to Blink at load
        /// via <see cref="CoerceEffect"/> (runtime-only).
        /// </summary>
        [JsonIgnore]
        public LegacyEffect Effect
        {
            get
            {
                if (_effect.HasValue)
                    return _effect.Value;
                if (string.IsNullOrWhiteSpace(_effectRaw))
                    _effect = LegacyEffect.None;
                else
                    _effect = EnumText.Parse(_effectRaw, LegacyEffect.Unknown);
                return _effect.Value;
            }
            set { _effect = value; _effectRaw = EnumText.Write(value); }
        }

        /// <summary>Load-time coercion that changes only what the runtime sees — the
        /// serialized <see cref="EffectRaw"/> stays untouched (Flash → Blink, etc.).</summary>
        internal void CoerceEffect(LegacyEffect effect) => _effect = effect;

        /// <summary><see cref="LegacyContentKind.Property"/> only: the value source.
        /// Other kinds ignore this field.</summary>
        [JsonProperty("source")]
        public PropertySpec Source { get; set; }

        /// <summary>
        /// Rotation membership for the segment display. Default true (absent → true;
        /// true is suppressed on save). Overlay-only screens set this false — they are
        /// rule targets that never serve as base. Consumed in P10b; inert this phase.
        /// </summary>
        [JsonProperty("inRotation")]
        [DefaultValue(true)]
        public bool InRotation { get; set; } = true;

        /// <summary>
        /// Reserved format key, uninterpreted in v1. Non-empty unknown text is cleared
        /// with a warning at load (same degrade style as field-mapping formats).
        /// </summary>
        [JsonProperty("format")]
        public string Format { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips — a future version's fields must survive load → save (the
        /// member-level complement of the EnumText unknown-value discipline).</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        /// <summary>
        /// Whether <paramref name="text"/> renders on the 7-segment display: 1–3 display
        /// positions, each covered by <see cref="SevenSegment.CharToSegment"/>. Positions
        /// are counted the way the encoder folds ("-1.5" is three positions, the dot rides
        /// the '1' — see <see cref="SevenSegment.EncodeWithDots"/>). Space is a deliberate
        /// blank; any other character the segment table cannot draw (it would fall back to
        /// blank) fails, so a screen never silently shows empty positions.
        /// </summary>
        public static bool IsRenderableText(string text)
        {
            int positions;
            return TryCountRenderablePositions(text, out positions)
                && positions >= 1 && positions <= 3;
        }

        /// <summary>
        /// Whether <paramref name="text"/> is a valid <see cref="LegacyContentKind.Message"/>:
        /// every character is renderable (or a folding dot), any length ≥ 1 position.
        /// </summary>
        public static bool IsRenderableMessage(string text)
        {
            int positions;
            return TryCountRenderablePositions(text, out positions) && positions >= 1;
        }

        /// <summary>Counts display positions the way <see cref="SevenSegment.EncodeWithDots"/>
        /// folds dots, returning false when any non-space character has no segment coverage.</summary>
        private static bool TryCountRenderablePositions(string text, out int positions)
        {
            positions = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (char ch in text)
            {
                if (ch == '.' || ch == ',')
                {
                    if (positions == 0)
                        positions++;   // nothing to fold onto — a leading dot takes a slot
                    continue;
                }
                if (ch != ' ' && SevenSegment.CharToSegment(ch) == SevenSegment.Blank)
                    return false;
                positions++;
            }
            return true;
        }
    }
}
