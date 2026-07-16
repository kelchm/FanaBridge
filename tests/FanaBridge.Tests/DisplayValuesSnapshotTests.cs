using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The display-values screen state, retargeted at the wire-driven twin
    /// (<see cref="VirtualItmDisplay"/>) — the producer of the snapshot the UI mirror
    /// consumes since the snapshot bookkeeping left <see cref="ItmDisplayDriver"/>. These
    /// drive the REAL driver + lifecycle over a RecordingTransport wrapped in the REAL
    /// <see cref="TappedDeviceTransport"/> (the twin attached as its observer, the same
    /// seam production uses) with real SimHub telemetry, then assert what the twin renders
    /// from the frames that ACTUALLY went out: per-slot rendered strings, page identity,
    /// placeholder behavior around the wire's own DisplayReset, change-gated + throttled
    /// recomposition, and teardown. The twin follows the bytes, so these double as the
    /// telemetry→wire→screen integration goldens. Absolute wire output is pinned
    /// separately by the golden frame-sequence gate in
    /// <see cref="DisplayCustomizationWiringTests"/>; nothing here asserts frames.
    /// </summary>
    public class DisplayValuesSnapshotTests
    {
        // ── Test doubles (see ItmDisplayDriverTests) ─────────────────────

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return true;
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

        // The production seam: the driver's encoder writes through the tap, the twin is
        // its observer, and both share one injected clock so their throttles stay in
        // step. Update ticks the twin with the driver's lifecycle state (the host-side
        // annotation the wire does not carry); Push feeds ONE firmware report to both
        // consumers of the push stream, exactly as the device instance does per frame.
        private sealed class Mirror
        {
            public readonly ItmDisplayDriver Driver;
            public readonly VirtualItmDisplay Twin;

            public Mirror(ItmDisplayDriver driver, VirtualItmDisplay twin)
            {
                Driver = driver;
                Twin = twin;
            }

            public DisplayValuesSnapshot Snapshot => Twin.Snapshot;
            public bool IsRunning => Driver.IsRunning;
            public bool Enabled { set => Driver.Enabled = value; }

            public void Start() => Driver.Start();
            public void Update(GameData data)
            {
                Driver.Update(data);
                Twin.Tick(Driver.Lifecycle.State);
            }
            public void Push(byte[] report)
            {
                Driver.OnSubscriptionReport(report);
                Twin.OnSubscriptionReport(report);
            }
            // The device instance cold-starts the twin on the same teardown edge that
            // stops the driver (disconnect, End) — a stale screen never outlives its
            // session.
            public void Stop()
            {
                Driver.Stop();
                Twin.OnColdStart();
            }
        }

        private static Mirror MakeMirror(out Clock clock)
        {
            var c = new Clock();
            clock = c;
            var twin = new VirtualItmDisplay(nowMs: c.Now);
            var tap = new TappedDeviceTransport(new RecordingTransport());
            tap.AttachObserver(twin);
            var driver = new ItmDisplayDriver(new ItmEncoder(tap), c.Now);
            return new Mirror(driver, twin);
        }

        // ── GameData (see ItmTelemetryTests) ─────────────────────────────
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);
        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        // The quick guide's page-1 example values: 15/73 laps, P2 of 20, gear 6 at 268,
        // current lap 1:36.911, last lap 2:14.169.
        private static GameData LapInfoData()
        {
            var s = NewStatus();
            Set(s, "SpeedLocal", 268.0);
            Set(s, "Gear", "6");
            Set(s, "CurrentLap", 15);
            Set(s, "TotalLaps", 73);
            Set(s, "Position", 2);
            Set(s, "OpponentsCount", 20);
            Set(s, "CurrentLapTime", TimeSpan.FromSeconds(96.911));
            Set(s, "LastLapTime", TimeSpan.FromSeconds(134.169));
            return Data(s);
        }

        // ── Firmware push reports (see ItmDisplayDriverTests) ────────────

        private static readonly byte[] LapInfoPush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");

        private static readonly byte[] TyrePush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static byte[] UnsubReport(params byte[] handles)
        {
            var list = new List<byte> { 0xFF, 0x05, 0x01 };
            foreach (var h in handles) { list.Add(0x03); list.Add(h); list.Add(0xFF); list.Add(0xFF); list.Add(0x00); }
            return list.ToArray();
        }

        // The page-1 firmware handles, unsubscribed as the front edge of a wheel-button
        // page change (real firmware drops the old set before pushing the new one).
        private static byte[] LapInfoUnsub => UnsubReport(0x00, 0x01, 0x82, 0x83, 0x04, 0x05);

        // Brings the driver to push-confirmed sync, completes the post-sync repaint
        // (values, tap, ParamDefs), then lets the snapshot throttle window pass so the
        // twin's published snapshot reflects the settled values + suffixes.
        private static void SyncAndSettle(Mirror m, Clock clock, GameData data, byte[]? push = null)
        {
            m.Start();
            m.Update(data);                    // bring-up
            m.Push(push ?? LapInfoPush);
            clock.T += 50;
            m.Update(data);                    // judged → Synced; first paint
            clock.T += 20;
            m.Update(data);                    // second tap + ParamDefs
            Assert.True(m.IsRunning);
            clock.T += 250;
            m.Update(data);                    // snapshot throttle window passed
        }

        private static string FieldValue(DisplayValueSlot? slot)
            => Assert.Single(Assert.IsAssignableFrom<DisplayValueSlot>(slot).Fields).Value;

        // ── Composition ──────────────────────────────────────────────────

        [Fact]
        public void SyncedWithValues_RendersTheGuideExamplePage()
        {
            var m = MakeMirror(out var clock);
            SyncAndSettle(m, clock, LapInfoData());

            var snap = m.Snapshot;
            Assert.NotNull(snap);
            Assert.Equal(ItmPage.LapInfo, snap!.Page);
            Assert.Equal(1, snap.WirePage);
            Assert.Equal("Lap Info", snap.PageName);
            Assert.Equal(ItmLifecycleState.Synced, snap.State);
            Assert.False(snap.ShowingPlaceholders);

            Assert.Equal("LAPS:", snap.LeftTop!.Label);
            Assert.Equal("15 /73", FieldValue(snap.LeftTop));
            Assert.Equal("POSITION:", snap.LeftBottom!.Label);
            Assert.Equal("02 /20", FieldValue(snap.LeftBottom));
            Assert.Equal("CURRENT LAP:", snap.RightTop!.Label);
            Assert.Equal("01:36.911", FieldValue(snap.RightTop));
            Assert.Equal("LAST LAP:", snap.RightBottom!.Label);
            Assert.Equal("02:14.169", FieldValue(snap.RightBottom));

            Assert.Equal("6", snap.GearText);
            Assert.Equal("268", snap.SpeedText);
        }

        [Fact]
        public void UnchangedFrames_KeepTheSameSnapshotReference()
        {
            var m = MakeMirror(out var clock);
            var data = LapInfoData();
            SyncAndSettle(m, clock, data);

            var before = m.Snapshot;
            for (int i = 0; i < 20; i++)
            {
                clock.T += 300;   // periodic unchanged re-asserts happen in here
                m.Update(data);
            }
            Assert.Same(before, m.Snapshot);
        }

        [Fact]
        public void ChangedValue_Recomposes_ButOnlyAfterTheThrottleWindow()
        {
            var m = MakeMirror(out var clock);
            // Pin the window explicitly: this test asserts the throttle CONTRACT and
            // must not drift with the production default (tuned for UI liveness).
            m.Twin.SnapshotIntervalMs = 250;
            var s = NewStatus();
            Set(s, "SpeedLocal", 268.0);
            Set(s, "Gear", "6");
            Set(s, "CurrentLap", 15);
            Set(s, "TotalLaps", 73);
            Set(s, "Position", 2);
            Set(s, "OpponentsCount", 20);
            Set(s, "CurrentLapTime", TimeSpan.FromSeconds(96.911));
            Set(s, "LastLapTime", TimeSpan.FromSeconds(134.169));
            var data = Data(s);
            SyncAndSettle(m, clock, data);
            var settled = m.Snapshot;

            // A changed value sent inside the throttle window does not recompose yet…
            Set(s, "CurrentLap", 16);
            clock.T += 100;
            m.Update(data);   // the new value goes on the wire and the twin paints it
            Assert.Same(settled, m.Snapshot);

            // …but the held change composes once the window passes, with no further edge.
            clock.T += 200;
            m.Update(data);
            var recomposed = m.Snapshot;
            Assert.NotSame(settled, recomposed);
            Assert.Equal("16 /73", FieldValue(recomposed!.LeftTop));
        }

        [Fact]
        public void GameExit_ShowsTheResetPlaceholders_AndResumeRepaints()
        {
            var m = MakeMirror(out var clock);
            var data = LapInfoData();
            SyncAndSettle(m, clock, data);

            // Game exits: the lifecycle sends the actual DisplayReset frame (FF 05 05);
            // the twin observes it on the wire and clears its painted fields — no edge
            // heuristic, the twin just follows the bytes.
            clock.T += 300;
            m.Update(Data(NewStatus(), gameRunning: false));
            var idle = m.Snapshot;
            Assert.NotNull(idle);
            Assert.Equal(ItmLifecycleState.Synced, idle!.State);
            Assert.True(idle.ShowingPlaceholders);
            Assert.Equal(ItmPage.LapInfo, idle.Page);   // the page structure stays
            Assert.Equal("--- / -", FieldValue(idle.LeftTop));
            Assert.Equal("--:--.-", FieldValue(idle.RightTop));
            Assert.Equal("-", idle.GearText);
            Assert.Equal("---", idle.SpeedText);

            // Telemetry returns: the repaint puts values back on the display and the
            // snapshot follows once the throttle window passes.
            clock.T += 300;
            m.Update(data);
            clock.T += 300;
            m.Update(data);
            var resumed = m.Snapshot;
            Assert.False(resumed!.ShowingPlaceholders);
            Assert.Equal("15 /73", FieldValue(resumed.LeftTop));
        }

        [Fact]
        public void SyncedBeforeAnyValues_ShowsPlaceholders()
        {
            // Bring-up with no game running: the display is synced but shows the
            // post-reset placeholders (nothing has been sent).
            var m = MakeMirror(out var clock);
            var idle = Data(NewStatus(), gameRunning: false);
            m.Start();
            m.Update(idle);
            m.Push(LapInfoPush);
            clock.T += 50;
            m.Update(idle);
            clock.T += 250;
            m.Update(idle);

            var snap = m.Snapshot;
            Assert.NotNull(snap);
            Assert.Equal(ItmLifecycleState.Synced, snap!.State);
            Assert.True(snap.ShowingPlaceholders);
            Assert.Equal("--- / -", FieldValue(snap.LeftTop));
            Assert.Equal("--- / -", FieldValue(snap.LeftBottom));
            Assert.Equal("--:--.-", FieldValue(snap.RightTop));
        }

        [Fact]
        public void WheelButtonPageChange_FollowsToTheNewPage()
        {
            var m = MakeMirror(out var clock);
            var s = NewStatus();
            Set(s, "SpeedLocal", 164.0);
            Set(s, "Gear", "3");
            Set(s, "TyreTemperatureFrontLeft", 75.0);
            Set(s, "TyreTemperatureRearLeft", 82.0);
            Set(s, "TyreTemperatureFrontRight", 73.0);
            Set(s, "TyreTemperatureRearRight", 81.0);
            var data = Data(s);
            SyncAndSettle(m, clock, data);   // page 1 first

            // The wheel button moves to page 5 (Tyre Temps): the firmware drops the
            // page-1 subscriptions and pushes the page-5 set — the twin infers the new
            // page from the pushed parameter set (no PageSet on the OUT wire).
            m.Push(LapInfoUnsub);
            clock.T += 2;
            m.Push(TyrePush);
            clock.T += 50;
            m.Update(data);                  // adopted; repaint
            clock.T += 20;
            m.Update(data);                  // tap + defs (temp-unit suffixes)
            clock.T += 250;
            m.Update(data);                  // throttle window passed

            var snap = m.Snapshot;
            Assert.Equal(ItmPage.TyreTemps, snap!.Page);
            Assert.Equal(5, snap.WirePage);
            Assert.Equal("FL TIRE TEMP:", snap.LeftTop!.Label);
            Assert.Equal("075 C", FieldValue(snap.LeftTop));
            Assert.Equal("082 C", FieldValue(snap.LeftBottom));
            Assert.Equal("073 C", FieldValue(snap.RightTop));
            Assert.Equal("081 C", FieldValue(snap.RightBottom));
            Assert.Equal("3", snap.GearText);
            Assert.Equal("164", snap.SpeedText);
        }

        [Fact]
        public void GearWireForm_IsLatchedWithThePaintedValue_NotTheLiveSubscription()
        {
            // Formula V3-class displays declare GEAR as ASCII text (dataType low nibble
            // 1). The twin latches the declared wire form WITH the painted value, so an
            // unsubscribe of the gear handle (the front edge of a page change, within the
            // identity grace window) drops the live table entry but leaves the painted
            // ASCII '6' — and its text form — on the held page. Reading the form from the
            // live map instead would find no gear entry, fall back to numeric, and
            // misdecode '6' (0x36) as "54".
            var textGearPush = HexToBytes(
                "ff0501" + "0300010034" + "0301040011" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");
            var m = MakeMirror(out var clock);
            var s = NewStatus();
            Set(s, "SpeedLocal", 268.0);
            Set(s, "Gear", "6");
            Set(s, "CurrentLap", 15);
            Set(s, "TotalLaps", 73);
            var data = Data(s);
            SyncAndSettle(m, clock, data, textGearPush);
            Assert.Equal("6", m.Snapshot!.GearText);
            var settled = m.Snapshot;

            // The page change begins: the firmware unsubscribes the gear handle. Within
            // the grace window the page is still held and the painted glyphs stay put.
            m.Push(UnsubReport(0x01));
            clock.T += 10;                  // inside the page-identity grace window
            m.Update(data);

            var snap = m.Snapshot;
            Assert.Same(settled, snap);     // nothing on the screen moved within grace
            Assert.Equal(ItmLifecycleState.Synced, snap!.State);
            Assert.Equal("15 /73", FieldValue(snap.LeftTop));
            Assert.Equal("6", snap.GearText);   // not the numeric misdecode "54"
        }

        [Fact]
        public void LegacyPage_HasNoSlots()
        {
            var m = MakeMirror(out var clock);
            var data = LapInfoData();
            SyncAndSettle(m, clock, data);

            // An unsubscribe-all with nothing following = the legacy ITM page.
            m.Push(UnsubReport(0, 1, 0x82, 0x83, 4, 5));
            clock.T += 50;
            m.Update(data);   // judged: empty set → grace opens
            clock.T += 150;
            m.Update(data);   // grace expired → legacy page adopted
            clock.T += 250;
            m.Update(data);

            var snap = m.Snapshot;
            Assert.Equal(ItmPage.Legacy, snap!.Page);
            Assert.Equal("Legacy", snap.PageName);
            Assert.Null(snap.LeftTop);
            Assert.Null(snap.RightBottom);
            Assert.Null(snap.GearText);
            Assert.Null(snap.SpeedText);
        }

        [Fact]
        public void UserDisable_SurfacesTheDisabledState()
        {
            var m = MakeMirror(out var clock);
            var data = LapInfoData();
            SyncAndSettle(m, clock, data);

            m.Enabled = false;
            clock.T += 300;
            m.Update(data);
            Assert.Equal(ItmLifecycleState.Disabled, m.Snapshot!.State);
        }

        [Fact]
        public void Stop_ClearsThePublishedSnapshot()
        {
            var m = MakeMirror(out var clock);
            SyncAndSettle(m, clock, LapInfoData());
            Assert.NotNull(m.Snapshot);

            m.Stop();
            Assert.Null(m.Snapshot);
        }
    }
}
