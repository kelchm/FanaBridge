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
    }
}
