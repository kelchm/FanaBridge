using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using Xunit;

namespace FanaBridge.Tests.Devices
{
    /// <summary>
    /// The gateway must stay quiet (no throws, no false positives) when
    /// SimHub's Devices plugin isn't resolvable or initialized yet — the
    /// add-device prompt evaluates on every status refresh, including early
    /// in startup.
    /// </summary>
    public class SimHubDevicesGatewayTests
    {
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
        }
    }
}
