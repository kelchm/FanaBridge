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
    ///      shrink; any ADDITION fails and is named.
    /// Pure source scan, no compiled reflection: it reads the .cs files on disk so it
    /// stays honest even for types that never load in the net48 test host.
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

        [Fact]
        public void EveryFile_DeclaresNamespaceMatchingItsFolder()
        {
            var violations = new List<string>();

            foreach (var file in EnumerateSourceFiles())
            {
                var rel = RepoRelative(file);
                if (PathNamespaceAllowlist.Contains(rel))
                    continue;

                var expected = ExpectedNamespace(rel);
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

        // --- helpers ---------------------------------------------------------

        private static IEnumerable<string> EnumerateSourceFiles()
        {
            var srcRoot = Path.Combine(RepoRoot(), "src");
            return Directory
                .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
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

        // Expected namespace: "FanaBridge" + the directory segments below the project
        // folder. Both projects (FanaBridge.Core and FanaBridge) map to the FanaBridge
        // root, so src/<project>/A/B/File.cs => FanaBridge.A.B.
        private static string ExpectedNamespace(string repoRelative)
        {
            var segments = repoRelative.Split('/');
            // segments: [ "src", "<project>", dir..., "File.cs" ]
            var dirs = segments.Skip(2).Take(segments.Length - 3); // drop src, project, filename
            return string.Join(".", new[] { "FanaBridge" }.Concat(dirs));
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
