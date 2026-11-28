using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.ControlMapper;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Transport;
using FanaBridge.Devices;
using FanaBridge.Tests.TestDoubles;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;
using Xunit;

namespace FanaBridge.Tests.Plugin.ControlMapper
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

        private static int CountFanaBridge(List<IVariantProvider> list)
        {
            int n = 0;
            foreach (var p in list) if (p is FanaBridgeVariantProvider) n++;
            return n;
        }

        /// <summary>A provider list whose enumeration throws, to make a rebuild fail.</summary>
        private sealed class UnreadableProviderList : List<IVariantProvider>, System.Collections.IEnumerable
        {
            /// <summary>Runs as the bridge starts reading the list, just before the
            /// read fails, standing in for a lookup that races the registration.</summary>
            public System.Action? OnRead;

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                OnRead?.Invoke();
                throw new System.InvalidOperationException("simulated read failure");
            }
        }

        private static void AssertInertPlaceholder(IVariantProvider p)
        {
            Assert.IsNotType<FanaBridgeVariantProvider>(p);
            Assert.IsNotType<FakeStockProvider>(p);
            Assert.Null(p.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));
        }

        [Fact]
        public void EnsureRegistered_ReusesTrailingStaleFanaBridgeSlot()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            // A previous plugin instance left its own provider behind, last.
            var stale = new FanaBridgeVariantProvider();
            helper.Snapshot().Add(stale);
            List<IVariantProvider> before = helper.Snapshot();

            Assert.True(bridge.EnsureRegistered(pm));

            // Ours takes the stale provider's slot in a newly published list: same
            // length, never shorter (#132). The old list may still be mid-lookup, so
            // its slot must not be overwritten in place.
            List<IVariantProvider> published = helper.Snapshot();
            Assert.NotSame(before, published);
            Assert.Equal(2, before.Count);
            Assert.Same(stale, before[1]);
            Assert.Equal(2, published.Count);
            Assert.Same(stock, published[0]);
            Assert.IsType<FanaBridgeVariantProvider>(published[1]);
            Assert.NotSame(stale, published[1]);
            Assert.Equal(1, CountFanaBridge(published));
        }

        [Fact]
        public void EnsureRegistered_ReadFails_NeverActivatesProvider()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));
            var ours = (FanaBridgeVariantProvider)helper.Snapshot()[1];
            bridge.Unregister();

            // Reading the list throws. A lookup racing that read must not find ours active.
            bool? activeDuringRead = null;
            var unreadable = new UnreadableProviderList { stock, ours };
            unreadable.OnRead = () => activeDuringRead = ours.IsActive;
            helper.Replace(unreadable);

            Assert.False(bridge.EnsureRegistered(pm));

            Assert.False(activeDuringRead);
            Assert.False(ours.IsActive);
            Assert.False(bridge.IsRegistered);
            Assert.Same(unreadable, helper.Snapshot());        // nothing published
            Assert.Equal(1, worker.UpdateControllerListCalls); // no re-enumerate for a failed registration
        }

        [Fact]
        public void EnsureRegistered_OursNotLast_LeavesPlaceholderAndAppends()
        {
            var stock = new FakeStockProvider("x");
            var other = new FakeStockProvider("y");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));
            var ours = helper.Snapshot()[1];
            helper.Snapshot().Add(other); // [stock, ours, other]

            Assert.True(bridge.EnsureRegistered(pm));

            // [stock, inert, other, ours]: every entry keeps its slot and ours goes last.
            List<IVariantProvider> list = helper.Snapshot();
            Assert.Equal(4, list.Count);
            Assert.Same(stock, list[0]);
            AssertInertPlaceholder(list[1]);
            Assert.Same(other, list[2]);
            Assert.Same(ours, list[3]);
            Assert.Equal(1, CountFanaBridge(list));
        }

        [Fact]
        public void EnsureRegistered_Rebuild_PublishesNewList_LeavesOldUnmodified()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            List<IVariantProvider> before = helper.Snapshot();

            Assert.True(bridge.EnsureRegistered(pm));

            // A lookup in progress may still be walking the old list, so it must be
            // swapped out, never mutated.
            Assert.NotSame(before, helper.Snapshot());
            Assert.Single(before);
            Assert.Same(stock, before[0]);
        }

        [Fact]
        public void RepeatedPluginReloads_DoNotGrowTheList()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));

            // Each reload: the old instance retires, and a new bridge registers against
            // the same retained Control Mapper list.
            for (int i = 0; i < 3; i++)
            {
                bridge.Retire();
                bridge = new ControlMapperBridge();
                Assert.True(bridge.EnsureRegistered(pm));

                List<IVariantProvider> list = helper.Snapshot();
                Assert.Equal(2, list.Count);
                Assert.Same(stock, list[0]);
                Assert.True(((FanaBridgeVariantProvider)list[1]).IsActive);
                Assert.Equal(1, CountFanaBridge(list));
            }
        }

        [Fact]
        public void Unregister_LeavesProviderInPlace_AndDeactivatesIt()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));
            List<IVariantProvider> before = helper.Snapshot();
            IVariantProvider[] contents = before.ToArray();

            bridge.Unregister();

            // Control Mapper reads the list lock-free and re-reads it during a lookup,
            // so unregistering must not touch the list at all (#132).
            Assert.False(bridge.IsRegistered);
            List<IVariantProvider> list = helper.Snapshot();
            Assert.Same(before, list);
            Assert.Equal(contents, list.ToArray());
            var ours = Assert.IsType<FanaBridgeVariantProvider>(list[1]);
            Assert.False(ours.IsActive);
        }

        [Fact]
        public void Unregister_ThenEviction_EnsureRegisteredAppendsActiveProvider()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            Assert.True(bridge.EnsureRegistered(pm));
            bridge.Unregister();

            // Control Mapper rebuilds its list without us while we're off.
            helper.ResetToStockOnly(stock);

            Assert.True(bridge.EnsureRegistered(pm));

            List<IVariantProvider> list = helper.Snapshot();
            Assert.Equal(2, list.Count);
            Assert.Same(stock, list[0]);
            Assert.True(Assert.IsType<FanaBridgeVariantProvider>(list[1]).IsActive);
            Assert.True(bridge.IsRegistered);
            Assert.Equal(2, worker.UpdateControllerListCalls);
        }

        [Fact]
        public void Retire_DeactivatesAndBlocksReRegistration()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            Assert.True(bridge.EnsureRegistered(pm));
            List<IVariantProvider> before = helper.Snapshot();

            bridge.Retire();

            Assert.False(bridge.EnsureRegistered(pm));
            Assert.False(bridge.IsRegistered);
            Assert.False(bridge.IsGivenUp);
            Assert.Same(before, helper.Snapshot());
            Assert.False(((FanaBridgeVariantProvider)before[1]).IsActive);
            Assert.Equal(1, worker.UpdateControllerListCalls); // no re-enumerate after retiring

            // Evicted after retiring: still doesn't come back.
            helper.ResetToStockOnly(before[0]);
            Assert.False(bridge.EnsureRegistered(pm));
            Assert.Single(helper.Snapshot());
        }

        [Fact]
        public void EnsureRegistered_AfterReactivation_DoesNotReEnumerateAgain()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            Assert.True(bridge.EnsureRegistered(pm));
            bridge.Unregister();
            Assert.True(bridge.EnsureRegistered(pm));    // re-activates and re-enumerates
            Assert.Equal(2, worker.UpdateControllerListCalls);

            Assert.True(bridge.EnsureRegistered(pm));    // steady state

            Assert.Equal(2, worker.UpdateControllerListCalls);
        }

        [Fact]
        public void GetVariant_AnswersOnlyWhileActive()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            Assert.True(bridge.EnsureRegistered(pm));
            var ours = (FanaBridgeVariantProvider)helper.Snapshot()[1];
            ours.ResolveVariant = (vid, pid) => "FS_WHEEL_SWTYPE_TEST";

            Assert.Equal("FS_WHEEL_SWTYPE_TEST", ours.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));

            bridge.Unregister();
            Assert.Null(ours.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));

            Assert.True(bridge.EnsureRegistered(pm));
            Assert.Equal("FS_WHEEL_SWTYPE_TEST", ours.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));
        }

        [Fact]
        public void DescribeResolution_MarksInactiveProviderAndPlaceholder()
        {
            var stock = new FakeStockProvider("x");
            var (bridge, pm, helper, worker) = NewSetup(stock: stock);
            helper.Snapshot().Add(new FanaBridgeVariantProvider());
            helper.Snapshot().Add(new FakeStockProvider("y"));    // [stock, stale, other]
            Assert.True(bridge.EnsureRegistered(pm));          // [stock, inert, other, ours]
            bridge.Unregister();

            string report = bridge.DescribeResolution(pm);

            Assert.Contains("Placeholder (replaced FanaBridge provider", report);
            Assert.Contains(nameof(FanaBridgeVariantProvider) + " (inactive)", report);
        }

        [Fact]
        public void EnsureRegistered_AfterUnregister_ReactivatesInPlace_AndReEnumerates()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            Assert.True(bridge.EnsureRegistered(pm));
            bridge.Unregister();
            List<IVariantProvider> before = helper.Snapshot();

            Assert.True(bridge.EnsureRegistered(pm));

            Assert.True(bridge.IsRegistered);
            Assert.Same(before, helper.Snapshot());            // list untouched
            Assert.True(((FanaBridgeVariantProvider)before[1]).IsActive);
            Assert.Equal(2, worker.UpdateControllerListCalls); // rim re-keyed on re-activation
        }

        [Fact]
        public void RegisterUnregisterCycles_NeverShrinkTheList()
        {
            var (bridge, pm, helper, worker) = NewSetup(stock: new FakeStockProvider("x"));
            int last = helper.Snapshot().Count;
            for (int i = 0; i < 3; i++)
            {
                Assert.True(bridge.EnsureRegistered(pm));
                Assert.True(helper.Snapshot().Count >= last);
                last = helper.Snapshot().Count;

                bridge.Unregister();
                Assert.True(helper.Snapshot().Count >= last);
                last = helper.Snapshot().Count;
            }
            Assert.Equal(2, last); // stock + ours: cycling doesn't grow it either
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

        private sealed class FakeTransport : IConnectableTransport
        {
            public bool Connected;
            public FakeReportStream Identity { get; } = new FakeReportStream();
            public bool Connect(int productId) { Connected = true; return true; }
            public void Disconnect() => Connected = false;
            public void Dispose() => Disconnect();
            public bool IsConnected => Connected;
            public bool IsDevicePresent => Connected;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                FanatecTransport.TransportConnectStatus.Connected;
            public bool SendCol03(byte[] data) => true;
            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => Identity;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col03MaxInputReportLength => 64;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class FakeBus : IHidBusEnumerator
        {
            private readonly int _pid;
            public FakeBus(int pid) => _pid = pid;
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) =>
                new[] { new HidDeviceInfo(_pid, 64, 64, "ClubSport DD") };
        }

        private static byte[] WheelFrame(string wheelCode)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = 0x0C;
            b[FanatecIdentity.OffWireCode] = FanatecDeviceTables.Wheels.First(kv => kv.Value == wheelCode).Key;
            return b;
        }

        private static void CommitWheel(FanatecWheelbase wb, FakeTransport t, ref long clock, string wheelCode)
        {
            t.Identity.Enqueue(WheelFrame(wheelCode));
            clock += 10;
            wb.UpdateIdentity();
            clock += 250;
            Assert.True(wb.UpdateIdentity());
        }

        [Fact]
        public void StampFriendlyNames_UsesOneViewThroughout_EvenWhenLiveWheelChanges()
        {
            var transport = new FakeTransport();
            long clock = 0;
            var wb = new FanatecWheelbase(transport, new FakeBus(0x0020), () => clock);
            Assert.True(wb.AutoConnect());
            CommitWheel(wb, transport, ref clock, "PSWBMW");

            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wb, withDisplayEncoder: false);

            // Register a renamed BMW device instance so GetConnectedWheelDisplayName(snap)
            // supplies the target name for BMW while Formula V2 is unregistered.
            var bmwProfile = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(bmwProfile);
            var bmwInst = new FanatecWheelDeviceInstance(
                new DeviceConfig { Profile = bmwProfile, Capabilities = new WheelCapabilities(bmwProfile) },
                panels: null,
                ledHost: new FakeLedModuleHost());
            bmwInst.CustomName = "My Renamed BMW";
            plugin.RegisterDeviceInstance(bmwInst);

            var viewBmw = plugin.CaptureView();

            // Swap the live wheelbase to Formula V2 before stamping with viewBmw.
            CommitWheel(wb, transport, ref clock, "CSSWFORMV2");
            Assert.Equal("CSSWFORMV2", wb.WheelCode);

            var (bridge, pm, _, _) = NewSetup(stock: new FakeStockProvider("x"));
            Assert.True(bridge.EnsureRegistered(pm));

            var cm = (IFakeControlMapper)pm.Plugin!;
            // Prior FanaBridge default name for BMW: must be recognized as ours from
            // viewBmw (not the live Formula V2) and updated to the device's rename.
            var bmwMapping = new FakeControllerDescription
            {
                VendorID = FanaBridgeVariantProvider.FanatecVendorId,
                ProductId = 0x0020,
                Variant = "FS_WHEEL_SWTYPE_PSWBMW",
                CustomName = "Podium BMW M4 GT3",
            };
            var userNamedMapping = new FakeControllerDescription
            {
                VendorID = FanaBridgeVariantProvider.FanatecVendorId,
                ProductId = 0x0020,
                Variant = "FS_WHEEL_SWTYPE_PSWBMW",
                CustomName = "User Typed Name",
            };
            var formMapping = new FakeControllerDescription
            {
                VendorID = FanaBridgeVariantProvider.FanatecVendorId,
                ProductId = 0x0020,
                Variant = "FS_WHEEL_SWTYPE_CSSWFORMV2",
                CustomName = null,
            };
            cm.Settings.ControllerMappings.Add(new FakeControllerSourceMapping { ControllerDescription = bmwMapping });
            cm.Settings.ControllerMappings.Add(new FakeControllerSourceMapping { ControllerDescription = userNamedMapping });
            cm.Settings.ControllerMappings.Add(new FakeControllerSourceMapping { ControllerDescription = formMapping });

            bridge.StampFriendlyNames(viewBmw);

            Assert.Equal("My Renamed BMW", bmwMapping.CustomName);
            Assert.Equal("User Typed Name", userNamedMapping.CustomName);
            Assert.Null(formMapping.CustomName);
        }

        [Fact]
        public void DescribeResolution_UsesOneViewForVariantFriendlyNameAndPid()
        {
            var transport = new FakeTransport();
            long clock = 0;
            var wb = new FanatecWheelbase(transport, new FakeBus(0x0020), () => clock);
            Assert.True(wb.AutoConnect());
            CommitWheel(wb, transport, ref clock, "PSWBMW");

            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wb, withDisplayEncoder: false);

            var viewBmw = plugin.CaptureView();

            // Disconnect live wheelbase after capturing viewBmw.
            wb.Disconnect();
            Assert.False(wb.Snapshot.Connected);

            var (bridge, pm, _, _) = NewSetup(stock: new FakeStockProvider("FS_WHEEL_SWTYPE_PSWBMW"));
            Assert.True(bridge.EnsureRegistered(pm));

            string report = bridge.DescribeResolution(pm, viewBmw);

            Assert.Contains("FanaBridge would emit (id): FS_WHEEL_SWTYPE_PSWBMW", report);
            Assert.Contains("Live wheel PID: 0x0020", report);
            string expectedFriendly = FanaBridgeVariantProvider.ComputeFriendlyName(viewBmw.Snapshot)!;
            Assert.Contains("Friendly name (-> CustomName): " + expectedFriendly, report);
        }
    }
}
