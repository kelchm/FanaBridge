using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Reads the plugin's persisted settings without a running plugin.
    /// </summary>
    /// <remarks>
    /// SimHub builds and saves device instances whether or not FanaBridge is
    /// enabled, so device registration cannot go through
    /// <see cref="FanatecPlugin.Settings"/> — the singleton may not exist. The
    /// settings file itself is always there, at the path SimHub's common-settings
    /// helpers use: <c>PluginsData\Common\{PluginType}.{Name}.json</c>, relative
    /// to SimHub's working directory.
    ///
    /// Only the profile overrides are read here. They decide which profile backs
    /// a device — and therefore the LED editor's size — so registration has to
    /// see the same override the running plugin would apply, or a device whose
    /// override changes its LED layout could never get an editor that fits.
    /// </remarks>
    internal static class PersistedPluginSettings
    {
        private const string SettingsFileName = "FanatecPlugin.FanaBridgeSettings.json";

        // SimHub keeps rolling copies of every settings file it writes, and
        // falls back through them when the current one will not parse.
        private const string BackupDirectoryName = "_Backups";
        private const int MaxBackupVersions = 10;

        /// <summary>Where SimHub keeps this plugin's settings.</summary>
        internal static readonly Func<string> DefaultSettingsPath = () =>
            Path.Combine("PluginsData", "Common", SettingsFileName);

        /// <summary>Test seam: locates the settings file.</summary>
        internal static Func<string> SettingsPathResolver = DefaultSettingsPath;

        /// <summary>
        /// The persisted wheel-match-key → profile-override-key map, or an empty
        /// map when the file is missing or unreadable. Never throws: registration
        /// must succeed with default profiles rather than fail outright.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ReadProfileOverrides()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var path = SettingsPathResolver();
                if (string.IsNullOrEmpty(path))
                    return result;

                var overrides = ReadOverridesObject(path);
                if (overrides == null)
                    return result;

                foreach (var prop in overrides.Properties())
                {
                    var value = prop.Value?.Type == JTokenType.String ? (string)prop.Value : null;
                    if (!string.IsNullOrEmpty(prop.Name) && !string.IsNullOrEmpty(value))
                        result[prop.Name] = value;
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "PersistedPluginSettings: could not read profile overrides: " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Reads the overrides from the settings file, falling back through
        /// SimHub's rolling backups exactly as SimHub itself does.
        /// </summary>
        /// <remarks>
        /// Matching that fallback matters: if the plugin recovers an override
        /// from a backup while device registration only looked at a corrupt
        /// primary file, the two disagree about which profile a device has —
        /// and the LED editor, which is sized at registration, would be built
        /// for the wrong one for the rest of the session.
        /// </remarks>
        private static JObject ReadOverridesObject(string path)
        {
            foreach (var candidate in CandidatePaths(path))
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    if (JObject.Parse(File.ReadAllText(candidate))["ProfileOverrides"]
                        is JObject overrides)
                    {
                        return overrides;
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "PersistedPluginSettings: " + Path.GetFileName(candidate) +
                        " could not be read (" + ex.Message + "); trying the previous copy");
                }
            }

            return null;
        }

        private static IEnumerable<string> CandidatePaths(string path)
        {
            yield return path;

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            for (int version = 1; version <= MaxBackupVersions; version++)
            {
                yield return Path.Combine(
                    directory, BackupDirectoryName, name + "_b" + version + extension);
            }
        }
    }
}
