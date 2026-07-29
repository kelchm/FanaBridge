using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace FanaBridge.Tests.Architecture
{
    /// <summary>
    /// Architecture guard for the v2 Display copy layer (<c>DisplayCopy.cs</c>).
    /// (a) banned-vocabulary scan — "global", "rest", "waved off" must not appear as
    ///     user copy in any v2-view file under <c>src/FanaBridge/UI/Display</c>.
    /// (b) ruled-term presence — every NAMING PASS / shared-field ruled term appears
    ///     in DisplayCopy.
    /// (c) centralized-copy law (symbol centralization) —
    ///     <list type="bullet">
    ///     <item>XAML: every attribute (any quote style) and every text node (nested
    ///     included) via <see cref="XDocument"/> — prose (letters beyond the
    ///     structural-glyph allowlist) fails unless the value is a binding/resource
    ///     reference. Values must come from bindings or code-behind.</item>
    ///     <item>Code-behind: keep the literal-RHS ban on Text/Content/ToolTip/Header
    ///     assignments (statement must name <c>DisplayCopy</c>; value-membership alone
    ///     is not enough) AND ban <c>const</c>/<c>static</c> string field declarations
    ///     in v2 view files outside DisplayCopy (no local copy tables).</item>
    ///     </list>
    /// Honest limit: code-behind checks are text/regex only — they cannot prove symbol
    /// provenance beyond the statement (e.g. which assembly defined a named constant).
    /// Full symbol provenance needs a compiler. The runtime UI pass is the final copy check.
    /// NamespaceGuard folder conventions apply (this file lives under Architecture/).
    /// </summary>
    public class CopyLayerGuardTests
    {
        // Fail from day one: the copy surface is new, so banned words are defects now.
        private const bool FailMode = true;

        // Structural glyphs / empty / binding markers — not user prose.
        private static readonly HashSet<string> StructuralLiteralAllowlist =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "",
                " ",
                "·",
                "•",
                "—",
                "–",
                "›",
                "…",
                "↑",
                "↓",
                "×",
                "✕",
                "+",
                "-",
                "/",
                ":",
                "|",
                "(",
                ")",
            };

        private static readonly string DisplayCopyRelative =
            "src/FanaBridge/UI/Display/DisplayCopy.cs";

        private static readonly string DisplayUiRootRelative =
            "src/FanaBridge/UI/Display";

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

            var csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            var xamlFiles = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories);
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

        /// <summary>
        /// Symbol-centralization law: XAML user-visible attributes and element body
        /// prose allow no non-structural literals; code-behind assignments to those
        /// props must name <c>DisplayCopy</c> on the statement; v2 view files may not
        /// declare local const/static string copy tables.
        /// </summary>
        [Fact]
        public void V2DisplayViews_UserVisibleStrings_ComeFromDisplayCopy()
        {
            var copyPath = Absolute(DisplayCopyRelative);
            Assert.True(File.Exists(copyPath), "DisplayCopy.cs missing");

            var root = Absolute(DisplayUiRootRelative);
            var csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("DisplayCopy.cs", StringComparison.OrdinalIgnoreCase));
            var xamlFiles = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories);
            var files = csFiles.Concat(xamlFiles)
                .Where(IsV2ViewSurface)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Assert.NotEmpty(files);

            var hits = new List<string>();
            foreach (var path in files)
            {
                var text = File.ReadAllText(path);
                bool isXaml = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
                string rel = RelFromRepo(path);

                if (isXaml)
                {
                    // XDocument walk: every attribute + every text node (nested included).
                    foreach (var lit in ExtractXamlProseLiterals(text))
                    {
                        hits.Add(
                            $"{rel}: XAML prose (must be binding or code-behind): \"{Truncate(lit, 80)}\"");
                    }
                }
                else
                {
                    // Code-behind: each assignment to Text/Content/ToolTip/Header must
                    // reference DisplayCopy on the statement (no value-membership loophole).
                    foreach (var statement in ExtractCodeBehindUserCopyAssignments(text))
                    {
                        if (AssignmentIsStructuralOnly(statement))
                            continue;
                        if (statement.IndexOf("DisplayCopy", StringComparison.Ordinal) < 0)
                        {
                            hits.Add(
                                $"{rel}: user-visible assignment does not reference DisplayCopy: {Truncate(statement.Trim(), 120)}");
                        }
                    }

                    // No local copy tables in v2 view files (const/static string fields).
                    if (IsV2ViewCodeBehind(path))
                    {
                        foreach (var decl in ExtractConstOrStaticStringDeclarations(text))
                        {
                            if (DeclarationIsStructuralOnly(decl))
                                continue;
                            hits.Add(
                                $"{rel}: const/static string in v2 view (use DisplayCopy): {Truncate(decl.Trim(), 120)}");
                        }
                    }
                }
            }

            _output.WriteLine(
                $"Copy-layer symbol centralization: {files.Count} file(s), {hits.Count} hit(s) (FailMode={FailMode})");
            foreach (var h in hits)
                _output.WriteLine("  " + h);

            if (FailMode)
            {
                Assert.True(
                    hits.Count == 0,
                    "User-visible strings not symbol-centralized via DisplayCopy (v2 views):\n"
                        + string.Join("\n", hits));
            }
        }

        private static bool IsV2ViewSurface(string absolutePath)
        {
            string name = Path.GetFileName(absolutePath);
            // V2 views + diagnostics.
            if (name.IndexOf("V2", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.StartsWith("DisplayDiagnostics", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// Code-behind of a v2 view surface (not models/helpers) — const/static string
        /// tables are banned here; models may still hold structural keys.
        /// </summary>
        private static bool IsV2ViewCodeBehind(string absolutePath)
        {
            if (!absolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;
            string name = Path.GetFileName(absolutePath);
            if (name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
                return IsV2ViewSurface(absolutePath);
            // Bare *View.cs without XAML partner (none today) — still a view file.
            if (name.IndexOf("View", StringComparison.OrdinalIgnoreCase) >= 0
                && IsV2ViewSurface(absolutePath))
                return true;
            return false;
        }

        private static bool IsAllowlistedStructural(string lit)
        {
            if (lit == null)
                return true;
            if (StructuralLiteralAllowlist.Contains(lit))
                return true;
            // Pure whitespace
            if (string.IsNullOrWhiteSpace(lit))
                return true;
            return false;
        }

        /// <summary>
        /// Code-behind assignments to user-visible props. Captures the full RHS
        /// expression up to the terminating semicolon / comma so we can require a
        /// <c>DisplayCopy</c> symbol reference on the statement (not merely a value
        /// that happens to equal a DisplayCopy literal).
        /// </summary>
        private static IEnumerable<string> ExtractCodeBehindUserCopyAssignments(string src)
        {
            var list = new List<string>();
            // .Text = … / Text = … / ToolTip = … / Header = … / Content = …
            // Stop at ; or , that ends the assignment (object-initializer / statement).
            var matches = Regex.Matches(
                src,
                @"(?:\.Text|\.Content|\.ToolTip|\.Header|ToolTip|Header|(?<![\w.])Text|(?<![\w.])Content)\s*=\s*([^;,\n]+)",
                RegexOptions.CultureInvariant);
            foreach (Match m in matches)
            {
                if (m.Success && m.Groups.Count > 1)
                    list.Add(m.Value);
            }
            return list;
        }

        /// <summary>
        /// True when the assignment RHS is only structural (allowlisted literal,
        /// null, empty, or a non-prose expression with no string literal body).
        /// </summary>
        private static bool AssignmentIsStructuralOnly(string statement)
        {
            // Extract string literals on the RHS of '='.
            int eq = statement.IndexOf('=');
            if (eq < 0)
                return true;
            string rhs = statement.Substring(eq + 1).Trim();
            if (rhs.Length == 0 || rhs == "null")
                return true;

            var lits = ExtractStringLiterals(rhs).ToList();
            if (lits.Count == 0)
            {
                // No string literal — e.g. DisplayCopy.Foo, someBinding, model.Bar.
                // These are fine; the DisplayCopy name check still applies when the
                // RHS is not null/structural. Pure non-literal expressions (model props)
                // are allowed without DisplayCopy (they are not authored copy).
                return true;
            }

            // Every literal on the RHS must be structural.
            foreach (var lit in lits)
            {
                if (!IsAllowlistedStructural(lit))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when a const/static string declaration's literal body is only structural
        /// (glyph / empty / pure whitespace) — not a local copy table entry.
        /// </summary>
        private static bool DeclarationIsStructuralOnly(string declaration)
        {
            var lits = ExtractStringLiterals(declaration).ToList();
            if (lits.Count == 0)
                return true;
            foreach (var lit in lits)
            {
                if (!IsAllowlistedStructural(lit))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Field declarations of the form <c>const string X = "…"</c> or
        /// <c>static readonly string X = "…"</c> (and static string variants).
        /// Captures the whole declaration line-ish for reporting.
        /// </summary>
        private static IEnumerable<string> ExtractConstOrStaticStringDeclarations(string src)
        {
            var list = new List<string>();
            // const string Name = "…";
            // static readonly string Name = "…";
            // static string Name = "…";
            // private/public/internal/protected optional; multi-space tolerant.
            var matches = Regex.Matches(
                src,
                @"(?:(?:public|private|internal|protected|static|readonly|const)\s+)+string\s+\w+\s*=\s*[^;]+;",
                RegexOptions.CultureInvariant);
            foreach (Match m in matches)
            {
                if (m.Success)
                    list.Add(m.Value);
            }
            return list;
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
        /// Prose literals in v2 view XAML: every attribute value and every text node
        /// (nested included) that has letters beyond the structural-glyph allowlist and
        /// is not a binding/resource reference. Parsed via <see cref="XDocument"/> so
        /// quote style and nesting cannot hide copy.
        /// </summary>
        private static IEnumerable<string> ExtractXamlProseLiterals(string xaml)
        {
            var list = new List<string>();
            foreach (var (value, userVisible) in EnumerateXamlStringValues(xaml))
            {
                if (IsXamlProse(value, userVisible))
                    list.Add(value);
            }
            return list;
        }

        /// <summary>
        /// All attribute values + text-node values from a v2 XAML document (raw, for
        /// banned-vocabulary scan). Bindings and empties included as-is.
        /// </summary>
        private static IEnumerable<string> ExtractXamlUserCopyLiterals(string xaml)
        {
            foreach (var (value, _) in EnumerateXamlStringValues(xaml))
                yield return value;
        }

        /// <summary>
        /// Walk every attribute (any quote style) and every text node (nested included).
        /// </summary>
        // Attributes whose values face the user: PascalCase single tokens there are
        // still prose ("Unit", "Speed"); everywhere else they are technical tokens
        // (enum values, event handler names).
        private static readonly HashSet<string> UserVisibleAttributes =
            new HashSet<string>(StringComparer.Ordinal)
            { "Text", "Content", "ToolTip", "Header", "Title", "Watermark", "Tag" };

        private static IEnumerable<(string Value, bool UserVisible)> EnumerateXamlStringValues(string xaml)
        {
            var list = new List<(string, bool)>();
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                // Malformed XAML is a build failure elsewhere; surface as empty so the
                // guard does not throw mid-suite.
                return list;
            }

            foreach (var el in doc.Descendants())
            {
                foreach (var attr in el.Attributes())
                {
                    // xmlns / x:Class are document infrastructure, not copy surface.
                    if (attr.IsNamespaceDeclaration)
                        continue;
                    if (attr.Name.LocalName == "Class"
                        && attr.Name.NamespaceName == "http://schemas.microsoft.com/winfx/2006/xaml")
                        continue;
                    list.Add((attr.Value, UserVisibleAttributes.Contains(attr.Name.LocalName)));
                }
            }

            foreach (var node in doc.DescendantNodes())
            {
                // Text nodes are rendered content — always a user-visible position.
                if (node is XText text)
                {
                    string v = text.Value;
                    if (v != null)
                        list.Add((v, true));
                }
            }

            return list;
        }

        /// <summary>
        /// True when a string is user prose under the centralized-copy law: has letters
        /// beyond the structural-glyph allowlist, and is not a binding/resource reference.
        /// Technical XAML tokens (PascalCase enums, event handlers, URIs, hex colors) are
        /// not prose; plain lowercase words ("unit") and multi-word strings are.
        /// </summary>
        private static bool IsXamlProse(string value, bool userVisiblePosition = false)
        {
            if (value == null)
                return false;
            string trimmed = value.Trim();
            if (IsAllowlistedStructural(trimmed) || IsAllowlistedStructural(value))
                return false;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (IsBindingOrResourceReference(trimmed))
                return false;
            if (!ContainsLetter(value))
                return false;

            // Hex colors (#RGB / #RRGGBB / #AARRGGBB) — letters A–F are not prose.
            if (IsHexColor(trimmed))
                return false;

            // Multi-word / spaced content with letters → always prose.
            if (value.IndexOf(' ') >= 0 || value.IndexOf('\t') >= 0 || value.IndexOf('\n') >= 0)
                return true;

            // Document / type-system tokens.
            if (trimmed.IndexOf("://", StringComparison.Ordinal) >= 0)
                return false;
            if (trimmed.StartsWith("clr-namespace:", StringComparison.Ordinal))
                return false;
            if (trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                return false;

            // User-visible positions (Text/ToolTip/… attributes, rendered text nodes)
            // get NO single-token exemption: "Unit" and <Run>Speed</Run> are prose there.
            if (userVisiblePosition)
                return true;

            // Single token in a TECHNICAL position: all-lowercase letters only → prose.
            // PascalCase / mixed / underscores / digits → technical (Horizontal, Click_Handler).
            bool anyUpper = false, anyLower = false, anyNonLetter = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (char.IsUpper(c))
                    anyUpper = true;
                else if (char.IsLower(c))
                    anyLower = true;
                else if (!char.IsLetter(c))
                    anyNonLetter = true;
            }
            if (anyLower && !anyUpper && !anyNonLetter)
                return true;

            return false;
        }

        private static bool IsBindingOrResourceReference(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            if (value[0] == '{')
                return true; // markup extension / binding / StaticResource
            if (value.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool ContainsLetter(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i]))
                    return true;
            }
            return false;
        }

        private static bool IsHexColor(string s)
        {
            if (s.Length < 4 || s[0] != '#')
                return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
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
