using System.Collections.Generic;
using FanaBridge.ControlMapper;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;
using Xunit;

namespace FanaBridge.Tests.Plugin.ControlMapper
{
    /// <summary>
    /// Covers <see cref="ControlMapperIntegration"/>, the plugin's owner of the
    /// bridge: it decides when a bridge may be created and registered, so
    /// <c>FanatecPlugin</c>'s reconcile and teardown paths stay thin.
    /// </summary>
    public class ControlMapperIntegrationTests
    {
        private static (FakePluginManager pm, FakeVariantHelper helper, FakeRemapperWorker worker) NewControlMapper(IVariantProvider stock)
        {
            IFakeControlMapper cm = CmFake.NewPlugin();
            cm.Worker.Helper.InitList();
            cm.Worker.Helper.Snapshot().Add(stock);
            return (new FakePluginManager(cm), cm.Worker.Helper, cm.Worker);
        }

        [Fact]
        public void Reconcile_Enabled_CreatesBridgeAndRegisters()
        {
            var (pm, helper, worker) = NewControlMapper(new FakeStockProvider("x"));
            var integration = new ControlMapperIntegration();

            integration.Reconcile(enabled: true, pm);

            Assert.NotNull(integration.Bridge);
            Assert.True(integration.Bridge!.IsRegistered);
            Assert.True(Assert.IsType<FanaBridgeVariantProvider>(helper.Snapshot()[1]).IsActive);
        }

        [Fact]
        public void Retire_BeforeFirstRegistration_LaterReconcileRegistersNothing()
        {
            var stock = new FakeStockProvider("x");
            var (pm, helper, worker) = NewControlMapper(stock);
            List<IVariantProvider> before = helper.Snapshot();
            var integration = new ControlMapperIntegration();

            integration.Retire();
            integration.Reconcile(enabled: true, pm);

            Assert.Null(integration.Bridge);
            Assert.Same(before, helper.Snapshot());
            Assert.Single(before);
            Assert.Same(stock, before[0]);
            Assert.Equal(0, worker.UpdateControllerListCalls);
        }

        [Fact]
        public void Retire_AfterRegistration_LaterReconcileDoesNotReactivate()
        {
            var (pm, helper, worker) = NewControlMapper(new FakeStockProvider("x"));
            var integration = new ControlMapperIntegration();
            integration.Reconcile(enabled: true, pm);
            var ours = (FanaBridgeVariantProvider)helper.Snapshot()[1];

            integration.Retire();
            integration.Reconcile(enabled: true, pm);

            Assert.False(ours.IsActive);
            Assert.False(integration.Bridge!.IsRegistered);
            Assert.Equal(1, worker.UpdateControllerListCalls);
        }

        [Fact]
        public void Retire_DuringRegistrationReEnumerate_DoesNotDeadlock()
        {
            var (pm, helper, worker) = NewControlMapper(new FakeStockProvider("x"));
            var integration = new ControlMapperIntegration();

            // SimHub's UpdateControllerList dispatches synchronously to the UI thread, where
            // teardown can run. Model teardown as Retire on another thread inside it: if
            // registration ran under the integration's lifecycle lock, Retire would block.
            bool retireCompleted = false;
            worker.OnUpdate = () =>
            {
                var t = System.Threading.Tasks.Task.Run(integration.Retire);
                retireCompleted = t.Wait(System.TimeSpan.FromSeconds(5));
            };

            integration.Reconcile(enabled: true, pm);

            Assert.True(retireCompleted,
                "registration must run outside the lifecycle lock; Retire during re-enumerate deadlocks otherwise");
            Assert.Equal(1, worker.UpdateControllerListCalls);
            Assert.False(Assert.IsType<FanaBridgeVariantProvider>(helper.Snapshot()[1]).IsActive);
            Assert.False(integration.Bridge!.IsRegistered);
        }

        [Fact]
        public void Reconcile_DisabledBeforeEnable_CreatesNothing()
        {
            var stock = new FakeStockProvider("x");
            var (pm, helper, worker) = NewControlMapper(stock);
            List<IVariantProvider> before = helper.Snapshot();
            var integration = new ControlMapperIntegration();

            integration.Reconcile(enabled: false, pm);

            Assert.Null(integration.Bridge);
            Assert.Same(before, helper.Snapshot());
            Assert.Single(before);
            Assert.Same(stock, before[0]);
            Assert.Equal(0, worker.UpdateControllerListCalls);
        }

        [Fact]
        public void Reconcile_EnableDisableEnable_TogglesProviderInPlace()
        {
            var (pm, helper, worker) = NewControlMapper(new FakeStockProvider("x"));
            var integration = new ControlMapperIntegration();
            integration.Reconcile(enabled: true, pm);
            ControlMapperBridge bridge = integration.Bridge!;
            List<IVariantProvider> list = helper.Snapshot();
            var ours = Assert.IsType<FanaBridgeVariantProvider>(list[1]);
            Assert.True(ours.IsActive);
            Assert.Equal(1, worker.UpdateControllerListCalls);

            integration.Reconcile(enabled: false, pm);

            Assert.Same(list, helper.Snapshot());
            Assert.Equal(2, list.Count);
            Assert.Same(ours, list[1]);
            Assert.False(ours.IsActive);
            Assert.False(bridge.IsRegistered);
            Assert.Equal(1, worker.UpdateControllerListCalls);

            integration.Reconcile(enabled: true, pm);

            Assert.Same(bridge, integration.Bridge);
            Assert.Same(list, helper.Snapshot());
            Assert.Equal(2, list.Count);
            Assert.Same(ours, list[1]);
            Assert.True(ours.IsActive);
            Assert.True(bridge.IsRegistered);
            Assert.Equal(2, worker.UpdateControllerListCalls); // one re-key on reactivation
        }
    }
}
