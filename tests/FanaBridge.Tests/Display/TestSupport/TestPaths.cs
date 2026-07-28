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

        /// <summary>PBME catalog draft fixture (tests tree, with scratch fallback).</summary>
        public static string CatalogPath()
        {
            var path = Path.Combine(
                RepoRoot(), "tests", "FanaBridge.Tests", "Display",
                "Fixtures", "pbme-catalog-draft.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(
                    RepoRoot(), "scratch", "plans", "display-customization",
                    "catalog", "pbme-catalog-draft.json");
            }
            Assert.True(File.Exists(path), "catalog fixture missing: " + path);
            return path;
        }
    }
}
