using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Transport;
using FanaBridge.Diagnostics;
using FanaBridge.Tests.TestDoubles;
using Xunit;

namespace FanaBridge.Tests.Plugin.Diagnostics
{
    public class DiagnosticsReportTests
    {
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
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) =>
                new[] { new HidDeviceInfo(0x0020, 64, 64, "ClubSport DD") };
        }

        private static byte[] Ff08(byte wire, byte module = 0)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = 0x0C;
            b[FanatecIdentity.OffWireCode] = wire;
            b[FanatecIdentity.OffModule] = module;
            return b;
        }

        private static byte WheelWire(string code) =>
            FanatecDeviceTables.Wheels.First(kv => kv.Value == code).Key;

        private static byte HubWire(string code) =>
            FanatecDeviceTables.Hubs.First(kv => kv.Value == code).Key;

        [Fact]
        public void AppendSystemReport_UsesCapturedViewSnapshot_AndNotesWhenLatestRawFrameDiffers()
        {
            var transport = new FakeTransport();
            long now = 0;
            var wb = new FanatecWheelbase(transport, new FakeBus(), () => now);
            Assert.True(wb.AutoConnect());

            byte bmwWire = WheelWire("PSWBMW");
            byte formWire = WheelWire("CSSWFORMV2");

            transport.Identity.Enqueue(Ff08(bmwWire));
            now += 10;
            wb.UpdateIdentity();
            now += 250;
            Assert.True(wb.UpdateIdentity());

            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wb, withDisplayEncoder: false);
            var viewBmw = plugin.CaptureView();

            // Commit a different wheel on the live wheelbase after viewBmw was captured.
            transport.Identity.Enqueue(Ff08(formWire));
            now += 10;
            wb.UpdateIdentity();
            now += 250;
            Assert.True(wb.UpdateIdentity());
            Assert.Equal("CSSWFORMV2", wb.WheelCode);

            var sb = new StringBuilder();
            DiagnosticsReport.AppendSystemReport(sb, viewBmw, statusDetail: null);
            string output = sb.ToString();

            // Committed fields come from viewBmw, never re-reading wb.Snapshot.
            Assert.Contains("PSWBMW", output);
            Assert.DoesNotContain("CSSWFORMV2", output);
            // Raw frame is latest reading (formWire), so the settling/mismatch note is emitted.
            Assert.Contains("Raw (latest):", output);
            Assert.Contains("Raw note:", output);
            Assert.Contains(string.Format("[0x18]=0x{0:X2}", formWire), output);
        }

        [Fact]
        public void DescribeRawFrameMismatch_MatchesCommittedWireCodes_AndIgnoresNonHubModuleByte()
        {
            var transport = new FakeTransport();
            long now = 0;
            var wb = new FanatecWheelbase(transport, new FakeBus(), () => now);
            Assert.True(wb.AutoConnect());

            byte bmwWire = WheelWire("PSWBMW");
            transport.Identity.Enqueue(Ff08(bmwWire, module: 0x55));
            now += 10;
            wb.UpdateIdentity();
            now += 250;
            Assert.True(wb.UpdateIdentity());

            // Non-hub wheel with stray 0x1F byte matches committed snapshot (ModuleWireCode == 0).
            Assert.Null(DiagnosticsReport.DescribeRawFrameMismatch(wb.LastRawReport, wb.Snapshot));

            // Hub with PBME committed, then latest reading has PBMR -> mismatch reported.
            byte hubWire = HubWire("PHUB");
            transport.Identity.Enqueue(Ff08(hubWire, module: 0x01));
            now += 10;
            wb.UpdateIdentity();
            now += 250;
            Assert.True(wb.UpdateIdentity());
            Assert.Null(DiagnosticsReport.DescribeRawFrameMismatch(wb.LastRawReport, wb.Snapshot));

            transport.Identity.Enqueue(Ff08(hubWire, module: 0x02));
            now += 10;
            wb.UpdateIdentity(); // settling toward PBMR

            string? note = DiagnosticsReport.DescribeRawFrameMismatch(wb.LastRawReport, wb.Snapshot);
            Assert.NotNull(note);
            Assert.Contains("[0x1F]=0x02", note);
        }
    }
}
