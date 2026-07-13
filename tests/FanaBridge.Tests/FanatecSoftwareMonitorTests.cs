using System.Collections.Generic;
using System.Linq;
using FanaBridge.Diagnostics;
using Xunit;

namespace FanaBridge.Tests
{
    public class FanatecSoftwareMonitorTests
    {
        private sealed class Clock { public long T; public long Now() => T; }

        [Fact]
        public void NoVendorProcesses_NoWarning()
        {
            var m = new FanatecSoftwareMonitor(_ => false, () => 0);
            Assert.Null(m.DetectedProcess);
            Assert.Null(m.Warning);
        }

        [Fact]
        public void VendorService_Detected_WithWarning()
        {
            var m = new FanatecSoftwareMonitor(n => n == "FanatecService", () => 0);
            Assert.Equal("FanatecService", m.DetectedProcess);
            Assert.Contains("FanatecService", m.Warning);
        }

        [Fact]
        public void VendorApp_Detected()
        {
            var m = new FanatecSoftwareMonitor(n => n == "Fanatec", () => 0);
            Assert.Equal("Fanatec", m.DetectedProcess);
        }

        [Fact]
        public void PnpDriverService_IsNotACoDriver()
        {
            // FWPnpService (the driver-package PnP service) auto-starts on every boot and
            // never emits ITM traffic — it must not trip the warning.
            var m = new FanatecSoftwareMonitor(n => n == "FWPnpService", () => 0);
            Assert.Null(m.DetectedProcess);
        }

        [Fact]
        public void Checks_AreTtlCached()
        {
            var clock = new Clock();
            int probes = 0;
            var m = new FanatecSoftwareMonitor(_ => { probes++; return false; }, clock.Now);

            _ = m.DetectedProcess;
            int first = probes;
            _ = m.DetectedProcess;                 // inside the TTL — no new probe
            Assert.Equal(first, probes);

            clock.T += m.CacheTtlMs;
            _ = m.DetectedProcess;                 // TTL expired — re-probes
            Assert.True(probes > first);
        }

        [Fact]
        public void DetectionEdges_LoggedOnceEachWay()
        {
            var clock = new Clock();
            bool running = false;
            var logs = new List<string>();
            var m = new FanatecSoftwareMonitor(n => running && n == "FanatecService", clock.Now, logs.Add);

            _ = m.DetectedProcess;                 // not running — nothing to log
            Assert.Empty(logs);

            running = true;
            clock.T += m.CacheTtlMs;
            _ = m.DetectedProcess;                 // appeared → logged
            clock.T += m.CacheTtlMs;
            _ = m.DetectedProcess;                 // still running → no repeat
            Assert.Single(logs, l => l.Contains("detected running"));

            running = false;
            clock.T += m.CacheTtlMs;
            _ = m.DetectedProcess;                 // disappeared → logged once
            Assert.Single(logs, l => l.Contains("no longer running"));
        }
    }
}
