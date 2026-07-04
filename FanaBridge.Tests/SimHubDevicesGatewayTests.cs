using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// <see cref="SimHubDevicesGateway.IsSimilarDescriptor"/> must mirror how
    /// SimHub counts existing devices against a candidate descriptor's
    /// MaximumInstances, including its quirks — a mismatch either blocks the
    /// prompt's button for an addable device or offers an add that ends in
    /// SimHub's instance-cap error dialog.
    /// </summary>
    public class SimHubDevicesGatewayTests
    {
        [Fact]
        public void SameId_IsSimilar()
        {
            Assert.True(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_PSWBMW", null,
                "Fanatec_PSWBMW", null));
        }

        [Fact]
        public void DifferentStandaloneWheels_AreNotSimilar()
        {
            Assert.False(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_PSWBMW", null,
                "Fanatec_PSWF1", null));
        }

        [Fact]
        public void HubModuleCombos_SharingModuleParent_AreSimilar()
        {
            // Same module on two different hubs — SimHub refuses the second add.
            Assert.True(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_PHUB_PBME", "Fanatec_Module_PBME",
                "Fanatec_APM2_PBME", "Fanatec_Module_PBME"));
        }

        [Fact]
        public void HubModuleCombos_WithDifferentModules_AreNotSimilar()
        {
            Assert.False(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_PHUB_PBME", "Fanatec_Module_PBME",
                "Fanatec_PHUB_PBMR", "Fanatec_Module_PBMR"));
        }

        [Fact]
        public void ExistingParentMatchingCandidateId_IsSimilar()
        {
            Assert.True(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_PHUB_PBME", "Fanatec_Module_PBME",
                "Fanatec_Module_PBME", null));
        }

        [Fact]
        public void ExistingIdMatchingCandidateParent_IsSimilar_WhenExistingHasAParent()
        {
            Assert.True(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_Module_PBME", "Fanatec_SomeParent",
                "Fanatec_PHUB_PBME", "Fanatec_Module_PBME"));
        }

        [Fact]
        public void ParentlessExisting_DoesNotMatchCandidateParent()
        {
            // SimHub quirk, preserved deliberately: when the existing device has
            // no parent, its id is never compared against the candidate's parent.
            Assert.False(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_Module_PBME", null,
                "Fanatec_PHUB_PBME", "Fanatec_Module_PBME"));
        }

        [Fact]
        public void NullParents_DoNotMatchEachOther()
        {
            Assert.False(SimHubDevicesGateway.IsSimilarDescriptor(
                "Fanatec_PSWBMW", null,
                "Fanatec_PSWF1", null));
        }

        [Fact]
        public void Comparison_IsCaseSensitive_LikeSimHub()
        {
            Assert.False(SimHubDevicesGateway.IsSimilarDescriptor(
                "FANATEC_PSWBMW", null,
                "Fanatec_PSWBMW", null));
        }

        [Fact]
        public void Resolve_NullPluginManager_ReturnsNull()
        {
            Assert.Null(SimHubDevicesGateway.Resolve(null));
        }

        [Fact]
        public void Queries_NullDevicesPlugin_AreQuiet()
        {
            Assert.False(SimHubDevicesGateway.HasDescriptor(null, "Fanatec_PSWBMW"));
            Assert.False(SimHubDevicesGateway.IsDeviceAdded(null, "Fanatec_PSWBMW"));
            Assert.Null(SimHubDevicesGateway.FindBlockingDevice(null, "Fanatec_PSWBMW", null));
        }
    }
}
