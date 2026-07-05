using System;
using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests
{
    public class FanatecDisplayDriverTests
    {
        // ── Test stub ────────────────────────────────────────────────────

        /// <summary>
        /// Records every col01 report the driver emits through DisplayEncoder.
        /// Only SendCol01 is exercised by the display path.
        /// </summary>
        private class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;

            public List<byte[]> SentCol01Reports { get; } = new List<byte[]>();

            // Simulate a transport-level send failure (SendCol01 returns false).
            public bool SendReturns { get; set; } = true;

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01Reports.Add(copy);
                return SendReturns;
            }

            public bool SendCol03(byte[] data) => true;
            public int ReadCol03(byte[] buffer, int timeoutMs) => -1;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOpDisposable();

            private sealed class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }

            /// <summary>The 3 segment bytes (positions 5-7) of the most recent report.</summary>
            public (byte, byte, byte) LastSegments
            {
                get
                {
                    var r = SentCol01Reports[SentCol01Reports.Count - 1];
                    return (r[5], r[6], r[7]);
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static FanatecDisplayDriver MakeDriver(RecordingTransport transport, string mode)
        {
            var encoder = new DisplayEncoder(transport);
            var settings = new DisplaySettings { DisplayMode = mode };
            return new FanatecDisplayDriver(encoder, settings);
        }

        // StatusDataBase is abstract with internal setters, so it can't be
        // constructed or populated directly from the test assembly. Its only
        // concrete subclass is the generic StatusData<T>; we close it over
        // object, create an instance, and drive the internal setters via
        // reflection.
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly
                .GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));

        private static void Set(object statusData, string property, object value)
        {
            statusData.GetType().GetProperty(property).GetSetMethod(true)
                .Invoke(statusData, new[] { value });
        }

        private static GameData MakeData(
            string gear = "1",
            double speedLocal = 0,
            double speedKmh = 0,
            double rpms = 0,
            double redLineReached = 0)
        {
            var status = System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(StatusDataType);
            Set(status, "Gear", gear);
            Set(status, "SpeedLocal", speedLocal);
            Set(status, "SpeedKmh", speedKmh);
            Set(status, "Rpms", rpms);
            Set(status, "CarSettings_RPMRedLineReached", redLineReached);

            // The driver only renders while a game is feeding telemetry, so test
            // frames are game-running (GameRunning has an internal setter).
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { true });
            return d;
        }

        /// <summary>A frame after game exit: GameRunning false, but NewData still
        /// holding the last values — SimHub keeps them around after a game ends.</summary>
        private static GameData NotRunningData(string gear = "3")
        {
            var d = MakeData(gear: gear);
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { false });
            return d;
        }

        // ── DisplayMode / construction ───────────────────────────────────

        [Fact]
        public void DisplayMode_DefaultsToGear_WhenSettingsNull()
        {
            var driver = new FanatecDisplayDriver(new DisplayEncoder(new RecordingTransport()), null);
            Assert.Equal("Gear", driver.DisplayMode);
        }

        [Fact]
        public void DisplayMode_ReflectsSettings()
        {
            var driver = MakeDriver(new RecordingTransport(), "Speed");
            Assert.Equal("Speed", driver.DisplayMode);
        }

        [Fact]
        public void UpdateSettings_NullResetsToDefault()
        {
            var driver = MakeDriver(new RecordingTransport(), "Speed");
            driver.UpdateSettings(null);
            Assert.Equal("Gear", driver.DisplayMode);
        }

        // ── Update guards ────────────────────────────────────────────────

        [Fact]
        public void Update_NullNewData_DoesNothing()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Speed");

            driver.Update(new GameData()); // NewData is null by default

            Assert.Empty(transport.SentCol01Reports);
        }

        [Fact]
        public void Update_UnknownMode_FallsBackToGear()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "TotallyBogusMode");

            driver.Update(MakeData(gear: "4"));

            // Gear 4 rendered as blank / digit-4 / blank
            Assert.Equal((SevenSegment.Blank, SevenSegment.Digit4, SevenSegment.Blank), transport.LastSegments);
            Assert.Equal("4", driver.CurrentGear);
        }

        // ── Gear mode ────────────────────────────────────────────────────

        [Theory]
        [InlineData("R", -1, SevenSegment.R)]
        [InlineData("N", 0, SevenSegment.N)]
        [InlineData("3", 3, SevenSegment.Digit3)]
        [InlineData("reverse", -1, SevenSegment.R)]
        [InlineData("", 0, SevenSegment.N)]      // empty -> neutral
        [InlineData("garbage", 0, SevenSegment.N)] // unparseable -> neutral
        public void Gear_RendersExpectedSegment(string gearStr, int _, byte expectedMiddle)
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: gearStr));

            Assert.Equal((SevenSegment.Blank, expectedMiddle, SevenSegment.Blank), transport.LastSegments);
        }

        [Fact]
        public void Gear_SetsCurrentTextAndGear()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "R"));

            Assert.Equal("R", driver.CurrentGear);
            Assert.Equal("R", driver.CurrentText);
        }

        [Fact]
        public void Gear_RateLimits_UnchangedValue()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "2"));
            driver.Update(MakeData(gear: "2"));
            driver.Update(MakeData(gear: "2"));

            Assert.Single(transport.SentCol01Reports);
        }

        [Fact]
        public void Gear_WritesAgain_WhenValueChanges()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "2"));
            driver.Update(MakeData(gear: "3"));

            Assert.Equal(2, transport.SentCol01Reports.Count);
        }

        // ── Speed mode (the SpeedLocal fix, issue #27) ───────────────────

        [Fact]
        public void Speed_UsesSpeedLocal_NotSpeedKmh()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Speed");

            // 88 mph would read as 141 km/h; the display must follow SpeedLocal.
            driver.Update(MakeData(speedLocal: 88, speedKmh: 141));

            Assert.Equal("88", driver.CurrentText);
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit8, SevenSegment.Digit8),
                transport.LastSegments);
        }

        [Fact]
        public void Speed_RoundsToNearestInt()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Speed");

            driver.Update(MakeData(speedLocal: 99.6));

            Assert.Equal("100", driver.CurrentText);
        }

        [Theory]
        [InlineData(-5, "0")]
        [InlineData(1234, "999")]
        public void Speed_ClampsToRange(double speedLocal, string expected)
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Speed");

            driver.Update(MakeData(speedLocal: speedLocal));

            Assert.Equal(expected, driver.CurrentText);
        }

        [Fact]
        public void Speed_RateLimits_UnchangedValue()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Speed");

            driver.Update(MakeData(speedLocal: 120));
            driver.Update(MakeData(speedLocal: 120.2)); // rounds to same int

            Assert.Single(transport.SentCol01Reports);
        }

        // ── GearAndSpeed mode ────────────────────────────────────────────
        // NOTE: the gear/speed switch is gated on DateTime.UtcNow (a 2s overlay
        // after each gear change) which isn't injectable, so only the gear-
        // overlay branch is deterministically reachable. A fresh driver always
        // starts inside the overlay window on the first gear it sees.

        [Fact]
        public void GearAndSpeed_ShowsGearOverlay_OnFirstFrame()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "GearAndSpeed");

            driver.Update(MakeData(gear: "5", speedLocal: 200));

            Assert.Equal("5", driver.CurrentText);
            Assert.Equal((SevenSegment.Blank, SevenSegment.Digit5, SevenSegment.Blank), transport.LastSegments);
        }

        // ── GearUpshiftBrackets mode ─────────────────────────────────────

        [Fact]
        public void UpshiftBrackets_NoBrackets_BelowRedline()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "GearUpshiftBrackets");

            driver.Update(MakeData(gear: "3", rpms: 6000, redLineReached: 0));

            Assert.Equal((SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank), transport.LastSegments);
            Assert.Equal("3", driver.CurrentText);
        }

        [Fact]
        public void UpshiftBrackets_ShowsBrackets_AtRedline()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "GearUpshiftBrackets");

            driver.Update(MakeData(gear: "3", rpms: 8000, redLineReached: 1));

            Assert.Equal((SevenSegment.BracketLeft, SevenSegment.Digit3, SevenSegment.BracketRight), transport.LastSegments);
            Assert.Equal("[3]", driver.CurrentText);
        }

        [Fact]
        public void UpshiftBrackets_NoBrackets_WhenRpmsZero()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "GearUpshiftBrackets");

            // redline flag set but no rpms -> brackets require both
            driver.Update(MakeData(gear: "3", rpms: 0, redLineReached: 1));

            Assert.Equal("3", driver.CurrentText);
        }

        [Fact]
        public void UpshiftBrackets_RewritesWhenBracketStateChanges()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "GearUpshiftBrackets");

            driver.Update(MakeData(gear: "3", rpms: 8000, redLineReached: 0)); // no brackets
            driver.Update(MakeData(gear: "3", rpms: 8000, redLineReached: 1)); // brackets on

            Assert.Equal(2, transport.SentCol01Reports.Count);
        }

        [Fact]
        public void UpshiftBrackets_RateLimits_UnchangedState()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "GearUpshiftBrackets");

            driver.Update(MakeData(gear: "3", rpms: 8000, redLineReached: 1));
            driver.Update(MakeData(gear: "3", rpms: 8100, redLineReached: 1));

            Assert.Single(transport.SentCol01Reports);
        }

        // ── Game gating (issue #54) ──────────────────────────────────────

        [Fact]
        public void Idle_BeforeAnyGame_WritesNothing()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(NotRunningData());
            driver.Update(NotRunningData());

            // The firmware may be using the display itself (e.g. the tuning
            // menu) — nothing may be written before a game ever ran.
            Assert.Empty(transport.SentCol01Reports);
        }

        [Fact]
        public void GameExit_BlanksDisplayOnce()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "3"));
            transport.SentCol01Reports.Clear();

            // Game exits; SimHub still reports the stale gear in NewData.
            driver.Update(NotRunningData(gear: "3"));
            Assert.Single(transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                transport.LastSegments);

            transport.SentCol01Reports.Clear();
            driver.Update(NotRunningData(gear: "3"));   // no repeated writes while idle
            Assert.Empty(transport.SentCol01Reports);
        }

        [Fact]
        public void GameExit_BlankRetriedUntilAccepted()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "3"));

            transport.SendReturns = false;              // blanking write declined
            driver.Update(NotRunningData());
            transport.SentCol01Reports.Clear();

            transport.SendReturns = true;
            driver.Update(NotRunningData());            // retried and accepted
            Assert.Single(transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                transport.LastSegments);

            transport.SentCol01Reports.Clear();
            driver.Update(NotRunningData());            // silent after success
            Assert.Empty(transport.SentCol01Reports);
        }

        [Fact]
        public void GameRestart_AfterExit_ShowsSameGearAgain()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "3"));
            driver.Update(NotRunningData());            // exit — blanked
            transport.SentCol01Reports.Clear();

            driver.Update(MakeData(gear: "3"));         // same gear as before the exit

            Assert.Single(transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank),
                transport.LastSegments);
        }

        // ── Failed-send retry (rate limiter must not latch on failure) ───

        [Fact]
        public void FailedGearWrite_RetriedNextFrame()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            transport.SendReturns = false;
            driver.Update(MakeData(gear: "3"));         // write fails — must not latch
            transport.SentCol01Reports.Clear();

            transport.SendReturns = true;
            driver.Update(MakeData(gear: "3"));         // unchanged gear is retried

            Assert.Single(transport.SentCol01Reports);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank),
                transport.LastSegments);
        }

        // ── Clear ────────────────────────────────────────────────────────

        [Fact]
        public void Clear_BlanksDisplayAndResetsState()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "4"));
            driver.Clear();

            Assert.Equal((SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank), transport.LastSegments);
            Assert.Equal("", driver.CurrentText);
            Assert.Equal("", driver.CurrentGear);
        }

        [Fact]
        public void Clear_ResetsRateLimiter_SoSameValueWritesAgain()
        {
            var transport = new RecordingTransport();
            var driver = MakeDriver(transport, "Gear");

            driver.Update(MakeData(gear: "4"));
            int afterFirst = transport.SentCol01Reports.Count;

            driver.Clear();
            driver.Update(MakeData(gear: "4")); // same gear, but cache was reset

            // first write + clear write + second write
            Assert.Equal(afterFirst + 2, transport.SentCol01Reports.Count);
        }
    }
}
