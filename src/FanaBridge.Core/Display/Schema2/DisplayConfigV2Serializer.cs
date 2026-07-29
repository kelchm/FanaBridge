using System;
using Newtonsoft.Json;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// JSON persistence for <see cref="DisplayConfigV2"/>: camelCase property and enum names via
    /// raw-string storage, null/default suppression, and lenient loading — unknown members
    /// are preserved via <see cref="DisplayConfigV2.ExtensionData"/> (and the same bag on
    /// every schema-closure type), unknown enum values degrade only at runtime (raw text
    /// is preserved verbatim so a save after a load never destroys what a future version
    /// wrote), and a document that cannot be parsed at all yields defaults with a warning.
    /// Loading never throws. Every load runs <see cref="DisplayConfigV2Validator.Normalize"/>,
    /// so a loaded config always carries runtime degrade marks for §14 survivors-model rules.
    /// </summary>
    public static class DisplayConfigV2Serializer
    {
        /// <summary>Serializes the document (indented, defaults omitted).</summary>
        public static string Save(DisplayConfigV2 config)
            => JsonConvert.SerializeObject(config, Settings);

        /// <summary>
        /// Structural deep clone via save → deserialize. Does <b>not</b> run
        /// <see cref="DisplayConfigV2Validator.Normalize"/> — the UI edit session uses this
        /// so mutations produce a fresh document identity while unknown members and key
        /// order survive verbatim. Null source yields a fresh default document.
        /// <para>
        /// Fails closed: serialization/deserialization failure throws
        /// <see cref="InvalidOperationException"/> — never returns a silent default
        /// document that could be mutated and published in place of the source.
        /// </para>
        /// </summary>
        public static DisplayConfigV2 Clone(DisplayConfigV2 config)
        {
            if (config == null)
                return new DisplayConfigV2();

            var hook = CloneHookForTest;
            if (hook != null)
            {
                CloneHookForTest = null;
                var hooked = hook(config);
                if (hooked == null)
                {
                    throw new InvalidOperationException(
                        "DisplayConfigV2Serializer.Clone: deserialize returned null");
                }
                return hooked;
            }

            try
            {
                var clone = JsonConvert.DeserializeObject<DisplayConfigV2>(
                    Save(config), Settings);
                if (clone == null)
                {
                    throw new InvalidOperationException(
                        "DisplayConfigV2Serializer.Clone: deserialize returned null");
                }
                return clone;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "DisplayConfigV2Serializer.Clone failed — refusing empty default document",
                    ex);
            }
        }

        /// <summary>
        /// Structural deep clone of a schema subtree (summon / ChildRef / IdleSpec / …).
        /// Fails closed like <see cref="Clone"/> — never returns a silent default.
        /// Null source yields null.
        /// </summary>
        public static T CloneNode<T>(T node) where T : class
        {
            if (node == null)
                return null;
            try
            {
                var clone = JsonConvert.DeserializeObject<T>(
                    JsonConvert.SerializeObject(node, Settings), Settings);
                if (clone == null)
                {
                    throw new InvalidOperationException(
                        "DisplayConfigV2Serializer.CloneNode: deserialize returned null for "
                        + typeof(T).Name);
                }
                return clone;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "DisplayConfigV2Serializer.CloneNode failed for " + typeof(T).Name
                    + " — refusing empty default",
                    ex);
            }
        }

        /// <summary>
        /// Test seam: one-shot override for <see cref="Clone"/>. Invoked once then cleared.
        /// Used to simulate clone corruption without inventing unserializable graphs.
        /// </summary>
        internal static Func<DisplayConfigV2, DisplayConfigV2> CloneHookForTest { get; set; }

        /// <summary>
        /// Parses and normalizes a document. Null/blank input yields a fresh default config
        /// silently; anything else that fails to parse yields the same default with a
        /// warning to <paramref name="log"/>. Never throws. Always runs
        /// <see cref="DisplayConfigV2Validator.Normalize"/> (capability rules skipped —
        /// no catalog at the store boundary; callers that have a <c>WheelCatalog</c> may
        /// re-Normalize with it).
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
            return DisplayConfigV2Validator.Normalize(config ?? new DisplayConfigV2(), log);
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

        // No enum converter — enum-valued fields are raw strings
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
