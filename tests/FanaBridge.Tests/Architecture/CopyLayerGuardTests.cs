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
    /// Architecture guard for the v2 Display copy layer (<c>DisplayCopy.cs</c>).
    /// (a) banned-vocabulary scan — "global", "rest", "waved off" must not appear as
    ///     user copy; FailMode=true from day one (surface is new).
    /// (b) ruled-term presence — every NAMING PASS / shared-field ruled term appears
    ///     in the table. NamespaceGuard folder conventions apply (this file lives under
    ///     Architecture/).
    /// </summary>
    public class CopyLayerGuardTests
    {
        // Fail from day one: the copy surface is new, so banned words are defects now.
        private const bool FailMode = true;

        private static readonly string DisplayCopyRelative =
            "src/FanaBridge/UI/Display/DisplayCopy.cs";

        // Banned as user copy (DECISIONS §7e + field-filter ruling).
        private static readonly string[] BannedVocabulary =
        {
            "global",
            "rest",
            "waved off",
        };

        // Every ruled term must appear as a string literal (or format fragment) in DisplayCopy.
        private static readonly string[] RuledTerms =
        {
            "Acts as an entrypoint",
            "↑",
            "override",
            "layer",
            "Priority",
            "Rotation",
            "off-rotation",
            "Manual paging",
            "waiting",
            "outranked",
            "off-screen",
            "OFF",
            "DISMISSED",
            "CAN'T RUN HERE",
            "ITM",
            "Legacy Only",
            "Off",
            "On",
            "on Legacy",
            "LEGACY",
            "Base page",
            "base",
            "cycle (2+ pages)",
            "cycle",
            "Shared",
            "appears on every ITM page",
            "of {2} ITM pages",
            "Show all fields",
            "Showing {0} ({1} of {2})",
        };

        private readonly ITestOutputHelper _output;

        public CopyLayerGuardTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void DisplayCopy_BannedVocabulary_AbsentFromStringLiterals()
        {
            var path = Absolute(DisplayCopyRelative);
            Assert.True(File.Exists(path), "DisplayCopy.cs missing at " + DisplayCopyRelative);

            var text = File.ReadAllText(path);
            var literals = ExtractStringLiterals(text);
            var hits = new List<string>();

            foreach (var banned in BannedVocabulary)
            {
                foreach (var lit in literals)
                {
                    if (ContainsBannedAsCopy(lit, banned))
                        hits.Add($"\"{banned}\" in string literal: \"{Truncate(lit, 80)}\"");
                }
            }

            _output.WriteLine(
                $"Copy-layer banned scan: {hits.Count} hit(s) (FailMode={FailMode})");
            foreach (var h in hits)
                _output.WriteLine("  " + h);

            if (FailMode)
            {
                Assert.True(
                    hits.Count == 0,
                    "Banned vocabulary in DisplayCopy.cs user copy:\n"
                        + string.Join("\n", hits));
            }
        }

        [Fact]
        public void DisplayCopy_RuledTerms_AllPresent()
        {
            var path = Absolute(DisplayCopyRelative);
            Assert.True(File.Exists(path), "DisplayCopy.cs missing at " + DisplayCopyRelative);

            var text = File.ReadAllText(path);
            var missing = RuledTerms.Where(t => !text.Contains(t)).ToList();

            _output.WriteLine(
                $"Ruled-term presence: {RuledTerms.Length - missing.Count}/{RuledTerms.Length} present");
            foreach (var m in missing)
                _output.WriteLine("  missing: " + m);

            Assert.True(
                missing.Count == 0,
                "Ruled terms missing from DisplayCopy.cs:\n" + string.Join("\n", missing));
        }

        /// <summary>
        /// "rest" is a whole word (not "entrypoint"); "global" / "waved off" are substring
        /// matches case-insensitive inside a string literal body.
        /// </summary>
        private static bool ContainsBannedAsCopy(string literal, string banned)
        {
            if (string.Equals(banned, "rest", StringComparison.OrdinalIgnoreCase))
            {
                // Whole-word only — avoid false positives on "entrypoint", "interest", etc.
                return Regex.IsMatch(
                    literal,
                    @"\brest\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            return literal.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> ExtractStringLiterals(string src)
        {
            var list = new List<string>();
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                char n = i + 1 < src.Length ? src[i + 1] : '\0';

                // Skip // comments
                if (c == '/' && n == '/')
                {
                    i += 2;
                    while (i < src.Length && src[i] != '\n')
                        i++;
                    continue;
                }

                // Skip /* */ comments
                if (c == '/' && n == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                        i++;
                    i = Math.Min(src.Length, i + 2);
                    continue;
                }

                // @"..." verbatim
                if (c == '@' && n == '"')
                {
                    i += 2;
                    var start = i;
                    while (i < src.Length)
                    {
                        if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }
                        if (src[i] == '"')
                        {
                            list.Add(src.Substring(start, i - start).Replace("\"\"", "\""));
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // "..." ordinary (incl. interpolated $"" bodies as best-effort)
                if (c == '"')
                {
                    i++;
                    var start = i;
                    while (i < src.Length)
                    {
                        if (src[i] == '\\' && i + 1 < src.Length)
                        {
                            i += 2;
                            continue;
                        }
                        if (src[i] == '"')
                        {
                            list.Add(UnescapeOrdinary(src.Substring(start, i - start)));
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                i++;
            }

            return list;
        }

        private static string UnescapeOrdinary(string s)
            => s.Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";

        private static string Absolute(string repoRelative)
            => Path.Combine(RepoRoot(), repoRelative.Replace('/', Path.DirectorySeparatorChar));

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
