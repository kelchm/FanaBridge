using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Display;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The display-values snapshot <see cref="ItmDisplayDriver"/> composes for the UI's
    /// live mirror: per-slot rendered strings from the values/suffixes as last sent,
    /// page identity + wire page, lifecycle state, placeholder behavior around resets,
    /// change-gated + throttled recomposition, and teardown on Stop. Wire behavior is
    /// covered by <see cref="ItmDisplayDriverTests"/> and the golden frame-sequence
    /// gate in <see cref="DisplayCustomizationWiringTests"/> (which pins the absolute
    /// col03 output of a scripted session, so the snapshot/observer path cannot add,
    /// drop, or reorder a frame unnoticed) — nothing here asserts frames.
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

        private static ItmDisplayDriver MakeDriver(out Clock clock)
        {
            clock = new Clock();
            return new ItmDisplayDriver(new ItmEncoder(new RecordingTransport()), clock.Now);
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

        // Brings the driver to push-confirmed sync, completes the post-sync repaint
        // (values, tap, ParamDefs), then lets the snapshot throttle window pass so the
        // published snapshot reflects the settled values + suffixes.
        private static void SyncAndSettle(ItmDisplayDriver driver, Clock clock, GameData data,
            byte[]? push = null)
        {
            driver.Start();
            driver.Update(data);                    // bring-up
            driver.OnSubscriptionReport(push ?? LapInfoPush);
            clock.T += 50;
            driver.Update(data);                    // judged → Synced; first paint
            clock.T += 20;
            driver.Update(data);                    // second tap + ParamDefs
            Assert.True(driver.IsRunning);
            clock.T += 250;
            driver.Update(data);                    // snapshot throttle window passed
        }

        private static string FieldValue(DisplayValueSlot? slot)
            => Assert.Single(Assert.IsAssignableFrom<DisplayValueSlot>(slot).Fields).Value;

        // ── Composition ──────────────────────────────────────────────────

        [Fact]
        public void SyncedWithValues_RendersTheGuideExamplePage()
        {
            var driver = MakeDriver(out var clock);
            SyncAndSettle(driver, clock, LapInfoData());

            var snap = driver.ValuesSnapshot;
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
            var driver = MakeDriver(out var clock);
            var data = LapInfoData();
            SyncAndSettle(driver, clock, data);

            var before = driver.ValuesSnapshot;
            for (int i = 0; i < 20; i++)
            {
                clock.T += 300;   // periodic unchanged re-asserts happen in here
                driver.Update(data);
            }
            Assert.Same(before, driver.ValuesSnapshot);
        }

        [Fact]
        public void ChangedValue_Recomposes_ButOnlyAfterTheThrottleWindow()
        {
            var driver = MakeDriver(out var clock);
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
            SyncAndSettle(driver, clock, data);
            var settled = driver.ValuesSnapshot;

            // A changed value sent inside the throttle window does not recompose yet…
            Set(s, "CurrentLap", 16);
            clock.T += 100;
            driver.Update(data);   // the new value goes on the wire
            Assert.Same(settled, driver.ValuesSnapshot);

            // …but the held change composes once the window passes, with no further edge.
            clock.T += 200;
            driver.Update(data);
            var recomposed = driver.ValuesSnapshot;
            Assert.NotSame(settled, recomposed);
            Assert.Equal("16 /73", FieldValue(recomposed!.LeftTop));
        }

        [Fact]
        public void GameExit_ShowsTheResetPlaceholders_AndResumeRepaints()
        {
            var driver = MakeDriver(out var clock);
            var data = LapInfoData();
            SyncAndSettle(driver, clock, data);

            // Game exits: the lifecycle sends DisplayReset; the mirror follows suit.
            clock.T += 300;
            driver.Update(Data(NewStatus(), gameRunning: false));
            var idle = driver.ValuesSnapshot;
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
            driver.Update(data);
            clock.T += 300;
            driver.Update(data);
            var resumed = driver.ValuesSnapshot;
            Assert.False(resumed!.ShowingPlaceholders);
            Assert.Equal("15 /73", FieldValue(resumed.LeftTop));
        }

        [Fact]
        public void SyncedBeforeAnyValues_ShowsPlaceholders()
        {
            // Bring-up with no game running: the display is synced but shows the
            // post-reset placeholders (nothing has been sent).
            var driver = MakeDriver(out var clock);
            var idle = Data(NewStatus(), gameRunning: false);
            driver.Start();
            driver.Update(idle);
            driver.OnSubscriptionReport(LapInfoPush);
            clock.T += 50;
            driver.Update(idle);
            clock.T += 250;
            driver.Update(idle);

            var snap = driver.ValuesSnapshot;
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
            var driver = MakeDriver(out var clock);
            var s = NewStatus();
            Set(s, "SpeedLocal", 164.0);
            Set(s, "Gear", "3");
            Set(s, "TyreTemperatureFrontLeft", 75.0);
            Set(s, "TyreTemperatureRearLeft", 82.0);
            Set(s, "TyreTemperatureFrontRight", 73.0);
            Set(s, "TyreTemperatureRearRight", 81.0);
            var data = Data(s);
            SyncAndSettle(driver, clock, data);   // page 1 first

            // The wheel button moves to page 5 (Tyre Temps).
            driver.OnSubscriptionReport(TyrePush);
            clock.T += 50;
            driver.Update(data);                  // adopted; repaint
            clock.T += 20;
            driver.Update(data);                  // tap + defs (temp-unit suffixes)
            clock.T += 250;
            driver.Update(data);                  // throttle window passed

            var snap = driver.ValuesSnapshot;
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
        public void GearWireForm_IsLatchedAtSendTime_NotReadFromLiveSubscriptions()
        {
            // Formula V3-class displays declare GEAR as ASCII text (dataType low nibble
            // 1). A wheel-button page change front-runs with unsubscribe entries that
            // leave the LIVE subscription map immediately, while the state stays Synced
            // through the accumulate/grace windows. A compose landing in that window
            // must render the latched ASCII '6' with the wire form latched at the same
            // send — resolving it from the live map would find no gear entry, fall back
            // to numeric, and misdecode '6' (0x36) as "54".
            var textGearPush = HexToBytes(
                "ff0501" + "0300010034" + "0301040011" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");
            var driver = MakeDriver(out var clock);
            var s = NewStatus();
            Set(s, "SpeedLocal", 268.0);
            Set(s, "Gear", "6");
            Set(s, "CurrentLap", 15);
            Set(s, "TotalLaps", 73);
            var data = Data(s);
            SyncAndSettle(driver, clock, data, textGearPush);
            Assert.Equal("6", driver.ValuesSnapshot!.GearText);
            var settled = driver.ValuesSnapshot;

            // A changed value sent inside the throttle window keeps a recompose pending…
            Set(s, "CurrentLap", 16);
            clock.T += 100;
            driver.Update(data);

            // …then the page change begins: the firmware unsubscribes the gear handle.
            driver.OnSubscriptionReport(UnsubReport(0x01));
            clock.T += 160;   // past the accumulate window (Synced, in grace) and the compose throttle
            driver.Update(data);

            var snap = driver.ValuesSnapshot;
            Assert.NotSame(settled, snap);   // the pending change composed on this tick
            Assert.Equal(ItmLifecycleState.Synced, snap!.State);
            Assert.Equal("16 /73", FieldValue(snap.LeftTop));
            Assert.Equal("6", snap.GearText);   // not the numeric misdecode "54"
        }

        [Fact]
        public void LegacyPage_HasNoSlots()
        {
            var driver = MakeDriver(out var clock);
            var data = LapInfoData();
            SyncAndSettle(driver, clock, data);

            // An unsubscribe-all with nothing following = the legacy ITM page.
            driver.OnSubscriptionReport(UnsubReport(0, 1, 0x82, 0x83, 4, 5));
            clock.T += 50;
            driver.Update(data);   // judged: empty set → grace opens
            clock.T += 150;
            driver.Update(data);   // grace expired → legacy page adopted
            clock.T += 250;
            driver.Update(data);

            var snap = driver.ValuesSnapshot;
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
            var driver = MakeDriver(out var clock);
            var data = LapInfoData();
            SyncAndSettle(driver, clock, data);

            driver.Enabled = false;
            clock.T += 300;
            driver.Update(data);
            Assert.Equal(ItmLifecycleState.Disabled, driver.ValuesSnapshot!.State);
        }

        [Fact]
        public void Stop_ClearsThePublishedSnapshot()
        {
            var driver = MakeDriver(out var clock);
            SyncAndSettle(driver, clock, LapInfoData());
            Assert.NotNull(driver.ValuesSnapshot);

            driver.Stop();
            Assert.Null(driver.ValuesSnapshot);
        }
    }
}
