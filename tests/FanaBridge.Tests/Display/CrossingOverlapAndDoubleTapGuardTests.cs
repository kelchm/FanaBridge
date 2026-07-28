using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// E7 risk pins: crossing-overlap law (director + lifecycle) and double-tap ownership
    /// (driver owns ValueDoubleTapMs / DefDoubleTapMs — E7 must not add a second tap).
    /// </summary>
    public class CrossingOverlapAndDoubleTapGuardTests
    {
        // ── Lifecycle harness (mirrors ItmLifecycleControllerTests) ──────

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> Sent { get; } = new List<byte[]>();
            public bool SendReturns { get; set; } = true;

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return SendReturns;
            }

            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private static bool IsPageSetTo(byte[] r, byte page)
            => r.Length > 4 && r[1] == 0x05 && r[2] == 0x04 && r[4] == page;

        private static List<ItmSubscription> PushFor(byte page, byte deviceId = 3)
        {
            IReadOnlyList<ushort> ps = null;
            foreach (var p in ItmDeviceCatalog.PagesFor(deviceId))
                if (p.Number == page)
                    ps = p.Params;
            if (ps == null)
                throw new InvalidOperationException("no such page " + page);
            var list = new List<ItmSubscription>();
            for (int i = 0; i < ps.Count; i++)
                list.Add(new ItmSubscription((byte)i, ps[i], 0x12));
            return list;
        }

        private static void Sync(ItmLifecycleController c, Clock clock, bool live = true)
        {
            c.Start();
            c.Tick(live);
            c.OnPush(PushFor(c.DefaultPage));
            clock.T += c.AccumulateWindowMs;
            c.Tick(live);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
        }

        // ── Crossing-overlap ─────────────────────────────────────────────

        [Fact]
        public void CrossingOverlap_SecondRequestQueuedUntilFirstLands()
        {
            // ROUND-3 LAW (engine-replan §6): "two crossings never overlap; second starts
            // when first lands, ~4 s worst case"
            //
            // ACTUAL (pinned here):
            // - Host RequestPage while Switching queues one _pendingRequest; no second
            //   BeginSwitch until ConfirmSync of the first. Concurrent procedures do not run.
            // - "~4 s" is design-era (two Legacy UI crossings), NOT SwitchQuietMs(50) +
            //   PageSetSpacingMs(100) + PushDeadlineMs(250). See CrossingOverlap_TimingConstants.
            var t = new RecordingTransport();
            var clock = new Clock();
            var c = new ItmLifecycleController(new ItmEncoder(t), deviceId: 3, clock.Now, _ => { });
            Sync(c, clock);

            // First crossing: page 1 → 2.
            c.RequestPage(2);
            Assert.Equal(ItmLifecycleState.Switching, c.State);

            // Second request while first is in flight — queued, not overlapping.
            c.RequestPage(5);
            Assert.Equal(ItmLifecycleState.Switching, c.State);
            Assert.Equal(1, c.CurrentPage); // still on pre-switch page (DefaultPage)
            // Target of the in-flight procedure remains 2 (pending holds 5).
            var pending = typeof(ItmLifecycleController).GetField(
                "_pendingRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(pending);
            Assert.Equal((byte)5, pending!.GetValue(c));

            // Drain quiet window + PageSet for page 2.
            clock.T += c.SwitchQuietMs;
            c.Tick(true);
            Assert.Contains(t.Sent, r => IsPageSetTo(r, 2));
            // No PageSet for 5 yet.
            Assert.DoesNotContain(t.Sent, r => IsPageSetTo(r, 5));

            // First lands → ConfirmSync starts second BeginSwitch.
            c.OnPush(PushFor(2));
            clock.T += c.AccumulateWindowMs;
            c.Tick(true);
            Assert.Equal(ItmLifecycleState.Switching, c.State);
            Assert.Equal((byte)0, pending.GetValue(c)); // pending consumed

            // Second procedure's PageSet fires after its quiet window.
            clock.T += c.SwitchQuietMs;
            c.Tick(true);
            Assert.Contains(t.Sent, r => IsPageSetTo(r, 5));
        }

        [Fact]
        public void CrossingOverlap_TimingConstants_NotFourSeconds()
        {
            // Explicit non-amendment of the round-3 "~4 s" figure: built defaults differ.
            var c = new ItmLifecycleController(
                new ItmEncoder(new RecordingTransport()), deviceId: 3, () => 0L, _ => { });
            Assert.Equal(50, c.SwitchQuietMs);
            Assert.Equal(100, c.PageSetSpacingMs);
            Assert.Equal(250, c.PushDeadlineMs);
            // Sum of primary switch path ≪ 4000 ms.
            Assert.True(c.SwitchQuietMs + c.PageSetSpacingMs + c.PushDeadlineMs < 4000);
        }

        // ── Double-tap guard (H) — wire-trace, not textual scan ─────────

        // Lap Info push (device 3) — same as ItmDisplayDriverTests.
        private static readonly byte[] LapInfoPush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static GameData EmptyRunningData()
        {
            var statusType = typeof(GameData).Assembly
                .GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));
            var status = FormatterServices.GetUninitializedObject(statusType);
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { true });
            return d;
        }

        [Fact]
        public void E7_AddsNoSecondDoubleTap_WireTrace()
        {
            // One driver-owned value double-tap sequence after sync; no additional
            // mapper/director send or delay.
            var t = new RecordingTransport();
            var clock = new Clock();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now);
            var data = EmptyRunningData();

            driver.Start();
            driver.Update(data); // bring-up
            driver.OnSubscriptionReport(LapInfoPush);
            clock.T += 50;
            t.Sent.Clear();
            driver.Update(data); // judged → Synced; first paint
            Assert.Equal(1, t.Sent.Count(IsValueUpdate));

            clock.T += driver.ValueDoubleTapMs;
            driver.Update(data); // second tap only
            Assert.Equal(2, t.Sent.Count(IsValueUpdate));

            // Further frames inside refresh interval: no third value send from E7.
            clock.T += 1;
            driver.Update(data);
            Assert.Equal(2, t.Sent.Count(IsValueUpdate));
        }

        private static bool IsValueUpdate(byte[] r)
            => r.Length > 2 && r[1] == 0x05 && r[2] == 0x01;
    }

}
