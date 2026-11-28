using System;
using System.Collections.Generic;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Transport;
using FanaBridge.Tests.TestDoubles;
using Xunit;

namespace FanaBridge.Tests.Core.Devices
{
    public class ConnectionMonitorTests
    {
        // ── Test stub ────────────────────────────────────────────────────

        // The wheelbase now owns its transport, so a single stub stands in for
        // both connection state (IsConnected / IsDevicePresent) and identity
        // servicing.
        private class StubWheelbase : IWheelbaseConnection
        {
            public bool IsConnected { get; set; } = true;
            public bool IsDevicePresent { get; set; } = true;
            public int DisconnectCalls { get; private set; }
            public int UpdateCalls { get; private set; }
            public bool UpdateThrows { get; set; }

            public void Disconnect() => DisconnectCalls++;

            public bool UpdateIdentity()
            {
                if (UpdateThrows)
                    throw new InvalidOperationException("identity update failed");
                UpdateCalls++;
                return true;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static ConnectionMonitor Create(
            StubWheelbase wheelbase, Func<bool> tryConnect)
            => new ConnectionMonitor(wheelbase, tryConnect);

        /// <summary>Pump Update() n times and return the last result.</summary>
        private static bool PumpFrames(ConnectionMonitor monitor, int count)
        {
            bool last = false;
            for (int i = 0; i < count; i++)
                last = monitor.Update();
            return last;
        }

        // ── Constructor validation ───────────────────────────────────────

        [Fact]
        public void Constructor_NullWheelbase_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new ConnectionMonitor(null, () => true));
        }

