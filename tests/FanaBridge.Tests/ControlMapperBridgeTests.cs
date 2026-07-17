using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
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
        public void TransientGetPluginFailure_QuietlyRetries_ThenRecovers()
        {
            // The plugin collection can be in flux during startup — a throwing
            // GetPlugin<T> is a transient there, and one bad tick must not kill
            // the integration for the whole session (it previously latched
            // give-up on the first throw).
            IFakeControlMapper cm = CmFake.NewPlugin();
            cm.Worker.Helper.InitList();
            cm.Settings.RecognizeIndiviualWheels = true;
            var pm = new FakePluginManagerFlaky(cm, throwsRemaining: 3);
            var bridge = new ControlMapperBridge();

            for (int i = 0; i < 3; i++)
            {
                Assert.Null(bridge.IsRecognizeIndividualWheelsOn(pm));  // failed this tick...
                Assert.False(bridge.IsGivenUp);                         // ...but not dead
            }

            Assert.True(bridge.IsRecognizeIndividualWheelsOn(pm));      // recovered
            Assert.False(bridge.IsGivenUp);
        }

        [Fact]
        public void PersistentGetPluginFailure_EscalatesToGiveUp()
        {
            // A genuine shape change fails deterministically on every call, so
            // the persistent-failure threshold still reaches give-up — the
            // escalation exists so transients don't, not so shape changes never do.
            IFakeControlMapper cm = CmFake.NewPlugin();
            cm.Worker.Helper.InitList();
            var pm = new FakePluginManagerFlaky(cm, throwsRemaining: int.MaxValue);
            var bridge = new ControlMapperBridge();

            for (int i = 0; i < ControlMapperBridge.LIVE_RESOLVE_FAILURES_BEFORE_GIVE_UP; i++)
            {
                Assert.False(bridge.IsGivenUp);
                bridge.IsRecognizeIndividualWheelsOn(pm);
            }

            Assert.True(bridge.IsGivenUp);
        }

        [Fact]
        public void SuccessBetweenFailures_ResetsTheEscalationCounter()
        {
            IFakeControlMapper cm = CmFake.NewPlugin();
            cm.Worker.Helper.InitList();
            var bridge = new ControlMapperBridge();

            // Almost reach the threshold, recover once, then fail again just as
            // long — the counter must have reset, so give-up is never latched.
            int almost = ControlMapperBridge.LIVE_RESOLVE_FAILURES_BEFORE_GIVE_UP - 1;
            var pm = new FakePluginManagerFlaky(cm, throwsRemaining: almost);
            for (int i = 0; i < almost; i++)
                bridge.IsRecognizeIndividualWheelsOn(pm);
            bridge.IsRecognizeIndividualWheelsOn(pm);      // succeeds — resets

            pm.ThrowsRemaining = almost;
            for (int i = 0; i < almost; i++)
                bridge.IsRecognizeIndividualWheelsOn(pm);

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
