using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace FanaBridge.Tests.Contracts
{
    /// <summary>
    /// Guards against drift in the public enum metadata of SimHub's
    /// SimHub.FanatecManaged.dll. FanaBridge no longer references that assembly,
    /// but its Control Mapper variant ids ("FS_WHEEL_SWTYPE_&lt;code&gt;") must stay
    /// name-compatible with the stock enum members so existing per-wheel mappings
    /// keep resolving (see FanaBridgeVariantProvider.StockWheelSuffixOverrides).
    /// The DLL's public enums are read via reflection and compared against a
    /// committed snapshot; a SimHub update that adds or renames wheel ids fails
    /// this test so the divergence gets reviewed instead of slipping by.
    /// </summary>
    public class SimHubEnumSnapshotTests
    {
        private const string DllName = "SimHub.FanatecManaged.dll";
        // Fixture stays at tests/FanaBridge.Tests/Snapshots/ (project root); csproj copies it to output.
        private const string SnapshotRelativePath = "Snapshots\\SimHub.FanatecManaged.enums.txt";

        [SkippableFact]
        public void PublicEnums_MatchCommittedSnapshot()
        {
            string simHubDir = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SimHubDir")?.Value ?? "";
            Assert.False(string.IsNullOrWhiteSpace(simHubDir), "SimHubDir assembly metadata missing from test assembly.");

            string dllPath = Path.Combine(simHubDir, DllName);
            if (!File.Exists(dllPath))
            {
                // CI stages a full SimHub install, so a missing DLL there is a
                // broken pipeline, never a reason to silently skip.
                Assert.False(
                    Environment.GetEnvironmentVariable("CI") == "true",
                    $"{DllName} not found at {dllPath} in CI; the staged SimHub install is incomplete.");
                Skip.If(true, $"{DllName} not found at {dllPath}; install SimHub or set SimHubDir in Directory.Build.props.user.");
            }

            string actual = GenerateSnapshot(dllPath);

            string snapshotPath = Path.Combine(AppContext.BaseDirectory, SnapshotRelativePath);
            Assert.True(File.Exists(snapshotPath), $"Committed snapshot not found at {snapshotPath}.");
            string expected = Normalize(File.ReadAllText(snapshotPath));

            Assert.True(
                expected == actual,
                "SimHub.FanatecManaged.dll public enums differ from the committed snapshot " +
                $"(tests\\FanaBridge.Tests\\{SnapshotRelativePath}).\n" +
                "Regenerate with .\\scripts\\update-simhub-enum-snapshot.ps1, then review " +
                "FanaBridgeVariantProvider.cs (StockWheelSuffixOverrides) and " +
                "FanatecDeviceTables.cs for new or renamed wheel ids.\n\n" +
                "Actual enum metadata read from " + dllPath + ":\n" + actual);
        }

        /// <summary>
        /// Dumps every public enum in the assembly as "FullName" lines followed by
        /// indented "NAME = value" members. Must stay format-identical to
        /// update-simhub-enum-snapshot.ps1; the snapshot comparison catches drift
        /// between the two implementations.
        /// </summary>
        private static string GenerateSnapshot(string dllPath)
        {
            ResolveEventHandler resolve = (s, e) => Assembly.ReflectionOnlyLoad(e.Name);
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolve;
            try
            {
                // Reflection-only: the assembly contains native interop types whose
                // initialization must not run in the test host; metadata is enough.
                Assembly asm = Assembly.ReflectionOnlyLoadFrom(dllPath);
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Partial type load is expected for this assembly; enums resolve fine.
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                var sb = new StringBuilder();
                foreach (Type type in types
                    .Where(t => t.IsEnum && t.IsPublic)
                    .OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    sb.Append(type.FullName).Append('\n');
                    var members = type
                        .GetFields(BindingFlags.Public | BindingFlags.Static)
                        .Select(f => new { f.Name, Value = Convert.ToInt64(f.GetRawConstantValue()) })
                        .OrderBy(m => m.Value)
                        .ThenBy(m => m.Name, StringComparer.Ordinal);
                    foreach (var member in members)
                        sb.Append("  ").Append(member.Name).Append(" = ").Append(member.Value).Append('\n');
                }
                return sb.ToString();
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolve;
            }
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n");
    }
}
