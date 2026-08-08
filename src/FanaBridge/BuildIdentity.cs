using System;
using System.Reflection;

namespace FanaBridge.Plugin
{
    /// <summary>
    /// Build identity of the plugin assembly, read from attributes embedded at
    /// compile time (see Directory.Build.props): informational version plus build
    /// configuration and short commit hash.
    ///
    /// Single source of truth for the About panel and the diagnostics report, so
    /// the version/commit logic lives in one place rather than being duplicated.
    /// </summary>
    internal static class BuildIdentity
    {
        private static readonly Assembly Asm = typeof(BuildIdentity).Assembly;

        /// <summary>Informational version, e.g. "0.4.0". Never null.</summary>
        public static string Version =>
            Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Asm.GetName().Version?.ToString()
            ?? "unknown";

        /// <summary>Build configuration ("Debug"/"Release"), or null if not embedded.</summary>
        public static string Configuration => Metadata("BuildConfiguration");

        /// <summary>Short git commit hash, or null if not embedded (e.g. built without git).</summary>
        public static string CommitHash => Metadata("CommitHash");

        /// <summary>
        /// Full build identity, e.g. "0.4.0 · Debug · a6d7872", omitting the
        /// configuration and/or commit when they aren't embedded.
        /// </summary>
        public static string Full
        {
            get
            {
                string info = Version;
                if (!string.IsNullOrEmpty(Configuration)) info += " · " + Configuration;
                if (!string.IsNullOrEmpty(CommitHash)) info += " · " + CommitHash;
                return info;
            }
        }

        private static string Metadata(string key)
        {
            foreach (var meta in Asm.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (string.Equals(meta.Key, key, StringComparison.OrdinalIgnoreCase))
                    return meta.Value;
            return null;
        }
    }
}
