using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace FanaBridge.Tests.Contracts
{
    /// <summary>
    /// XAML counterpart of IDE0130: every <c>x:Class</c> under src must equal its
    /// project namespace root plus relative folder. C# layout is compiler-enforced.
    /// </summary>
    public class XamlClassLayoutTests
    {
        private static readonly (string Dir, string Root)[] Projects =
        {
            ("src/FanaBridge.Core", "FanaBridge.Core"),
            ("src/FanaBridge", "FanaBridge"),
            ("src/FanaBridge.Updater", "FanaBridge.Updater"),
            ("tests/FanaBridge.Tests", "FanaBridge.Tests"),
        };

        private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Fact]
        public void Xaml_xClass_matches_directory_layout()
        {
            string repoRoot = FindRepoRoot();
            var violations = new List<string>();
            int scanned = 0;

            foreach (var (projectRoot, nsRoot) in Projects)
            {
                string projectDir = Path.Combine(repoRoot, projectRoot.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(projectDir)) continue;

                foreach (string file in Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories))
                {
                    string norm = file.Replace('\\', '/');
                    if (norm.Contains("/bin/") || norm.Contains("/obj/")) continue;

                    string relative = file.Substring(repoRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');

                    List<string> classes;
                    try { classes = ExtractXamlClasses(File.ReadAllText(file)); }
                    catch (XmlException ex)
                    {
                        violations.Add($"{relative}: malformed XAML ({ex.Message})");
                        continue;
                    }
                    if (classes.Count == 0) continue;
                    scanned++;

                    string under = relative.Substring(projectRoot.Length).TrimStart('/');
                    int slash = under.LastIndexOf('/');
                    string dir = slash < 0 ? "" : under.Substring(0, slash);
                    string expected = string.IsNullOrEmpty(dir) ? nsRoot : nsRoot + "." + dir.Replace('/', '.');

                    foreach (string full in classes)
                    {
                        int dot = full.LastIndexOf('.');
                        string ns = dot <= 0 ? full : full.Substring(0, dot);
                        if (ns != expected)
                            violations.Add($"{relative}: x:Class '{full}' is in namespace '{ns}', expected '{expected}'");
                    }
                }
            }

            Assert.True(scanned > 0,
                "no XAML with x:Class found — the contract test is not scanning src/");
            Assert.True(violations.Count == 0,
                "XAML x:Class layout violations:\n" + string.Join("\n", violations));
        }

        private static List<string> ExtractXamlClasses(string xaml)
        {
            var list = new List<string>();
            var doc = XDocument.Parse(xaml, LoadOptions.None);
            if (doc.Root == null) return list;
            foreach (XAttribute attr in doc.Descendants().Attributes())
            {
                if (attr.Name != XamlNs + "Class" || string.IsNullOrEmpty(attr.Value)) continue;
                if (!list.Contains(attr.Value, StringComparer.Ordinal)) list.Add(attr.Value);
            }
            return list;
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
