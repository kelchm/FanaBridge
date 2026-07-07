using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests
{
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

        // Frame classifiers (col03: [0]=0xFF, [1]=class, [2]=subcmd)
        private static bool IsEnable(byte[] r) => r[1] == 0x02 && r[2] == 0x02;
        private static bool IsValueUpdate(byte[] r) => r[1] == 0x05 && r[2] == 0x01;

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

        // The driver only runs while a game is feeding telemetry, so test frames are
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

        // A firmware subscription report (col03-IN). Tyre page: SPEED@0, GEAR@1,
        // FL(42)@0x82, RL(48)@0x83.
        private static readonly byte[] TyreSubReport =
            HexToBytes("ff05010300010034030104001203822a00320383300032");
        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static void Enable(ItmDisplayDriver driver, Clock clock)
        {
            driver.Start();
            driver.Update(EmptyData());   // Enabling -> Running (Enable + seed)
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        [Fact]
        public void Update_BeforeStart_SendsNothing()
        {
            var driver = MakeDriver(out var t, out _);
            driver.Update(EmptyData());
            Assert.Empty(t.Sent);
            Assert.False(driver.IsRunning);
        }

        private static bool IsItmModeOn(byte[] r) => r[1] == 0x05 && r[2] == 0x02 && r[3] == 0x01;

        [Fact]
        public void Start_EnablesOnce_ThenRunning()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);

            Assert.True(driver.IsRunning);
            Assert.Single(t.Sent.Where(IsEnable));
        }

        [Fact]
        public void BringUp_ForcesDefaultPage()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);

            // Bring-up starts the session (FF 02 02) and forces page 1 (FF 05 04 03 01) so the
            // display matches the Lap Info seed. Detecting the wheel's actual page is deferred (#43).
            Assert.Contains(t.Sent, IsEnable);
            Assert.Contains(t.Sent, r => r.Length > 4 && r[1] == 0x05 && r[2] == 0x04 && r[3] == 0x03 && r[4] == 0x01);
        }

        [Fact]
        public void BringUp_ForcesConfiguredDefaultPage()
        {
            var driver = MakeDriver(out var t, out var clock);
            driver.DefaultPage = 5;   // Tyre Temps
            Enable(driver, clock);

            // The forced-page PageSet uses the configured default page, not 1.
            Assert.Contains(t.Sent, r => r.Length > 4 && r[1] == 0x05 && r[2] == 0x04 && r[4] == 0x05);
            // And the seed reflects that page (Tyre Temps = SPEED, GEAR + 4 tyre temps = 6 params).
            Assert.Equal(6, driver.SubscriptionCount);
        }

        [Fact]
        public void ChangingDefaultPageWhileRunning_ForcesItLive()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);   // bring-up on the default page (1)
            t.Sent.Clear();          // ignore bring-up frames

            driver.DefaultPage = 3;  // user picks a new default page in settings
            clock.T += 1000;
            driver.Update(EmptyData());

            // A PageSet to the new page is issued live — no re-enable / reconnect needed.
            Assert.Contains(t.Sent, r => r.Length > 4 && r[1] == 0x05 && r[2] == 0x04 && r[4] == 0x03);
        }

        [Fact]
        public void ChangingDefaultPageWhileRunning_DoesNotReseedSubscriptions()
        {
            var driver = MakeDriver(out _, out var clock);
            driver.DefaultPage = 1;
            Enable(driver, clock);
            int before = driver.SubscriptionCount;   // page 1's seeded set

            driver.DefaultPage = 3;   // switch page live
            clock.T += 1000;
            driver.Update(EmptyData());

            // The PageSet is issued (see ChangingDefaultPageWhileRunning_ForcesItLive), but the
            // subscriptions are NOT speculatively reseeded — we wait for the firmware's push. A flaked
            // switch, or a switch to the page already shown (no push), must not strand the display on
            // wrong-page handles, so the existing set is kept until the firmware replaces it.
            Assert.Equal(before, driver.SubscriptionCount);
        }

        [Fact]
        public void BringUp_Bentley_TargetsDevice4()
        {
            var t = new RecordingTransport();
            var clock = new Clock();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now, deviceId: 4);   // Bentley

            driver.Start();
            driver.Update(EmptyData());   // Enabling -> Running

            // The forced-page PageSet targets device 4 (Bentley), not the default 3.
            Assert.Contains(t.Sent, r => r.Length > 4 && r[1] == 0x05 && r[2] == 0x04 && r[3] == 0x04 && r[4] == 0x01);
        }

        [Fact]
        public void Bentley_ValueUpdate_UsesDevice4()
        {
            var t = new RecordingTransport();
            var clock = new Clock();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now, deviceId: 4);   // Bentley
            driver.Start();
            driver.Update(EmptyData());   // bring-up + Lap Info seed

            var s = NewStatus();
            Set(s, "SpeedLocal", 100.0);
            clock.T += 1000;             // past the value interval
            driver.Update(Data(s));

            var vu = t.Sent.LastOrDefault(IsValueUpdate);
            Assert.NotNull(vu);
            Assert.Equal(0x04, vu[3]);   // per-entry device id = Bentley
        }

        [Fact]
        public void Enable_SeedsLapInfoSubscriptions()
        {
            var driver = MakeDriver(out _, out var clock);
            Enable(driver, clock);

            // Lap Info has 6 params; seeded so the forced page 1 populates immediately,
            // before the firmware's push (from the SetPage) arrives.
            Assert.Equal(6, driver.SubscriptionCount);
        }

        [Fact]
        public void Enable_DoesNotOverwriteAlreadyReceivedSubscriptions()
        {
            var driver = MakeDriver(out _, out var clock);
            driver.OnSubscriptionReport(TyreSubReport);   // 4 subs arrive before Enable
            Enable(driver, clock);

            Assert.Equal(4, driver.SubscriptionCount);    // seed skipped
        }

        // ── Enable/disable gate ──────────────────────────────────────────
        private static bool IsItmModeOff(byte[] r) => r[1] == 0x05 && r[2] == 0x02 && r[3] == 0x00;

        [Fact]
        public void Disabled_SendsItmModeOff_AndGoesDormant()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            t.Sent.Clear();

            driver.Enabled = false;
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, IsItmModeOff);   // FF 05 02 00 sent
            Assert.False(driver.IsRunning);
            Assert.Equal(0, driver.SubscriptionCount);

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
            Enable(driver, clock);
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
            Enable(driver, clock);
            driver.Enabled = false;
            driver.Update(EmptyData());
            t.Sent.Clear();

            driver.Enabled = true;
            driver.Update(EmptyData());   // Disabled -> Enabling -> Running in one tick

            Assert.True(driver.IsRunning);
            Assert.Contains(t.Sent, IsItmModeOn);   // gate turned back on
            Assert.Contains(t.Sent, IsEnable);      // session re-enabled
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
        public void SendValues_UnknownSubscribedParam_LogsOnce()
        {
            var t = new RecordingTransport();
            var clock = new Clock();
            var logs = new List<string>();
            var driver = new ItmDisplayDriver(new ItmEncoder(t), clock.Now, logs.Add);

            // Subscribe param 9999 (outside every page layout) at handle 2 (fw 0x82).
            driver.OnSubscriptionReport(HexToBytes("ff050103820f2700"));
            driver.Start();
            driver.Update(EmptyData());   // Enabling -> Running
            clock.T += 40;
            driver.Update(EmptyData());   // SendSubscribedValues encounters the unknown param
            clock.T += 40;
            driver.Update(EmptyData());   // ticks again — must not re-log

            Assert.Single(logs, m => m.Contains("no encoder for subscribed param 9999"));
        }

        // ── Subscription handling ────────────────────────────────────────

        [Fact]
        public void OnSubscriptionReport_AddsSubscriptions()
        {
            var driver = MakeDriver(out _, out _);
            driver.OnSubscriptionReport(TyreSubReport);
            Assert.Equal(4, driver.SubscriptionCount);   // SPEED, GEAR, FL, RL
        }

        [Fact]
        public void OnSubscriptionReport_UnsubscribeRemovesHandles()
        {
            var driver = MakeDriver(out _, out _);
            driver.OnSubscriptionReport(TyreSubReport);

            // Unsubscribe handles 0 and 1 (FF FF param).
            driver.OnSubscriptionReport(HexToBytes("ff05010300ffff340301ffff12"));
            Assert.Equal(2, driver.SubscriptionCount);   // FL, RL remain
        }

        // A firmware unsubscribe report (FF FF param) for the given handles.
        private static byte[] UnsubReport(params byte[] handles)
        {
            var list = new List<byte> { 0xFF, 0x05, 0x01 };
            foreach (var h in handles) { list.Add(0x03); list.Add(h); list.Add(0xFF); list.Add(0xFF); list.Add(0x00); }
            return list.ToArray();
        }

        [Fact]
        public void AllUnsubscribed_SendsNoValues()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);   // seeds the 6 Lap Info handles
            driver.OnSubscriptionReport(UnsubReport(0, 1, 2, 3, 4, 5));
            Assert.Equal(0, driver.SubscriptionCount);
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(Data(NewStatus()));

            Assert.DoesNotContain(t.Sent, IsValueUpdate);   // nothing subscribed
        }

        [Fact]
        public void SubscriptionWithUnits_SendsParamDefsSuffix()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);          // Lap Info seed
            driver.OnSubscriptionReport(TyreSubReport);   // tyre temps carry 'C'
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(EmptyData());     // ParamDefs refreshed here

            var pd = t.Sent.First(IsParamDefs);
            // entry: [03][slot][posLo][posHi][suffixLen][suffix]; tyre FL handle 2 -> slot 0x82
            Assert.Equal(0x03, pd[3]);
            Assert.Equal(0x82, pd[4]);                 // slot = 0x80 | handle 2
            Assert.Equal(0x01, pd[7]);                 // suffix length 1
            Assert.Equal((byte)'C', pd[8]);            // Celsius
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

            driver.Start();
            driver.Update(Data(s));         // Enable + seed Lap Info (lap@h2, position@h3)
            t.Sent.Clear();
            clock.T += 40;
            driver.Update(Data(s));         // ParamDefs with the dynamic totals

            var pd = t.Sent.First(IsParamDefs);
            // First suffixed slot is lap (handle 2 -> slot 0x82) with "/34".
            Assert.Equal(0x82, pd[4]);
            Assert.Equal(0x03, pd[7]);                 // suffix length 3
            Assert.Equal((byte)'/', pd[8]);
            Assert.Equal((byte)'3', pd[9]);
            Assert.Equal((byte)'4', pd[10]);
        }

        [Fact]
        public void Total_ClearedWithBlankSuffix_WhenItBecomesImplausible()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "OpponentsCount", 2); Set(s, "Position", 2);   // field 2, P2 -> "/2"
            driver.Start();
            driver.Update(Data(s));          // Enable + seed (position@h3)
            clock.T += 40;
            driver.Update(Data(s));          // sends "/2" on the position slot
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
            driver.Start();
            driver.Update(Data(s));
            clock.T += 40;
            driver.Update(Data(s));

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
            driver.OnSubscriptionReport(HexToBytes("ff05010382050018"));   // fuel @ handle 2 (slot 0x82)
            Enable(driver, clock);                                          // seed skipped; only fuel subscribed

            var s = NewStatus();
            Set(s, "Fuel", 12.0); Set(s, "MaxFuel", 0.0);   // no tank capacity reported
            t.Sent.Clear();
            clock.T += 40;
            driver.Update(Data(s));

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
            driver.OnSubscriptionReport(TyreSubReport);   // tyre temps @ 0x82/0x83
            Enable(driver, clock);

            var s = NewStatus();
            Set(s, "TemperatureUnit", "F");   // frame reports Fahrenheit
            t.Sent.Clear();
            clock.T += 40;
            driver.Update(Data(s));

            // The tyre slot (0x82) label comes from the frame's TemperatureUnit, not a fixed
            // default — normalized to a single char.
            var pd = t.Sent.First(IsParamDefs);
            bool tyreF = false;
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x82 && pd[i + 4] == 0x01 && pd[i + 5] == (byte)'F') tyreF = true;
            Assert.True(tyreF);
        }

        private static bool IsParamDefs(byte[] r) => r[1] == 0x05 && r[2] == 0x03;

        [Fact]
        public void Running_SendsValuesForSubscribedParams()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            driver.OnSubscriptionReport(TyreSubReport);
            t.Sent.Clear();

            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 88.0);
            clock.T += 40;
            driver.Update(Data(s));

            var vu = t.Sent.First(IsValueUpdate);
            // Header [FF 05 01], then entries [03][handle][idLo][idHi][size][val...].
            // First subscribed handle is 0 (SPEED).
            Assert.Equal(0x03, vu[3]);   // entry marker
            Assert.Equal(0x00, vu[4]);   // handle 0
            Assert.Contains(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_NotResent_WhenUnchanged()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            driver.OnSubscriptionReport(TyreSubReport);

            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            clock.T += 40;
            driver.Update(Data(s));
            t.Sent.Clear();

            clock.T += 100;
            driver.Update(Data(s));   // identical telemetry

            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_Resent_WhenSubscriptionChanges()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            driver.OnSubscriptionReport(TyreSubReport);

            var s = NewStatus();
            clock.T += 40;
            driver.Update(Data(s));
            t.Sent.Clear();

            // A new subscription report forces a fresh send even with identical telemetry.
            driver.OnSubscriptionReport(TyreSubReport);
            clock.T += 40;
            driver.Update(Data(s));

            Assert.Contains(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void Values_RateLimited_WithinInterval()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            driver.OnSubscriptionReport(TyreSubReport);

            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);
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
            Enable(driver, clock);
            driver.OnSubscriptionReport(TyreSubReport);

            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 80.0);

            // A value send that fails at the transport must NOT be recorded as last-sent...
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

        // ── Game gating (issue #54) ──────────────────────────────────────

        [Fact]
        public void BringUp_RunsWithoutLiveTelemetry()
        {
            // ITM is always-on: bring-up runs at connect so the default page (and its
            // live settings preview) works in idle, before any game has run.
            var driver = MakeDriver(out var t, out _);
            driver.Start();
            driver.Update(NotRunningData());

            Assert.Contains(t.Sent, IsEnable);
            Assert.True(driver.IsRunning);
        }

        [Fact]
        public void Idle_SendsNoValues_EvenWithStaleTelemetry()
        {
            // SimHub keeps the last telemetry values after a game exits — they must
            // never be painted while no game is running.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(NotRunningData());   // bring-up in idle
            driver.OnSubscriptionReport(TyreSubReport);
            t.Sent.Clear();

            var stale = NewStatus();
            Set(stale, "TyreTemperatureFrontLeft", 88.0);
            clock.T += 100;
            driver.Update(Data(stale, gameRunning: false));

            Assert.DoesNotContain(t.Sent, IsValueUpdate);
        }

        // DisplayReset (FF 05 05 01): every ITM field reverts to its per-field placeholder
        // ("--- / -", "--:--.-", …); session/page/subs untouched. No effect on the Legacy ITM page.
        private static bool IsDisplayReset(byte[] r) => r[1] == 0x05 && r[2] == 0x05 && r[3] == 0x01;

        [Fact]
        public void GameExit_ResetsFields_SessionStaysUp()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);             // live game, running (6 Lap Info subs)
            t.Sent.Clear();

            driver.Update(NotRunningData());   // game exited — fields revert to placeholders

            Assert.Contains(t.Sent, IsDisplayReset);
            Assert.DoesNotContain(t.Sent, IsItmModeOff);   // session NOT torn down
            Assert.True(driver.IsRunning);
            Assert.Equal(6, driver.SubscriptionCount);     // subscriptions kept

            // Idle afterwards: no repeat resets, no values from stale telemetry.
            t.Sent.Clear();
            clock.T += 500;
            driver.Update(NotRunningData());
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void GameExit_Reset_RetriedUntilAccepted()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);

            t.SendReturns = false;             // transport declines the reset
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
        public void GameRestart_AfterExit_RepaintsUnchangedValues()
        {
            var driver = MakeDriver(out var t, out var clock);
            var s = NewStatus();
            Set(s, "CurrentLap", 5);

            driver.Start();
            driver.Update(Data(s));            // bring-up (seeds Lap Info)
            clock.T += 40;
            driver.Update(Data(s));            // values painted
            driver.Update(Data(s, gameRunning: false));   // exit — fields reset to placeholders
            t.Sent.Clear();

            clock.T += 40;
            driver.Update(Data(s));            // game returns with IDENTICAL telemetry

            // The reset cleared the dirty tracking, so even unchanged values are
            // repainted — otherwise the display would sit on placeholders until a
            // value actually changed.
            Assert.Contains(t.Sent, IsValueUpdate);
        }

        [Fact]
        public void DefaultPageChange_PreviewsLive_WhileIdle()
        {
            // The settings panel's default-page preview: with no game running, changing
            // the setting still switches the display immediately.
            var driver = MakeDriver(out var t, out var clock);
            driver.Start();
            driver.Update(NotRunningData());   // bring-up in idle
            t.Sent.Clear();

            driver.DefaultPage = 3;
            clock.T += 100;
            driver.Update(NotRunningData());

            Assert.Contains(t.Sent, r => r.Length > 4 && r[1] == 0x05 && r[2] == 0x04 && r[4] == 0x03);
        }

        [Fact]
        public void UserDisable_ItmOff_RetriedUntilAccepted()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            driver.Enabled = false;

            t.SendReturns = false;             // off command declined — must not latch Disabled
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

        // ── Stop / restart ───────────────────────────────────────────────

        [Fact]
        public void Stop_ClearsSubscriptionsAndHalts()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            driver.OnSubscriptionReport(TyreSubReport);

            driver.Stop();
            Assert.False(driver.IsRunning);
            Assert.Equal(0, driver.SubscriptionCount);
            t.Sent.Clear();

            clock.T += 1000;
            driver.Update(EmptyData());
            Assert.Empty(t.Sent);

            driver.Start();
            driver.Update(EmptyData());
            Assert.Contains(t.Sent, IsEnable);   // fresh Enable on restart
        }
    }
}
