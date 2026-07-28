using System;
using Newtonsoft.Json;

namespace FanaBridge.Display.Catalog
{
    /// <summary>
    /// Parse-from-string loaders for shipped catalog documents and the global alias
    /// table. Never throws: a blank input yields an empty document; unparseable input
    /// yields an empty document with a warning. Unknown members are preserved via
    /// <c>[JsonExtensionData]</c> on every catalog type. No validation in this phase.
    /// </summary>
    public static class CatalogLoader
    {
        /// <summary>Serializes a wheel catalog (indented, nulls omitted).</summary>
        public static string Save(WheelCatalog catalog)
            => JsonConvert.SerializeObject(catalog, Settings);

        /// <summary>Serializes an alias table (indented, nulls omitted).</summary>
        public static string Save(AliasTable table)
            => JsonConvert.SerializeObject(table, Settings);

        /// <summary>
        /// Parses a wheel catalog. Null/blank → empty document silently; parse failure →
        /// empty document with a warning. Never throws.
        /// </summary>
        public static WheelCatalog LoadWheelCatalog(string json, Action<string> log)
        {
            WheelCatalog catalog = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    catalog = JsonConvert.DeserializeObject<WheelCatalog>(json, Settings);
                }
                catch (Exception ex)
                {
                    SafeLog(log,
                        "Catalog: could not parse wheel catalog — using empty (" + ex.Message + ")");
                }
            }
            return catalog ?? new WheelCatalog();
        }

        /// <summary>
        /// Parses the global alias table. Null/blank → empty document silently; parse
        /// failure → empty document with a warning. Never throws.
        /// </summary>
        public static AliasTable LoadAliasTable(string json, Action<string> log)
        {
            AliasTable table = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    table = JsonConvert.DeserializeObject<AliasTable>(json, Settings);
                }
                catch (Exception ex)
                {
                    SafeLog(log,
                        "Catalog: could not parse alias table — using empty (" + ex.Message + ")");
                }
            }
            return table ?? new AliasTable();
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

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
        };
    }
}
