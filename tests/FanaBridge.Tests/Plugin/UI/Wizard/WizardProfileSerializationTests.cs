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
                new LedDefinition
                {
                    Channel = LedChannel.ButtonRgb,
                    HwIndex = 3,
                    Role = LedRole.Button,
                    Label = "Button 4",
                    InputMapping = new InputMapping { Button = "JoystickPlugin.FANATEC_Wheel.Button3" },
                },
            },
        };

        private static JObject Serialize(WheelProfile profile) =>
            JObject.Parse(WheelProfileWizardDialog.SerializeProfile(profile));

        private static JArray Leds(JObject json)
        {
            var leds = json["leds"] as JArray;
            Assert.NotNull(leds);
            return leds!;
        }

        private static JObject Led(JObject json, int index)
        {
            var led = Leds(json)[index] as JObject;
            Assert.NotNull(led);
            return led!;
        }

        [Fact]
        public void SerializeProfile_WritesRequiredLedFieldsEvenWhenZero()
        {
            var json = Serialize(ProfileWithZeroValues());

            // Required by wheel-profile.schema.json's LedDefinition.
            foreach (var led in Leds(json).Cast<JObject>())
            {
                Assert.NotNull(led["channel"]);
                Assert.NotNull(led["hwIndex"]);
                Assert.NotNull(led["role"]);
                Assert.NotNull(led["label"]);
            }

            var first = Led(json, 0);
            Assert.Equal("revRgb", first.Value<string>("channel"));
            Assert.Equal(0, first.Value<int>("hwIndex"));
            Assert.Equal("rev", first.Value<string>("role"));
        }

        [Fact]
        public void SerializeProfile_StillOmitsNulls()
        {
            var json = Serialize(ProfileWithZeroValues());

            var match = json["match"] as JObject;
            Assert.NotNull(match);
            Assert.Null(match!["moduleType"]);
            Assert.Null(json["itmDeviceId"]);
            Assert.Null(json["colorFormat"]);
            Assert.Null(json["verified"]);

            var first = Led(json, 0);
            Assert.Null(first["input"]);
            Assert.Null(first["inputMapping"]);

            // A button-only mapping writes just the button.
            var mapping = Led(json, 1)["inputMapping"] as JObject;
            Assert.NotNull(mapping);
            Assert.Equal("JoystickPlugin.FANATEC_Wheel.Button3", mapping!.Value<string>("button"));
            Assert.Null(mapping["relative"]);
            Assert.Null(mapping["absolute"]);
        }

        [Fact]
        public void SerializeProfile_RoundTrips()
        {
            var original = ProfileWithZeroValues();
            original.VerifiedRaw = false;
            original.ColorFormatRaw = "Rgb555";
            original.ItmDeviceIdRaw = 4;

            var loaded = JsonConvert.DeserializeObject<WheelProfile>(
                WheelProfileWizardDialog.SerializeProfile(original));

            Assert.NotNull(loaded);
            Assert.Equal(original.SchemaVersion, loaded!.SchemaVersion);
            Assert.Equal(original.Id, loaded.Id);
            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.ShortName, loaded.ShortName);
            Assert.NotNull(loaded.Match);
            Assert.Equal(original.Match.WheelType, loaded.Match.WheelType);
            Assert.Equal(original.Match.ModuleType, loaded.Match.ModuleType);
            Assert.Equal(original.Display, loaded.Display);
            Assert.Equal(original.VerifiedRaw, loaded.VerifiedRaw);
            Assert.Equal(original.ColorFormatRaw, loaded.ColorFormatRaw);
            Assert.Equal(original.ItmDeviceIdRaw, loaded.ItmDeviceIdRaw);

            Assert.Equal(
                original.Leds.Select(l => (l.Channel, l.HwIndex, l.Role, l.Label)),
                loaded.Leds.Select(l => (l.Channel, l.HwIndex, l.Role, l.Label)));
            // InputMapping has no value equality; compare its JSON form.
            Assert.Equal(
                original.Leds.Select(l => JsonConvert.SerializeObject(l.InputMapping)),
                loaded.Leds.Select(l => JsonConvert.SerializeObject(l.InputMapping)));
        }
    }
}