        [Fact]
        public void Constructor_NullTryConnect_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new ConnectionMonitor(new StubWheelbase(), null));
        }

        // ── Initial connect ──────────────────────────────────────────────

        [Fact]
        public void TryInitialConnect_Success_IsConnectedTrue()
        {
            var monitor = Create(new StubWheelbase(), () => true);
            Assert.True(monitor.TryInitialConnect());
            Assert.True(monitor.IsConnected);
        }

        [Fact]
        public void TryInitialConnect_Failure_IsConnectedFalse()
        {
            var monitor = Create(new StubWheelbase(), () => false);
            Assert.False(monitor.TryInitialConnect());
            Assert.False(monitor.IsConnected);
        }

        // ── Reconnect after initial failure ──────────────────────────────

        [Fact]
        public void Update_WhenDisconnected_AttemptsReconnect()
        {
            int attempts = 0;
            var monitor = Create(new StubWheelbase(), () =>
            {
                attempts++;
                return attempts >= 3; // fail first two, succeed third
            });

            monitor.TryInitialConnect(); // fails (attempt 1)
            Assert.False(monitor.IsConnected);

            // Cooldown is 0 after TryInitialConnect, so first Update retries immediately.
            // Attempt 2 fails → enters 300-frame cooldown.
            bool result = monitor.Update();
            Assert.False(result);
            Assert.False(monitor.IsConnected);

            // Pump through the 300-frame cooldown (frames are no-ops)
            PumpFrames(monitor, 300);
            Assert.False(monitor.IsConnected);

            // Next Update exits cooldown and triggers attempt 3 → succeeds
            result = monitor.Update();
            Assert.True(result);
            Assert.True(monitor.IsConnected);
        }

        [Fact]
        public void Update_ReconnectSuccess_FiresConnectedEvent()
        {
            int connectCount = 0;
            bool connectedFired = false;

            var monitor = Create(new StubWheelbase(), () => ++connectCount >= 2);
            monitor.Connected += () => connectedFired = true;

            monitor.TryInitialConnect(); // fails
            monitor.Update();            // fails, enters cooldown
            PumpFrames(monitor, 300);    // exits cooldown, succeeds

            Assert.True(connectedFired);
        }

        // ── HID bus check (every 120 frames) ────────────────────────────

        [Fact]
        public void Update_DeviceNotPresent_DisconnectsAndFiresEvent()
        {
            var wheelbase = new StubWheelbase();
            bool disconnectedFired = false;

            var monitor = Create(wheelbase, () => true);
            monitor.Disconnected += () => disconnectedFired = true;
            monitor.TryInitialConnect();

            // Pump to frame 120 where the bus check happens
            wheelbase.IsDevicePresent = false;
            PumpFrames(monitor, 120);

            Assert.False(monitor.IsConnected);
            Assert.True(disconnectedFired);
            Assert.Equal(1, wheelbase.DisconnectCalls);
        }

        // ── Stream check (every 60 frames, when not on 120) ─────────────

        [Fact]
        public void Update_StreamLost_DisconnectsAndFiresEvent()
        {
            var wheelbase = new StubWheelbase();
            bool disconnectedFired = false;

            var monitor = Create(wheelbase, () => true);
            monitor.Disconnected += () => disconnectedFired = true;
            monitor.TryInitialConnect();

            // Frame 60 triggers the stream check (not 120)
            wheelbase.IsConnected = false;
            PumpFrames(monitor, 60);

            Assert.False(monitor.IsConnected);
            Assert.True(disconnectedFired);
        }

        [Fact]
        public void Update_StreamLost_TearsDownWheelbase()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            wheelbase.IsConnected = false;
            PumpFrames(monitor, 60); // stream check at frame 60

            // The monitor must reset the wheelbase on the stream-loss path,
            // not just flip its own flag — otherwise stale identity/transport
            // state survives into the reconnect.
            Assert.Equal(1, wheelbase.DisconnectCalls);
        }

        // ── Identity update failure ──────────────────────────────────────

        [Fact]
        public void Update_IdentityThrows_DisconnectsWithShortCooldown()
        {
            var wheelbase = new StubWheelbase();
            int connectAttempts = 0;

            var monitor = Create(wheelbase, () =>
            {
                connectAttempts++;
                return true;
            });
            monitor.TryInitialConnect(); // attempt 1

            // Identity is serviced every frame; make it throw to trigger disconnect.
            wheelbase.UpdateThrows = true;
            bool result = monitor.Update();

            Assert.False(result);
            Assert.False(monitor.IsConnected);

            // Short cooldown = 60 frames. Pump through cooldown, then reconnect.
            wheelbase.UpdateThrows = false;
            PumpFrames(monitor, 60);
            Assert.False(monitor.IsConnected);

            result = monitor.Update(); // exits cooldown → reconnect
            Assert.True(result);
            Assert.True(monitor.IsConnected);
            Assert.Equal(2, connectAttempts);
        }

        [Fact]
        public void Update_IdentityThrows_TearsDownWheelbase()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            wheelbase.UpdateThrows = true;
            monitor.Update();

            Assert.Equal(1, wheelbase.DisconnectCalls);
        }

        // ── ForceReconnect ───────────────────────────────────────────────
        // ForceReconnect only REQUESTS the reconnect (it may be called from the
        // UI thread); the work happens on the next frame's Update so all device
        // I/O stays on the frame thread.

        [Fact]
        public void ForceReconnect_WhenConnected_DisconnectsAndReconnectsOnNextUpdate()
        {
            var wheelbase = new StubWheelbase();
            bool connectedFired = false;

            var monitor = Create(wheelbase, () => true);
            monitor.Connected += () => connectedFired = true;
            monitor.TryInitialConnect();

            monitor.ForceReconnect();
            Assert.Equal(0, wheelbase.DisconnectCalls); // deferred — nothing yet

            monitor.Update();

            Assert.True(monitor.IsConnected);
            Assert.True(connectedFired);
            Assert.Equal(1, wheelbase.DisconnectCalls);
        }

        [Fact]
        public void ForceReconnect_WhenDisconnected_SkipsDisconnectAndBypassesCooldown()
        {
            var wheelbase = new StubWheelbase();
            int connectAttempts = 0;

            var monitor = Create(wheelbase, () => ++connectAttempts >= 3);
            monitor.TryInitialConnect(); // fails (attempt 1)
            monitor.Update();            // fails (attempt 2) → long cooldown armed

            Assert.False(monitor.IsConnected);
            Assert.Equal(0, wheelbase.DisconnectCalls);

            monitor.ForceReconnect();
            monitor.Update(); // attempt 3, immediately (cooldown bypassed) → succeeds

            Assert.True(monitor.IsConnected);
            Assert.Equal(0, wheelbase.DisconnectCalls); // wasn't connected, so no disconnect
        }

        [Fact]
        public void ForceReconnect_Failure_FiresDisconnected()
        {
            var wheelbase = new StubWheelbase();
            bool disconnectedFired = false;
            int connectAttempts = 0;

            var monitor = Create(wheelbase, () => ++connectAttempts == 1);
            monitor.Disconnected += () => disconnectedFired = true;
            monitor.TryInitialConnect(); // attempt 1 → succeeds

            monitor.ForceReconnect();
            monitor.Update(); // disconnects, then attempt 2 → fails

            Assert.False(monitor.IsConnected);
            Assert.True(disconnectedFired);
        }

        // ── Steady state ─────────────────────────────────────────────────

        [Fact]
        public void Update_SteadyState_ReturnsTrue()
        {
            var monitor = Create(new StubWheelbase(), () => true);
            monitor.TryInitialConnect();

            // Run 240 frames (covers multiple heartbeat cycles)
            for (int i = 0; i < 240; i++)
                Assert.True(monitor.Update());
        }

        [Fact]
        public void Update_SteadyState_ServicesIdentityEveryFrame()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            // Identity is serviced on every frame (cheap non-blocking drain),
            // not on a poll interval.
            PumpFrames(monitor, 120);

            Assert.True(wheelbase.UpdateCalls >= 100);
        }

        // ── Disconnect reason (feeds the UI Status detail) ───────────────

        [Fact]
        public void LastDisconnectReason_NullWhileConnected()
        {
            var monitor = Create(new StubWheelbase(), () => true);
            monitor.TryInitialConnect();
            Assert.Null(monitor.LastDisconnectReason);
        }

        [Fact]
        public void LastDisconnectReason_SetOnBusLoss()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            wheelbase.IsDevicePresent = false;
            PumpFrames(monitor, 120); // bus check

            Assert.False(string.IsNullOrEmpty(monitor.LastDisconnectReason));
        }

        [Fact]
        public void LastDisconnectReason_SetOnStreamLoss()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            wheelbase.IsConnected = false;
            PumpFrames(monitor, 60); // stream check

            Assert.False(string.IsNullOrEmpty(monitor.LastDisconnectReason));
        }

        [Fact]
        public void LastDisconnectReason_SetOnIdentityFailure()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            wheelbase.UpdateThrows = true;
            monitor.Update();

            Assert.False(string.IsNullOrEmpty(monitor.LastDisconnectReason));
        }

        [Fact]
        public void LastDisconnectReason_ClearedOnReconnect()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            wheelbase.IsConnected = false;
            PumpFrames(monitor, 60); // stream loss → reason set, enters cooldown
            Assert.False(string.IsNullOrEmpty(monitor.LastDisconnectReason));

            // Recover and let the cooldown elapse, then reconnect.
            wheelbase.IsConnected = true;
            PumpFrames(monitor, 300); // COOLDOWN_LONG
            Assert.True(monitor.Update());
            Assert.True(monitor.IsConnected);
            Assert.Null(monitor.LastDisconnectReason);
        }

        // ── Transport health vs published snapshot (#137 Stage 1a) ───────

        private sealed class UnhealthyStreamTransport : IConnectableTransport
        {
            public bool ConnectResult = true;
            public bool Connected;
            public bool DevicePresent = true;
            public FakeReportStream Identity { get; } = new FakeReportStream();

            public bool Connect(int productId)
            {
                Connected = ConnectResult;
                return ConnectResult;
            }

            public void Disconnect() => Connected = false;
            public void Dispose() => Disconnect();
            public bool IsConnected => Connected;
            public bool IsDevicePresent => DevicePresent;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                Connected ? FanatecTransport.TransportConnectStatus.Connected
                          : FanatecTransport.TransportConnectStatus.NoDeviceForPid;
            public bool SendCol03(byte[] data) => Connected;
            public bool SendCol01(byte[] data) => Connected;
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

        private sealed class SingleDeviceBus : IHidBusEnumerator
        {
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) =>
                new[] { new HidDeviceInfo(0x0020, 64, 64, "ClubSport DD") };
        }

        [Fact]
        public void Heartbeat_TransportUnhealthyWhileSnapshotStillConnected_DisconnectsAndReconnects()
        {
            // IWheelbaseConnection.IsConnected must stay backed by live transport
            // health (not the published snapshot). When a col03 reader faults or
            // a stream closes while the device stays on the HID bus, the snapshot
            // still says Connected until the stream-check heartbeat fires and
            // calls Disconnect().
            var transport = new UnhealthyStreamTransport();
            long now = 0;
            var wb = new FanatecWheelbase(transport, new SingleDeviceBus(), () => now);
            bool disconnectedFired = false;
            bool reconnectedFired = false;

            var monitor = new ConnectionMonitor(wb, wb.AutoConnect);
            monitor.Disconnected += () => disconnectedFired = true;
            monitor.Connected += () => reconnectedFired = true;

            Assert.True(monitor.TryInitialConnect());
            Assert.True(wb.IsConnected);
            Assert.True(wb.Snapshot.Connected);
            long connectedEpoch = wb.Snapshot.ConnectionEpoch;

            // Simulate a dropped stream / faulted col03 reader while the device
            // remains present on the USB bus.
            transport.Connected = false;
            transport.DevicePresent = true;

            // Live transport health is false, while the published snapshot still
            // reports connected until the heartbeat acts.
            Assert.False(wb.IsConnected);
            Assert.True(wb.Snapshot.Connected);

            // Stream check runs at frame 60 (before the 120-frame HID bus check).
            PumpFrames(monitor, 60);

            Assert.True(disconnectedFired);
            Assert.False(monitor.IsConnected);
            Assert.False(wb.Snapshot.Connected);
            Assert.Equal(connectedEpoch + 1, wb.Snapshot.ConnectionEpoch);

            // After the cooldown expires, the monitor reconnects and publishes a
            // fresh connected snapshot in the next connection epoch.
            PumpFrames(monitor, 300); // COOLDOWN_LONG
            Assert.True(monitor.Update());
            Assert.True(reconnectedFired);
            Assert.True(monitor.IsConnected);
            Assert.True(wb.IsConnected);
            Assert.True(wb.Snapshot.Connected);
            Assert.Equal(connectedEpoch + 2, wb.Snapshot.ConnectionEpoch);
        }
    }
}
