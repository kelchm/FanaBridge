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
    /// Golden screen-state tests — the test-oracle payoff of the wire-driven twin
    /// (research §1.4, R8). They drive the REAL stack (ItmEncoder + ItmLifecycleController
    /// + ItmDisplayDriver over a RecordingTransport) with the outbound
    /// <see cref="TappedDeviceTransport"/> tap + <see cref="ItmFrameDecoder"/> +
    /// <see cref="VirtualItmDisplay"/> attached exactly as production wires them, and
    /// assert the FULL rendered screen state the twin publishes at each checkpoint along a
    /// session's ladder: bring-up → sync → values → suffix → wheel-button page change →
    /// game-exit DisplayReset → recovery → gate cycle → wheel hot-swap. Because the twin
    /// consumes only the bytes that actually went out, these are alacritty-style
    /// replay-and-diff goldens: any encoder or lifecycle change that alters what reaches
    /// the panel shows up here as a changed screen, with no hardware.
    ///
    /// The divergence test closes the loop: a value injected onto the wire that the driver
    /// never sent from its telemetry proves the twin renders what was SENT, not what the
    /// host intended.
    /// </summary>
    public class ItmScreenGoldenTests
    {
        private const byte Dev = ItmEncoder.DefaultDeviceId;   // the wheel OLED, device 3

        // ── Harness: the production seam (encoder → tap → twin) over the real driver ──

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected => true;
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
            public int Col03MaxInputReportLength => 64;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private sealed class Harness
        {
            public readonly ItmDisplayDriver Driver;
            public readonly VirtualItmDisplay Twin;
            public readonly ItmEncoder Encoder;   // the SAME encoder the driver writes through
            public readonly Clock Clock;

            public Harness()
            {
                Clock = new Clock();
                Twin = new VirtualItmDisplay(nowMs: Clock.Now);
                var tap = new TappedDeviceTransport(new RecordingTransport());
                tap.AttachObserver(Twin);
                Encoder = new ItmEncoder(tap);
                Driver = new ItmDisplayDriver(Encoder, Clock.Now);
            }

            public DisplayValuesSnapshot Screen => Twin.Snapshot;

            public void Update(GameData data)
            {
                Driver.Enabled = true;
                Driver.Start();                    // idempotent — re-arms after a disable
                Driver.Update(data);
                Twin.Tick(Driver.Lifecycle.State);
            }

            // Drives the driver while it is user-disabled (gated off), exactly as the
            // device instance does when the ITM toggle is off.
            public void UpdateDisabled(GameData data)
            {
                Driver.Enabled = false;
                Driver.Update(data);
                Twin.Tick(Driver.Lifecycle.State);
            }

            public void Push(byte[] report)
            {
                Driver.OnSubscriptionReport(report);
                Twin.OnSubscriptionReport(report);
            }

            public void Advance(long ms) => Clock.T += ms;

            // The wheel/hub/module hot-swap edge: the same cold-start signal the device
            // instance hands both the driver and the twin (invisible on the ITM channel).
            public void HotSwap()
            {
                Driver.OnWheelChanged();
                Twin.OnColdStart();
            }
        }

        // ── GameData ──────────────────────────────────────────────────────
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

        // The quick guide's page-1 example telemetry.
        private static GameData LapInfo()
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

        private static GameData TyreTemps()
        {
            var s = NewStatus();
            Set(s, "SpeedLocal", 164.0);
            Set(s, "Gear", "3");
            Set(s, "TyreTemperatureFrontLeft", 75.0);
            Set(s, "TyreTemperatureRearLeft", 82.0);
            Set(s, "TyreTemperatureFrontRight", 73.0);
            Set(s, "TyreTemperatureRearRight", 81.0);
            return Data(s);
        }

        // ── Firmware pushes ────────────────────────────────────────────────
        private static readonly byte[] LapInfoPush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");
        private static readonly byte[] TyrePush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");
        private static byte[] LapInfoUnsub => UnsubReport(0x00, 0x01, 0x82, 0x83, 0x04, 0x05);

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

        // ── Screen assertions ──────────────────────────────────────────────

        private static string Field(DisplayValueSlot? slot)
            => Assert.Single(Assert.IsAssignableFrom<DisplayValueSlot>(slot).Fields).Value;

        // Brings the driver to push-confirmed sync on page 1 and settles the twin.
        private static void SyncPageOne(Harness h, GameData data)
        {
            h.Update(data);            // bring-up: gate-on, enable, PageSet(1)
            h.Push(LapInfoPush);
            h.Advance(60);
            h.Update(data);            // Synced; first values
            h.Advance(30);
            h.Update(data);            // second tap + ParamDefs
            h.Advance(260);
            h.Update(data);            // throttle window passed
        }

        // ── The ladder ─────────────────────────────────────────────────────

        [Fact]
        public void BringUp_ShowsTheHostSelectedPage_WithPlaceholders()
        {
            var h = new Harness();
            var data = LapInfo();
            h.Update(data);            // bring-up sends PageSet(1) — the host selection

            var screen = h.Screen;
            Assert.NotNull(screen);
            Assert.Equal(ItmPage.LapInfo, screen!.Page);   // grounded from the observed PageSet
            Assert.Equal(1, screen.WirePage);
            Assert.True(screen.ShowingPlaceholders);        // nothing painted yet
            Assert.Equal("--- / -", Field(screen.LeftTop));
            Assert.Equal("-", screen.GearText);
            Assert.Equal("---", screen.SpeedText);
        }

        [Fact]
        public void SyncedWithValuesAndSuffixes_RendersTheWholeGuidePage()
        {
            var h = new Harness();
            SyncPageOne(h, LapInfo());

            var screen = h.Screen;
            Assert.Equal(ItmPage.LapInfo, screen!.Page);
            Assert.Equal(ItmLifecycleState.Synced, screen.State);
            Assert.False(screen.ShowingPlaceholders);

            // Full screen — every slot, the suffix decorations, and the center zone.
            Assert.Equal("LAPS:", screen.LeftTop!.Label);
            Assert.Equal("15 /73", Field(screen.LeftTop));       // lap total suffix
            Assert.Equal("POSITION:", screen.LeftBottom!.Label);
            Assert.Equal("02 /20", Field(screen.LeftBottom));    // field-size suffix
            Assert.Equal("CURRENT LAP:", screen.RightTop!.Label);
            Assert.Equal("01:36.911", Field(screen.RightTop));
            Assert.Equal("LAST LAP:", screen.RightBottom!.Label);
            Assert.Equal("02:14.169", Field(screen.RightBottom));
            Assert.Equal("6", screen.GearText);
            Assert.Equal("268", screen.SpeedText);
        }

        [Fact]
        public void SuffixTracksAMovingTotal()
        {
            // The lap total is a live suffix: when the total laps changes, the driver
            // re-sends ParamDefs and the twin's decoration follows.
            var h = new Harness();
            var s = NewStatus();
            Set(s, "SpeedLocal", 268.0);
            Set(s, "Gear", "6");
            Set(s, "CurrentLap", 15);
            Set(s, "TotalLaps", 73);
            Set(s, "Position", 2);
            Set(s, "OpponentsCount", 20);
            var data = Data(s);
            SyncPageOne(h, data);
            Assert.Equal("15 /73", Field(h.Screen!.LeftTop));

            // Field grows to 24 cars: the position suffix follows the new total.
            Set(s, "OpponentsCount", 24);
            h.Advance(300);
            h.Update(data);
            Assert.Equal("02 /24", Field(h.Screen!.LeftBottom));
        }

        [Fact]
        public void WheelButtonPageChange_ReplacesTheWholeScreen()
        {
            var h = new Harness();
            SyncPageOne(h, LapInfo());
            Assert.Equal(ItmPage.LapInfo, h.Screen!.Page);

            // The wheel button navigates to page 5: the firmware drops the page-1 subs
            // and pushes the page-5 set. No PageSet is on the OUT wire — the twin infers
            // the page from the pushed parameter set.
            var tyre = TyreTemps();
            h.Push(LapInfoUnsub);
            h.Advance(2);
            h.Push(TyrePush);
            h.Advance(60);
            h.Update(tyre);            // Synced on page 5; repaint
            h.Advance(30);
            h.Update(tyre);            // tap + temp-unit ParamDefs
            h.Advance(260);
            h.Update(tyre);

            var screen = h.Screen;
            Assert.Equal(ItmPage.TyreTemps, screen!.Page);
            Assert.Equal(5, screen.WirePage);
            Assert.False(screen.ShowingPlaceholders);
            Assert.Equal("FL TIRE TEMP:", screen.LeftTop!.Label);
            Assert.Equal("075 C", Field(screen.LeftTop));
            Assert.Equal("082 C", Field(screen.LeftBottom));
            Assert.Equal("073 C", Field(screen.RightTop));
            Assert.Equal("081 C", Field(screen.RightBottom));
            Assert.Equal("3", screen.GearText);
            Assert.Equal("164", screen.SpeedText);
        }

        [Fact]
        public void GameExit_DisplayReset_ClearsToPlaceholders_OnTheSamePage()
        {
            var h = new Harness();
            var data = LapInfo();
            SyncPageOne(h, data);
            Assert.False(h.Screen!.ShowingPlaceholders);

            // The game exits: the lifecycle emits the real DisplayReset (FF 05 05). The
            // twin follows the byte — no live/dead edge heuristic.
            h.Advance(300);
            h.Update(Data(NewStatus(), gameRunning: false));

            var screen = h.Screen;
            Assert.True(screen!.ShowingPlaceholders);
            Assert.Equal(ItmPage.LapInfo, screen.Page);   // page structure retained
            Assert.Equal("--- / -", Field(screen.LeftTop));
            Assert.Equal("--:--.-", Field(screen.RightTop));
            Assert.Equal("-", screen.GearText);
        }

        [Fact]
        public void PushDeadlineMissed_EntersRecovery_ScreenHeld()
        {
            // Bring-up with no firmware push: the lifecycle climbs into Recovery. The
            // twin has the host-selected page from the PageSet but nothing to paint, and
            // stamps the Recovery caption the owner elicits.
            var h = new Harness();
            var data = LapInfo();
            h.Update(data);            // bring-up: PageSet(1)

            h.Advance(h.Driver.Lifecycle.PushDeadlineMs + 100);
            h.Update(data);

            Assert.Equal(ItmLifecycleState.Recovery, h.Driver.Lifecycle.State);
            var screen = h.Screen;
            Assert.Equal(ItmLifecycleState.Recovery, screen!.State);
            Assert.True(screen.ShowingPlaceholders);
        }

        [Fact]
        public void GateCycle_BlanksThenRestores()
        {
            var h = new Harness();
            var data = LapInfo();
            SyncPageOne(h, data);
            Assert.Equal("15 /73", Field(h.Screen!.LeftTop));

            // User turns ITM off: the driver gates the panel off (FF 05 02 00). The twin
            // drops to the blank legacy view.
            h.Advance(300);
            h.UpdateDisabled(data);
            var off = h.Screen;
            Assert.Equal(ItmLifecycleState.Disabled, off!.State);
            Assert.Null(off.Page);
            Assert.Null(off.LeftTop);
            Assert.Null(off.GearText);

            // User turns ITM back on: the lifecycle re-runs bring-up and re-syncs, and
            // the twin re-grounds and repaints from the fresh session.
            h.Advance(300);
            h.Update(data);            // re-armed; bring-up
            h.Push(LapInfoPush);
            h.Advance(60);
            h.Update(data);
            h.Advance(30);
            h.Update(data);
            h.Advance(260);
            h.Update(data);

            var on = h.Screen;
            Assert.Equal(ItmPage.LapInfo, on!.Page);
            Assert.Equal("15 /73", Field(on.LeftTop));
            Assert.Equal("6", on.GearText);
        }

        [Fact]
        public void WheelHotSwap_ColdStartsThenReGrounds()
        {
            var h = new Harness();
            SyncPageOne(h, LapInfo());
            Assert.NotNull(h.Screen);

            // A wheel/hub/module change: the panel is cold with no trace on the ITM
            // channel. The twin drops everything (a stale screen never outlives the
            // session it described).
            h.HotSwap();
            Assert.Null(h.Screen);

            // The new wheel brings up and syncs onto page 5: the twin re-grounds from the
            // fresh frames alone.
            var tyre = TyreTemps();
            h.Advance(10);
            // The lifecycle's cold restart targets page 1 by default; drive its own
            // bring-up + a page-5 push (a wheel that rests on Tyre Temps).
            h.Update(tyre);            // bring-up on the default page
            h.Push(LapInfoUnsub);
            h.Advance(2);
            h.Push(TyrePush);
            h.Advance(60);
            h.Update(tyre);
            h.Advance(30);
            h.Update(tyre);
            h.Advance(260);
            h.Update(tyre);

            var screen = h.Screen;
            Assert.Equal(ItmPage.TyreTemps, screen!.Page);
            Assert.Equal("075 C", Field(screen.LeftTop));
        }

        // ── The divergence test (wire, not intent) ─────────────────────────

        [Fact]
        public void TwinFollowsTheWire_NotTheDriversIntent()
        {
            // The driver latches its intent from telemetry (lap 15) and sends it. Then a
            // frame the driver's telemetry never produced is injected on the SAME wire —
            // a lap value of 42 at the subscribed handle, encoded directly through the
            // encoder the twin taps (this is the "impossible via the driver's public API"
            // mutation the spec calls for, simulated by tapping a manually-built frame).
            // A model-driven mirror would still show the driver's intent (15); the
            // wire-driven twin must show what actually went out (42).
            var h = new Harness();
            var data = LapInfo();
            SyncPageOne(h, data);
            Assert.Equal("15 /73", Field(h.Screen!.LeftTop));   // intent and wire agree so far

            // Inject the divergent value straight onto the wire. Firmware handle 2 is LAP
            // on page 1 (the push's 0x82 slot marker strips to handle 2). The driver never
            // encoded this value from its telemetry.
            Assert.True(h.Encoder.SendValues(
                new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 42) }, Dev));
            h.Advance(300);
            h.Twin.Tick(h.Driver.Lifecycle.State);

            // The twin renders the wire value, diverging from the driver's latched intent.
            Assert.Equal("42 /73", Field(h.Screen!.LeftTop));
        }
    }
}
