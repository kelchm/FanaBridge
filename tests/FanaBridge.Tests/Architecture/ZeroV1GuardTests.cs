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
    /// Zero-v1 guard (engine-replan-v2.md §E8b / DOD-007). Two tiers:
    /// <list type="bullet">
    /// <item><b>DELETED-NOW</b> — engine/schema burn identifiers; FailMode=true (must be
    /// absent from src after E8b).</item>
    /// <item><b>UI-EXIT</b> — types the v1 views still reference until E9-exit; report-only
    /// (FailMode=false) until that gate.</item>
    /// </list>
    /// </summary>
    public class ZeroV1GuardTests
    {
        // DELETED-NOW tier: engine/schema burn-down — must be empty after E8b.
        private const bool FailModeDeletedNow = true;

        // UI-EXIT tier: report-only until E9-exit.
        private const bool FailModeUiExit = false;

        /// <summary>
        /// Engine/schema identifiers deleted at E8b. FailModeDeletedNow requires ABSENCE.
        /// </summary>
        private static readonly string[] DeletedNowIdentifiers =
        {
            "DisplayRuleEngine",
            "RuleEngineInput",
            "RuleEngineResult",
            "RuleIntent",
            "RuleLiveState",
            "DisplayRuleStack",
            "LegacyModeMigration",
            "DisplayActionHub",
            "DisplayRuleCarrierAdapter",
        };

        /// <summary>
        /// UI-coupled retirement-manifest identifiers retained until E9-exit.
        /// RuleStatus + DisplayRuleSnapshot stay with the v1 Overview/Triggers surface.
        /// </summary>
        private static readonly string[] UiExitIdentifiers =
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
            "RuleStatus",
            "DisplayTriggersView",
            "DisplayTriggersEditModel",
            "DisplayPagesView",
            "DisplayPagesEditModel",
            "DisplayVirtualPagesView",
            "DisplayVirtualPagesEditModel",
            "TriggerRuleSet",
        };

        // Keeper allowlist — must stay absent from both manifests.
        private static readonly string[] KeeperAllowlist =
        {
            "LegacyValueFormatter",
            "LegacyEffectClock",
            "LegacyDisplayDriver",
            "schemaVersion",
            "pages",
            "id",
        };

        // v1-exclusive JSON literals — UI-EXIT / serializer-coupled until E9-exit.
        private static readonly string[] GlobalJsonLiterals =
        {
            "segmentDisplay",
            "fieldMappings",
            "baseScreenId",
            "basePage",
            "contentKind",
            "inRotation",
        };

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
            var set = new HashSet<string>(
                DeletedNowIdentifiers.Concat(UiExitIdentifiers), StringComparer.Ordinal);
            var offenders = KeeperAllowlist.Where(k => set.Contains(k)).ToList();
            Assert.True(
                offenders.Count == 0,
                "Keeper allowlist names must not appear on the retirement manifest: "
                    + string.Join(", ", offenders));
        }

        [Fact]
        public void DeletedNow_IdentifierScan_MustBeEmpty()
        {
            var hits = ScanIdentifiers(DeletedNowIdentifiers);
            Report("DELETED-NOW identifier", hits, FailModeDeletedNow);
            if (FailModeDeletedNow && hits.Count > 0)
            {
                Assert.Fail(
                    "DELETED-NOW retirement-manifest identifiers still present in src:\n"
                        + string.Join("\n", hits.Select(kv =>
                            kv.Key + ": " + string.Join(", ", kv.Value))));
            }
        }

        [Fact]
        public void UiExit_IdentifierScan_ReportOnlyUntilE9()
        {
            var hits = ScanIdentifiers(UiExitIdentifiers);
            Report("UI-EXIT identifier", hits, FailModeUiExit);
            if (FailModeUiExit && hits.Count > 0)
            {
                Assert.Fail(
                    "UI-EXIT retirement-manifest identifiers still present in src:\n"
                        + string.Join("\n", hits.Select(kv =>
                            kv.Key + ": " + string.Join(", ", kv.Value))));
            }
        }

        [Fact]
        public void JsonLiteralScan_ReportOnlyUntilE9()
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

            Report("UI-EXIT JSON-literal", hits, FailModeUiExit);
            if (FailModeUiExit && hits.Count > 0)
            {
                Assert.Fail(
                    "v1-exclusive JSON literals still present in src:\n"
                        + string.Join("\n", hits.Select(kv =>
                            "\"" + kv.Key + "\": " + string.Join(", ", kv.Value))));
            }
        }

        private SortedDictionary<string, SortedSet<string>> ScanIdentifiers(string[] ids)
        {
            var hits = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var file in EnumerateSourceFiles())
            {
                var rel = RepoRelative(file);
                var code = StripCommentsAndStringsBestEffort(File.ReadAllText(file));
                foreach (var id in ids)
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
            return hits;
        }

        private void Report(
            string label, SortedDictionary<string, SortedSet<string>> hits, bool failMode)
        {
            int fileCount = hits.Values
                .SelectMany(s => s)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            _output.WriteLine(
                $"ZeroV1 {label} scan: {hits.Count} identifiers across {fileCount} files "
                + $"(FailMode={failMode})");
            foreach (var kv in hits)
            {
                _output.WriteLine($"  {kv.Key}: {kv.Value.Count} file(s)");
                foreach (var f in kv.Value)
                    _output.WriteLine($"    - {f}");
            }
        }

        private static void CollectStringLiteralHits(
            string text, string literal, string rel,
            SortedDictionary<string, SortedSet<string>> hits)
        {
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
