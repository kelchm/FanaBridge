using System.Collections.Generic;
using System.Linq;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.UI.Wizard;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Plugin.UI.Wizard
{
    public class WizardProfileSerializationTests
    {
        // Zero-valued fields (first enum members, hwIndex 0) are what
        // DefaultValueHandling.Ignore used to drop.
        private static WheelProfile ProfileWithZeroValues() => new WheelProfile
        {
            Schema = "wheel-profile.schema.json",
            SchemaVersion = WheelProfile.CurrentSchemaVersion,
            Id = "test-wheel",
            Name = "Test Wheel",
            ShortName = "Test Wheel",
            Match = new ProfileMatch { WheelType = "CSSWFORMV3" },
            Display = "none",
            Leds = new List<LedDefinition>
            {
                new LedDefinition { Channel = LedChannel.RevRgb, HwIndex = 0, Role = LedRole.Rev, Label = "Rev 1" },
                new LedDefinition { Channel = LedChannel.ButtonRgb, HwIndex = 3, Role = LedRole.Button, Label = "Button 4" },
            },
        };

        [Fact]
        public void SerializeProfile_WritesRequiredLedFieldsEvenWhenZero()
        {
            var json = JObject.Parse(WheelProfileWizardDialog.SerializeProfile(ProfileWithZeroValues()));

            // Required by wheel-profile.schema.json's LedDefinition.
            foreach (var led in json["leds"])
            {
                Assert.NotNull(led["channel"]);
                Assert.NotNull(led["hwIndex"]);
                Assert.NotNull(led["role"]);
                Assert.NotNull(led["label"]);
            }

            var first = json["leds"][0];
            Assert.Equal("revRgb", (string)first["channel"]);
            Assert.Equal(0, (int)first["hwIndex"]);
            Assert.Equal("rev", (string)first["role"]);
        }

        [Fact]
        public void SerializeProfile_StillOmitsNulls()
        {
            var json = JObject.Parse(WheelProfileWizardDialog.SerializeProfile(ProfileWithZeroValues()));

            Assert.Null(json["match"]["moduleType"]);
            Assert.Null(json["itmDeviceId"]);
            Assert.Null(json["colorFormat"]);
            Assert.Null(json["leds"][0]["input"]);
            Assert.Null(json["leds"][0]["inputMapping"]);
        }

        [Fact]
        public void SerializeProfile_RoundTrips()
        {
            var original = ProfileWithZeroValues();
            var loaded = JsonConvert.DeserializeObject<WheelProfile>(
                WheelProfileWizardDialog.SerializeProfile(original));

            Assert.Equal(
                original.Leds.Select(l => (l.Channel, l.HwIndex, l.Role, l.Label)),
                loaded.Leds.Select(l => (l.Channel, l.HwIndex, l.Role, l.Label)));
        }
    }
}
