using System.Collections.Generic;
using System.Linq;
using FanaBridge.Core.Devices.Profiles;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Profiles
{
    /// <summary>
    /// SimHub offers a full color picker for every LED. These tests pin which
    /// channels are honest about that and which need explaining to the user.
    /// </summary>
    public class LedColorLimitationTests
    {
        private static WheelCapabilities Caps(params (string channel, int count)[] groups)
        {
            var leds = new List<object>();
            foreach (var (channel, count) in groups)
                for (int i = 0; i < count; i++)
                    leds.Add(new
                    {
                        channel,
                        hwIndex = i,
                        // Roles as the shipped profiles assign them — the notice
                        // reads these, not the channel names.
                        role = channel == "buttonAuxIntensity" ? "encoder"
                             : channel.StartsWith("button") ? "button"
                             : channel.ToLowerInvariant().Contains("flag") ? "flag"
                             : "rev",
                        label = channel + i,
                    });

            var json = JsonConvert.SerializeObject(new { id = "TEST", name = "Test", leds });
            return new WheelCapabilities(JsonConvert.DeserializeObject<WheelProfile>(json));
        }

        [Fact]
        public void FullColorWheel_HasNoLimitations()
        {
            Assert.Empty(LedColorLimitation.ForCapabilities(Caps(("revRgb", 9), ("flagRgb", 2))));
        }

        [Theory]
        [InlineData("legacyRevOnOff", 9)]
        [InlineData("legacyRevStripe", 1)]
        [InlineData("legacyRev3Bit", 9)]
        [InlineData("legacyFlag3Bit", 6)]
        [InlineData("buttonAuxIntensity", 4)]
        public void LimitedChannel_ProducesANotice(string channel, int count)
        {
            var limitations = LedColorLimitation.ForCapabilities(Caps((channel, count)));

            Assert.Single(limitations);
            Assert.False(string.IsNullOrWhiteSpace(limitations[0].Text));
        }

        [Fact]
        public void MultipleLimitedChannels_ProduceOneNoticeEach()
        {
            // A wheel can mix them — each limitation is a different thing to explain.
            var limitations = LedColorLimitation.ForCapabilities(
                Caps(("legacyRevOnOff", 9), ("buttonAuxIntensity", 2)));

            Assert.Equal(2, limitations.Count);
            Assert.Equal(limitations.Count, limitations.Select(l => l.Text).Distinct().Count());
        }

        [Fact]
        public void ThreeBitRevAndFlag_ShareASingleNotice()
        {
            // Same limitation, same explanation — saying it twice would be noise.
            Assert.Single(LedColorLimitation.ForCapabilities(
                Caps(("legacyRev3Bit", 9), ("legacyFlag3Bit", 6))));
        }

        [Theory]
        // The notice has to name the LEDs the user is looking at, not say "these LEDs".
        [InlineData("legacyRev3Bit", "Rev LEDs")]
        [InlineData("legacyFlag3Bit", "Flag LEDs")]
        public void ThreeBitNotice_NamesTheRoleInvolved(string channel, string expected)
        {
            var limitation = Assert.Single(LedColorLimitation.ForCapabilities(Caps((channel, 6))));
            Assert.Contains(expected, limitation.Text);
        }

        [Fact]
        public void ThreeBitNotice_NamesBothRolesWhenBothArePresent()
        {
            var limitation = Assert.Single(LedColorLimitation.ForCapabilities(
                Caps(("legacyRev3Bit", 9), ("legacyFlag3Bit", 6))));
            // Shares the noun rather than repeating it as "rev LEDs and flag LEDs".
            Assert.Contains("Rev and Flag LEDs", limitation.Text);
        }

        [Fact]
        public void Notice_NamesWhateverRoleTheProfileAssigned()
        {
            // Nothing ties a channel to a role — a custom profile can drive flag
            // LEDs through the on/off channel, and the notice must follow it.
            var json = JsonConvert.SerializeObject(new
            {
                id = "TEST",
                name = "Test",
                leds = Enumerable.Range(0, 4).Select(i => new
                {
                    channel = "legacyRevOnOff",
                    hwIndex = i,
                    role = "flag",
                    label = "Flag " + i,
                }),
            });
            var caps = new WheelCapabilities(JsonConvert.DeserializeObject<WheelProfile>(json));

            var limitation = Assert.Single(LedColorLimitation.ForCapabilities(caps));
            Assert.Contains("Flag LEDs", limitation.Text);
            Assert.DoesNotContain("Rev LEDs", limitation.Text);
        }

        [Theory]
        // A hub has no LEDs of its own — they belong to the module attached to it.
        [InlineData("", "This wheel's")]
        [InlineData("PBME", "This module's")]
        public void Notice_NamesWheelOrModuleFromTheProfile(string moduleType, string expected)
        {
            var json = JsonConvert.SerializeObject(new
            {
                id = "TEST",
                name = "Test",
                match = new { wheelType = "PHUB", moduleType },
                leds = Enumerable.Range(0, 9).Select(i => new
                {
                    channel = "legacyRev3Bit",
                    hwIndex = i,
                    role = "rev",
                    label = "Rev " + i,
                }),
            });
            var caps = new WheelCapabilities(JsonConvert.DeserializeObject<WheelProfile>(json));

            var limitation = Assert.Single(LedColorLimitation.ForCapabilities(caps));
            Assert.StartsWith(expected, limitation.Text);
        }

        [Fact]
        public void Notice_NamesEachGroupSeparately_WhenLimitationsDiffer()
        {
            // Two unrelated limitations on one device, each naming its own LEDs.
            var limitations = LedColorLimitation.ForCapabilities(
                Caps(("legacyRevOnOff", 9), ("buttonAuxIntensity", 3)));

            Assert.Equal(2, limitations.Count);
            Assert.Contains(limitations, l => l.Text.Contains("Rev LEDs"));
            Assert.Contains(limitations, l => l.Text.Contains("Encoder LEDs"));
        }

        [Fact]
        public void NullCapabilities_AreHandled()
        {
            Assert.Empty(LedColorLimitation.ForCapabilities(null));
        }
    }
}
