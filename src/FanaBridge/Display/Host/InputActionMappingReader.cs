using System;
using System.Collections.Generic;
using System.Reflection;
using SimHub.Plugins;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Read-only projection of SimHub's live plugin-action mapping settings.
    /// </summary>
    internal static class InputActionMappingReader
    {
        /// <summary>
        /// PluginManager exposes its live Settings property internally. Plugins can
        /// safely read that surface by reflection; failure degrades to an empty list.
        /// </summary>
        internal static IReadOnlyList<string> Read(object pluginManager)
        {
            if (pluginManager == null)
                return Array.Empty<string>();

            try
            {
                var property = pluginManager.GetType().GetProperty(
                    "Settings",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return Read(property?.GetValue(pluginManager) as PluginManagerSettings);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        internal static IReadOnlyList<string> Read(PluginManagerSettings settings)
        {
            var mappings = settings?.InputActionMapping;
            if (mappings == null || mappings.Count == 0)
                return Array.Empty<string>();

            var targets = new List<string>(mappings.Count);
            for (int i = 0; i < mappings.Count; i++)
            {
                string target = mappings[i]?.Target;
                if (!string.IsNullOrEmpty(target))
                    targets.Add(target);
            }
            return targets;
        }
    }
}
