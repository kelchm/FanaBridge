using System;
using Newtonsoft.Json;

namespace FanaBridge.Customization
{
    /// <summary>
    /// JSON persistence for <see cref="DisplayCustomizationConfig"/>, following the wheel
    /// profile conventions: camelCase property and enum names, null/default suppression,
    /// and lenient loading — unknown fields are ignored, unknown enum values degrade per
    /// rule (their text is preserved verbatim, see <see cref="EnumText"/>, so a save
    /// after a load never destroys what a future version wrote), and a document that
    /// cannot be parsed at all yields defaults with a warning. Loading never throws: the
    /// config rides per-device settings, and a bad document must cost its bad elements,
    /// never the device. Every load runs <see cref="DisplayConfigValidator.Normalize"/>,
    /// so a loaded config always satisfies the rule engine's invariants as-is.
    /// </summary>
    public static class DisplayConfigSerializer
    {
        /// <summary>Serializes the document (indented, defaults omitted).</summary>
        public static string Save(DisplayCustomizationConfig config)
            => JsonConvert.SerializeObject(config, Settings);

        /// <summary>
        /// Parses and normalizes a document. Null/blank input yields a fresh default
        /// config silently (a device that has never been customized); anything else that
        /// fails to parse yields the same default with a warning to <paramref name="log"/>.
        /// </summary>
        public static DisplayCustomizationConfig Load(string json, Action<string> log)
        {
            DisplayCustomizationConfig config = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    config = JsonConvert.DeserializeObject<DisplayCustomizationConfig>(
                        json, Settings);
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        "DisplayConfig: could not parse config — using defaults (" + ex.Message + ")");
                }
            }
            return DisplayConfigValidator.Normalize(
                config ?? new DisplayCustomizationConfig(), log);
        }

        // Same shape the profile wizard writes profiles with. Enum-valued fields are raw
        // strings on the model (see EnumText), so no enum converter runs — and none must
        // be added: converting at parse time would re-introduce the round-trip data loss
        // for values only a future version knows.
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
        };
    }
}
