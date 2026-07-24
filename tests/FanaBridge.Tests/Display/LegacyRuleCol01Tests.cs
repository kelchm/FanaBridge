using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Profiles;
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
                h.Stack.TryShowSpecialScreen = p => h.Driver.ShowSpecialScreen(p);
                h.Stack.OnSpecialReleased = () =>
                {
                    h.Driver.ArmExitBlank();
                    h.Driver.InvalidateSegmentGates();
                };
                return h;
            }

            public DisplayRuleSnapshot Tick(GameData data) => Stack.Tick(null, data);

            /// <summary>Device-instance arbitration stand-in: the rule path owns every
            /// frame (idle included) through the sink; mode Update only when the rule
            /// path is off (flag-off classic).</summary>
            public void DriveFrame(GameData data, bool useRulePath)
            {
                if (useRulePath)
                {
                    Tick(data);
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
            + "\"segmentDisplay\": { \"baseScreenId\": \"pit\", "
            + "\"screens\": [ { \"id\": \"pit\", \"name\": \"Pit\", \"text\": \"PIT\" } ] } }";

        private const string SpeedScreenConfig =
            "{ \"schemaVersion\": 1, "
            + "\"segmentDisplay\": { \"baseScreenId\": \"spd\", "
            + "\"screens\": [ { \"id\": \"spd\", \"name\": \"Speed\", "
            + "\"contentKind\": \"speed\" } ] } }";

        private const string ScrollMsgConfig =
            "{ \"schemaVersion\": 1, "
            + "\"segmentDisplay\": { \"baseScreenId\": \"msg\", "
            + "\"screens\": [ { \"id\": \"msg\", \"name\": \"Hello\", "
            + "\"contentKind\": \"message\", \"text\": \"HELLO\", "
            + "\"effect\": \"scroll\" } ] } }";

        private const string BlinkPitConfig =
            "{ \"schemaVersion\": 1, "
            + "\"segmentDisplay\": { \"baseScreenId\": \"pit\", "
            + "\"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\", "
            + "\"effect\": \"blink\" } ] } }";

        private const string BlankBaseConfig =
            "{ \"schemaVersion\": 1, "
            + "\"segmentDisplay\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ], "
            + "\"rules\": [ { \"id\": \"l1\", "
            + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
            + "\"show\": { \"kind\": \"screen\", \"screenId\": \"pit\" }, "
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

        // ── Empty world (flag-on = silence; flag-off = classic mode) ─────

        [Fact]
        public void EmptyLegacyWorld_FlagOn_NoModeFallback_Silence()
        {
            // Phase 9a: flag-on + empty legacy world must produce NO mode-driver writes
            // (None-migrated / deliberately emptied → silence, not the old mode fallback).
            const string itmOnly =
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"name\": \"Fast\", "
                + "\"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Speed\" }, \"value\": 100 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

            var h = Harness.Create(itmOnly, displayMode: "Speed");
            h.Control.Land(1);
            Assert.False(DisplayRuleStack.HasLegacyWorld(h.Stack.Config));
            Assert.True(DisplayRuleStack.LegacyRuleWrites);

            // Device-instance stand-in for flag-on empty world: rule path off, no Update.
            bool useRulePath = DisplayRuleStack.LegacyRuleWrites
                && DisplayRuleStack.HasLegacyWorld(h.Stack.Config);
            Assert.False(useRulePath);
            h.Tick(Live(speedLocal: 120));
            // No mode Update — silence.
            Assert.Empty(h.Transport.SentCol01Reports);
            Assert.Equal(0, h.UpdateCalls);
        }

        [Fact]
        public void EmptyLegacyWorld_FlagOff_ClassicModeFallback_Bytes()
        {
            // Flag-off keeps today's classic mode driver (frozen displayMode), including
            // the mode != None gate that LegacyPageActive no longer carries.
            DisplayRuleStack.LegacyRuleWrites = false;
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

        // ── Migrated mode path vs classic driver byte parity (P10a re-anchor) ─

        [Theory]
        [InlineData("Gear", "1", 0.0, 0.0, 0.0)]
        [InlineData("Gear", "R", 0.0, 0.0, 0.0)]
        [InlineData("Speed", "1", 88.0, 0.0, 0.0)]
        [InlineData("Speed", "1", 120.4, 0.0, 0.0)]
        public void MigratedRulePath_TimingFreeModes_ByteIdenticalToClassicDriver(
            string mode, string gear, double speedLocal, double rpms, double redLine)
        {
            // Cross-path: classic mode driver sequence vs migrated rule path for the
            // same live frame sequence — RecordingTransport both sides.
            // Spec P10a: Speed/Gear modes — untouched strict parity.
            string kind = mode == "Speed" ? "speed" : "gear";
            string configJson =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"baseScreenId\": \"m1\", "
                + "\"screens\": [ { \"id\": \"m1\", \"name\": \"M\", "
                + "\"contentKind\": \"" + kind + "\" } ] } }";

            var classic = new RecordingTransport();
            var classicDriver = new LegacyDisplayDriver(
                new DisplayEncoder(classic), new DisplaySettings { DisplayMode = mode });

            var rule = Harness.Create(configJson, displayMode: mode);
            rule.Control.Land(1);

            // Scripted sequence: live frames with value changes.
            var frames = new[]
            {
                Live(gear: gear, speedLocal: speedLocal, rpms: rpms, redLine: redLine),
                Live(gear: gear, speedLocal: speedLocal, rpms: rpms, redLine: redLine),
                Live(gear: gear == "R" ? "1" : "2", speedLocal: speedLocal + 10,
                    rpms: rpms, redLine: redLine),
                Live(gear: gear == "R" ? "2" : "4", speedLocal: speedLocal + 20,
                    rpms: rpms + 500, redLine: redLine > 0 ? 0.0 : 1.0),
            };

            foreach (var f in frames)
            {
                classicDriver.Update(f);
                rule.DriveFrame(f, useRulePath: true);
            }

            Assert.Equal(classic.SentCol01Reports.Count, rule.Transport.SentCol01Reports.Count);
            for (int i = 0; i < classic.SentCol01Reports.Count; i++)
            {
                Assert.Equal(
                    classic.SentCol01Reports[i],
                    rule.Transport.SentCol01Reports[i]);
            }
        }

        // Spec P10a: GearUpshiftBrackets → Gear base + Redline overlay of GearBrackets.
        [Theory]
        [InlineData("3", 5000.0, 0.0)]
        [InlineData("3", 5000.0, 1.0)]
        [InlineData("3", 0.0, 1.0)] // engine-off: both paths suppress brackets
        public void MigratedGearUpshiftBrackets_Trio_ByteIdenticalToClassicDriver(
            string gear, double rpms, double redLine)
        {
            var settings = new DisplaySettings { DisplayMode = "GearUpshiftBrackets" };
            var migrated = LegacyModeMigration.Apply(settings, null);
            string configJson = DisplayConfigSerializer.Save(migrated);

            var classic = new RecordingTransport();
            var classicDriver = new LegacyDisplayDriver(
                new DisplayEncoder(classic),
                new DisplaySettings { DisplayMode = "GearUpshiftBrackets" });

            var rule = Harness.Create(configJson, displayMode: "GearUpshiftBrackets");
            rule.Control.Land(1);

            var frames = new[]
            {
                Live(gear: gear, rpms: rpms, redLine: redLine),
                Live(gear: gear, rpms: rpms, redLine: redLine),
                Live(gear: "4", rpms: rpms, redLine: redLine),
                Live(gear: "4", rpms: rpms + 500, redLine: redLine > 0 ? 0.0 : 1.0),
            };

            foreach (var f in frames)
            {
                classicDriver.Update(f);
                rule.DriveFrame(f, useRulePath: true);
            }

            Assert.Equal(classic.SentCol01Reports.Count, rule.Transport.SentCol01Reports.Count);
            for (int i = 0; i < classic.SentCol01Reports.Count; i++)
            {
                Assert.Equal(
                    classic.SentCol01Reports[i],
                    rule.Transport.SentCol01Reports[i]);
            }
        }

        // Spec P10a: GearAndSpeed trio on injected clock — base Speed + Changes 2s overlay,
        // including mid-window extension on gear change. Edge first-sample never fires
        // (engine contract); first real change starts the window.
        [Fact]
        public void MigratedGearAndSpeed_RulePath_InjectedClock_Sequence()
        {
            var settings = new DisplaySettings { DisplayMode = "GearAndSpeed" };
            var migrated = LegacyModeMigration.Apply(settings, null);
            string configJson = DisplayConfigSerializer.Save(migrated);

            var h = Harness.Create(configJson, displayMode: "GearAndSpeed");
            h.Control.Land(1);

            // First sample: Changes never fires → Speed base.
            h.T = 0;
            h.Tick(Live(gear: "3", speedLocal: 100));
            Assert.Equal(
                (SevenSegment.Digit1, SevenSegment.Digit0, SevenSegment.Digit0),
                h.Transport.LastSegments);

            // Gear change → 2s overlay.
            h.T = 16;
            h.Tick(Live(gear: "4", speedLocal: 100));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit4, SevenSegment.Blank),
                h.Transport.LastSegments);

            // Mid-window re-fire extends the hold (ForDuration from each re-fire).
            h.T = 1000;
            h.Tick(Live(gear: "5", speedLocal: 100));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit5, SevenSegment.Blank),
                h.Transport.LastSegments);

            // Still inside the extended window (fired at T=1000, hold 2000).
            h.T = 2500;
            h.Tick(Live(gear: "5", speedLocal: 100));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit5, SevenSegment.Blank),
                h.Transport.LastSegments);

            // Window expired → Speed base.
            h.T = 1000 + LegacyValueFormatter.GearOverlayMs;
            h.Tick(Live(gear: "5", speedLocal: 100));
            Assert.Equal(
                (SevenSegment.Digit1, SevenSegment.Digit0, SevenSegment.Digit0),
                h.Transport.LastSegments);
        }

        // P10a review round: TRUE cross-path GearAndSpeed for every frame whose bytes
        // cannot depend on wall-clock timing — all writes land milliseconds after their
        // gear edge, far inside the driver's 2 s window (expiry timing stays
        // injected-clock-only, above). Also pins the two review findings: the
        // valid→blank gear fold (blank ≡ neutral on BOTH paths → an edge → overlay)
        // and the documented first-frame deviation (driver's int.MinValue seed shows
        // gear on frame 1; the Changes rule never fires on a first sample → Speed).
        [Fact]
        public void MigratedGearAndSpeed_CrossPath_WithinWindowFramesByteIdentical()
        {
            var settings = new DisplaySettings { DisplayMode = "GearAndSpeed" };
            var migrated = LegacyModeMigration.Apply(settings, null);
            string configJson = DisplayConfigSerializer.Save(migrated);

            var classic = new RecordingTransport();
            var classicDriver = new LegacyDisplayDriver(
                new DisplayEncoder(classic),
                new DisplaySettings { DisplayMode = "GearAndSpeed" });

            var h = Harness.Create(configJson, displayMode: "GearAndSpeed");
            h.Control.Land(1);

            // Frame 1 — the documented deviation (spec §6 / STATUS): gear vs speed.
            h.T = 0;
            classicDriver.Update(Live(gear: "3", speedLocal: 100));
            h.Tick(Live(gear: "3", speedLocal: 100));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank),
                classic.LastSegments);
            Assert.Equal(
                (SevenSegment.Digit1, SevenSegment.Digit0, SevenSegment.Digit0),
                h.Transport.LastSegments);

            int classicStart = classic.SentCol01Reports.Count;
            int ruleStart = h.Transport.SentCol01Reports.Count;

            var frames = new[]
            {
                Live(gear: "4", speedLocal: 100),   // change → gear overlay, both paths
                Live(gear: "4", speedLocal: 100),   // steady in-window → change-gated
                Live(gear: "", speedLocal: 100),    // valid→blank folds to neutral → edge
                Live(gear: "5", speedLocal: 100),   // blank→5 → edge
            };
            foreach (var f in frames)
            {
                h.T += 16;
                classicDriver.Update(f);
                h.Tick(f);
            }

            var classicSeq = classic.SentCol01Reports.GetRange(
                classicStart, classic.SentCol01Reports.Count - classicStart);
            var ruleSeq = h.Transport.SentCol01Reports.GetRange(
                ruleStart, h.Transport.SentCol01Reports.Count - ruleStart);
            Assert.Equal(classicSeq.Count, ruleSeq.Count);
            for (int i = 0; i < classicSeq.Count; i++)
                Assert.Equal(classicSeq[i], ruleSeq[i]);
        }

        [Fact]
        public void NoneMode_FlagOn_EmptyWorld_ZeroCol01Writes()
        {
            // Fresh session, frozen None, control != Off → zero col01 writes.
            const string empty =
                "{ \"schemaVersion\": 1 }";
            var h = Harness.Create(empty, displayMode: DisplaySettings.ModeNone);
            h.Control.Land(1);
            Assert.False(DisplayRuleStack.HasLegacyWorld(h.Stack.Config));

            h.Tick(Live(gear: "5", speedLocal: 200));
            h.Tick(Live(gear: "6", speedLocal: 210));
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        // ── Idle parity (in-game gates content per kind, never the wire) ─

        [Fact]
        public void TextBase_PaintsAtIdle()
        {
            // "Display something while parked": a Text base paints on pure idle
            // frames — no game ever ran.
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            h.Tick(Idle());
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                h.Transport.LastSegments);
            // Change-gated: identical idle frames never re-send.
            h.Tick(Idle());
            h.Tick(Idle());
            Assert.Single(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void IdleEligibleRule_WinsAndPaintsAtIdle_InGameRuleCannot()
        {
            // Two rules on the same live property: the "any"-eligible one paints at
            // idle; the default (inGame) one is ineligible there — its screen never
            // shows without a game even though its condition holds.
            const string cfg =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"prk\", \"text\": \"TIP\" }, "
                + "{ \"id\": \"pit\", \"text\": \"PIT\" } ], "
                + "\"rules\": [ "
                + "{ \"id\": \"g1\", \"runs\": \"inGame\", "
                + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"screen\", \"screenId\": \"pit\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } }, "
                + "{ \"id\": \"a1\", \"runs\": \"always\", "
                + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"screen\", \"screenId\": \"prk\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

            var h = Harness.Create(cfg);
            h.Control.Land(1);

            // Idle frame with the condition satisfied (stale or live — a level read):
            // the inGame rule (higher priority) is ineligible, the any rule wins.
            var idle = Idle();
            Set(idle.NewData, "IsInPitLane", 1);
            h.Tick(idle);
            Assert.Equal(
                (SevenSegment.T, SevenSegment.I, SevenSegment.P),
                h.Transport.LastSegments);
        }

        [Fact]
        public void DynamicBase_PureIdle_StaysSilent_NeverPaintsStale()
        {
            // Idle GameData still carries stale values (SimHub keeps them after
            // exit) — dynamic kinds resolve blank, and a blank over a page we never
            // painted is a no-op: FanaBridge stays wire-silent until it has content
            // (the wheel keeps its own resting display).
            var h = Harness.Create(SpeedScreenConfig);
            h.Control.Land(1);
            h.Tick(Idle());   // Idle() carries gear "3" stale data
            h.Tick(Idle());
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void GameExit_DynamicBase_BlankEmergesFromResolution()
        {
            // Live speed content, then exit: the blank is one change-gated write
            // from the stack's own resolution — no driver Update involved.
            var h = Harness.Create(SpeedScreenConfig);
            h.Control.Land(1);
            h.DriveFrame(Live(speedLocal: 88), useRulePath: true);
            Assert.NotEqual(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);
            h.Transport.SentCol01Reports.Clear();

            h.DriveFrame(Idle(), useRulePath: true);
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);
            Assert.Equal(0, h.UpdateCalls);   // the rule path never calls mode Update

            h.Transport.SentCol01Reports.Clear();
            h.DriveFrame(Idle(), useRulePath: true);
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void GameExit_TextBase_ContentPersistsAtIdle()
        {
            // A Text base is idle-safe content: game exit changes nothing on the
            // wire (no blank, no re-send — the text stays up while parked).
            var h = Harness.Create(StaticPitConfig);
            h.Control.Land(1);
            h.DriveFrame(Live(), useRulePath: true);
            h.Transport.SentCol01Reports.Clear();

            h.DriveFrame(Idle(), useRulePath: true);
            h.DriveFrame(Idle(), useRulePath: true);
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void DeclinedHandbackClear_RulePathIdleBlank_RetriesUntilAccepted()
        {
            // Display-test handback: Clear() declined (residue still on the wheel)
            // and the instance arms the exit-blank latch. Clear() reset
            // _hasLastSegments even though nothing was cleared — the first-blank
            // no-op must NOT swallow the rule path's idle blank while the latch is
            // armed, or the residue stays frozen forever.
            var h = Harness.Create(SpeedScreenConfig);
            h.Control.Land(1);

            h.Transport.SendReturns = false;
            Assert.False(h.Driver.Clear());          // declined handback blank
            h.Driver.ArmExitBlank();                 // instance's decline path
            h.Transport.SentCol01Reports.Clear();
            h.Transport.SendReturns = true;

            h.Tick(Idle());                          // dynamic base → blank resolve
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);
            Assert.False(h.Driver.NeedsExitBlank);   // accepted blank cleared the latch

            h.Tick(Idle());                          // then silence
            Assert.Single(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void ScrollEffect_TicksAtIdle()
        {
            // Effects run on the stack clock, game or not — a scrolling message
            // keeps stepping while parked.
            var h = Harness.Create(ScrollMsgConfig);
            h.Control.Land(1);
            h.T = 0;
            h.Tick(Idle());
            int first = h.Transport.SentCol01Reports.Count;
            Assert.True(first > 0);
            h.T += LegacyEffectClock.ScrollStepMs;
            h.Tick(Idle());
            Assert.True(h.Transport.SentCol01Reports.Count > first);
        }

        // ── Base / blank handoff ─────────────────────────────────────────

        [Fact]
        public void NoActiveRule_NullBase_StaysSilent_UntilContentPainted()
        {
            var h = Harness.Create(BlankBaseConfig);
            h.Control.Land(1);
            // Pit condition false → base null → nothing to show. A blank over a page
            // we never painted is a no-op — the wheel keeps its resting display.
            h.Tick(Live(isInPit: 0));
            Assert.Empty(h.Transport.SentCol01Reports);
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

        // ── Special commands (firmware OLED screens) ─────────────────────

        private const string LogoSpecialConfig =
            "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"baseScreenId\": \"pit\", "
            + "\"screens\": [ { \"id\": \"pit\", \"name\": \"Pit\", \"text\": \"PIT\" } ], "
            + "\"rules\": [ { \"id\": \"s1\", "
            + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
            + "\"show\": { \"kind\": \"special\", \"command\": \"logo\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

        private const string LogoSpecialIdleConfig =
            "{ \"schemaVersion\": 1, \"segmentDisplay\": { "
            + "\"rules\": [ { \"id\": \"s1\", \"runs\": \"always\", "
            + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
            + "\"show\": { \"kind\": \"special\", \"command\": \"logo\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

        private static byte[] SpecialFrame(byte pattern)
            => new byte[] { 0x01, 0xF8, 0x09, 0x01, SpecialCommands.Subcommand, pattern, 0x00, 0x00 };

        [Fact]
        public void Special_WinEdge_SendsLogoFrameOnce_ChangeGatedAcrossHeldTicks()
        {
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);

            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), h.Transport.SentCol01Reports[0]);

            // Held ticks: no re-send.
            h.T += 16;
            h.Tick(Live(isInPit: 1));
            h.T += 16;
            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void Special_Release_ContentReclaims_ByteGolden()
        {
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);

            h.Tick(Live(isInPit: 1));
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), h.Transport.SentCol01Reports[0]);
            h.Transport.SentCol01Reports.Clear();

            // Release → base PIT reclaims via normal segment write.
            h.T += 2000;
            h.Tick(Live(isInPit: 0));
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                h.Transport.LastSegments);
        }

        [Fact]
        public void Special_Release_EmptyResolution_WritesBlankOnce()
        {
            const string blankBase =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { "
                + "\"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ], "
                + "\"rules\": [ { \"id\": \"s1\", "
                + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"special\", \"command\": \"logo\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

            var h = Harness.Create(blankBase);
            h.Control.Land(1);
            h.Tick(Live(isInPit: 1));
            h.Transport.SentCol01Reports.Clear();

            h.T += 2000;
            h.Tick(Live(isInPit: 0));
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                h.Transport.LastSegments);

            // Blank-once: subsequent idle ticks stay silent.
            h.Transport.SentCol01Reports.Clear();
            h.T += 16;
            h.Tick(Live(isInPit: 0));
            Assert.Empty(h.Transport.SentCol01Reports);
        }

        [Fact]
        public void Special_DeclinedSend_RetriesNextTick()
        {
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);

            h.Transport.SendReturns = false;
            h.Tick(Live(isInPit: 1));
            h.Transport.SentCol01Reports.Clear();

            h.Transport.SendReturns = true;
            h.T += 16;
            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), h.Transport.SentCol01Reports[0]);
        }

        [Fact]
        public void Special_FlagOff_LogOnly_NoCol01Writes()
        {
            DisplayRuleStack.LegacyRuleWrites = false;
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);
            h.Tick(Live(isInPit: 1));

            Assert.Empty(h.Transport.SentCol01Reports);
            Assert.Contains(h.Log, m => m ==
                "DisplayRules: special command wants 'Fanatec logo' (text write lands in a later phase)");
        }

        [Fact]
        public void Special_NullSink_DoesNotLatch_SendsWhenSinkRebinds()
        {
            // The display-test / Off gates unbind the sink: a special winning during
            // that window must NOT latch as "sent" — when the gate reopens, the held
            // winner still gets its frame.
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);

            h.Stack.TryShowSpecialScreen = null;
            h.Tick(Live(isInPit: 1));
            Assert.Empty(h.Transport.SentCol01Reports);

            h.Stack.TryShowSpecialScreen = p => h.Driver.ShowSpecialScreen(p);
            h.T += 16;
            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), h.Transport.SentCol01Reports[0]);
        }

        [Fact]
        public void Special_FlagOffReleasesLatch_FlagOnResends()
        {
            // Flag-off mid-hold releases the latch (the classic path may repaint over
            // the screen); flag-on re-sends from a clean win edge.
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);
            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);

            DisplayRuleStack.LegacyRuleWrites = false;
            h.T += 16;
            h.Tick(Live(isInPit: 1));   // release fires; log-only, no writes
            Assert.Single(h.Transport.SentCol01Reports);

            DisplayRuleStack.LegacyRuleWrites = true;
            h.T += 16;
            h.Tick(Live(isInPit: 1));   // fresh win edge → re-send
            Assert.Equal(2, h.Transport.SentCol01Reports.Count);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo),
                h.Transport.SentCol01Reports[1]);
        }

        [Fact]
        public void Special_Keepalive_ResendsHeldScreen_InsideRevertWindow()
        {
            // Firmware reverts a selected screen after ~60s without a refresh — a held
            // command re-sends every KeepaliveMs.
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);
            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);

            h.T += SpecialCommands.KeepaliveMs - 16;
            h.Tick(Live(isInPit: 1));
            Assert.Single(h.Transport.SentCol01Reports);   // not yet due

            h.T += 16;
            h.Tick(Live(isInPit: 1));                       // due → re-send
            Assert.Equal(2, h.Transport.SentCol01Reports.Count);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo),
                h.Transport.SentCol01Reports[1]);
        }

        private const string TwoSpecialConfig =
            "{ \"schemaVersion\": 1, \"segmentDisplay\": { "
            + "\"rules\": [ { \"id\": \"w1\", "
            + "\"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
            + "\"show\": { \"kind\": \"special\", \"command\": \"white\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } }, "
            + "{ \"id\": \"s1\", "
            + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
            + "\"show\": { \"kind\": \"special\", \"command\": \"logo\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

        [Fact]
        public void Special_DeclinedTransition_CaptionKeepsAcceptedScreen()
        {
            // Mirror truth: during declined retries of a NEW screen, the caption keeps
            // the last ACCEPTED screen (the hardware still shows it).
            var h = Harness.Create(TwoSpecialConfig);
            h.Control.Land(1);
            var snap = h.Tick(Live(isInPit: 1, fuel: 50));
            Assert.Equal("Fanatec logo", snap.LegacyScreenName);

            h.Transport.SendReturns = false;
            h.T += 600;                                     // past the dwell floor
            snap = h.Tick(Live(isInPit: 1, fuel: 5)) ?? snap;   // white wins, send declined
            Assert.Equal("Fanatec logo", snap.LegacyScreenName);

            h.Transport.SendReturns = true;
            h.T += 16;
            snap = h.Tick(Live(isInPit: 1, fuel: 5)) ?? snap;   // retry accepted
            Assert.Equal("White screen", snap.LegacyScreenName);
        }

        [Fact]
        public void Special_IdleEligible_FiresAtIdle()
        {
            var h = Harness.Create(LogoSpecialIdleConfig);
            h.Control.Land(1);

            var idle = Idle();
            Set(idle.NewData, "IsInPitLane", 1);
            h.Tick(idle);

            Assert.Single(h.Transport.SentCol01Reports);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), h.Transport.SentCol01Reports[0]);
        }

        [Fact]
        public void Special_Snapshot_BlankSegments_AndCommandLabelCaption()
        {
            var h = Harness.Create(LogoSpecialConfig);
            h.Control.Land(1);
            var snap = h.Tick(Live(isInPit: 1));
            Assert.NotNull(snap);
            // Mirror cannot draw firmware art — blank face + command label as caption.
            Assert.Equal("Fanatec logo", snap.LegacyScreenName);
            Assert.Equal(
                new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank },
                snap.LegacySegments);
            // Winner row label carries the command name via DescribeTarget.
            DisplayRuleRow? winner = null;
            for (int i = 0; i < snap.LegacyRules.Count; i++)
            {
                if (snap.LegacyRules[i].Status == RuleStatus.OnScreen)
                {
                    winner = snap.LegacyRules[i];
                    break;
                }
            }
            Assert.NotNull(winner);
            Assert.Contains("Fanatec logo", winner.Value.Label);
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
                "{ \"segmentDisplay\": { \"screens\": [ { \"id\": \"p\", \"text\": \"PIT\" } ] } }",
                _ => { });
            Assert.True(DisplayRuleStack.HasLegacyWorld(screensOnly));

            var empty = DisplayConfigSerializer.Load("{ \"schemaVersion\": 1 }", _ => { });
            Assert.False(DisplayRuleStack.HasLegacyWorld(empty));

            var itmOnly = DisplayConfigSerializer.Load(
                "{ \"itm\": { \"basePage\": \"tyreTemps\" } }", _ => { });
            Assert.False(DisplayRuleStack.HasLegacyWorld(itmOnly));
        }

        // ── Forward-compat base: unknown kind blanks, id preserved ───────

        [Fact]
        public void UnknownKindBaseScreen_ResolvesNoContent_BaseIdPreserved()
        {
            // Validator keeps baseScreenId pointing at a future contentKind screen; the
            // rule path must not invent text (no content → wire-silent on a page we
            // never painted) and must not mutate the doc.
            const string json =
                "{ \"schemaVersion\": 1, "
                + "\"segmentDisplay\": { \"baseScreenId\": \"x1\", "
                + "\"screens\": [ { \"id\": \"x1\", \"name\": \"Future\", \"text\": \"PIT\", "
                + "\"contentKind\": \"hologram\" } ] } }";

            var h = Harness.Create(json);
            Assert.Equal("x1", h.Stack.Config.Legacy.BaseScreenId);
            Assert.Equal(LegacyContentKind.Unknown, h.Stack.Config.Legacy.Screens[0].ContentKind);

            h.Control.Land(1);
            h.Tick(Live());
            Assert.Empty(h.Transport.SentCol01Reports);
            Assert.Equal("x1", h.Stack.Config.Legacy.BaseScreenId);
        }

        // ── Mid-frame config swap: one ownership decision via FrameConfig ─

        [Fact]
        public void MidFrameConfigSwap_FrameConfigKeepsSingleCol01Ownership()
        {
            // TickLegacyRules latches FrameConfig from a non-empty world, writes col01 via
            // the rule sink, then AfterTickForTest swaps the volatile to empty. Arbitration
            // must still follow FrameConfig (rule path) — never re-read CurrentConfig —
            // so mode Update does not also write col01 this frame.
            var profile = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(profile);
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = profile,
                    Capabilities = new WheelCapabilities(profile!),
                },
                itmClock: () => null,
                log: _ => { });

            var world = DisplayConfigSerializer.Load(StaticPitConfig, _ => { });
            runtime.SetConfig(world);

            var transport = new RecordingTransport();
            var settings = new DisplaySettings { DisplayMode = "Gear" };
            var driver = new LegacyDisplayDriver(new DisplayEncoder(transport), settings);
            int modeUpdates = 0;
            runtime.SetLegacySegmentWriter((a, b, c) => driver.TryShowSegments(a, b, c));

            runtime.AfterTickForTest = () => runtime.ClearConfig();

            runtime.TickLegacyRules(null, Live(gear: "7", speedLocal: 200), settings);

            // Volatile is empty after the seam; frame-latched config still has the world.
            Assert.Null(runtime.CurrentConfig);
            Assert.True(DisplayRuleStack.HasLegacyWorld(runtime.FrameConfig));

            bool useRulePath = DisplayRuleStack.LegacyRuleWrites
                && DisplayRuleStack.HasLegacyWorld(runtime.FrameConfig);
            Assert.True(useRulePath);

            // Device-instance DriveLegacyCol01 stand-in: rule path owns live frames.
            bool telemetryLive = true;
            if (useRulePath)
            {
                if (!telemetryLive)
                {
                    modeUpdates++;
                    driver.Update(Live(gear: "7"));
                }
            }
            else
            {
                modeUpdates++;
                driver.Update(Live(gear: "7"));
            }

            Assert.Equal(0, modeUpdates);
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), transport.LastSegments);
            // Re-reading the volatile would have flipped ownership and painted gear 7.
            Assert.False(DisplayRuleStack.HasLegacyWorld(runtime.CurrentConfig));
        }

        [Fact]
        public void TickLegacyRules_EmptyWorld_ClearsPublishedLegacySnapshot()
        {
            // Mirrors the basic-wheel bug: once the legacy world empties, TickLegacyRules
            // must still run and drop stack + snapshot so LegacySegments / name clear.
            var profile = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(profile);
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = profile,
                    Capabilities = new WheelCapabilities(profile!),
                },
                itmClock: () => null,
                log: _ => { });

            var world = DisplayConfigSerializer.Load(StaticPitConfig, _ => { });
            runtime.SetConfig(world);

            var transport = new RecordingTransport();
            var settings = new DisplaySettings { DisplayMode = "Gear" };
            var driver = new LegacyDisplayDriver(new DisplayEncoder(transport), settings);
            runtime.SetLegacySegmentWriter((a, b, c) => driver.TryShowSegments(a, b, c));

            runtime.TickLegacyRules(null, Live(), settings);
            Assert.NotNull(runtime.RuleSnapshot);
            Assert.Equal(
                new byte[] { SevenSegment.P, SevenSegment.I, SevenSegment.T },
                runtime.RuleSnapshot!.LegacySegments);
            Assert.Equal("Pit", runtime.RuleSnapshot.LegacyScreenName);

            // Empty the world live (UI Apply / settings) — next basic frame still ticks.
            runtime.ClearConfig();
            runtime.TickLegacyRules(null, Live(), settings);

            Assert.Null(runtime.Stack);
            Assert.Null(runtime.RuleSnapshot);
            Assert.Null(runtime.Snapshot?.Rules);
        }
    }
}
