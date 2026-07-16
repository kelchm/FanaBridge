using System.Collections.Generic;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;

// Test doubles for SimHub's Control Mapper internals. The final namespace is a
// deliberate type-name shim: ControlMapperBridge resolves SimHub's plugin type by
// its FULL NAME from the PluginManager's assembly, so the fake must live at
// exactly SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin.

namespace FanaBridge.Tests.CmFakes
{
    /// <summary>Surface used by tests to reach the fake plugin's worker without
    /// naming the shadowing <c>ControlMapperPlugin</c> type directly.</summary>
    public interface IFakeControlMapper
    {
        FakeRemapperWorker Worker { get; }
        FakeSettings Settings { get; }
    }

    /// <summary>Creates the fake ControlMapperPlugin by full name via reflection,
    /// so no test source references the type that shadows SimHub's real one
    /// (avoids the type-conflict warning while still putting a type at the exact
    /// name the bridge looks up).</summary>
    public static class CmFake
    {
        public static IFakeControlMapper NewPlugin()
        {
            var t = System.Type.GetType(
                "SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin");
            return (IFakeControlMapper)System.Activator.CreateInstance(t);
        }
    }

    /// <summary>Test double for SimHub's RemapperWorker. Field/method names match
    /// what <see cref="ControlMapperBridge"/> reflects into.</summary>
    public class FakeRemapperWorker
    {
        internal FakeVariantHelper variantHelper = new FakeVariantHelper();
        public FakeVariantHelper Helper => variantHelper;
        public int UpdateControllerListCalls;
        /// <summary>When set, UpdateControllerList throws — drives the bridge's
        /// defensive catch in EnsureRegistered (a SimHub internal misbehaving mid-call).</summary>
        public bool ThrowOnUpdate;
        /// <summary>Invoked inside UpdateControllerList to model SimHub's synchronous
        /// Dispatcher.Invoke to the UI thread — used to assert the bridge isn't holding
        /// <c>_sync</c> while it re-enumerates (see the deadlock regression tests).</summary>
        public System.Action? OnUpdate;
        internal void UpdateControllerList()
        {
            UpdateControllerListCalls++;
            OnUpdate?.Invoke();
            if (ThrowOnUpdate)
                throw new System.InvalidOperationException("simulated SimHub failure");
        }
    }

    /// <summary>Test double for SimHub's internal VariantHelper. Holds the
    /// private <c>VariantProviders</c> list the bridge swaps.</summary>
    public class FakeVariantHelper
    {
        private List<IVariantProvider>? VariantProviders;

        public void InitList() => VariantProviders = new List<IVariantProvider>();
        public void NullList() => VariantProviders = null;
        public List<IVariantProvider> Snapshot() => VariantProviders!;
        public void ResetToStockOnly(IVariantProvider stock)
            => VariantProviders = new List<IVariantProvider> { stock };
    }

    /// <summary>Stand-in for the stock Fanatec/Simucube providers — returns a
    /// fixed variant string and never fires its change event.</summary>
    public class FakeStockProvider : IVariantProvider
    {
        private readonly string _variant;
        public FakeStockProvider(string variant) { _variant = variant; }
        public string GetVariant(int vendorid, int productid) => _variant;
        public event System.EventHandler VariantChanged { add { } remove { } }
    }

    /// <summary>Stand-in for SimHub's PluginManager exposing the generic
    /// <c>GetPlugin&lt;T&gt;()</c> the bridge invokes by reflection, plus the sanctioned
    /// <c>GetControlMapperInterface()</c> the role reader falls back to.</summary>
    public class FakePluginManager
    {
        private readonly object? _plugin;
        public FakePluginManager(object? plugin) { _plugin = plugin; }
        public T GetPlugin<T>() => (T)_plugin!;
        /// <summary>Test-only accessor to the wrapped fake plugin.</summary>
        public object? Plugin => _plugin;

        /// <summary>The role catalog the reader reaches via reflection when the rim has no
        /// mappings of its own; null models Control Mapper not being loaded.</summary>
        public FakeControlMapperInterface? ControlMapperInterface { get; set; }
        public FakeControlMapperInterface? GetControlMapperInterface() => ControlMapperInterface;
    }

    /// <summary>Stand-in for SimHub's ControlMapperInterface — only the sanctioned role
    /// catalog method the reader uses.</summary>
    public class FakeControlMapperInterface
    {
        public List<string> Roles { get; } = new List<string>();
        public List<string> GetAvailableButtonRoles() => Roles;
    }

