using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BA63Driver.Mapper;
using FanaBridge.Adapters;
using FanaBridge.Devices.Profiles;
using FanaBridge.Leds;
using FanaBridge.Transport;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Tests for <see cref="FanatecLedDriver"/> — the physical mapper it builds
    /// and the per-channel command dispatch in <c>SendLeds</c>.
    /// </summary>
    public class FanatecLedDriverTests
    {
        // ── Transport stub ───────────────────────────────────────────────

        private sealed class RecordingTransport : IDeviceTransport
        {
            private readonly object _lock = new object();
            private readonly List<byte[]> _col01 = new List<byte[]>();
            private readonly List<byte[]> _col03 = new List<byte[]>();

            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength => 64;

            public bool SendCol01(byte[] data) { lock (_lock) { _col01.Add((byte[])data.Clone()); } return true; }
            public bool SendCol03(byte[] data) { lock (_lock) { _col03.Add((byte[])data.Clone()); } return true; }
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }

            public byte[][] Col01 { get { lock (_lock) { return _col01.ToArray(); } } }
            public byte[][] Col03 { get { lock (_lock) { return _col03.ToArray(); } } }
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        // Builds caps from (channel, count) groups. hwIndex restarts per channel,
        // except buttonAuxIntensity continues after buttonRgb (they share the
        // single col03 0x03 intensity payload), matching the real profiles.
        private static WheelCapabilities Caps(params (string channel, int count)[] groups)
        {
            int buttonRgbCount = groups.Where(g => g.channel == "buttonRgb").Sum(g => g.count);
            var leds = new List<object>();
            foreach (var (channel, count) in groups)
            {
                for (int i = 0; i < count; i++)
                {
                    int hw = channel == "buttonAuxIntensity" ? buttonRgbCount + i : i;
                    string role = channel == "buttonAuxIntensity" ? "encoder"
                                : channel.StartsWith("button") ? "button"
                                : "rev";
                    leds.Add(new { channel, hwIndex = hw, role, label = channel + i });
                }
            }
            var json = JsonConvert.SerializeObject(new { id = "TEST", name = "Test", leds });
            return new WheelCapabilities(JsonConvert.DeserializeObject<WheelProfile>(json));
        }

        private static FanatecLedDriver BuildDriver(WheelCapabilities caps, IDeviceTransport transport)
            => new FanatecLedDriver(caps, new LedEncoder(transport), new LegacyLedEncoder(transport));

        // Lights every LED white via raw/individual state, runs one frame, and
        // returns the recorded reports. Dispose() blocks until the async write
        // task finishes, so all reports are present when this returns.
        private static RecordingTransport RunAllWhite(WheelCapabilities caps)
        {
            var transport = new RecordingTransport();
            var driver = BuildDriver(caps, transport);
            var raw = new Color[caps.AllLedCount];
            for (int i = 0; i < raw.Length; i++) raw[i] = Color.FromArgb(255, 255, 255, 255);
            var state = new LedDeviceState(new Color[0], new Color[0], new Color[0], new Color[0], raw);

            Assert.True(driver.SendLeds(state, forceRefresh: true));
            driver.Dispose();
            return transport;
        }

        // As RunAllWhite, but drives every LED with a chosen color.
        private static RecordingTransport RunAllColor(WheelCapabilities caps, Color color)
        {
            var transport = new RecordingTransport();
            var driver = BuildDriver(caps, transport);
            var raw = new Color[caps.AllLedCount];
            for (int i = 0; i < raw.Length; i++) raw[i] = color;
            var state = new LedDeviceState(new Color[0], new Color[0], new Color[0], new Color[0], raw);

            Assert.True(driver.SendLeds(state, forceRefresh: true));
            driver.Dispose();
            return transport;
        }

        private static bool Col03Has(RecordingTransport t, byte subcmd) => t.Col03.Any(r => r[2] == subcmd);
        private static bool Col01Has(RecordingTransport t, byte subcmd) => t.Col01.Any(r => r[3] == subcmd);
        private static byte[] Col03Last(RecordingTransport t, byte subcmd) => t.Col03.Last(r => r[2] == subcmd);

        // ── Mapper regression tests (the GTSWX button-LED bug) ───────────
        //
        // ButtonRangeMap's first arg indexes the BUTTONS section array, not the
        // combined layout; it must be 0 or grouped ("Individual LEDs: Disabled")
        // mode reads ButtonsState out of bounds and button LEDs stay black.

        [Fact]
        public void Mapper_GroupedMode_ButtonLedsReadFromButtonsState()
        {
            var caps = Caps(("revRgb", 9), ("flagRgb", 6), ("buttonRgb", 4)); // GTSWX shape
            var mapper = BuildDriver(caps, new RecordingTransport()).GetPhysicalMapper();

            int revFlagCount = caps.RevFlagCount;   // 15
            int buttonCount = caps.ButtonLedCount;  // 4

            // Grouped mode: sections populated, RawState empty. Gray values keep
            // the assertion independent of any ColorOrder channel remapping.
            var buttonsState = new Color[buttonCount];
            for (int i = 0; i < buttonCount; i++)
                buttonsState[i] = Color.FromArgb(255, 40 + i * 20, 40 + i * 20, 40 + i * 20);

            var state = new LedDeviceState(new Color[revFlagCount], buttonsState,
                new Color[0], new Color[0], new Color[0]);

            for (int i = 0; i < buttonCount; i++)
            {
                Color c = mapper.GetColor(revFlagCount + i, state, ignoreBrightness: true);
                Assert.False(c.R == 0 && c.G == 0 && c.B == 0,
                    $"Button {i} resolved to black — ButtonsState not reached.");
                Assert.Equal(40 + i * 20, c.R);
            }
        }

        [Fact]
        public void Mapper_GroupedMode_TelemetryLedsReadFromLedsState()
        {
            var caps = Caps(("revRgb", 9), ("flagRgb", 6), ("buttonRgb", 4));
            var mapper = BuildDriver(caps, new RecordingTransport()).GetPhysicalMapper();

            int revFlagCount = caps.RevFlagCount;
            var ledsState = new Color[revFlagCount];
            for (int i = 0; i < revFlagCount; i++)
                ledsState[i] = Color.FromArgb(255, 10 + i, 10 + i, 10 + i);

            var state = new LedDeviceState(ledsState, new Color[caps.ButtonLedCount],
                new Color[0], new Color[0], new Color[0]);

            for (int i = 0; i < revFlagCount; i++)
                Assert.Equal(10 + i, mapper.GetColor(i, state, ignoreBrightness: true).R);
        }

        [Fact]
        public void Mapper_IndividualMode_ButtonLedsReadFromRawState()
        {
            // Individual mode: RawState carries the full contiguous layout indexed
            // by physical position. This path worked even with the original bug.
            var caps = Caps(("revRgb", 9), ("flagRgb", 6), ("buttonRgb", 4));
            var mapper = BuildDriver(caps, new RecordingTransport()).GetPhysicalMapper();

            var rawState = new Color[caps.AllLedCount];
            int firstButton = caps.RevFlagCount;
            rawState[firstButton] = Color.FromArgb(255, 70, 70, 70);

            var state = new LedDeviceState(new Color[0], new Color[0], new Color[0], new Color[0], rawState);
            Assert.Equal(70, mapper.GetColor(firstButton, state, ignoreBrightness: true).R);
        }

        // ── Per-combo dispatch tests ─────────────────────────────────────
        //
        // One representative example per wheel-lighting combo, asserting which
        // hardware command (col03 subcmd / col01 subcmd) a lit LED produces.
        // col03 subcmd is byte[2]; col01 subcmd is byte[3].

        [Fact] // CSSWFORMV2 etc.
        public void Dispatch_RevFlagRgb_SendsRevAndFlagColorReports()
        {
            var t = RunAllWhite(Caps(("revRgb", 9), ("flagRgb", 6)));

            Assert.True(Col03Has(t, 0x00), "rev colors (0x00) missing");
            Assert.True(Col03Has(t, 0x01), "flag colors (0x01) missing");
            Assert.False(Col03Has(t, 0x02), "unexpected button colors (0x02)");
            Assert.False(Col03Has(t, 0x03), "unexpected button intensities (0x03)");
            Assert.Empty(t.Col01);

            var rev = Col03Last(t, 0x00); // RGB565 of white, big-endian at offset 3
            Assert.True(rev[3] != 0 || rev[4] != 0, "rev LED 0 color is zero");
        }

        [Fact] // GTSWX
        public void Dispatch_RevFlagButtonRgb_SendsAllCol03Channels()
        {
            var t = RunAllWhite(Caps(("revRgb", 9), ("flagRgb", 6), ("buttonRgb", 4)));

            Assert.True(Col03Has(t, 0x00), "rev colors (0x00) missing");
            Assert.True(Col03Has(t, 0x01), "flag colors (0x01) missing");
            Assert.True(Col03Has(t, 0x02), "button colors (0x02) missing");
            Assert.True(Col03Has(t, 0x03), "button intensities (0x03) missing");
            Assert.Empty(t.Col01);

            var btn = Col03Last(t, 0x02);
            Assert.True(btn[3] != 0 || btn[4] != 0, "button LED 0 color is zero");
        }

        [Fact] // PBMR
        public void Dispatch_ButtonRgbOnly_SendsButtonColorAndIntensity()
        {
            var t = RunAllWhite(Caps(("buttonRgb", 12)));

            Assert.False(Col03Has(t, 0x00), "unexpected rev colors (0x00)");
            Assert.False(Col03Has(t, 0x01), "unexpected flag colors (0x01)");
            Assert.True(Col03Has(t, 0x02), "button colors (0x02) missing");
            Assert.True(Col03Has(t, 0x03), "button intensities (0x03) missing");
            Assert.Empty(t.Col01);
        }

        [Fact] // PSWBMW
        public void Dispatch_ButtonRgbWithAuxIntensity_SetsAuxSlotsInIntensityReport()
        {
            var t = RunAllWhite(Caps(("buttonRgb", 12), ("buttonAuxIntensity", 3)));

            Assert.True(Col03Has(t, 0x02), "button colors (0x02) missing");
            Assert.True(Col03Has(t, 0x03), "button intensities (0x03) missing");

            // Aux LEDs occupy intensity slots 12..14 (after the 12 buttonRgb).
            // Payload starts at byte 3, so slot 12 lands at byte 15.
            var intensity = Col03Last(t, 0x03);
            Assert.NotEqual(0, intensity[3]);       // buttonRgb 0 intensity
            Assert.NotEqual(0, intensity[3 + 12]);  // aux 0 intensity
        }

        [Fact] // CSL P1 / WRC
        public void Dispatch_LegacyRevStripe_AssertsColorModeAndSendsData()
        {
            var t = RunAllWhite(Caps(("legacyRevStripe", 1)));

            Assert.Empty(t.Col03);
            Assert.True(Col01Has(t, 0x07), "color-mode assert (0x07) missing");
            Assert.True(Col01Has(t, 0x08), "LED data (0x08) missing");
        }

        [Fact] // CSSWBMW etc.
        public void Dispatch_LegacyRevOnOff_SendsBitmask()
        {
            var t = RunAllWhite(Caps(("legacyRevOnOff", 9)));

            Assert.Empty(t.Col03);
            Assert.True(Col01Has(t, 0x08), "bitmask LED data (0x08) missing");
        }

        [Fact] // GTSWPRO
        public void Dispatch_LegacyRev3Bit_SendsRgbData()
        {
            var t = RunAllWhite(Caps(("legacyRev3Bit", 9)));

            Assert.Empty(t.Col03);
            Assert.True(Col01Has(t, 0x0A), "3-bit rev data (0x0A) missing");
        }

        [Fact] // supported channel, no shipped profile yet
        public void Dispatch_LegacyFlag3Bit_SendsFlagData()
        {
            var t = RunAllWhite(Caps(("legacyFlag3Bit", 6)));

            Assert.Empty(t.Col03);
            Assert.True(Col01Has(t, 0x0B), "3-bit flag data (0x0B) missing");
        }

        [Fact] // #76 — the exact color that breaks the rim must never reach the wire
        public void Dispatch_LegacyRevStripe_MidDarkGreen_IsSnappedNotSentRaw()
        {
            // Unsnapped, RGB(0,128,0) encodes to RGB333 0x0001 -> wire "01 00", which
            // is byte-identical to the "LED 0 only" pattern and makes the driver stack
            // switch the rim out of color mode. White would pass this test under
            // either encoder, so it has to be this color.
            var t = RunAllColor(Caps(("legacyRevStripe", 1)), Color.FromArgb(255, 0, 128, 0));

            var colorFrames = t.Col01.Where(r => r[3] == 0x08).ToList();
            Assert.NotEmpty(colorFrames);
            Assert.All(colorFrames, r =>
                Assert.False(r[4] == 0x01 && r[5] == 0x00, "raw 01 00 payload reached the wire"));

            // Snapped to full green: data_lo = 0x01, data_hi = 0xC0.
            Assert.Contains(colorFrames, r => r[4] == 0x01 && r[5] == 0xC0);
        }

        [Fact] // #82 — the rim white LED has no business in an LED write path
        public void Dispatch_LegacyChannels_NeverSendWhiteLed()
        {
            foreach (var caps in new[]
                     {
                         Caps(("legacyRevStripe", 1)),
                         Caps(("legacyRevOnOff", 9)),
                         Caps(("legacyRev3Bit", 9)),
                         Caps(("legacyFlag3Bit", 6)),
                     })
            {
                var t = RunAllWhite(caps);
                Assert.False(Col01Has(t, 0x02), "rim white LED (0x02) must not be sent");
            }
        }

        [Fact] // #82 — 0x06 is stored state, only the stripe path may touch it
        public void Dispatch_NonStripeChannels_NeverTouchStripePreference()
        {
            foreach (var caps in new[]
                     {
                         Caps(("legacyRevOnOff", 9)),
                         Caps(("legacyRev3Bit", 9)),
                         Caps(("legacyFlag3Bit", 6)),
                     })
            {
                var t = RunAllWhite(caps);
                Assert.False(Col01Has(t, 0x06), "stripe preference (0x06) must not be sent");
            }
        }
    }
}
