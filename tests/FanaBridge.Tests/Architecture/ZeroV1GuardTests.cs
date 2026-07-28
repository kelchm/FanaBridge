using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace FanaBridge.Tests.Architecture
{
    /// <summary>
    /// Zero-v1 guard (engine-replan-v2.md §E8b / DOD-007). Report-only until E8b exit:
    /// inventory of retirement-manifest identifiers and v1-exclusive JSON parse targets
    /// still present in src non-test C#. Flips to fail when <see cref="FailMode"/> is true.
    /// </summary>
    public class ZeroV1GuardTests
    {
        // Flips to true at E8b exit — while false, tests emit inventory and PASS.
        private const bool FailMode = false;

        // RETIREMENT MANIFEST — engine-replan-v2.md §E8b "RETIREMENT MANIFEST".
        private static readonly string[] ManifestIdentifiers =
        {
            "DisplayCustomizationConfig",
            "ItmRuleSet",
            "LegacyRuleSet",
            "FieldMapping",
            "DisplayRule",
            "RuleCondition",
            "RuleTarget",
            "HoldSpec",
            "ConditionKind",
            "TargetKind",
            "HoldKind",
            "RuleEligibility",
            "LegacyScreen",
            "LegacyContentKind",
            "LegacyEffect",
            "DisplayConfigSerializer",
            "DisplayConfigValidator",
            "DisplayRuleEngine",
            "RuleEngineInput",
            "RuleEngineResult",
            "RuleIntent",
            "RuleStatus",
            "RuleLiveState",
            "DisplayRuleStack",
            "LegacyModeMigration",
            "DisplayTriggersView",
            "DisplayTriggersEditModel",
            "DisplayPagesView",
            "DisplayPagesEditModel",
            "DisplayVirtualPagesView",
            "DisplayVirtualPagesEditModel",
            "TriggerRuleSet",
        };

        // Keeper allowlist — must stay absent from the manifest.
        private static readonly string[] KeeperAllowlist =
        {
            "LegacyValueFormatter",
            "LegacyEffectClock",
            "LegacyDisplayDriver",
            "schemaVersion",
            "pages",
            "id",
        };

        // v1-exclusive JSON literals used as parse targets (string literals in src).
        private static readonly string[] GlobalJsonLiterals =
        {
            "segmentDisplay",
            "fieldMappings",
            "baseScreenId",
            "basePage",
            "contentKind",
            "inRotation",
        };

        // "show"/"when"/"hold" only in v1 rule-member parsing files.
        private static readonly string[] RuleMemberJsonLiterals = { "show", "when", "hold" };

        private static readonly HashSet<string> RuleMemberParseFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "src/FanaBridge.Core/Display/Rules/DisplayConfigSerializer.cs",
                "src/FanaBridge.Core/Display/Rules/DisplayRule.cs",
            };

        private readonly ITestOutputHelper _output;

        public ZeroV1GuardTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Manifest_DoesNotContainKeeperAllowlistNames()
        {
            var set = new HashSet<string>(ManifestIdentifiers, StringComparer.Ordinal);
            var offenders = KeeperAllowlist.Where(k => set.Contains(k)).ToList();
            Assert.True(
                offenders.Count == 0,
                "Keeper allowlist names must not appear on the retirement manifest: "
                    + string.Join(", ", offenders));
        }

        [Fact]
        public void IdentifierScan_ReportOnlyUntilE8b()
        {
            var hits = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var file in EnumerateSourceFiles())
            {
                var rel = RepoRelative(file);
                var code = StripCommentsAndStringsBestEffort(File.ReadAllText(file));
                foreach (var id in ManifestIdentifiers)
                {
                    var rx = new Regex(@"\b" + Regex.Escape(id) + @"\b");
                    if (rx.IsMatch(code))
                    {
                        if (!hits.TryGetValue(id, out var files))
                        {
                            files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                            hits[id] = files;
                        }
                        files.Add(rel);
                    }
                }
            }

            int fileCount = hits.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _output.WriteLine(
                $"ZeroV1 identifier scan: {hits.Count} identifiers across {fileCount} files (FailMode={FailMode})");
            foreach (var kv in hits)
            {
                _output.WriteLine($"  {kv.Key}: {kv.Value.Count} file(s)");
                foreach (var f in kv.Value)
                    _output.WriteLine($"    - {f}");
            }

            if (FailMode && hits.Count > 0)
            {
                Assert.True(
                    false,
                    "Retirement-manifest identifiers still present in src:\n"
                        + string.Join("\n", hits.Select(kv =>
                            kv.Key + ": " + string.Join(", ", kv.Value))));
            }
        }

        [Fact]
        public void JsonLiteralScan_ReportOnlyUntilE8b()
        {
            var hits = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var file in EnumerateSourceFiles())
            {
                var rel = RepoRelative(file);
                var text = File.ReadAllText(file);
                foreach (var lit in GlobalJsonLiterals)
                    CollectStringLiteralHits(text, lit, rel, hits);

                if (RuleMemberParseFiles.Contains(rel))
                {
                    foreach (var lit in RuleMemberJsonLiterals)
                        CollectStringLiteralHits(text, lit, rel, hits);
                }
            }

            int fileCount = hits.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _output.WriteLine(
                $"ZeroV1 JSON-literal scan: {hits.Count} literals across {fileCount} files (FailMode={FailMode})");
            foreach (var kv in hits)
            {
                _output.WriteLine($"  \"{kv.Key}\": {kv.Value.Count} file(s)");
                foreach (var f in kv.Value)
                    _output.WriteLine($"    - {f}");
            }

            if (FailMode && hits.Count > 0)
            {
                Assert.True(
                    false,
                    "v1-exclusive JSON literals still present in src:\n"
                        + string.Join("\n", hits.Select(kv =>
                            "\"" + kv.Key + "\": " + string.Join(", ", kv.Value))));
            }
        }

        private static void CollectStringLiteralHits(
            string text, string literal, string rel,
            SortedDictionary<string, SortedSet<string>> hits)
        {
            // Match "literal" as a C# string token (best-effort).
            var rx = new Regex("\"" + Regex.Escape(literal) + "\"");
            if (!rx.IsMatch(text))
                return;
            if (!hits.TryGetValue(literal, out var files))
            {
                files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                hits[literal] = files;
            }
            files.Add(rel);
        }

        /// <summary>
        /// Best-effort strip of // and /* */ comments and "..." / @"..." / $"" strings so
        /// identifier matches focus on code. Deterministic; not a full C# lexer.
        /// </summary>
        private static string StripCommentsAndStringsBestEffort(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                char n = i + 1 < src.Length ? src[i + 1] : '\0';

                if (c == '/' && n == '/')
                {
                    i += 2;
                    while (i < src.Length && src[i] != '\n')
                        i++;
                    continue;
                }
                if (c == '/' && n == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                        i++;
                    i = Math.Min(src.Length, i + 2);
                    continue;
                }
                if (c == '@' && n == '"')
                {
                    i += 2;
                    while (i < src.Length)
                    {
                        if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }
                        if (src[i] == '"')
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    sb.Append(' ');
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    while (i < src.Length)
                    {
                        if (src[i] == '\\' && i + 1 < src.Length)
                        {
                            i += 2;
                            continue;
                        }
                        if (src[i] == '"')
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    sb.Append(' ');
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

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

        private static string RepoRelative(string absolute)
        {
            var root = RepoRoot().Replace('\\', '/').TrimEnd('/');
            var norm = absolute.Replace('\\', '/');
            return norm.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(root.Length + 1)
                : norm;
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
