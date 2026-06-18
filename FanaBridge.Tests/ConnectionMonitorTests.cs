using System;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    public class ConnectionMonitorTests
    {
        // ── Test stub ────────────────────────────────────────────────────

        // The wheelbase now owns its transport, so a single stub stands in for
        // both connection state (IsConnected / IsDevicePresent) and identity
        // polling.
        private class StubWheelbase : IWheelbaseConnection
        {
            public bool IsConnected { get; set; } = true;
            public bool IsDevicePresent { get; set; } = true;
            public int DisconnectCalls { get; private set; }
            public int PollCalls { get; private set; }
            public bool PollThrows { get; set; }

            public void Disconnect() => DisconnectCalls++;

            public bool PollWheelIdentity()
            {
                if (PollThrows)
                    throw new InvalidOperationException("wheel poll failed");
                PollCalls++;
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

        // ── Wheel poll failure ───────────────────────────────────────────

        [Fact]
        public void Update_PollThrows_DisconnectsWithShortCooldown()
        {
            var wheelbase = new StubWheelbase();
            int connectAttempts = 0;

            var monitor = Create(wheelbase, () =>
            {
                connectAttempts++;
                return true;
            });
            monitor.TryInitialConnect(); // attempt 1

            // Poll happens on the very first frame when cooldown is 0.
            // Make it throw to trigger disconnect.
            wheelbase.PollThrows = true;
            bool result = monitor.Update();

            Assert.False(result);
            Assert.False(monitor.IsConnected);

            // Short cooldown = 60 frames. Pump through cooldown, then reconnect.
            wheelbase.PollThrows = false;
            PumpFrames(monitor, 60);
            Assert.False(monitor.IsConnected);

            result = monitor.Update(); // exits cooldown → reconnect
            Assert.True(result);
            Assert.True(monitor.IsConnected);
            Assert.Equal(2, connectAttempts);
        }

        [Fact]
        public void Update_PollThrows_TearsDownWheelbase()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            // Poll happens on the first frame when cooldown is 0.
            wheelbase.PollThrows = true;
            monitor.Update();

            Assert.Equal(1, wheelbase.DisconnectCalls);
        }

        // ── ForceReconnect ───────────────────────────────────────────────

        [Fact]
        public void ForceReconnect_WhenConnected_DisconnectsAndReconnects()
        {
            var wheelbase = new StubWheelbase();
            bool connectedFired = false;

            var monitor = Create(wheelbase, () => true);
            monitor.Connected += () => connectedFired = true;
            monitor.TryInitialConnect();

            monitor.ForceReconnect();

            Assert.True(monitor.IsConnected);
            Assert.True(connectedFired);
            Assert.Equal(1, wheelbase.DisconnectCalls);
        }

        [Fact]
        public void ForceReconnect_WhenDisconnected_SkipsDisconnect()
        {
            var wheelbase = new StubWheelbase();
            int connectAttempts = 0;

            var monitor = Create(wheelbase, () => ++connectAttempts >= 2);
            monitor.TryInitialConnect(); // fails (attempt 1)

            Assert.False(monitor.IsConnected);
            Assert.Equal(0, wheelbase.DisconnectCalls);

            monitor.ForceReconnect(); // attempt 2 → succeeds

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

            monitor.ForceReconnect(); // attempt 2 → fails

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
        public void Update_SteadyState_PollsWheelIdentity()
        {
            var wheelbase = new StubWheelbase();
            var monitor = Create(wheelbase, () => true);
            monitor.TryInitialConnect();

            // Poll happens immediately on first frame, then every 30 frames
            PumpFrames(monitor, 120);

            // Expect polls at frames: 1, 31, 61, 91 = 4 polls
            Assert.True(wheelbase.PollCalls >= 4);
        }
    }
}
