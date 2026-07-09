using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Guards the connect-failure message taxonomy. The original code reported
    /// "another process may hold the interface" for EVERY transport failure, which
    /// misdescribes the common col01-only/legacy case (e.g. a CSL Elite that exposes
    /// a single out=8/in=34 collection and no col03). These tests lock in that only
    /// a genuine col03 open failure mentions exclusive-access contention.
    /// </summary>
    public class ConnectFailureMessageTests
    {
        private const string Name = "FANATEC CSL Elite Wheel Base";
        private const int Pid = 0x0E03;

        private static string Describe(FanatecTransport.TransportConnectStatus status)
            => FanatecWheelbase.DescribeConnectFailure(status, Name, Pid);

        [Fact]
        public void NoCol03Interface_DoesNotBlameAnotherProcess_AndNamesCol03()
        {
            string msg = Describe(FanatecTransport.TransportConnectStatus.NoCol03Interface);

            Assert.DoesNotContain("another process", msg);
            Assert.Contains("col03", msg);
            Assert.Contains("0x0E03", msg);
        }

        [Fact]
        public void Col03OpenFailed_IsTheOnlyCaseThatBlamesAnotherProcess()
        {
            string msg = Describe(FanatecTransport.TransportConnectStatus.Col03OpenFailed);

            Assert.Contains("another process", msg);
            Assert.Contains("0x0E03", msg);
        }

        [Fact]
        public void NoDeviceForPid_DescribesAMissingDevice_NotContention()
        {
            string msg = Describe(FanatecTransport.TransportConnectStatus.NoDeviceForPid);

            Assert.DoesNotContain("another process", msg);
            Assert.Contains("No HID device", msg);
        }

        [Theory]
        [InlineData(FanatecTransport.TransportConnectStatus.NoDeviceForPid)]
        [InlineData(FanatecTransport.TransportConnectStatus.NoCol03Interface)]
        [InlineData(FanatecTransport.TransportConnectStatus.Col03OpenFailed)]
        [InlineData(FanatecTransport.TransportConnectStatus.UnexpectedError)]
        public void EveryFailure_ProducesANonEmptyMessageWithTheProductAndPid(
            FanatecTransport.TransportConnectStatus status)
        {
            string msg = Describe(status);

            Assert.False(string.IsNullOrWhiteSpace(msg));
            Assert.Contains(Name, msg);
            Assert.Contains("0x0E03", msg);
        }
    }
}
