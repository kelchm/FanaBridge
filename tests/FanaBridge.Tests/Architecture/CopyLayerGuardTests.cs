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
    ///     user copy in any v2-view file under <c>src/FanaBridge/UI/Display</c>;
    ///     FailMode=true from day one (surface is new). v1 views are excluded via an
    ///     explicit list that E9-exit deletes.
    /// (b) ruled-term presence — every NAMING PASS / shared-field ruled term appears
    ///     in DisplayCopy. NamespaceGuard folder conventions apply (this file lives
    ///     under Architecture/).
    /// </summary>
    public class CopyLayerGuardTests
    {
        // Fail from day one: the copy surface is new, so banned words are defects now.
        private const bool FailMode = true;

        private static readonly string DisplayCopyRelative =
            "src/FanaBridge/UI/Display/DisplayCopy.cs";

        private static readonly string DisplayUiRootRelative =
            "src/FanaBridge/UI/Display";

        // v1 view / edit-model files still in tree this round — E9-exit deletes them.
        // Banned-vocabulary scan skips these so the guard can enforce v2 views only.
        // v1 XAML counterparts are on the exclude list too (Text/Content/ToolTip scan).
        private static readonly string[] V1ViewExcludeFileNames =
        {
            "DisplayPagesView.xaml",
            "DisplayPagesView.xaml.cs",
            "DisplayPagesEditModel.cs",
            "DisplayTriggersView.xaml",
            "DisplayTriggersView.xaml.cs",
            "DisplayTriggersEditModel.cs",
            "DisplayVirtualPagesView.xaml",
            "DisplayVirtualPagesView.xaml.cs",
            "DisplayVirtualPagesEditModel.cs",
            "TriggerRuleSet.cs",
        };

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
        public void V2DisplayViews_BannedVocabulary_AbsentFromStringLiterals()
        {
            var root = Absolute(DisplayUiRootRelative);
            Assert.True(Directory.Exists(root), "Display UI root missing at " + DisplayUiRootRelative);

            var csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedV1View(f));
            var xamlFiles = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !IsExcludedV1View(f));
            var files = csFiles.Concat(xamlFiles)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Assert.NotEmpty(files);

            var hits = new List<string>();
            foreach (var path in files)
            {
                var text = File.ReadAllText(path);
                bool isXaml = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
                var literals = isXaml
                    ? ExtractXamlUserCopyLiterals(text)
                    : ExtractStringLiterals(text);
                string rel = RelFromRepo(path);

                foreach (var banned in BannedVocabulary)
                {
                    foreach (var lit in literals)
                    {
                        if (ContainsBannedAsCopy(lit, banned))
                            hits.Add($"{rel}: \"{banned}\" in {(isXaml ? "XAML attr" : "string literal")}: \"{Truncate(lit, 80)}\"");
                    }
                }
            }

            _output.WriteLine(
                $"Copy-layer banned scan: {files.Count} file(s), {hits.Count} hit(s) (FailMode={FailMode})");
            foreach (var h in hits)
                _output.WriteLine("  " + h);

            if (FailMode)
            {
                Assert.True(
                    hits.Count == 0,
                    "Banned vocabulary in v2 Display view string literals / XAML copy:\n"
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

        private static bool IsExcludedV1View(string absolutePath)
        {
            string name = Path.GetFileName(absolutePath);
            for (int i = 0; i < V1ViewExcludeFileNames.Length; i++)
            {
                if (string.Equals(name, V1ViewExcludeFileNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

        /// <summary>
        /// User-facing XAML copy: Text, Content, and ToolTip attribute string values
        /// (quoted). Bindings and empty values are included as-is; the banned check
        /// only flags real vocabulary hits.
        /// </summary>
        private static IEnumerable<string> ExtractXamlUserCopyLiterals(string xaml)
        {
            var matches = Regex.Matches(
                xaml,
                @"(?:Text|Content|ToolTip)\s*=\s*""([^""]*)""",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var list = new List<string>(matches.Count);
            foreach (Match m in matches)
            {
                if (m.Success && m.Groups.Count > 1)
                    list.Add(m.Groups[1].Value);
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

        private static string RelFromRepo(string absolutePath)
        {
            string root = RepoRoot();
            if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string rel = absolutePath.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel.Replace(Path.DirectorySeparatorChar, '/');
            }
            return absolutePath;
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
