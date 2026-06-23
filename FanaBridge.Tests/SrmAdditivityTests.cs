using System;
using FanaBridge.Devices;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Demonstrates the core promise of the device model: a new device class (the SRM
    /// converter) drops in as a probe + driver with NO change to DeviceManager /
    /// FanatecPlugin, and the priority-ordered binder routes devices to the right
    /// driver — including resolving the ambiguous 0EB7:0005 identity by order.
    /// </summary>
    public class SrmAdditivityTests
    {
        // ── Minimal fakes ────────────────────────────────────────────────
        private sealed class FakeTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength => 64;
            public bool SendCol03(byte[] data) => true;
            public bool SendCol01(byte[] data) => true;
            public int ReadCol03(byte[] buffer, int timeoutMs) => 0; // no frames
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class FakeDriver : IDeviceDriver
        {
            public FakeDriver(DeviceClass c) { Class = c; }
            public DeviceClass Class { get; }
            public bool IsConnected => true;
            public bool Service() => false;
            public DeviceSnapshot Snapshot => new DeviceSnapshot { Stable = true };
            public void Dispose() { }
        }

        private sealed class FakeProbe : IDeviceProbe
        {
            private readonly Func<IDeviceDriver> _bind;
            public FakeProbe(int priority, Func<IDeviceDriver> bind) { Priority = priority; _bind = bind; }
            public int Priority { get; }
            public bool CouldMatch(HidDeviceGroup dev) => true;
            public IDeviceDriver TryBind(HidDeviceGroup dev, IDeviceTransport io) => _bind();
        }

        private static HidDeviceGroup Group(int vid, int pid, bool col03)
            => new HidDeviceGroup(vid, pid, col03);

        // ── SrmProbe pre-filter ──────────────────────────────────────────
        [Theory]
        [InlineData(0x1DD2, 0x2011)] // native
        [InlineData(0x0EB7, 0x0E03)] // interim
        [InlineData(0x0EB7, 0x0005)] // emulation
        public void SrmProbe_Matches_AllThreeGenerations(int vid, int pid)
            => Assert.True(new SrmProbe().CouldMatch(Group(vid, pid, col03: false)));

        [Fact]
        public void SrmProbe_DoesNotMatch_UnrelatedDevice()
            => Assert.False(new SrmProbe().CouldMatch(Group(0x1234, 0x5678, col03: true)));

        [Fact]
        public void FanatecBaseProbe_DoesNotMatch_SrmNativeVid()
            => Assert.False(new FanatecBaseProbe().CouldMatch(Group(0x1DD2, 0x2011, col03: true)));

        // ── Binder routing with the real probes ──────────────────────────
        [Fact]
        public void Binder_RoutesFanatecBase_ToBaseDriver()
        {
            var probes = new IDeviceProbe[] { new FanatecBaseProbe(), new SrmProbe() };
            var driver = ProbeBinder.Bind(Group(0x0EB7, 0x0020, col03: true), new FakeTransport(), probes);

            Assert.NotNull(driver);
            Assert.Equal(DeviceClass.Base, driver.Class);
        }

        [Fact]
        public void Binder_RoutesSrmNative_ToWheelDirectDriver()
        {
            // SRM native enumerates under a non-Fanatec VID, so FanatecBaseProbe's cheap
            // CouldMatch rejects it and SrmProbe binds — additivity, no manager change.
            var probes = new IDeviceProbe[] { new FanatecBaseProbe(), new SrmProbe() };
            var driver = ProbeBinder.Bind(Group(0x1DD2, 0x2011, col03: false), new FakeTransport(), probes);

            Assert.NotNull(driver);
            Assert.Equal(DeviceClass.WheelDirect, driver.Class);
        }

        [Fact]
        public void Binder_ReturnsNull_WhenNoProbeMatches()
        {
            var probes = new IDeviceProbe[] { new FanatecBaseProbe(), new SrmProbe() };
            var driver = ProbeBinder.Bind(Group(0x9999, 0x9999, col03: false), new FakeTransport(), probes);

            Assert.Null(driver);
        }

        // ── Priority resolution (the 0EB7:0005 ambiguity shape) ──────────
        [Fact]
        public void Binder_LowerPriorityNumber_WinsWhenBothMatch()
        {
            var basePr = new FakeProbe(10, () => new FakeDriver(DeviceClass.Base));
            var srm = new FakeProbe(50, () => new FakeDriver(DeviceClass.WheelDirect));

            // Pass unordered to prove the binder sorts by priority.
            var driver = ProbeBinder.Bind(
                Group(0x0EB7, 0x0005, col03: true), new FakeTransport(),
                new IDeviceProbe[] { srm, basePr });

            Assert.Equal(DeviceClass.Base, driver.Class); // priority 10 wins
        }

        [Fact]
        public void Binder_FallsThrough_WhenHigherPriorityDeclines()
        {
            // The real 0EB7:0005 resolution: the base probe (priority 10) declines because
            // FF 08 stays silent on an SRM emulator, so the SRM probe (priority 50) binds.
            var basePr = new FakeProbe(10, () => null);
            var srm = new FakeProbe(50, () => new FakeDriver(DeviceClass.WheelDirect));

            var driver = ProbeBinder.Bind(
                Group(0x0EB7, 0x0005, col03: true), new FakeTransport(),
                new IDeviceProbe[] { basePr, srm });

            Assert.Equal(DeviceClass.WheelDirect, driver.Class);
        }
    }
}
