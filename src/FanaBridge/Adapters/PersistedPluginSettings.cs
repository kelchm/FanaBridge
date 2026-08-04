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
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return result;

                var root = JObject.Parse(File.ReadAllText(path));
                if (!(root["ProfileOverrides"] is JObject overrides))
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
    }
}
