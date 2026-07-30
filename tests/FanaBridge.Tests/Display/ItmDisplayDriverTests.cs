using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Driver-level tests: telemetry→value mapping, ParamDefs suffixes, send pacing, and the
    /// post-sync repaint (immediate values + tight double-tap, then defs), all riding on the
    /// lifecycle controller. The lifecycle itself (states, recovery ladder, deadlines) is
    /// covered exhaustively in <see cref="ItmLifecycleControllerTests"/>.
    /// </summary>
    public class ItmDisplayDriverTests
    {
        // ── Test doubles ─────────────────────────────────────────────────

        private class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> Sent { get; } = new List<byte[]>();
            // Simulate a transport-level send failure (SendCol03 returns false).
            public bool SendReturns { get; set; } = true;
            // Optional per-frame accept/decline decision (null = use SendReturns).
            public Func<byte[], bool>? Decide { get; set; }

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return Decide?.Invoke(copy) ?? SendReturns;
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

        // Frame classifiers (col03: [0]=0xFF, [1]=class, [2]=subcmd)
        private static bool IsEnable(byte[] r) => r[1] == 0x02 && r[2] == 0x02;
        private static bool IsValueUpdate(byte[] r) => r[1] == 0x05 && r[2] == 0x01;
        private static bool IsParamDefs(byte[] r) => r[1] == 0x05 && r[2] == 0x03;
        private static bool IsPageSet(byte[] r) => r[1] == 0x05 && r[2] == 0x04;
        private static bool IsItmModeOn(byte[] r) => r[1] == 0x05 && r[2] == 0x02 && r[3] == 0x01;
        private static bool IsItmModeOff(byte[] r) => r[1] == 0x05 && r[2] == 0x02 && r[3] == 0x00;
        private static bool IsDisplayReset(byte[] r) => r[1] == 0x05 && r[2] == 0x05 && r[3] == 0x01;

        private sealed class Clock { public long T; public long Now() => T; }

        private static ItmDisplayDriver MakeDriver(out RecordingTransport t, out Clock clock)
        {
            t = new RecordingTransport();
            clock = new Clock();
            return new ItmDisplayDriver(new ItmEncoder(t), clock.Now);
        }

        // ── GameData (see ItmTelemetryTests) ─────────────────────────────
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);
        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p).GetSetMethod(true).Invoke(s, new[] { v });

        // The driver only sends values while a game is feeding telemetry, so test frames are
        // game-running by default (GameRunning has an internal setter — reflection).
        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { gameRunning });
            return d;
        }
        private static GameData EmptyData() => Data(NewStatus());
        private static GameData NotRunningData() => Data(NewStatus(), gameRunning: false);

        // ── Firmware push reports (col03-IN, complete pages) ─────────────
        // Confirmation matches on the parameter set, so syncing needs a page's COMPLETE push.
        // Entry: [dev][handle][pidLo][pidHi][dataType]; the 0x80 handle bit marks slot params.

        // Page 1 (Lap Info): SPEED@0, GEAR@1, LAP(505)@0x82, POSITION(501)@0x83,
        // LAP_TIME(509)@4, LAST_LAP(510)@5.
        private static readonly byte[] LapInfoPush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");

        // Page 5 (Tyre Temps): SPEED@0, GEAR@1, FL(42)@0x82, RL(48)@0x83, FR(45)@0x84, RR(51)@0x85.
        private static readonly byte[] TyrePush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");

        // Page 2 (Fuel/ERS/DRS): SPEED@0, GEAR@1, FUEL(5)@0x82, ERS(9)@3, DRS_ZONE(14)@4,
        // DRS_ACTIVE(15)@5, DELTA_OWN_BEST(516)@6.
        private static readonly byte[] FuelPush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382050018" + "0303090016" + "03040e0012" + "03050f0012" + "0306040214");

        // A partial report: SPEED@0, GEAR@1, FL(42)@0x82, RL(48)@0x83 — not a complete page.
        private static readonly byte[] PartialTyreReport =
            HexToBytes("ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032");

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        // A firmware unsubscribe report (FF FF param) for the given handles.
        private static byte[] UnsubReport(params byte[] handles)
        {
            var list = new List<byte> { 0xFF, 0x05, 0x01 };
            foreach (var h in handles) { list.Add(0x03); list.Add(h); list.Add(0xFF); list.Add(0xFF); list.Add(0x00); }
            return list.ToArray();
        }

        // Brings the driver to push-confirmed sync and completes the post-sync repaint
        // (first values, tight second tap, ParamDefs). Values only flow after this —
        // there is no pre-push seeding.
        private static void Sync(ItmDisplayDriver driver, Clock clock, byte[]? push = null, GameData? data = null)
        {
            var d = data ?? EmptyData();
            driver.Start();
            driver.Update(d);                    // bring-up: gate-on + enable + PageSet
            driver.OnSubscriptionReport(push ?? LapInfoPush);
            clock.T += 50;                       // push accumulation window
            driver.Update(d);                    // judged → Synced; first paint
            clock.T += 20;                       // value double-tap gap
            driver.Update(d);                    // second tap + ParamDefs
            Assert.True(driver.IsRunning);
        }

        // Delivers a push mid-session and runs the judgment + repaint ticks.
        private static void Push(ItmDisplayDriver driver, Clock clock, byte[] report, GameData? data = null)
        {
            var d = data ?? EmptyData();
            driver.OnSubscriptionReport(report);
            clock.T += 50;
            driver.Update(d);                    // judged; first paint
            clock.T += 20;
            driver.Update(d);                    // second tap + defs
        }

        // ── Lifecycle wiring ─────────────────────────────────────────────

        [Fact]
        public void Update_BeforeStart_SendsNothing()
        {
            var driver = MakeDriver(out var t, out _);
            driver.Update(EmptyData());
            Assert.Empty(t.Sent);
            Assert.False(driver.IsRunning);
        }

        [Fact]
        public void Start_SendsBringUp_ButNotRunningUntilPush()
        {
            var driver = MakeDriver(out var t, out _);
            driver.Start();
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, IsItmModeOn);
            Assert.Contains(t.Sent, IsEnable);
            Assert.Contains(t.Sent, IsPageSet);
            Assert.False(driver.IsRunning);   // confirmation only ever comes from the push
        }

        [Fact]
        public void BringUp_ForcesDefaultPage()
        {
            var driver = MakeDriver(out var t, out _);
            driver.Start();
            driver.Update(EmptyData());

            // PageSet(dev 3, page 1) — the configured default.
            Assert.Contains(t.Sent, r => r.Length > 4 && IsPageSet(r) && r[3] == 0x03 && r[4] == 0x01);
        }

        [Fact]
        public void BringUp_ForcesConfiguredDefaultPage()
        {
            var driver = MakeDriver(out var t, out _);
            driver.DefaultPage = 5;   // Tyre Temps
            driver.Start();
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, r => r.Length > 4 && IsPageSet(r) && r[4] == 0x05);
            // No seeding: nothing is subscribed until the firmware's push announces it.
            Assert.Equal(0, driver.SubscriptionCount);
        }

        [Fact]
        public void PushConfirmation_MakesRunning_AndAdoptsSubscriptions()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);

            Assert.True(driver.IsRunning);
            Assert.Equal(6, driver.SubscriptionCount);
        }

        [Fact]
        public void NoValues_BeforePushConfirmation()
        {
            // Values sent at guessed handles are ignored at best — and after a page change can
            // land on re-bound parameters. Nothing goes out until the push confirms.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(EmptyData());
            t.Sent.Clear();

            var s = NewStatus();
            Set(s, "SpeedLocal", 100.0);
            clock.T += 200;
            driver.Update(Data(s));

            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void ChangingDefaultPageWhileRunning_ForcesItLive()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);   // synced on page 1
            t.Sent.Clear();

            driver.DefaultPage = 3;  // user picks a new default page in settings
            clock.T += 1000;
            driver.Update(EmptyData());   // request registered; quiet window starts
            clock.T += 60;                // past the switch quiet window
            driver.Update(EmptyData());

            // A PageSet to the new page is issued live — no re-enable / reconnect needed.
            Assert.Contains(t.Sent, r => r.Length > 4 && IsPageSet(r) && r[4] == 0x03);
        }

        [Fact]
        public void ChangingDefaultPageWhileRunning_DoesNotReseedSubscriptions()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);
            int before = driver.SubscriptionCount;

            driver.DefaultPage = 3;
            clock.T += 1000;
            driver.Update(EmptyData());
            clock.T += 60;
            driver.Update(EmptyData());

            // The subscriptions are NOT speculatively replaced — the existing set stays until
            // the firmware's push announces the new page's handles.
            Assert.Equal(before, driver.SubscriptionCount);
        }

        [Fact]
        public void BringUp_Bentley_TargetsDevice4()
        {
            var t = new RecordingTransport();
            var clock = new Clock();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now, deviceId: 4);   // Bentley

            driver.Start();
            driver.Update(EmptyData());

            // The forced-page PageSet targets device 4 (Bentley), not the default 3.
            Assert.Contains(t.Sent, r => r.Length > 4 && IsPageSet(r) && r[3] == 0x04 && r[4] == 0x01);
        }

        [Fact]
        public void Bentley_ValueUpdate_UsesDevice4()
        {
            var t = new RecordingTransport();
            var clock = new Clock();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now, deviceId: 4);   // Bentley
            driver.Start();
            driver.Update(EmptyData());

            // Bentley page 1 push (device id 4 in every entry): SPEED@0, GEAR@1, LAP@0x82,
            // POSITION@0x83, LAP_TIME@4, LAST_LAP@5.
            driver.OnSubscriptionReport(HexToBytes(
                "ff0501" + "0400010034" + "0401040012" + "0482f90132" + "0483f50132" + "0404fd012a" + "0405fe012a"));
            clock.T += 50;

            var s = NewStatus();
            Set(s, "SpeedLocal", 100.0);
            driver.Update(Data(s));   // judged → Synced; first paint

            var vu = t.Sent.LastOrDefault(IsValueUpdate);
            Assert.NotNull(vu);
            Assert.Equal(0x04, vu![3]);   // per-entry device id = Bentley
        }

        // ── Enable/disable gate ──────────────────────────────────────────

        [Fact]
        public void Disabled_SendsItmModeOff_AndGoesDormant()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);
            t.Sent.Clear();

            driver.Enabled = false;
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, IsItmModeOff);   // FF 05 02 00 sent
            Assert.False(driver.IsRunning);
            Assert.False(driver.Lifecycle.ValuesAllowed);

            // Dormant: no values on later ticks.
            t.Sent.Clear();
            clock.T += 500;
            driver.Update(EmptyData());
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void Disabled_SendsItmModeOff_OnlyOnce()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);
            driver.Enabled = false;

            driver.Update(EmptyData());
            clock.T += 500;
            driver.Update(EmptyData());

            Assert.Single(t.Sent, IsItmModeOff);
        }

        [Fact]
        public void ReEnable_AfterDisable_ResumesBringUp()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);
            driver.Enabled = false;
            driver.Update(EmptyData());
            t.Sent.Clear();

            driver.Enabled = true;
            driver.Start();               // as the device instance does every frame while enabled
            clock.T += 200;               // past the PageSet spacing floor
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, IsItmModeOn);   // gate turned back on
            Assert.Contains(t.Sent, IsEnable);      // session enable (official parity)
            Assert.Contains(t.Sent, IsPageSet);     // and the page — confirmation via push
        }

        [Fact]
        public void DisabledFromIdle_SendsItmModeOff_WithoutBringUp()
        {
            // User has ITM disabled from the start (never Start()ed): still enforce "off".
            var driver = MakeDriver(out var t, out _);
            driver.Enabled = false;
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, IsItmModeOff);
            Assert.DoesNotContain(t.Sent, IsEnable);
            Assert.False(driver.IsRunning);
        }

        [Fact]
        public void UserDisable_ItmOff_RetriedUntilAccepted()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);
            driver.Enabled = false;

            t.SendReturns = false;             // off command declined — must be retried
            driver.Update(EmptyData());
            t.Sent.Clear();

            t.SendReturns = true;
            driver.Update(EmptyData());        // retried and accepted
            Assert.Contains(t.Sent, IsItmModeOff);

            t.Sent.Clear();
            clock.T += 500;
            driver.Update(EmptyData());        // dormant after success
            Assert.Empty(t.Sent);
        }

        // ── Subscription handling ────────────────────────────────────────

        [Fact]
        public void OnSubscriptionReport_AddsSubscriptions()
        {
            // Pushes are adopted in every state — even before Start (host state must never
            // be used to infer firmware state).
            var driver = MakeDriver(out _, out _);
            driver.OnSubscriptionReport(PartialTyreReport);
            Assert.Equal(4, driver.SubscriptionCount);   // SPEED, GEAR, FL, RL
        }

        [Fact]
        public void OnSubscriptionReport_UnsubscribeRemovesHandles()
        {
            var driver = MakeDriver(out _, out _);
            driver.OnSubscriptionReport(PartialTyreReport);

            // Unsubscribe handles 0 and 1 (FF FF param).
            driver.OnSubscriptionReport(HexToBytes("ff05010300ffff340301ffff12"));
            Assert.Equal(2, driver.SubscriptionCount);   // FL, RL remain
        }

        [Fact]
        public void AllUnsubscribed_SendsNoValues()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);
            driver.OnSubscriptionReport(UnsubReport(0, 1, 2, 3, 4, 5));
            clock.T += 50;
            driver.Update(Data(NewStatus()));
            Assert.Equal(0, driver.SubscriptionCount);
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(Data(NewStatus()));

            Assert.DoesNotContain(t.Sent, IsValueUpdate);   // nothing subscribed
        }

        [Fact]
        public void SendValues_UnknownSubscribedParam_LogsOnce()
        {
            var t = new RecordingTransport();
            var clock = new Clock();
            var logs = new List<string>();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now, logs.Add);
            Sync(driver, clock);

            // The firmware re-announces the page with param 9999 (outside every page layout)
            // at handle 2, in place of LAP — a set that matches no catalog page. It's held
            // through the grace window (suspected mid-flight fragment) then adopted, and the
            // repaint that follows encounters the unencodable param.
            driver.OnSubscriptionReport(HexToBytes("ff050103820f2700"));
            clock.T += 50;
            driver.Update(EmptyData());   // accumulation judged → grace opens (uncataloged set)
            clock.T += 120;
            driver.Update(EmptyData());   // grace expires → adopted → repaint → 9999 logged
            clock.T += 40;
            driver.Update(EmptyData());   // ticks again — must not re-log

            Assert.Single(logs, m => m.Contains("no encoder for subscribed param 9999"));
        }

        // ── ParamDefs (unit/total suffixes) ──────────────────────────────

        [Fact]
        public void SubscriptionWithUnits_SendsParamDefsSuffix()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock, TyrePush);   // tyre temps carry 'C'

            var pd = t.Sent.First(IsParamDefs);
            // entry: [03][slot][posLo][posHi][suffixLen][suffix]; tyre FL handle 2 -> slot 0x82
            Assert.Equal(0x03, pd[3]);
            Assert.Equal(0x82, pd[4]);                 // slot = 0x80 | handle 2
            Assert.Equal(0x01, pd[7]);                 // suffix length 1
            Assert.Equal((byte)'C', pd[8]);            // Celsius
        }

        [Fact]
        public void ParamDefs_SentAfterValueDoubleTap()
        {
            // Post-sync ordering: first values, tight second tap, then ParamDefs — matching
            // official-software post-switch behavior (defs-before-values coincided with a
            // lost first render in captures).
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(EmptyData());
            driver.OnSubscriptionReport(TyrePush);
            clock.T += 50;
            driver.Update(EmptyData());   // sync + first paint — defs must NOT be out yet
            Assert.Single(t.Sent, IsValueUpdate);
            Assert.DoesNotContain(t.Sent, IsParamDefs);

            clock.T += 20;
            driver.Update(EmptyData());   // second tap, then defs
            Assert.Equal(2, t.Sent.Count(IsValueUpdate));
            int lastValue = t.Sent.FindLastIndex(IsValueUpdate);
            int defs = t.Sent.FindIndex(IsParamDefs);
            Assert.True(defs > lastValue, "ParamDefs go out after the value double-tap");
        }

        [Fact]
        public void GameExitThenResume_ReDecoratesSuffix()
        {
            // On game return the repaint clears the suffix signature so the same suffix set is
            // re-sent rather than latched away. (Starting page = the tyre page here, so the
            // return repaints it in place rather than switching to a different default.)
            var driver = MakeDriver(out var t, out var clock);
            driver.DefaultPage = 5;                       // Tyre Temps — matches TyrePush
            Sync(driver, clock, TyrePush);
            Assert.Contains(t.Sent, IsParamDefs);

            clock.T += 40;
            driver.Update(NotRunningData());              // game exit → fields cleared to ---
            t.Sent.Clear();

            // Game returns, repaints in place: first values, tight second tap, then ParamDefs.
            var s = NewStatus();
            clock.T += 1000;
            driver.Update(Data(s));
            clock.T += driver.ValueDoubleTapMs;
            driver.Update(Data(s));
            clock.T += 40;
            driver.Update(Data(s));

            Assert.Contains(t.Sent, IsParamDefs);         // re-decorated despite unchanged sig
        }

        [Fact]
        public void ParamDefs_AreTightDoubleTapped()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock, TyrePush);                 // first def tap goes out with the sync
            Assert.Equal(1, t.Sent.Count(IsParamDefs));

            clock.T += driver.DefDoubleTapMs;              // ~50 ms later
            driver.Update(EmptyData());                    // the tight second tap
            Assert.Equal(2, t.Sent.Count(IsParamDefs));

            clock.T += 5000;                               // suffix unchanged -> no further sends
            driver.Update(EmptyData());
            Assert.Equal(2, t.Sent.Count(IsParamDefs));
        }

        [Fact]
        public void LapAndPosition_SendParamDefsTotalSuffix()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TotalLaps", 34);
            Set(s, "CurrentLap", 5);
            Set(s, "OpponentsCount", 20);   // field of 20 (list already includes the player)
            Set(s, "Position", 7);

            Sync(driver, clock, LapInfoPush, Data(s));   // lap@h2, position@h3

            var pd = t.Sent.First(IsParamDefs);
            // First suffixed slot is lap (handle 2 -> slot 0x82) with "/34".
            Assert.Equal(0x82, pd[4]);
            Assert.Equal(0x03, pd[7]);                 // suffix length 3
            Assert.Equal((byte)'/', pd[8]);
            Assert.Equal((byte)'3', pd[9]);
            Assert.Equal((byte)'4', pd[10]);
        }

        [Fact]
        public void Idle_NeverClearsTotals_TheFirmwareStillShows()
        {
            // Game exit must not actively blank the "/34"-style totals: the idle
            // ParamDefs pass skips telemetry-derived suffixes (authored plan-owned
            // ones stay idle-live) — the firmware keeps its decoration.
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TotalLaps", 34);
            Set(s, "CurrentLap", 5);
            Set(s, "OpponentsCount", 20);
            Set(s, "Position", 7);
            Sync(driver, clock, LapInfoPush, Data(s));   // totals "/34", "/20" sent
            clock.T += 100;
            driver.Update(Data(s));                      // flush the defs double-tap
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(NotRunningData());             // game exits
            clock.T += 500;
            driver.Update(NotRunningData());
            clock.T += 500;
            driver.Update(NotRunningData());

            Assert.DoesNotContain(t.Sent, IsParamDefs);
        }

        [Fact]
        public void Total_ClearedWithBlankSuffix_WhenItBecomesImplausible()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "OpponentsCount", 2); Set(s, "Position", 2);   // field 2, P2 -> "/2"
            Sync(driver, clock, LapInfoPush, Data(s));            // sends "/2" on the position slot
            t.Sent.Clear();

            Set(s, "Position", 3);           // now P3 in a field of 2 -> implausible
            clock.T += 40;
            driver.Update(Data(s));

            // The position slot (0x83) must be re-emitted with a blank " " suffix to clear it —
            // a zero-length suffix does NOT overwrite the firmware's default "/0" on hardware.
            var pd = t.Sent.First(IsParamDefs);
            bool clearsPosition = false;
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x83 && pd[i + 4] == 0x01 && pd[i + 5] == (byte)' ') clearsPosition = true;
            Assert.True(clearsPosition);
        }

        [Fact]
        public void ShowPositionTotalOff_SuppressesPositionTotal()
        {
            var driver = MakeDriver(out var t, out var clock);
            driver.ShowPositionTotal = false;
            var s = NewStatus();
            Set(s, "OpponentsCount", 20); Set(s, "Position", 7);   // would be "/20"
            Sync(driver, clock, LapInfoPush, Data(s));

            // Position slot (0x83) is present but blanked with a " " suffix (length 1) — a
            // zero-length suffix would leave the firmware's default "/0" showing.
            var pd = t.Sent.First(IsParamDefs);
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x83) { Assert.Equal(0x01, pd[i + 4]); Assert.Equal((byte)' ', pd[i + 5]); }
        }

        [Fact]
        public void Fuel_FallsBackToUnitLabel_WhenNoCapacity()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "Fuel", 12.0); Set(s, "MaxFuel", 0.0);   // no tank capacity reported
            Sync(driver, clock, FuelPush, Data(s));          // fuel @ handle 2 (slot 0x82)

            // With no capacity, the fuel slot (0x82) falls back to the unit label "L" (not a
            // blank " "), so a bare fuel value still reads as fuel.
            var pd = t.Sent.First(IsParamDefs);
            bool fuelLabeled = false;
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x82 && pd[i + 4] == 0x01 && pd[i + 5] == (byte)'L') fuelLabeled = true;
            Assert.True(fuelLabeled);
        }

        [Fact]
        public void TempSuffix_FollowsFrameUnit()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TemperatureUnit", "F");   // frame reports Fahrenheit
            Sync(driver, clock, TyrePush, Data(s));

            // The tyre slot (0x82) label comes from the frame's TemperatureUnit, not a fixed
            // default — normalized to a single char.
            var pd = t.Sent.First(IsParamDefs);
            bool tyreF = false;
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x82 && pd[i + 4] == 0x01 && pd[i + 5] == (byte)'F') tyreF = true;
            Assert.True(tyreF);
        }

        [Fact]
        public void ParamDefs_DeclinedSend_RetriedUntilAccepted()
        {
            var driver = MakeDriver(out var t, out var clock);

            // ParamDefs declined during the sync repaint: the suffix signature must NOT
            // latch, or the decoration would never be retried until the suffix set changes.
            t.Decide = r => !IsParamDefs(r);
            Sync(driver, clock, TyrePush);
            Assert.Contains(t.Sent, IsParamDefs);   // attempted
            t.Sent.Clear();

            t.Decide = null;
            clock.T += 40;
            driver.Update(EmptyData());             // unchanged suffix set — still retried
            Assert.Contains(t.Sent, IsParamDefs);
        }

        [Fact]
        public void ParamDefs_DoubleTap_Declined_RetriedNextTick()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock, TyrePush);          // first def tap accepted, second scheduled
            t.Sent.Clear();

            t.Decide = r => !IsParamDefs(r);        // the tight second tap gets declined
            clock.T += driver.DefDoubleTapMs;
            driver.Update(EmptyData());
            Assert.Contains(t.Sent, IsParamDefs);   // attempted
            t.Sent.Clear();

            t.Decide = null;
            clock.T += 5;
            driver.Update(EmptyData());             // retried next tick
            Assert.Contains(t.Sent, IsParamDefs);
        }

        // ── Values ───────────────────────────────────────────────────────

        [Fact]
        public void Synced_SendsValuesForSubscribedParams()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 88.0);
            Sync(driver, clock, TyrePush, Data(s));

            var vu = t.Sent.First(IsValueUpdate);
            // Header [FF 05 01], then entries [03][handle][idLo][idHi][size][val...].
            // First subscribed handle is 0 (SPEED).
            Assert.Equal(0x03, vu[3]);   // entry device id
            Assert.Equal(0x00, vu[4]);   // handle 0
        }

        [Fact]
        public void FirstValuesAfterSync_AreDoubleTapped()
        {
            // The first values after any push get a tight double-tap (~20 ms) — the periodic
            // re-assert heals whatever single-shot sends lose.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(EmptyData());
            driver.OnSubscriptionReport(LapInfoPush);
            clock.T += 50;
            driver.Update(EmptyData());                  // sync → immediate first paint
            Assert.Equal(1, t.Sent.Count(IsValueUpdate));

            clock.T += driver.ValueDoubleTapMs;
            driver.Update(EmptyData());                  // the tight second tap
            Assert.Equal(2, t.Sent.Count(IsValueUpdate));

            clock.T += 40;
            driver.Update(EmptyData());                  // unchanged values — no third send
            Assert.Equal(2, t.Sent.Count(IsValueUpdate));
        }

        [Fact]
        public void Values_NotResent_WhenUnchanged()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            Sync(driver, clock, TyrePush, Data(s));
            t.Sent.Clear();

            clock.T += 100;
            driver.Update(Data(s));   // identical telemetry

            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_Unchanged_ReassertedAfterRefreshInterval()
        {
            // ValueUpdate is unacked, so unchanged values are re-asserted every
            // RefreshIntervalMs as insurance against a lost frame sticking.
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            Sync(driver, clock, TyrePush, Data(s));
            t.Sent.Clear();

            clock.T += driver.RefreshIntervalMs;   // past the refresh window, telemetry unchanged
            driver.Update(Data(s));

            Assert.Contains(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_Resent_WhenSubscriptionChanges()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Sync(driver, clock, TyrePush, Data(s));
            t.Sent.Clear();

            // A new push (page change at the wheel) forces a fresh repaint even with
            // identical telemetry — the firmware may be showing stale cached values.
            Push(driver, clock, LapInfoPush, Data(s));

            Assert.Contains(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_RateLimited_WithinInterval()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            Sync(driver, clock, TyrePush, Data(s));
            clock.T += 40;
            driver.Update(Data(s));
            t.Sent.Clear();

            Set(s, "TyreTemperatureFrontLeft", 90.0);
            clock.T += 10;   // within ValueIntervalMs
            driver.Update(Data(s));

            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_RetriedAfterFailedSend()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            Sync(driver, clock, TyrePush, Data(s));

            // A value send that fails at the transport must NOT be recorded as last-sent...
            Set(s, "TyreTemperatureFrontLeft", 85.0);
            t.SendReturns = false;
            clock.T += 40;
            driver.Update(Data(s));
            t.Sent.Clear();

            // ...so when the transport recovers, the (unchanged) values are retried rather
            // than skipped as "already sent".
            t.SendReturns = true;
            clock.T += 40;
            driver.Update(Data(s));

            Assert.Contains(t.Sent, IsValueUpdate);
        }

        // The gear entry's value byte from a ValueUpdate frame
        // (header [FF 05 01], entries [dev][handle][idLo][idHi][size][val...]).
        private static byte? GearValue(byte[] r)
        {
            for (int i = 3; i + 6 <= r.Length && r[i] != 0; i += 5 + r[i + 4])
                if (r[i + 2] == 0x04 && r[i + 3] == 0x00) return r[i + 5];
            return null;
        }

        [Fact]
        public void Gear_NumericSlot_SendsNumericByte()
        {
            // TyrePush declares GEAR with dataType 0x12 (u8, as a PBME does):
            // gear "3" goes on the wire as numeric 0x03.
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "Gear", "3");
            Sync(driver, clock, TyrePush, Data(s));

            Assert.Equal((byte)0x03, GearValue(t.Sent.First(IsValueUpdate)));
        }

        [Fact]
        public void Gear_TextSlot_SendsAsciiChar()
        {
            // A display that declares GEAR as text (low nibble 1, as a Formula V3 does)
            // gets the ASCII form: gear "3" goes on the wire as '3' (0x33).
            var textPush = HexToBytes(
                "ff0501" + "0300010034" + "0301040011" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "Gear", "3");
            Sync(driver, clock, textPush, Data(s));

            Assert.Equal((byte)0x33, GearValue(t.Sent.First(IsValueUpdate)));
        }

        // ── Game gating & exit/resume ────────────────────────────────────

        [Fact]
        public void BringUp_RunsWithoutLiveTelemetry()
        {
            // ITM is always-on: bring-up runs at connect so the default page (and its
            // live settings preview) works in idle, before any game has run.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(NotRunningData());
            Assert.Contains(t.Sent, IsEnable);

            driver.OnSubscriptionReport(LapInfoPush);
            clock.T += 50;
            driver.Update(NotRunningData());
            Assert.True(driver.IsRunning);
        }

        [Fact]
        public void Idle_SendsNoValues_EvenWithStaleTelemetry()
        {
            // SimHub keeps the last telemetry values after a game exits — they must
            // never be painted while no game is running.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(NotRunningData());   // bring-up in idle (never-live: no gate-off)
            driver.OnSubscriptionReport(TyrePush);
            clock.T += 50;
            driver.Update(NotRunningData());   // synced, but no game
            t.Sent.Clear();

            var stale = NewStatus();
            Set(stale, "TyreTemperatureFrontLeft", 88.0);
            clock.T += 100;
            driver.Update(Data(stale, gameRunning: false));

            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Idle_ConfigSuffixEdit_SendsParamDefs_WithoutTelemetry()
        {
            // Idle parity: an authored suffix edit (field plans) must land on the
            // wire while no game is running — values stay gated, defs do not.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(NotRunningData());
            driver.OnSubscriptionReport(TyrePush);
            clock.T += 50;
            driver.Update(NotRunningData());   // synced at idle
            clock.T += 200;
            driver.Update(NotRunningData());   // initial defs settle
            clock.T += 200;
            driver.Update(NotRunningData());
            t.Sent.Clear();

            driver.Mapper.ConfigureFromPlans(new[]
            {
                new FieldRegionPlan
                {
                    ParamId = ItmParam.TyreFlTemp,
                    SuffixOwner = SuffixOwner.Override,
                    AlignedSuffixText = "!",
                },
            }, properties: null);
            clock.T += 100;
            driver.Update(NotRunningData());

            Assert.Contains(t.Sent, IsParamDefs);
            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void GameExit_ClearsFieldsToPlaceholders_StaysVisible()
        {
            // Game exit clears the fields to --- (DisplayReset) and keeps the ITM page visible.
            // No gate-off — the display never drops to legacy at idle; it stays Synced.
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);               // live game, synced
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(NotRunningData());   // game exited

            Assert.Contains(t.Sent, IsDisplayReset);         // fields cleared to ---
            Assert.DoesNotContain(t.Sent, IsItmModeOff);     // no gate-off (no legacy)
            Assert.True(driver.IsRunning);                   // stays Synced, page visible

            // Idle afterwards: no values from stale telemetry, no repeated resets.
            // ParamDefs MAY re-declare once (idle parity: suffixes are authored
            // content, and telemetry-derived totals actively clear) — but they are
            // signature-latched, so the idle steady state goes quiet.
            t.Sent.Clear();
            clock.T += 500;
            driver.Update(NotRunningData());
            Assert.DoesNotContain(t.Sent, IsValueUpdate);
            Assert.DoesNotContain(t.Sent, IsDisplayReset);
            Assert.All(t.Sent, r => Assert.True(IsParamDefs(r)));

            // Steady state: nothing left to send (defs signature latched; the tight
            // second tap included).
            clock.T += 500;
            driver.Update(NotRunningData());
            t.Sent.Clear();
            clock.T += 500;
            driver.Update(NotRunningData());
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void GameExit_Reset_RetriedUntilAccepted()
        {
            var driver = MakeDriver(out var t, out var clock);
            Sync(driver, clock);

            t.SendReturns = false;             // transport declines the reset
            clock.T += 40;
            driver.Update(NotRunningData());
            t.Sent.Clear();

            t.SendReturns = true;
            driver.Update(NotRunningData());   // retried and accepted
            Assert.Contains(t.Sent, IsDisplayReset);

            t.Sent.Clear();
            driver.Update(NotRunningData());   // no repeat after success
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void GameRestart_AfterExit_RepaintsInPlace()
        {
            // The display never went dark (stayed Synced showing ---); a game returning just
            // repaints fresh values over the placeholders — no reset, no gate cycle.
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "CurrentLap", 5);
            Sync(driver, clock, LapInfoPush, Data(s));

            clock.T += 40;
            driver.Update(Data(s, gameRunning: false));   // exit — reset to ---
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(Data(s));            // game returns with identical telemetry
            Assert.Contains(t.Sent, IsValueUpdate);        // repainted over the ---
            Assert.DoesNotContain(t.Sent, IsItmModeOn);    // no gate cycle
            Assert.DoesNotContain(t.Sent, IsPageSet);      // no re-page
        }

        [Fact]
        public void DefaultPageChange_PreviewsLive_WhileIdle()
        {
            // The settings panel's default-page preview: with no game running, changing
            // the setting still switches the display immediately.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(NotRunningData());   // bring-up in idle
            driver.OnSubscriptionReport(LapInfoPush);
            clock.T += 50;
            driver.Update(NotRunningData());   // synced in idle
            t.Sent.Clear();

            driver.DefaultPage = 3;
            clock.T += 100;
            driver.Update(NotRunningData());   // request registered; quiet window
            clock.T += 60;
            driver.Update(NotRunningData());

            Assert.Contains(t.Sent, r => r.Length > 4 && IsPageSet(r) && r[4] == 0x03);
        }

        // ── Bring-up transport-acceptance retries ────────────────────────

        [Fact]
        public void BringUp_DeclinedWrites_RetriedUntilAccepted()
        {
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();

            t.SendReturns = false;             // whole bring-up declined
            driver.Update(EmptyData());
            Assert.False(driver.IsRunning);
            t.Sent.Clear();

            t.SendReturns = true;
            driver.Update(EmptyData());        // retried and accepted
            Assert.Contains(t.Sent, IsItmModeOn);
            Assert.Contains(t.Sent, IsEnable);
            Assert.Contains(t.Sent, IsPageSet);
        }

        [Fact]
        public void BringUp_AcceptedSteps_NotResent_OnRetry()
        {
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();

            // Gate + Enable accepted, PageSet declined: bring-up stalls before AwaitPush.
            t.Decide = r => !IsPageSet(r);
            driver.Update(EmptyData());
            t.Sent.Clear();

            t.Decide = null;
            driver.Update(EmptyData());        // only the missing step is retried
            Assert.DoesNotContain(t.Sent, IsItmModeOn);   // already accepted last tick
            Assert.DoesNotContain(t.Sent, IsEnable);      // already accepted last tick
            Assert.Contains(t.Sent, IsPageSet);
        }

        // ── Wheel change (identity layer) ────────────────────────────────

        [Fact]
        public void WheelChanged_RestartsColdAndSuspendsValues()
        {
            // A hot-swap resets the display cold with zero trace on the ITM channel; the
            // identity layer's event restarts the lifecycle and nothing is sent until the
            // fresh push confirms.
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            Sync(driver, clock, TyrePush, Data(s));
            t.Sent.Clear();

            driver.OnWheelChanged();
            Assert.Equal(0, driver.SubscriptionCount);   // stale handles dropped
            clock.T += 200;
            driver.Update(Data(s));

            Assert.Contains(t.Sent, IsItmModeOn);        // full cold bring-up
            Assert.Contains(t.Sent, IsPageSet);
            Assert.DoesNotContain(t.Sent, IsValueUpdate);   // no values until the push

            Push(driver, clock, TyrePush, Data(s));      // fresh push after re-seat
            Assert.Contains(t.Sent, IsValueUpdate);      // repainted
        }
    }
}
