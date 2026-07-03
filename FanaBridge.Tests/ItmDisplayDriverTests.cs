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

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return true;
            }

            public bool SendCol01(byte[] data) => true;
            public int ReadCol03(byte[] buffer, int timeoutMs) => -1;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        // Frame classifiers (col03: [0]=0xFF, [1]=class, [2]=subcmd)
        private static bool IsEnable(byte[] r) => r[1] == 0x02 && r[2] == 0x02;
        private static bool IsValueUpdate(byte[] r) => r[1] == 0x05 && r[2] == 0x01;
        private static bool IsKeepalive(byte[] r) => r[1] == 0x05 && r[2] == 0x04 && r[3] == 0x02 && r[4] == 0x0B;

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
        private static GameData Data(object status) => new GameData { NewData = (StatusDataBase)status };
        private static GameData EmptyData() => Data(NewStatus());

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
        public void BringUp_EnablesFirmwareItmGateBeforeSession()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);

            int gateIdx = t.Sent.FindIndex(IsItmModeOn);
            int enableIdx = t.Sent.FindIndex(IsEnable);
            Assert.True(gateIdx >= 0, "ITM gate (FF 05 02 01) was not sent");
            Assert.True(gateIdx < enableIdx, "ITM gate must precede the session enable");
        }

        [Fact]
        public void Enable_SeedsLapInfoSubscriptions()
        {
            var driver = MakeDriver(out _, out var clock);
            Enable(driver, clock);

            // Lap Info has 6 params; seeded so the first (Enable-default) page populates
            // before any firmware push.
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

        [Fact]
        public void Running_SendsKeepalive_OnInterval()
        {
            var driver = MakeDriver(out var t, out var clock);
            Enable(driver, clock);
            t.Sent.Clear();

            clock.T += 100;
            driver.Update(EmptyData());

            Assert.Contains(t.Sent, IsKeepalive);
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

            // Dormant: no keepalives/values on later ticks.
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
        public void Total_ClearedWithEmptySuffix_WhenItBecomesImplausible()
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

            // The position slot (0x83) must be re-emitted with an empty suffix to clear it.
            var pd = t.Sent.First(IsParamDefs);
            bool clearsPosition = false;
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x83 && pd[i + 4] == 0x00) clearsPosition = true;
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

            // Position slot (0x83) is present but with an empty suffix (length 0).
            var pd = t.Sent.First(IsParamDefs);
            for (int i = 3; i + 5 <= pd.Length && pd[i] == 0x03; i += 5 + pd[i + 4])
                if (pd[i + 1] == 0x83) Assert.Equal(0x00, pd[i + 4]);   // suffix length 0
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
