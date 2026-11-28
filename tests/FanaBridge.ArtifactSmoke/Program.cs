using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Resources;

namespace FanaBridge.ArtifactSmoke
{
    /// <summary>
    /// Contract check for the merged plugin artifact (#104). Loads the exact
    /// ILRepack output in this process, reaching it only through reflection, and
    /// checks what the merge can break but the unit suite (which runs against
    /// the separate development assemblies) cannot see: the plugin entry type,
    /// the embedded wheel profiles, the WPF resource keys, and WPF construction
    /// of the plugin's controls. Exit code is the number of failed checks.
    ///
    /// Usage: FanaBridge.ArtifactSmoke.exe &lt;merged FanaBridge.dll&gt; [SimHub dir]
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static readonly List<string> UnmergedRequests = new List<string>();
        private static Assembly? _plugin;
        private static string _simHubDir = "";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2)
            {
                Console.Error.WriteLine("Usage: FanaBridge.ArtifactSmoke.exe <merged FanaBridge.dll> [SimHub dir]");
                return 2;
            }

            string merged = Path.GetFullPath(args[0]);
            _simHubDir = args.Length > 1 ? args[1] : Metadata("SimHubDir");
            Console.WriteLine("Merged artifact: " + merged);
            Console.WriteLine("SimHub assemblies: " + _simHubDir);
            if (!File.Exists(merged))
            {
                Console.Error.WriteLine("Merged artifact not found. Build with -p:MergePlugin=true first.");
                return 2;
            }
            if (string.IsNullOrWhiteSpace(_simHubDir) || !File.Exists(Path.Combine(_simHubDir, "SimHub.Plugins.dll")))
            {
                Console.Error.WriteLine("SimHub assemblies not found in '" + _simHubDir +
                                        "'. Pass the SimHub install directory as the second argument.");
                return 2;
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;

            Check("Merged assembly", () => LoadMerged(merged));
            if (_plugin != null)
            {
                Check("Plugin entry type", CheckPluginEntry);
                Check("Built-in wheel profiles", CheckProfiles);
                Check("WPF resource keys", CheckWpfResources);
                Check("WPF construction: SettingsControl", ConstructSettingsControl);
                Check("WPF construction: ScreenSettingsPanel", () => Construct("FanaBridge.UI.Settings.ScreenSettingsPanel"));
                Check("WPF construction: TuningSettingsPanel", () => Construct("FanaBridge.UI.Settings.TuningSettingsPanel"));
                Check("WPF construction: LedColorLimitationNotice", ConstructLedNotice);
                Check("WPF construction: WheelProfileWizardDialog", ConstructWizard);
            }

            // Any check can pull in an assembly the merge should have folded in.
            if (UnmergedRequests.Count > 0)
                Fail("Unmerged dependencies", "the artifact asked for " +
                     string.Join(", ", UnmergedRequests.Distinct()) +
                     ", which the merge should have folded into FanaBridge.dll");

            Console.WriteLine();
            if (Failures.Count == 0)
            {
                Console.WriteLine("Merged artifact contract: all checks passed.");
                return 0;
            }

            Console.WriteLine("Merged artifact contract: " + Failures.Count + " check(s) failed.");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                foreach (string failure in Failures)
                    Console.WriteLine("::error title=Merged artifact contract::" + failure);
            }
            return Failures.Count;
        }

        // ── Checks ───────────────────────────────────────────────────────

        private static string LoadMerged(string merged)
        {
            // Load a private copy from a directory holding nothing else, so no
            // development assembly can be probed next to it, and the profile
            // store's user directory (created beside the DLL) starts empty.
            // Read the input first: it may be the copy a previous run left there.
            byte[] image = File.ReadAllBytes(merged);
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifact-under-test");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, Path.GetFileName(merged));
            File.WriteAllBytes(copy, image);

            var asm = Assembly.LoadFrom(copy);
            if (asm.GetName().Name != "FanaBridge")
                throw new SmokeFailure("assembly name is '" + asm.GetName().Name + "', expected 'FanaBridge'");

            var unmergedRefs = asm.GetReferencedAssemblies()
                .Where(r => r.Name.StartsWith("FanaBridge.", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name)
                .ToList();
            if (unmergedRefs.Count > 0)
                throw new SmokeFailure("still references " + string.Join(", ", unmergedRefs) +
                                       "; the merge did not fold them in");

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                var reasons = ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct();
                throw new SmokeFailure("types failed to load: " + string.Join("; ", reasons));
            }

            foreach (string ns in new[] { "FanaBridge.Core", "FanaBridge.Updater" })
            {
                if (!types.Any(t => t.Namespace != null && (t.Namespace == ns || t.Namespace.StartsWith(ns + "."))))
                    throw new SmokeFailure("no types from " + ns + "; it was not merged in");
            }

            _plugin = asm;
            return types.Length + " types, " + asm.GetName().Version;
        }

        private static string CheckPluginEntry()
        {
            Type plugin = RequireType("FanaBridge.FanatecPlugin");
            Type iPlugin = Type.GetType("SimHub.Plugins.IPlugin, SimHub.Plugins", throwOnError: true)!;
            if (!plugin.IsPublic || plugin.IsAbstract)
                throw new SmokeFailure(plugin.FullName + " must be a public, non-abstract class");
            if (!iPlugin.IsAssignableFrom(plugin))
                throw new SmokeFailure(plugin.FullName + " does not implement " + iPlugin.FullName);

            var nameAttr = plugin.GetCustomAttributes(inherit: false)
                .FirstOrDefault(a => a.GetType().FullName == "SimHub.Plugins.PluginNameAttribute");
            if (nameAttr == null)
                throw new SmokeFailure(plugin.FullName + " has no [PluginName]");

            Activator.CreateInstance(plugin);
            return "implements IPlugin, constructs";
        }

        private static string CheckProfiles()
        {
            var expected = MetadataList("ExpectedProfiles");
            if (expected.Count == 0)
                throw new SmokeFailure("no expected profiles were embedded in this harness at build time");

            Type store = RequireType("FanaBridge.Core.Devices.Profiles.WheelProfileStore");
            Invoke(store, "Reload");
            var loaded = ((IEnumerable)Invoke(store, "GetAll")!).Cast<object>()
                .Where(p => Get(p, "Source")?.ToString() == "BuiltIn")
                .ToList();
            var loadedByResource = loaded.ToDictionary(p => (string)Get(p, "SourcePath")!, p => (string)Get(p, "Id")!);

            string[] resources = _plugin!.GetManifestResourceNames();
            var problems = new List<string>();
            foreach (string file in expected)
            {
                string? resource = resources.FirstOrDefault(r =>
                    r.EndsWith(".Profiles." + file, StringComparison.OrdinalIgnoreCase));
                if (resource == null)
                    problems.Add(file + ": no embedded resource");
                else if (!loadedByResource.ContainsKey(resource))
                    problems.Add(file + ": embedded as " + resource + " but did not load as a built-in profile");
            }
            if (loaded.Count != expected.Count)
                problems.Add("loaded " + loaded.Count + " built-in profiles, expected " + expected.Count);

            if (problems.Count > 0)
                throw new SmokeFailure(string.Join("; ", problems));
            return loaded.Count + " built-in profiles loaded";
        }

        private static string CheckWpfResources()
        {
            var xaml = MetadataList("ExpectedXaml");
            if (xaml.Count == 0)
                throw new SmokeFailure("no expected XAML files were embedded in this harness at build time");

            var resources = MetadataList("ExpectedWpfResources");
            if (resources.Count == 0)
                throw new SmokeFailure("no <Resource> items were read from FanaBridge.csproj at build time");

            // Each compiled page as BAML, plus each <Resource> item such as the
            // plugin icon.
            var expected = xaml.Select(x => Path.ChangeExtension(x, ".baml"))
                .Concat(resources)
                .Select(NormalizeResourceId)
                .ToList();

            const string container = "FanaBridge.g.resources";
            var keys = new List<string>();
            using (var stream = _plugin!.GetManifestResourceStream(container)
                                ?? throw new SmokeFailure("no " + container + " manifest resource"))
            using (var reader = new ResourceReader(stream))
            {
                foreach (DictionaryEntry entry in reader)
                    keys.Add((string)entry.Key);
            }

            var present = new HashSet<string>(keys.Select(NormalizeResourceId), StringComparer.Ordinal);
            var missing = expected.Where(k => !present.Contains(k)).ToList();
            if (missing.Count > 0)
                throw new SmokeFailure("missing from " + container + ": " + string.Join(", ", missing) +
                                       " (present: " + string.Join(", ", keys) + ")");
            return expected.Count + " resource keys present";
        }

        // WPF stores each resource under a lower-cased, URI-escaped form of its
        // project-relative path. Comparing both sides unescaped matches it
        // without re-implementing WPF's exact escaping (a space, '#' or a
        // literal "%20" in a file name).
        private static string NormalizeResourceId(string id) =>
            Uri.UnescapeDataString(id.Replace('\\', '/')).ToLowerInvariant();

        private static string Construct(string typeName, params object[] ctorArgs)
        {
            Type type = RequireType(typeName);
            Activator.CreateInstance(type, ctorArgs);
            return "constructed";
        }

        private static string ConstructLedNotice()
        {
            Type caps = RequireType("FanaBridge.Core.Devices.Profiles.WheelCapabilities");
            object none = (caps.GetProperty("None", BindingFlags.Public | BindingFlags.Static)
                           ?? throw new SmokeFailure(caps.FullName + ".None is missing")).GetValue(null);
            var resolve = Expression.Lambda(
                typeof(Func<>).MakeGenericType(caps), Expression.Constant(none, caps)).Compile();
            return Construct("FanaBridge.UI.Devices.LedColorLimitationNotice", resolve);
        }

        private static string ConstructSettingsControl() =>
            // The constructor SimHub's settings page uses (GetWPFSettingsControl).
            Construct("FanaBridge.UI.Settings.SettingsControl", PluginWithWheelbase());

        private static string ConstructWizard() =>
            Construct("FanaBridge.UI.Wizard.WheelProfileWizardDialog", PluginWithWheelbase());

        // A plugin with an unconnected wheelbase. The wizard reads the wheel
        // identity from it, and it only exists after Init, so install one the
        // same way the unit suite does.
        private static object PluginWithWheelbase()
        {
            Type pluginType = RequireType("FanaBridge.FanatecPlugin");
            object plugin = Activator.CreateInstance(pluginType);
            object wheelbase = Activator.CreateInstance(RequireType("FanaBridge.Core.Devices.FanatecWheelbase"));
            RequireMethod(pluginType, "InstallWheelbaseForTest", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(plugin, new[] { wheelbase, false });
            return plugin;
        }

        // ── Plumbing ─────────────────────────────────────────────────────

        private static void Check(string name, Func<string> check)
        {
            try
            {
                Console.WriteLine("PASS  " + name + ": " + check());
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
        }

        private static void Fail(string name, string reason)
        {
            Console.WriteLine("FAIL  " + name + ": " + reason);
            Failures.Add(name + ": " + reason.Split('\n')[0].Trim());
        }

        private static string Describe(Exception ex)
        {
            while (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;
            return ex is SmokeFailure ? ex.Message : ex.ToString();
        }

        private static Assembly? Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            // Satellite lookups (e.g. FanaBridge.resources) are WPF and the
            // resource manager probing for localized resources; none ship.
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(name, "FanaBridge", StringComparison.OrdinalIgnoreCase))
                return _plugin;
            if (name.StartsWith("FanaBridge.", StringComparison.OrdinalIgnoreCase))
            {
                lock (UnmergedRequests) UnmergedRequests.Add(name);
                return null;
            }

            // SimHub's own assemblies, at whatever version the install ships
            // (SimHub's host config would redirect them the same way).
            foreach (string ext in new[] { ".dll", ".exe" })
            {
                string path = Path.Combine(_simHubDir, name + ext);
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            return null;
        }

        private static Type RequireType(string fullName) =>
            _plugin!.GetType(fullName, throwOnError: false)
            ?? throw new SmokeFailure(fullName + " is not in the merged assembly");

        private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags) =>
            type.GetMethod(name, flags)
            ?? throw new SmokeFailure(type.FullName + "." + name + " is missing; this check calls it by name");

        private static object? Invoke(Type type, string method) =>
            (type.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
             ?? throw new SmokeFailure(type.FullName + "." + method + "() is missing; this check calls it by name"))
                .Invoke(null, null);

        private static object? Get(object target, string property) =>
            (target.GetType().GetProperty(property)
             ?? throw new SmokeFailure(target.GetType().FullName + "." + property + " is missing; this check reads it by name"))
                .GetValue(target);

        private static string Metadata(string key) =>
            typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value ?? "";

        private static List<string> MetadataList(string key) =>
            Metadata(key).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

        /// <summary>A contract violation whose message is already the diagnostic.</summary>
        private sealed class SmokeFailure : Exception
        {
            public SmokeFailure(string message) : base(message) { }
        }
    }
}
