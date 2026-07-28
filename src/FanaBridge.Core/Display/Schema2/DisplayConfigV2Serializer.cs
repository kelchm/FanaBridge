using System;
using Newtonsoft.Json;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// JSON persistence for <see cref="DisplayConfigV2"/>, mirroring the v1
    /// <c>DisplayConfigSerializer</c> contract: camelCase property and enum names via
    /// raw-string storage, null/default suppression, and lenient loading — unknown members
    /// are preserved via <see cref="DisplayConfigV2.ExtensionData"/> (and the same bag on
    /// every schema-closure type), unknown enum values degrade only at runtime (raw text
    /// is preserved verbatim so a save after a load never destroys what a future version
    /// wrote), and a document that cannot be parsed at all yields defaults with a warning.
    /// Loading never throws. No validator in this phase — load is parse-only.
    /// </summary>
    public static class DisplayConfigV2Serializer
    {
        /// <summary>Serializes the document (indented, defaults omitted).</summary>
        public static string Save(DisplayConfigV2 config)
            => JsonConvert.SerializeObject(config, Settings);

        /// <summary>
        /// Parses a document. Null/blank input yields a fresh default config silently;
        /// anything else that fails to parse yields the same default with a warning to
        /// <paramref name="log"/>. Never throws. Does not validate or coerce.
        /// </summary>
        public static DisplayConfigV2 Load(string json, Action<string> log)
        {
            DisplayConfigV2 config = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    config = JsonConvert.DeserializeObject<DisplayConfigV2>(json, Settings);
                }
                catch (Exception ex)
                {
                    SafeLog(log,
                        "DisplayConfigV2: could not parse config — using defaults (" + ex.Message + ")");
                }
            }
            return config ?? new DisplayConfigV2();
        }

        /// <summary>Invokes <paramref name="log"/> without letting a throwing callback
        /// break the never-throws load contract.</summary>
        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch
            {
                // Logger failures must not surface from Load.
            }
        }

        // Same shape as v1: no enum converter — enum-valued fields are raw strings
        // (EnumText), so converting at parse time would re-introduce round-trip data loss
        // for values only a future version knows.
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
        };
    }
}
