using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Catalog
{
    /// <summary>
    /// Parse-from-string loaders for shipped catalog documents and the global alias
    /// table. Never throws: a blank input yields an empty document; unparseable input
    /// yields an empty document with a warning. Unknown members are preserved via
    /// <c>[JsonExtensionData]</c> on every catalog type.
    /// </summary>
    public static class CatalogLoader
    {
        /// <summary>
        /// Embedded resource name suffix for the alias table (RootNamespace + path).
        /// </summary>
        private const string AliasResourceSuffix = ".Display.Catalog.Resources.alias-table.json";

        /// <summary>
        /// Embedded resource name infix for wheel catalogs under
        /// <c>Display/Catalog/Resources/</c>.
        /// </summary>
        private const string CatalogResourceFolderSuffix = ".Display.Catalog.Resources.";

        /// <summary>Serializes a wheel catalog (indented, nulls omitted).</summary>
        public static string Save(WheelCatalog catalog)
            => JsonConvert.SerializeObject(catalog, Settings);

        /// <summary>Serializes an alias table (indented, nulls omitted).</summary>
        public static string Save(AliasTable table)
            => JsonConvert.SerializeObject(table, Settings);

        /// <summary>
        /// Supported <see cref="WheelCatalog.CatalogVersion"/>. Only version 2 is
        /// accepted (catalogVersion 1 → 2 was atomic; no dual-shape reader).
        /// </summary>
        public const int SupportedCatalogVersion = 2;

        /// <summary>
        /// Parses a wheel catalog. Null/blank → empty document silently; parse failure →
        /// empty document with a warning. catalogVersion must be
        /// <see cref="SupportedCatalogVersion"/> (2); anything else → warn + empty
        /// (fail-closed data). Never throws.
        /// </summary>
        public static WheelCatalog LoadWheelCatalog(string json, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new WheelCatalog();

            WheelCatalog catalog = null;
            try
            {
                catalog = JsonConvert.DeserializeObject<WheelCatalog>(json, Settings);
            }
            catch (Exception ex)
            {
                SafeLog(log,
                    "Catalog: could not parse wheel catalog — using empty (" + ex.Message + ")");
                return new WheelCatalog();
            }

            if (catalog == null)
                return new WheelCatalog();

            if (catalog.CatalogVersion != SupportedCatalogVersion)
            {
                SafeLog(log,
                    "Catalog: catalogVersion " + catalog.CatalogVersion
                    + " not supported (expected " + SupportedCatalogVersion
                    + ") — using empty (fail-closed)");
                return new WheelCatalog();
            }

            return catalog;
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

        /// <summary>
        /// Loads every shipped wheel catalog from this assembly's embedded resources.
        /// Keyed by <see cref="WheelCatalog.WheelId"/> lowercased (OQ-2). Never throws.
        /// </summary>
        public static IReadOnlyDictionary<string, WheelCatalog> LoadShipped(
            Action<string> log = null)
        {
            var asm = typeof(CatalogLoader).Assembly;
            var byCode = new Dictionary<string, WheelCatalog>(StringComparer.Ordinal);

            foreach (var name in asm.GetManifestResourceNames())
            {
                int folder = name.IndexOf(CatalogResourceFolderSuffix, StringComparison.Ordinal);
                if (folder < 0)
                    continue;
                string file = name.Substring(folder + CatalogResourceFolderSuffix.Length);
                if (!file.EndsWith("-catalog.json", StringComparison.Ordinal))
                    continue;

                var catalog = LoadWheelCatalog(ReadResource(asm, name), log);
                string key = NormalizeWheelCode(catalog.WheelId);
                if (string.IsNullOrEmpty(key))
                {
                    // Fall back to file stem: "pbme-catalog.json" → "pbme"
                    key = file.Substring(0, file.Length - "-catalog.json".Length)
                        .ToLowerInvariant();
                    SafeLog(log,
                        "Catalog: shipped resource '" + file
                        + "' has no wheelId — indexing as '" + key + "'");
                }

                if (byCode.ContainsKey(key))
                {
                    SafeLog(log,
                        "Catalog: duplicate shipped wheel code '" + key
                        + "' — keeping first");
                    continue;
                }
                byCode[key] = catalog;
            }

            return byCode;
        }

        /// <summary>
        /// Loads the shipped global alias table from the embedded resource. Never throws.
        /// </summary>
        public static AliasTable LoadShippedAliasTable(Action<string> log = null)
        {
            var asm = typeof(CatalogLoader).Assembly;
            string name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(AliasResourceSuffix, StringComparison.Ordinal));
            if (name == null)
            {
                SafeLog(log, "Catalog: shipped alias table resource missing");
                return new AliasTable();
            }
            return LoadAliasTable(ReadResource(asm, name), log);
        }

        /// <summary>
        /// Resolves a shipped catalog by display <paramref name="moduleCode"/> first,
        /// then falls back to <paramref name="wheelCode"/> (both lowercased; OQ-2).
        /// This preserves the wheel/hub distinction for composites whose catalog belongs
        /// to the attached display module. When <paramref name="itmDeviceId"/> is set
        /// and the catalog declares
        /// a deviceId, a mismatch is <b>logged</b> (never throws — same style as parse
        /// failures) and the catalog is still returned. Unknown code → false, catalog
        /// null, warning logged.
        /// </summary>
        public static bool TryResolve(
            string wheelCode,
            out WheelCatalog catalog,
            Action<string> log = null,
            byte? itmDeviceId = null,
            string moduleCode = null)
        {
            string wheelKey = NormalizeWheelCode(wheelCode);
            string moduleKey = NormalizeWheelCode(moduleCode);
            if (wheelKey == null && moduleKey == null)
            {
                catalog = null;
                SafeLog(log, "Catalog: no shipped catalog for wheel code ''");
                return false;
            }

            var set = LoadShipped(log);
            catalog = null;
            string key = null;
            if (moduleKey != null && set.TryGetValue(moduleKey, out catalog))
                key = moduleKey;
            else if (wheelKey != null && set.TryGetValue(wheelKey, out catalog))
                key = wheelKey;

            if (key == null)
            {
                catalog = null;
                string identity = moduleKey == null
                    ? "'" + wheelKey + "'"
                    : "'" + moduleKey + "' (module), '" + (wheelKey ?? "") + "' (wheel)";
                SafeLog(log, "Catalog: no shipped catalog for wheel code " + identity);
                return false;
            }

            if (itmDeviceId.HasValue)
            {
                byte? declared = ReadDeclaredDeviceId(catalog);
                if (declared.HasValue && declared.Value != itmDeviceId.Value)
                {
                    // Error style: log-only (matches never-throws parse style).
                    SafeLog(log,
                        "Catalog: deviceId mismatch for wheel '" + key
                        + "' — catalog declares " + declared.Value
                        + ", runtime has " + itmDeviceId.Value);
                }
            }
            return true;
        }

        /// <summary>
        /// Reads a shipped resource body by file name (e.g. <c>pbme-catalog.json</c>)
        /// for tests that need the raw JSON. Returns null when missing.
        /// </summary>
        public static string ReadShippedResource(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            var asm = typeof(CatalogLoader).Assembly;
            string suffix = CatalogResourceFolderSuffix + fileName;
            string name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            return name == null ? null : ReadResource(asm, name);
        }

        /// <summary>Lowercases a wheel code for dictionary lookup (OQ-2).</summary>
        public static string NormalizeWheelCode(string wheelCode)
            => string.IsNullOrWhiteSpace(wheelCode)
                ? null
                : wheelCode.Trim().ToLowerInvariant();

        /// <summary>
        /// Catalog-declared ITM device id from extension data, or null when absent /
        /// unparseable. Never throws.
        /// </summary>
        public static byte? ReadDeclaredDeviceId(WheelCatalog catalog)
        {
            var data = catalog?.Itm?.ExtensionData;
            if (data == null)
                return null;
            if (!data.TryGetValue("deviceId", out JToken token) || token == null
                || token.Type == JTokenType.Null)
                return null;
            try
            {
                if (token.Type == JTokenType.Integer)
                    return token.Value<byte>();
                if (byte.TryParse(token.ToString(), out byte b))
                    return b;
            }
            catch
            {
                // Malformed extension value — treat as absent.
            }
            return null;
        }

        private static string ReadResource(Assembly asm, string name)
        {
            using (var stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null)
                    return null;
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
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
