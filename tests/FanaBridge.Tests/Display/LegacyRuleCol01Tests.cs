using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase 7b: rule-driven col01 writes through <see cref="LegacyDisplayDriver"/>
    /// (single writer). Byte goldens against <see cref="SevenSegment"/> constants,
    /// flag-off log-only fidelity, empty-world fallback parity, idle gate, base/blank
    /// handoff, effect clock, declined-send retry, mode-Update bypass.
    /// </summary>
    public class LegacyRuleCol01Tests : IDisposable
    {
        private readonly bool _priorFlag;

        public LegacyRuleCol01Tests()
        {
            _priorFlag = DisplayRuleStack.LegacyRuleWrites;
            DisplayRuleStack.LegacyRuleWrites = true;
        }

        public void Dispose() => DisplayRuleStack.LegacyRuleWrites = _priorFlag;

        // ── Recording transport (same shape as LegacyDisplayDriverTests) ─

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> SentCol01Reports { get; } = new List<byte[]>();
            public bool SendReturns { get; set; } = true;

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01Reports.Add(copy);
                return SendReturns;
            }

            public bool SendCol03(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOpDisposable();

            private sealed class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }

            public (byte, byte, byte) LastSegments
            {
                get
                {
                    var r = SentCol01Reports[SentCol01Reports.Count - 1];
                    return (r[5], r[6], r[7]);
                }
            }
        }

        private sealed class FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Idle;
            public byte? CurrentWirePage { get; set; }
            public long SyncGeneration { get; set; }
            public void RequestPage(byte wirePage) { }
            public void Land(byte wirePage)
            {
                State = ItmLifecycleState.Synced;
                CurrentWirePage = wirePage;
                SyncGeneration++;
            }
        }

        // ── GameData helpers ─────────────────────────────────────────────

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p).GetSetMethod(true).Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        private static GameData Live(
            string gear = "1",
            double speedLocal = 0,
            double rpms = 0,
            double redLine = 0,
            int position = 0,
            double fuel = 0,
            int isInPit = 0)
        {
            var s = NewStatus();
            Set(s, "Gear", gear);
            Set(s, "SpeedLocal", speedLocal);
            Set(s, "Rpms", rpms);
            Set(s, "CarSettings_RPMRedLineReached", redLine);
            Set(s, "Position", position);
            Set(s, "Fuel", fuel);
            Set(s, "IsInPitLane", isInPit);
            return Data(s, gameRunning: true);
        }

        private static GameData Idle()
        {
            var d = Live(gear: "3");
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { false });
            return d;
        }

        // ── Stack harness ────────────────────────────────────────────────

        private sealed class Harness
        {
            public readonly FakePageControl Control = new FakePageControl();
            public readonly List<string> Log = new List<string>();
            public long T;
            public DisplayRuleStack Stack;
            public RecordingTransport Transport;
            public LegacyDisplayDriver Driver;
            public int UpdateCalls;

            public static Harness Create(string configJson, string displayMode = "Gear")
            {
                var h = new Harness();
                h.Transport = new RecordingTransport();
                var encoder = new DisplayEncoder(h.Transport);
                h.Driver = new LegacyDisplayDriver(encoder,
                    new DisplaySettings { DisplayMode = displayMode });
                var config = DisplayConfigSerializer.Load(configJson, h.Log.Add);
                h.Stack = new DisplayRuleStack(config, h.Control, itmDeviceId: 2,
                    defaultWirePage: 1, h.Log.Add, () => h.T);
                h.Stack.TryWriteLegacySegments = (a, b, c) => h.Driver.TryShowSegments(a, b, c);
                return h;
            }

            public DisplayRuleSnapshot Tick(GameData data) => Stack.Tick(null, data);

            /// <summary>Device-instance arbitration stand-in: rule path on live frames;
            /// idle blank-once via Update; mode Update only when rule path is off.</summary>
            public void DriveFrame(GameData data, bool useRulePath)
            {
                bool live = data != null && data.GameRunning && data.NewData != null;
                if (useRulePath)
                {
                    Tick(data);
                    if (!live)
                    {
                        UpdateCalls++;
                        Driver.Update(data);
                    }
                }
                else
                {
                    Tick(data); // may log only
                    UpdateCalls++;
                    Driver.Update(data);
                }
            }
        }

        private const string StaticPitConfig =
            "{ \"schemaVersion\": 1, "
            + "\"legacy\": { \"baseScreenId\": \"pit\", "
            + "\"screens\": [ { \"id\": \"pit\", \"name\": \"Pit\", \"text\": \"PIT\" } ] } }";

        private const string SpeedScreenConfig =
            "{ \"schemaVersion\": 1, "
            + "\"legacy\": { \"baseScreenId\": \"spd\", "
            + "\"screens\": [ { \"id\": \"spd\", \"name\": \"Speed\", "
            + "\"contentKind\": \"speed\" } ] } }";

        private const string ScrollMsgConfig =
            "{ \"schemaVersion\": 1, "
            + "\"legacy\": { \"baseScreenId\": \"msg\", "
            + "\"screens\": [ { \"id\": \"msg\", \"name\": \"Hello\", "
            + "\"contentKind\": \"message\", \"text\": \"HELLO\", "
            + "\"effect\": \"scroll\" } ] } }";

        private const string BlinkPitConfig =
            "{ \"schemaVersion\": 1, "
            + "\"legacy\": { \"baseScreenId\": \"pit\", "
            + "\"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\", "
            + "\"effect\": \"blink\" } ] } }";

        private const string BlankBaseConfig =
            "{ \"schemaVersion\": 1, "
            + "\"legacy\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ], "
            + "\"rules\": [ { \"id\": \"l1\", "
            + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
            + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"pit\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

        // ── Byte goldens ─────────────────────────────────────────────────

        [Fact]
        public void RulePath_StaticText_WritesPIT_SevenSegmentBytes()
        {
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            h.Tick(Live());

            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), h.Transport.LastSegments);
        }

        [Fact]
        public void RulePath_Speed_UsesSpeedLocal_ZeroPadded()
        {
            var h = Harness.Create(SpeedScreenConfig);
            h.Control.Land(1);
            h.Tick(Live(speedLocal: 88));

            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit8, SevenSegment.Digit8),
                h.Transport.LastSegments);
        }

        [Fact]
        public void RulePath_IdenticalSegments_DoNotResend()
        {
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            h.Tick(Live());
            h.T += 16;
            h.Tick(Live());
            h.T += 16;
            h.Tick(Live());

            Assert.Single(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void RulePath_Snapshot_CarriesSegmentsAndScreenName()
        {
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            var snap = h.Tick(Live());
            Assert.NotNull(snap);
            Assert.Equal("Pit", snap.LegacyScreenName);
            Assert.Equal(
                new byte[] { SevenSegment.P, SevenSegment.I, SevenSegment.T },
                snap.LegacySegments);
        }

        // ── Flag off = log-only ──────────────────────────────────────────

        [Fact]
        public void FlagOff_LogOnly_ExactMessage_NoCol01Writes()
        {
            DisplayRuleStack.LegacyRuleWrites = false;
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            h.Tick(Live());

            Assert.Empty(h.Transport.SentCol01Reports);
            Assert.Contains(h.Log, m => m ==
                "DisplayRules: legacy surface wants screen 'pit' (text write lands in a later phase)");
        }

        [Fact]
        public void FlagOff_ModeUpdateStillDrives_FallbackParity()
        {
            DisplayRuleStack.LegacyRuleWrites = false;
            var h = Harness.Create(StaticPitConfig, displayMode: "Gear");
            h.Control.Land(1);
            h.DriveFrame(Live(gear: "4"), useRulePath: false);

            // Mode path: gear 4 as blank/digit4/blank — not PIT.
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit4, SevenSegment.Blank),
                h.Transport.LastSegments);
            Assert.Equal(1, h.UpdateCalls);
        }

        // ── Empty world = mode fallback ──────────────────────────────────

        [Fact]
        public void EmptyLegacyWorld_ModeUpdateDrives_NoRuleWrites()
        {
            // ITM-only config: HasLegacyWorld is false.
            const string itmOnly =
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"name\": \"Fast\", "
                + "\"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Speed\" }, \"value\": 100 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

            var h = Harness.Create(itmOnly, displayMode: "Speed");
            h.Control.Land(1);
            Assert.False(DisplayRuleStack.HasLegacyWorld(h.Stack.Config));

            h.DriveFrame(Live(speedLocal: 120), useRulePath: false);
            Assert.Equal(
                (SevenSegment.Digit1, SevenSegment.Digit2, SevenSegment.Digit0),
                h.Transport.LastSegments);
            Assert.Equal(1, h.UpdateCalls);
        }

        // ── Idle gate ────────────────────────────────────────────────────

        [Fact]
        public void Idle_NoCol01Writes_FromRulePath()
        {
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            // Never ran a live frame — pure idle.
            h.Tick(Idle());
            h.Tick(Idle());
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void GameExit_BlankOnce_ViaDriverUpdate()
        {
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            h.DriveFrame(Live(), useRulePath: true);
            h.Transport.SentCol01Reports.Clear();

            h.DriveFrame(Idle(), useRulePath: true);
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);

            h.Transport.SentCol01Reports.Clear();
            h.DriveFrame(Idle(), useRulePath: true);
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        // ── Base / blank handoff ─────────────────────────────────────────

        [Fact]
        public void NoActiveRule_NullBase_BlanksDisplay()
        {
            var h = Harness.Create(BlankBaseConfig);
            h.Control.Land(1);
            // Pit condition false → base null → blank.
            h.Tick(Live(isInPit: 0));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);
        }

        [Fact]
        public void RuleFires_ThenReleases_ReturnsToBlankBase()
        {
            var h = Harness.Create(BlankBaseConfig);
            h.Control.Land(1);
            h.Tick(Live(isInPit: 1));
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), h.Transport.LastSegments);

            h.T += 2000; // past dwell
            h.Tick(Live(isInPit: 0));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);
        }

        // ── Effect clock ─────────────────────────────────────────────────

        [Fact]
        public void Scroll_AdvancesVisibleWindow_OnInjectedClock()
        {
            var h = Harness.Create(ScrollMsgConfig);
            h.Control.Land(1);

            h.T = 0;
            h.Tick(Live());
            var first = h.Transport.LastSegments;
            // "HELLO" + pads — step 0 is H,E,L
            Assert.Equal((SevenSegment.H, SevenSegment.E, SevenSegment.L), first);

            h.T = LegacyEffectClock.ScrollStepMs; // one step
            h.Tick(Live());
            var second = h.Transport.LastSegments;
            Assert.Equal((SevenSegment.E, SevenSegment.L, SevenSegment.L), second);
            Assert.NotEqual(first, second);
            Assert.Equal(2, h.Transport.SentCol01Reports.Count);
        }

        [Fact]
        public void Blink_OffPhase_IsBlankFrame()
        {
            var h = Harness.Create(BlinkPitConfig);
            h.Control.Land(1);

            h.T = 0; // on phase
            h.Tick(Live());
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), h.Transport.LastSegments);

            h.T = LegacyEffectClock.BlinkHalfPeriodMs; // off phase
            h.Tick(Live());
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);
        }

        // ── Declined-send retry ──────────────────────────────────────────

        [Fact]
        public void DeclinedSend_RetriedNextFrame()
        {
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);

            h.Transport.SendReturns = false;
            h.Tick(Live());
            h.Transport.SentCol01Reports.Clear();

            h.Transport.SendReturns = true;
            h.T += 16;
            h.Tick(Live());

            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), h.Transport.LastSegments);
        }

        // ── Mode update never while rule world active ────────────────────

        [Fact]
        public void RuleWorldActive_ModeUpdateNeverCalled_OnLiveFrames()
        {
            var h = Harness.Create(StaticPitConfig, displayMode: "Gear");
            h.Control.Land(1);

            for (int i = 0; i < 5; i++)
            {
                h.T += 16;
                h.DriveFrame(Live(gear: "7", speedLocal: 200), useRulePath: true);
            }

            Assert.Equal(0, h.UpdateCalls);
            // Rule path shows PIT, not gear 7.
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), h.Transport.LastSegments);
        }

        [Fact]
        public void TryShowSegments_ChangeGates_IdenticalFrame()
        {
            var transport = new RecordingTransport();
            var driver = new LegacyDisplayDriver(new DisplayEncoder(transport),
                new DisplaySettings());

            Assert.True(driver.TryShowSegments(SevenSegment.P, SevenSegment.I, SevenSegment.T));
            Assert.True(driver.TryShowSegments(SevenSegment.P, SevenSegment.I, SevenSegment.T));
            Assert.Single(transport.SentCol01Reports);
        }

        [Fact]
        public void TryShowSegments_Declined_DoesNotLatch()
        {
            var transport = new RecordingTransport();
            var driver = new LegacyDisplayDriver(new DisplayEncoder(transport),
                new DisplaySettings());

            transport.SendReturns = false;
            Assert.False(driver.TryShowSegments(SevenSegment.P, SevenSegment.I, SevenSegment.T));
            transport.SentCol01Reports.Clear();

            transport.SendReturns = true;
            Assert.True(driver.TryShowSegments(SevenSegment.P, SevenSegment.I, SevenSegment.T));
            Assert.Single(transport.SentCol01Reports);
        }

        [Fact]
        public void HasLegacyWorld_ScreensOrRules_NotBaseAlone()
        {
            var screensOnly = DisplayConfigSerializer.Load(
                "{ \"legacy\": { \"screens\": [ { \"id\": \"p\", \"text\": \"PIT\" } ] } }",
                _ => { });
            Assert.True(DisplayRuleStack.HasLegacyWorld(screensOnly));

            var empty = DisplayConfigSerializer.Load("{ \"schemaVersion\": 1 }", _ => { });
            Assert.False(DisplayRuleStack.HasLegacyWorld(empty));

            var itmOnly = DisplayConfigSerializer.Load(
                "{ \"itm\": { \"basePage\": \"tyreTemps\" } }", _ => { });
            Assert.False(DisplayRuleStack.HasLegacyWorld(itmOnly));
        }
    }
}
