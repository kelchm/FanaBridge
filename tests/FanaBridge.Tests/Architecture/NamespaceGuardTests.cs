using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FanaBridge.Tests.Architecture
{
    /// <summary>
    /// Architecture guard (Phase 0, org remediation). Enforces the folder==namespace
    /// discipline the roadmap establishes so later v9 code is born in the target tree:
    ///   1. every src/**/*.cs declares FanaBridge.&lt;path segments under its project&gt;,
    ///      with an explicit shrinking allowlist of known, documented exceptions;
    ///   2. the retired namespace FanaBridge.Customization exists nowhere;
    ///   3. the flat FanaBridge.Adapters namespace is a ratchet — its file set may only
    ///      shrink; any ADDITION fails and is named;
    ///   4. every tests/**/*.cs declares FanaBridge.Tests.&lt;path segments&gt;, mirroring
    ///      the production tree the 0g test-suite move established (own allowlist);
    ///   5. every src/**/*.xaml x:Class sits in FanaBridge.&lt;folder path&gt; (markup is
    ///      outside the .cs scan; this keeps moved UserControls/Windows honest).
    /// Pure source scan, no compiled reflection: it reads the .cs/.xaml files on disk so
    /// it stays honest even for types that never load in the net48 test host.
    /// </summary>
    public class NamespaceGuardTests
    {
        // Files whose declared namespace legitimately does NOT match their folder path.
        // This list may only SHRINK. Add a true, documented mismatch here only after
        // confirming it is intentional; never to silence a fresh regression. Paths are
        // repo-relative with forward slashes.
        private static readonly HashSet<string> PathNamespaceAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Core log seam sits at root FanaBridge (not FanaBridge.Logging); moving it is a
            // whole-repo using churn deferred to the post-epic pass (roadmap §6.5).
            "src/FanaBridge.Core/Logging/Log.cs",

            // Plugin-side log sink + ModuleInitializer polyfill: declares root FanaBridge
            // (mirrors the core seam it forwards) plus a System.Runtime.CompilerServices
            // shim for the net48-missing ModuleInitializerAttribute. Rides the same §6.5 pass.
            "src/FanaBridge/Logging/SimHubLogSink.cs",
        };

        // Test files whose declared namespace legitimately does NOT match their folder.
        // Same rules as the src allowlist: may only SHRINK, documented, intentional-only.
        // TestDoubles/ deliberately mirrors the code under test rather than the folder.
        private static readonly HashSet<string> TestPathNamespaceAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Control Mapper fakes: declare FanaBridge.Tests.CmFakes plus a shadow of the
            // real SimHub.Plugins.OutputPlugins.ControlRemapper namespace so the doubles bind
            // where the production types would. Mirrors the code under test, not the folder.
            "tests/FanaBridge.Tests/TestDoubles/ControlMapperFakes.cs",

            // Shared fake report stream kept in the root FanaBridge.Tests namespace so every
            // test folder consumes it without an extra using. Intentional shared-double seam.
            "tests/FanaBridge.Tests/TestDoubles/FakeReportStream.cs",
        };

        // Ratchet baseline pinned post-0c: the files still declaring the flat
        // FanaBridge.Adapters namespace. Dissolving Adapters/ into Devices/, Leds/,
        // ControlMapper/ homes is roadmap §6.4 — this set may only shrink. An addition
        // here is a regression and the test names the offending file(s).
        private static readonly HashSet<string> AdaptersRatchetBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/FanaBridge/Adapters/ControlMapperBridge.cs",
            "src/FanaBridge/Adapters/ControlMapperReflection.cs",
            "src/FanaBridge/Adapters/FanaBridgeVariantProvider.cs",
            "src/FanaBridge/Adapters/FanatecDevicesRegistry.cs",
            "src/FanaBridge/Adapters/FanatecLedDriver.cs",
            "src/FanaBridge/Adapters/FanatecLedManager.cs",
            "src/FanaBridge/Adapters/FanatecWheelDeviceInstance.cs",
            "src/FanaBridge/Adapters/IDevicePanelFactory.cs",
            "src/FanaBridge/Adapters/SimHubDevicesGateway.cs",
        };

        // Column-0 namespace declarations only (block or file-scoped). Nested/indented
        // namespaces are intentionally not matched — none exist in this codebase and a
        // top-level anchor keeps the scan unambiguous.
        private static readonly Regex NamespaceLine =
            new Regex(@"^namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]?\s*$", RegexOptions.Compiled);

        // x:Class attribute in XAML markup, e.g. x:Class="FanaBridge.UI.Display.DisplayTabPanel".
        // Captures the fully-qualified type; the namespace is everything before the last dot.
        private static readonly Regex XamlClass =
            new Regex("x:Class\\s*=\\s*\"([A-Za-z_][A-Za-z0-9_.]*)\"", RegexOptions.Compiled);

        [Fact]
        public void EveryFile_DeclaresNamespaceMatchingItsFolder()
        {
            var violations = new List<string>();

            foreach (var file in EnumerateSourceFiles())
            {
                var rel = RepoRelative(file);
                if (PathNamespaceAllowlist.Contains(rel))
                    continue;

                var expected = ExpectedNamespace(rel, "FanaBridge");
                foreach (var declared in DeclaredNamespaces(file))
                {
                    if (!string.Equals(declared, expected, StringComparison.Ordinal))
                        violations.Add($"{rel}: declares '{declared}', expected '{expected}'");
                }
            }

            Assert.True(
                violations.Count == 0,
                "Namespace must equal FanaBridge.<folder path>. Offenders (fix the namespace, "
                    + "move the file, or — only if truly intentional — add to the allowlist with a comment):\n"
                    + string.Join("\n", violations));
        }

        [Fact]
        public void RetiredCustomizationNamespace_ExistsNowhere()
        {
            var offenders = new List<string>();

            foreach (var file in EnumerateSourceFiles())
            {
                foreach (var declared in DeclaredNamespaces(file))
                {
                    if (declared == "FanaBridge.Customization"
                        || declared.StartsWith("FanaBridge.Customization.", StringComparison.Ordinal))
                    {
                        offenders.Add(RepoRelative(file));
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "The FanaBridge.Customization namespace was retired (→ FanaBridge.Display.Rules). "
                    + "Reintroduced in:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void FlatAdaptersNamespace_OnlyShrinks()
        {
            var current = EnumerateSourceFiles()
                .Where(f => DeclaredNamespaces(f).Contains("FanaBridge.Adapters"))
                .Select(RepoRelative)
                .ToList();

            var additions = current.Where(f => !AdaptersRatchetBaseline.Contains(f)).ToList();

            Assert.True(
                additions.Count == 0,
                "No new types may land in the flat FanaBridge.Adapters namespace (it is being "
                    + "dissolved — roadmap §6.4). New file(s) in FanaBridge.Adapters:\n"
                    + string.Join("\n", additions));
        }

        [Fact]
        public void EveryTestFile_DeclaresNamespaceMatchingItsFolder()
        {
            var violations = new List<string>();

            foreach (var file in EnumerateTestFiles())
            {
                var rel = RepoRelative(file);
                if (TestPathNamespaceAllowlist.Contains(rel))
                    continue;

                var expected = ExpectedNamespace(rel, "FanaBridge.Tests");
                foreach (var declared in DeclaredNamespaces(file))
                {
                    if (!string.Equals(declared, expected, StringComparison.Ordinal))
                        violations.Add($"{rel}: declares '{declared}', expected '{expected}'");
                }
            }

            Assert.True(
                violations.Count == 0,
                "Test namespace must equal FanaBridge.Tests.<folder path> (mirrors the "
                    + "production tree — 0g). Offenders (fix the namespace, move the file, or "
                    + "— only if truly intentional — add to the test allowlist with a comment):\n"
                    + string.Join("\n", violations));
        }

        [Fact]
        public void EveryXamlClass_SitsInNamespaceMatchingItsFolder()
        {
            var violations = new List<string>();

            foreach (var file in EnumerateXamlFiles())
            {
                var rel = RepoRelative(file);
                var m = XamlClass.Match(File.ReadAllText(file));
                if (!m.Success)
                    continue; // resource dictionaries etc. carry no x:Class

                var fqn = m.Groups[1].Value;
                var lastDot = fqn.LastIndexOf('.');
                var declaredNs = lastDot > 0 ? fqn.Substring(0, lastDot) : string.Empty;

                var expected = ExpectedNamespace(rel, "FanaBridge");
                if (!string.Equals(declaredNs, expected, StringComparison.Ordinal))
                    violations.Add($"{rel}: x:Class '{fqn}' sits in '{declaredNs}', expected '{expected}'");
            }

            Assert.True(
                violations.Count == 0,
                "XAML x:Class namespace must equal FanaBridge.<folder path> (path==namespace "
                    + "for markup too). Offenders:\n" + string.Join("\n", violations));
        }

        // --- helpers ---------------------------------------------------------

        private static IEnumerable<string> EnumerateSourceFiles()
        {
            var srcRoot = Path.Combine(RepoRoot(), "src");
            return Directory
                .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsGenerated(p));
        }

        private static IEnumerable<string> EnumerateTestFiles()
        {
            var testsRoot = Path.Combine(RepoRoot(), "tests");
            return Directory
                .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsGenerated(p));
        }

        private static IEnumerable<string> EnumerateXamlFiles()
        {
            var srcRoot = Path.Combine(RepoRoot(), "src");
            return Directory
                .EnumerateFiles(srcRoot, "*.xaml", SearchOption.AllDirectories)
                .Where(p => !IsGenerated(p));
        }

        private static bool IsGenerated(string path)
        {
            var norm = path.Replace('\\', '/');
            return norm.Contains("/obj/") || norm.Contains("/bin/");
        }

        private static IEnumerable<string> DeclaredNamespaces(string file)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var m = NamespaceLine.Match(line);
                if (m.Success)
                    yield return m.Groups[1].Value;
            }
        }

        // Repo-relative path with forward slashes, e.g. "src/FanaBridge.Core/Logging/Log.cs".
        private static string RepoRelative(string absolute)
        {
            var root = RepoRoot().Replace('\\', '/').TrimEnd('/');
            var norm = absolute.Replace('\\', '/');
            return norm.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(root.Length + 1)
                : norm;
        }

        // Expected namespace: rootNamespace + the directory segments below the project
        // folder. Both source projects (FanaBridge.Core and FanaBridge) map to the
        // FanaBridge root, so src/<project>/A/B/File.cs => FanaBridge.A.B; the single test
        // project maps to FanaBridge.Tests, so tests/<project>/A/B/File.cs => FanaBridge.Tests.A.B.
        private static string ExpectedNamespace(string repoRelative, string rootNamespace)
        {
            var segments = repoRelative.Split('/');
            // segments: [ "<top>", "<project>", dir..., "File.ext" ]
            var dirs = segments.Skip(2).Take(segments.Length - 3); // drop top, project, filename
            return string.Join(".", new[] { rootNamespace }.Concat(dirs));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FanaBridge.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Could not locate repo root (FanaBridge.sln) above " + AppContext.BaseDirectory);
        }
    }
}
