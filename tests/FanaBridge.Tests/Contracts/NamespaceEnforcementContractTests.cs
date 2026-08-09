using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace FanaBridge.Tests.Contracts
{
    /// <summary>
    /// Pins the IDE0130 contract's anchors: case-sensitive <c>RootNamespace</c> on
    /// each csproj, and a closed allowlist of files that may suppress IDE0130.
    /// </summary>
    public class NamespaceEnforcementContractTests
    {
        // Ratified RootNamespace map. IDE0130 trusts RootNamespace and compares
        // case-insensitively; this pins the anchor so a typo like Fanabridge
        // cannot silently redefine the tree.
        private static readonly (string ProjectDir, string RootNamespace)[] RootNamespacePins =
        {
            ("src/FanaBridge.Core", "FanaBridge.Core"),
            ("src/FanaBridge", "FanaBridge"),
            ("src/FanaBridge.Updater", "FanaBridge.Updater"),
            ("tests/FanaBridge.Tests", "FanaBridge.Tests"),
        };

        // Deliberate IDE0130 exceptions only. Contracts/ is excluded from the
        // scan so this file (and siblings) may name the diagnostic without
        // becoming allowlist entries.
        private static readonly string[] Ide0130Allowlist =
        {
            "src/FanaBridge.Core/Logging/Log.cs",
            "src/FanaBridge/Properties/ModuleInitializerAttribute.cs",
            "tests/FanaBridge.Tests/Plugin/ControlMapper/ControlMapperFakes.cs",
        };

        [Fact]
        public void RootNamespace_pins_match_ratified_map()
        {
            string repoRoot = FindRepoRoot();
            var violations = new List<string>();

            foreach (var (projectDir, expected) in RootNamespacePins)
            {
                string csprojPath = Path.Combine(
                    repoRoot,
                    projectDir.Replace('/', Path.DirectorySeparatorChar),
                    Path.GetFileName(projectDir) + ".csproj");

                Assert.True(File.Exists(csprojPath), $"Missing csproj: {projectDir}");

                XDocument doc = XDocument.Load(csprojPath);
                string? actual = doc.Descendants()
                    .Where(e => e.Name.LocalName == "RootNamespace")
                    .Select(e => e.Value)
                    .FirstOrDefault();

                if (actual == null)
                {
                    // MSBuild default when omitted: project file name without extension.
                    string projectFileName = Path.GetFileNameWithoutExtension(csprojPath);
                    if (!string.Equals(projectFileName, expected, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{projectDir}: RootNamespace omitted (MSBuild default '{projectFileName}'), " +
                            $"expected '{expected}'");
                    }
                    continue;
                }

                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{projectDir}: RootNamespace is '{actual}', expected '{expected}' (ordinal)");
                }
            }

            Assert.True(violations.Count == 0,
                "RootNamespace pin violations:\n" + string.Join("\n", violations));
        }

        [Fact]
        public void Ide0130_pragma_allowlist_is_closed()
        {
            string repoRoot = FindRepoRoot();
            var offenders = new List<string>();
            var foundOnAllowlist = new HashSet<string>(StringComparer.Ordinal);

            foreach (string scanRoot in new[] { "src", "tests" })
            {
                string absRoot = Path.Combine(repoRoot, scanRoot);
                if (!Directory.Exists(absRoot)) continue;

                foreach (string file in Directory.EnumerateFiles(absRoot, "*.cs", SearchOption.AllDirectories))
                {
                    string norm = file.Replace('\\', '/');
                    if (norm.Contains("/bin/") || norm.Contains("/obj/")) continue;
                    // Contract tests name IDE0130 in prose; exclude so the
                    // allowlist stays only the production suppressions.
                    if (norm.Contains("/Contracts/")) continue;

                    string relative = file.Substring(repoRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');

                    string text = File.ReadAllText(file);
                    if (!text.Contains("IDE0130")) continue;

                    if (Ide0130Allowlist.Contains(relative, StringComparer.Ordinal))
                        foundOnAllowlist.Add(relative);
                    else
                        offenders.Add(relative);
                }
            }

            var missing = Ide0130Allowlist
                .Where(p => !foundOnAllowlist.Contains(p))
                .ToList();

            var messages = new List<string>();
            if (offenders.Count > 0)
                messages.Add("Unexpected IDE0130 mentions:\n" + string.Join("\n", offenders));
            if (missing.Count > 0)
                messages.Add("Allowlisted files no longer contain IDE0130 (dormant entries):\n" +
                             string.Join("\n", missing));

            Assert.True(messages.Count == 0, string.Join("\n\n", messages));
        }

        private static string FindRepoRoot()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string? start in new[]
            {
                Path.GetDirectoryName(asm.Location),
                TryCodeBaseDir(asm),
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.CurrentDirectory,
            })
            {
                for (string? dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                {
                    if (File.Exists(Path.Combine(dir, "FanaBridge.sln"))) return dir!;
                }
            }
            throw new InvalidOperationException(
                "Could not locate FanaBridge.sln. Run tests from a checkout of the repo.");
        }

        private static string? TryCodeBaseDir(Assembly asm)
        {
            try
            {
                if (string.IsNullOrEmpty(asm.CodeBase)) return null;
                var uri = new Uri(asm.CodeBase);
                return uri.IsFile ? Path.GetDirectoryName(uri.LocalPath) : null;
            }
            catch (UriFormatException) { return null; }
        }
    }
}
