using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Tests.CmFakes;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Exercises <see cref="ControlMapperBridge"/>'s reflection plumbing against
    /// hand-built doubles that mirror the exact shape the bridge reflects into
    /// (a <c>ControlMapperPlugin</c> with an internal <c>remapperWorker</c> →
    /// <c>variantHelper</c> → private <c>VariantProviders</c> list, and an
    /// internal <c>UpdateControllerList()</c>). The doubles carry the same member
    /// names the bridge looks up, so these tests catch a regression in the
    /// bridge's walk without needing SimHub or hardware.
    ///
    /// The fake <c>ControlMapperPlugin</c> is created via reflection
    /// (<see cref="CmFake.NewPlugin"/>) rather than by name, so the test code
    /// never references the type that intentionally shadows SimHub's real one.
    /// </summary>
    public class ControlMapperBridgeTests
    {
        private static (ControlMapperBridge bridge, FakePluginManager pm, FakeVariantHelper helper, FakeRemapperWorker worker)
            NewSetup(bool listInitialized = true, IVariantProvider? stock = null)
        {
            IFakeControlMapper cm = CmFake.NewPlugin();
            if (listInitialized)
            {
                cm.Worker.Helper.InitList();
                if (stock != null) cm.Worker.Helper.Snapshot().Add(stock);
            }
            else
            {
                cm.Worker.Helper.NullList();
            }
            return (new ControlMapperBridge(), new FakePluginManager(cm), cm.Worker.Helper, cm.Worker);
        }

        [Fact]
        public void EnsureRegistered_AppendsProviderAfterStock_AndReEnumerates()
        {
            var stock = new FakeStockProvider("FS_WHEEL_SWTYPE_PSWBMW");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);

            bool ok = bridge.EnsureRegistered(pm);

            Assert.True(ok);
            Assert.True(bridge.IsRegistered);
            Assert.False(bridge.IsGivenUp);

            List<IVariantProvider> list = helper.Snapshot();
            Assert.Equal(2, list.Count);
            Assert.Same(stock, list[0]);                       // stock stays ahead (wins)
            Assert.IsType<FanaBridgeVariantProvider>(list[1]); // ours appended last (gap-filler)
            Assert.Equal(1, worker.UpdateControllerListCalls); // re-keyed attached rim once
        }

        [Fact]
        public void EnsureRegistered_IsIdempotent()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));

            Assert.True(bridge.EnsureRegistered(pm));
            Assert.True(bridge.EnsureRegistered(pm));   // second call: no-op

            Assert.Equal(2, helper.Snapshot().Count);          // not double-added
            Assert.Equal(1, worker.UpdateControllerListCalls); // not re-enumerated again
        }

        [Fact]
        public void EnsureRegistered_NullProviderList_IsNoOp()
        {
            // Mirrors "Recognize Individual Wheels" being off (worker Stop()'d the list).
            var (bridge, pm, helper, worker) = NewSetup(listInitialized: false);

            bool ok = bridge.EnsureRegistered(pm);

            Assert.False(ok);
            Assert.False(bridge.IsRegistered);
            Assert.False(bridge.IsGivenUp);              // not a failure — nothing to do yet
            Assert.Null(helper.Snapshot());
            Assert.Equal(0, worker.UpdateControllerListCalls);
        }

        [Fact]
        public void EnsureRegistered_RecoversAfterEviction()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));

            // Simulate VariantHelper.Stop()+Start() rebuilding the list without us.
            helper.ResetToStockOnly(stock);
            Assert.Single(helper.Snapshot());

            Assert.True(bridge.EnsureRegistered(pm));   // re-appends
            Assert.Equal(2, helper.Snapshot().Count);
            Assert.Same(stock, helper.Snapshot()[0]);
            Assert.IsType<FanaBridgeVariantProvider>(helper.Snapshot()[1]);
            Assert.Equal(2, worker.UpdateControllerListCalls); // re-enumerated again on re-add
        }

        [Fact]
        public void EnsureRegistered_DropsStaleFanaBridgeProvider_FromPriorReload()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            // A previous plugin instance left its own provider behind.
            helper.Snapshot().Add(new FanaBridgeVariantProvider());
            Assert.Equal(2, helper.Snapshot().Count);

            Assert.True(bridge.EnsureRegistered(pm));

            // Stale one removed, exactly one FanaBridge provider, and it's last.
            List<IVariantProvider> list = helper.Snapshot();
            Assert.Equal(2, list.Count);
            Assert.IsType<FanaBridgeVariantProvider>(list[1]);
            int fanaCount = 0;
            foreach (var p in list) if (p is FanaBridgeVariantProvider) fanaCount++;
            Assert.Equal(1, fanaCount);
        }

        [Fact]
        public void Unregister_RemovesProvider()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));
            Assert.Equal(2, helper.Snapshot().Count);

            bridge.Unregister();

            Assert.False(bridge.IsRegistered);
            List<IVariantProvider> list = helper.Snapshot();
            Assert.Single(list);
            Assert.Same(stock, list[0]);
        }

        [Fact]
        public void Unregister_BeforeRegister_IsSafe()
        {
            var bridge = new ControlMapperBridge();
            bridge.Unregister(); // never resolved — must not throw
            Assert.False(bridge.IsRegistered);
        }

        [Fact]
        public void EnsureRegistered_ReEnumerateThrows_StillRegistersAndDoesNotThrow()
        {
            // A failing UpdateControllerList (SimHub internal misbehaving on re-enumerate)
            // must not throw out of EnsureRegistered, nor fail the registration — the
            // provider is in the list; re-keying an already-attached rim is best-effort.
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            worker.ThrowOnUpdate = true;

            bool ok = bridge.EnsureRegistered(pm); // must not throw

            Assert.True(ok);
            Assert.True(bridge.IsRegistered);
            Assert.False(bridge.IsGivenUp);
            Assert.Equal(2, helper.Snapshot().Count);          // provider still appended
            Assert.Equal(1, worker.UpdateControllerListCalls); // attempted once, threw, swallowed
        }

        [Fact]
        public void EnsureRegistered_ReEnumeratesOutsideLock_ReentrantCallDoesNotDeadlock()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));

            // SimHub's real UpdateControllerList does a synchronous Dispatcher.Invoke to the
            // UI thread, which re-enters the bridge (IsRecognizeIndividualWheelsOn takes _sync).
            // Model that re-entry as a call from another thread: if EnsureRegistered still holds
            // _sync while re-enumerating, the re-entrant call blocks forever (the deadlock).
            bool reentrantCompleted = false;
            worker.OnUpdate = () =>
            {
                var t = System.Threading.Tasks.Task.Run(() => bridge.IsRecognizeIndividualWheelsOn(pm));
                reentrantCompleted = t.Wait(System.TimeSpan.FromSeconds(5));
            };

            bool ok = bridge.EnsureRegistered(pm);

            Assert.True(ok);
            Assert.Equal(1, worker.UpdateControllerListCalls); // re-enumerate did run
            Assert.True(reentrantCompleted,
                "re-enumerate must run outside _sync; a UI-thread re-entry deadlocks if the lock is held");
        }

        [Fact]
        public void RequestReEnumerate_RunsOutsideLock_ReentrantCallDoesNotDeadlock()
        {
            // The WheelChanged path (fires on every wheel/hub swap, converter or genuine).
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            Assert.True(bridge.EnsureRegistered(pm));  // register first; the registration re-enumerate has OnUpdate=null
            worker.UpdateControllerListCalls = 0;

            bool reentrantCompleted = false;
            worker.OnUpdate = () =>
            {
                var t = System.Threading.Tasks.Task.Run(() => bridge.IsRecognizeIndividualWheelsOn(pm));
                reentrantCompleted = t.Wait(System.TimeSpan.FromSeconds(5));
            };

            bridge.RequestReEnumerate();

            Assert.Equal(1, worker.UpdateControllerListCalls);
            Assert.True(reentrantCompleted,
                "RequestReEnumerate (WheelChanged) must re-enumerate outside _sync");
        }

        [Fact]
        public void EnsureRegistered_PluginNotLoaded_QuietRetry()
        {
            var pm = new FakePluginManager(null); // GetPlugin<T>() returns null
            var bridge = new ControlMapperBridge();

            bool ok = bridge.EnsureRegistered(pm);

            Assert.False(ok);
            Assert.False(bridge.IsGivenUp); // will retry next tick, no warning logged
        }

        [Fact]
        public void EnsureRegistered_GivesUpOnce_WhenGetPluginMissing()
        {
            var pm = new FakePluginManagerNoGetPlugin();
            var bridge = new ControlMapperBridge();

            Assert.False(bridge.EnsureRegistered(pm));
            Assert.True(bridge.IsGivenUp);
            Assert.False(bridge.EnsureRegistered(pm)); // stays given-up, no retry
        }

        [Fact]
        public void DescribeResolution_ListsProviders_AndConfiguredFanatecSource()
        {
            var stock = new FakeStockProvider("FS_WHEEL_SWTYPE_CSLEPS4");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));

            var cm = (IFakeControlMapper)pm.Plugin!;
            // A configured Fanatec source (what the user added in Control Mapper).
            cm.Settings.ControllerMappings.Add(new FakeControllerSourceMapping
            {
                ControllerDescription = new FakeControllerDescription
                {
                    VendorID = 0x0EB7, ProductId = 0x0020,
                    ControllerName = "Fanatec Wheel", Variant = "FB:PHUB_PBMR"
                }
            });
            // A non-Fanatec source that must be filtered out of the report.
            cm.Settings.ControllerMappings.Add(new FakeControllerSourceMapping
            {
                ControllerDescription = new FakeControllerDescription
                {
                    VendorID = 0x1234, ProductId = 0x0001, ControllerName = "OtherPad", Variant = "z"
                }
            });

            string report = bridge.DescribeResolution(pm);

            Assert.Contains("FakeStockProvider", report);              // stock provider listed
            Assert.Contains(nameof(FanaBridgeVariantProvider), report); // ours listed
            Assert.Contains("FS_WHEEL_SWTYPE_CSLEPS4", report);        // stock's probed answer (wins)
            Assert.Contains("FB:PHUB_PBMR", report);                   // configured Fanatec source listed
            Assert.Contains("registered=True", report);
            Assert.DoesNotContain("OtherPad", report);                 // non-Fanatec filtered
        }

        [Fact]
        public void IsRecognizeIndividualWheelsOn_ReflectsTheSetting()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            var cm = (IFakeControlMapper)pm.Plugin!;

            cm.Settings.RecognizeIndiviualWheels = true;
            Assert.True(bridge.IsRecognizeIndividualWheelsOn(pm));

            cm.Settings.RecognizeIndiviualWheels = false;
            Assert.False(bridge.IsRecognizeIndividualWheelsOn(pm));
        }

        [Fact]
        public void IsRecognizeIndividualWheelsOn_ControlMapperNotLoaded_ReturnsNull()
        {
            var pm = new FakePluginManager(null); // GetPlugin<T>() returns null
            var bridge = new ControlMapperBridge();

            Assert.Null(bridge.IsRecognizeIndividualWheelsOn(pm));
            Assert.False(bridge.IsGivenUp);
        }

        [Fact]
        public void DescribeResolution_ControlMapperNotLoaded_DoesNotThrow()
        {
            var pm = new FakePluginManager(null); // GetPlugin<T>() returns null
            var bridge = new ControlMapperBridge();

            string report = bridge.DescribeResolution(pm);

            Assert.Contains("not loaded", report);
            Assert.False(bridge.IsGivenUp);
        }

        [Fact]
        public void DescribeResolution_RecognizeIndividualWheelsOff_ReportsNullList()
        {
            var (bridge, pm, helper, worker) = NewSetup(listInitialized: false);

            string report = bridge.DescribeResolution(pm);

            Assert.Contains("Recognize Individual Wheels", report);
        }
    }
}

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
    /// <c>GetPlugin&lt;T&gt;()</c> the bridge invokes by reflection.</summary>
    public class FakePluginManager
    {
        private readonly object? _plugin;
        public FakePluginManager(object? plugin) { _plugin = plugin; }
        public T GetPlugin<T>() => (T)_plugin!;
        /// <summary>Test-only accessor to the wrapped fake plugin.</summary>
        public object? Plugin => _plugin;
    }

    /// <summary>A PluginManager that lacks GetPlugin&lt;T&gt;(), to drive the
    /// bridge's graceful give-up path.</summary>
    public class FakePluginManagerNoGetPlugin
    {
    }

    /// <summary>Test double for SimHub's ControllerDescription — the public
    /// getters DescribeResolution reflects into.</summary>
    public class FakeControllerDescription
    {
        public int VendorID { get; set; }
        public int ProductId { get; set; }
        public string? ControllerName { get; set; }
        public string? Variant { get; set; }
    }

    /// <summary>Test double for ControllerSourceMapping (owns one description).</summary>
    public class FakeControllerSourceMapping
    {
        public FakeControllerDescription? ControllerDescription { get; set; }
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