    /// <summary>Test double for SimHub's ButtonMap — the reader reads TargetRole +
    /// HasRoleAssigned.</summary>
    public class FakeButtonMap
    {
        public string? TargetRole { get; set; }
        public bool HasRoleAssigned { get; set; }
    }

    /// <summary>Test double for SimHub's ControllerMapping (the input side) — the sparse
    /// button→role table the reader walks.</summary>
    public class FakeControllerMapping
    {
        public List<FakeButtonMap> Buttons { get; } = new List<FakeButtonMap>();
    }

    /// <summary>A PluginManager that lacks GetPlugin&lt;T&gt;(), to drive the
    /// bridge's graceful give-up path.</summary>
    public class FakePluginManagerNoGetPlugin
    {
    }

    /// <summary>A PluginManager whose GetPlugin&lt;T&gt;() throws for the first N
    /// calls (startup plugin-collection flux), then answers normally — drives the
    /// bridge's transient-vs-persistent escalation.</summary>
    public class FakePluginManagerFlaky
    {
        private readonly object _plugin;
        public int ThrowsRemaining;

        public FakePluginManagerFlaky(object plugin, int throwsRemaining)
        {
            _plugin = plugin;
            ThrowsRemaining = throwsRemaining;
        }

        public T GetPlugin<T>()
        {
            if (ThrowsRemaining > 0)
            {
                ThrowsRemaining--;
                throw new System.InvalidOperationException("plugin collection in flux");
            }
            return (T)_plugin;
        }
    }

    /// <summary>Test double for SimHub's ControllerDescription — the public
    /// getters DescribeResolution reflects into.</summary>
    public class FakeControllerDescription
    {
        public int VendorID { get; set; }
        public int ProductId { get; set; }
        public string? ControllerName { get; set; }
        public string? Variant { get; set; }
        /// <summary>The strongest half of Control Mapper's match key — the reader reads it
        /// for the RIW-off (single-base) narrowing.</summary>
        public string? InterfacePath { get; set; }
    }

    /// <summary>Test double for ControllerSourceMapping (owns one description and its
    /// button→role table).</summary>
    public class FakeControllerSourceMapping
    {
        public FakeControllerDescription? ControllerDescription { get; set; }
        /// <summary>The input-side mapping; null models a source with no mappings yet.</summary>
        public FakeControllerMapping? ControllerMapping { get; set; }
    }

    /// <summary>Mirrors the shape DescribeResolution reflects into:
    /// AvailableControllers (live) + ControllerMappings (configured).</summary>
    public class FakeSettings
    {
        public List<FakeControllerDescription> AvailableControllers { get; }
            = new List<FakeControllerDescription>();
        public List<FakeControllerSourceMapping> ControllerMappings { get; }
            = new List<FakeControllerSourceMapping>();
        /// <summary>Mirrors ControlMapperPluginSettings.RecognizeIndiviualWheels — SimHub's
        /// spelling — so the bridge's RIW read is covered by the exact property name.</summary>
        public bool RecognizeIndiviualWheels { get; set; } = true;
    }
}

namespace SimHub.Plugins.OutputPlugins.ControlRemapper
{
    /// <summary>
    /// Test double sharing the real ControlMapperPlugin's full type name so the
    /// bridge's <c>assembly.GetType("...ControlMapperPlugin")</c> lookup resolves
    /// to it inside the test assembly. Exposes the internal <c>remapperWorker</c>
    /// field the bridge reads. Created only via reflection (see
    /// <see cref="FanaBridge.Tests.CmFakes.CmFake"/>), never by name.
    /// </summary>
    public class ControlMapperPlugin : FanaBridge.Tests.CmFakes.IFakeControlMapper
    {
        internal FanaBridge.Tests.CmFakes.FakeRemapperWorker remapperWorker
            = new FanaBridge.Tests.CmFakes.FakeRemapperWorker();
        public FanaBridge.Tests.CmFakes.FakeRemapperWorker Worker => remapperWorker;

        // Public field at the exact name the bridge reflects for the diagnostic.
        public FanaBridge.Tests.CmFakes.FakeSettings controlMapperPluginSettings
            = new FanaBridge.Tests.CmFakes.FakeSettings();
        public FanaBridge.Tests.CmFakes.FakeSettings Settings => controlMapperPluginSettings;
    }
}
