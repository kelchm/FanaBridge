using System;
using System.IO;
using Xunit;

namespace FanaBridge.Tests.Display.TestSupport
{
    /// <summary>Keeper-owned path helpers shared by Display arbitration/composer tests.</summary>
    public static class TestPaths
    {
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FanaBridge.sln"))
                    || Directory.Exists(Path.Combine(dir.FullName, "src")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        /// <summary>
        /// PBME shipped catalog JSON (single source of truth in FanaBridge.Core).
        /// Prefer <see cref="FanaBridge.Display.Catalog.CatalogLoader.TryResolve"/> for
        /// parsed catalogs; this returns the raw resource body for callers that still
        /// load via string.
        /// </summary>
        public static string CatalogJson()
        {
            var json = FanaBridge.Display.Catalog.CatalogLoader.ReadShippedResource(
                "pbme-catalog.json");
            Assert.False(string.IsNullOrEmpty(json), "shipped pbme-catalog.json missing");
            return json!;
        }

        /// <summary>Obsolete disk path helper — use <see cref="CatalogJson"/>.</summary>
        public static string CatalogPath() => CatalogJson();
    }
}
