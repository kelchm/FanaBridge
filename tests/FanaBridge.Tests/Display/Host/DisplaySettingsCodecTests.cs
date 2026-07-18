using System.Collections.Generic;
using FanaBridge.Display.Host;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display.Host
{
    public class DisplaySettingsCodecTests
    {
        public static TheoryData<bool?, string?, bool, string?> MigrationCases
        {
            get
            {
                var cases = new TheoryData<bool?, string?, bool, string?>();
                bool?[] enabledValues = { true, false, null };
                string?[] modeValues = { DisplaySettings.ModeNone, "Gear", null };
                bool[] capabilityValues = { true, false };
                string?[] controlValues = { null, "Itm", "legacy", "Garbage" };

                foreach (bool? enabled in enabledValues)
                foreach (string? mode in modeValues)
                foreach (bool capable in capabilityValues)
                foreach (string? control in controlValues)
                    cases.Add(enabled, mode, capable, control);

                return cases;
            }
        }

        [Theory]
        [MemberData(nameof(MigrationCases))]
        public void Read_ResolvesFullMigrationMatrix_WithoutRewritingSource(
            bool? storedEnabled, string? storedMode, bool itmCapable, string? storedControl)
        {
            var source = new JObject
            {
                ["itmShowLapTotal"] = false,
                ["itmShowPositionTotal"] = true,
                ["itmDefaultPage"] = 5,
                ["unrelated"] = "preserved",
            };
            if (storedEnabled.HasValue)
                source["itmEnabled"] = storedEnabled.Value;
            if (storedMode != null)
                source["displayMode"] = storedMode;
            if (storedControl != null)
                source["displayControl"] = storedControl;
            var original = (JObject)source.DeepClone();

            var result = DisplaySettingsCodec.Read(source, itmCapable);

            bool enabled = storedEnabled ?? DisplaySettings.DefaultItmEnabled;
            string mode = storedMode ?? DisplaySettings.DefaultMode;
            Assert.Equal(ExpectedControl(storedControl, enabled, mode, itmCapable),
                result.DisplayControl);
            Assert.Equal(mode, result.DisplayMode);
            Assert.Equal(enabled, result.ItmEnabled);
            Assert.False(result.ItmShowLapTotal);
            Assert.True(result.ItmShowPositionTotal);
            Assert.Equal((byte)5, result.ItmDefaultPage);
            Assert.True(JToken.DeepEquals(original, source));
        }

        [Theory]
        [InlineData("itm", DisplaySettings.ControlItm, true)]
        [InlineData("LEGACY", DisplaySettings.ControlLegacy, false)]
        [InlineData("off", DisplaySettings.ControlOff, false)]
        public void ReadWriteRead_IsFixedPoint_AndWriteCanonicalizesMirror(
            string storedControl, string canonicalControl, bool mirroredEnabled)
        {
            var document = new JObject
            {
                ["displayControl"] = storedControl,
                ["displayMode"] = "GearAndSpeed",
                ["itmEnabled"] = !mirroredEnabled,
                ["itmShowLapTotal"] = false,
                ["itmShowPositionTotal"] = true,
                ["itmDefaultPage"] = 4,
                ["unrelated"] = 42,
            };

            var first = DisplaySettingsCodec.Read(document, itmCapable: false);
            DisplaySettingsCodec.Write(document, first);
            var firstWrite = (JObject)document.DeepClone();
            var second = DisplaySettingsCodec.Read(document, itmCapable: true);
            DisplaySettingsCodec.Write(document, second);

            Assert.Equal(canonicalControl, (string?)document["displayControl"]);
            Assert.Equal(mirroredEnabled, (bool)document["itmEnabled"]!);
            Assert.Equal(mirroredEnabled, second.ItmEnabled);
            Assert.Equal("GearAndSpeed", second.DisplayMode);
            Assert.False(second.ItmShowLapTotal);
            Assert.True(second.ItmShowPositionTotal);
            Assert.Equal((byte)4, second.ItmDefaultPage);
            Assert.Equal(42, (int)document["unrelated"]!);
            Assert.True(JToken.DeepEquals(firstWrite, document));
        }

        [Theory]
        [InlineData(DisplaySettings.ControlItm, true)]
        [InlineData(DisplaySettings.ControlLegacy, false)]
        [InlineData(DisplaySettings.ControlOff, false)]
        public void Write_EmitsDowngradeMirror(string control, bool expectedEnabled)
        {
            var settings = new DisplaySettings
            {
                DisplayControl = control,
                ItmEnabled = !expectedEnabled,
            };
            var document = new JObject();

            DisplaySettingsCodec.Write(document, settings);

            Assert.Equal(expectedEnabled, settings.ItmEnabled);
            Assert.Equal(expectedEnabled, (bool)document["itmEnabled"]!);
            Assert.NotNull(document["displayMode"]);
            Assert.NotNull(document["displayControl"]);
            Assert.NotNull(document["itmShowLapTotal"]);
            Assert.NotNull(document["itmShowPositionTotal"]);
            Assert.NotNull(document["itmDefaultPage"]);
        }

        [Theory]
        [InlineData(true, DisplaySettings.ControlItm)]
        [InlineData(false, DisplaySettings.ControlLegacy)]
        public void WriteDefaults_SelectsControl_ButPreservesOldItmEnabledDefault(
            bool itmCapable, string expectedControl)
        {
            var document = new JObject();

            DisplaySettingsCodec.WriteDefaults(document, itmCapable);

            Assert.Equal(DisplaySettings.DefaultMode, (string?)document["displayMode"]);
            Assert.Equal(expectedControl, (string?)document["displayControl"]);
            Assert.True((bool)document["itmEnabled"]!);
            Assert.Equal(DisplaySettings.DefaultShowLapTotal, (bool)document["itmShowLapTotal"]!);
            Assert.Equal(DisplaySettings.DefaultShowPositionTotal,
                (bool)document["itmShowPositionTotal"]!);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, (byte)document["itmDefaultPage"]!);
        }

        [Theory]
        [InlineData(DisplaySettings.ControlItm, "Gear", true, true)]
        [InlineData(DisplaySettings.ControlItm, DisplaySettings.ModeNone, true, false)]
        [InlineData(DisplaySettings.ControlLegacy, "Gear", false, true)]
        [InlineData(DisplaySettings.ControlLegacy, DisplaySettings.ModeNone, false, false)]
        [InlineData(DisplaySettings.ControlOff, "Gear", false, false)]
        [InlineData(DisplaySettings.ControlOff, DisplaySettings.ModeNone, false, false)]
        public void DerivedGates_FollowControlTruthTable(string control, string mode,
            bool itmActive, bool legacyPageActive)
        {
            var settings = new DisplaySettings
            {
                DisplayControl = control,
                DisplayMode = mode,
            };

            Assert.Equal(itmActive, settings.ItmActive);
            Assert.Equal(legacyPageActive, settings.LegacyPageActive);
        }

        private static string ExpectedControl(string? storedControl, bool itmEnabled,
            string displayMode, bool itmCapable)
        {
            if (storedControl == "Itm")
                return DisplaySettings.ControlItm;
            if (storedControl == "legacy")
                return DisplaySettings.ControlLegacy;
            if (!itmEnabled && displayMode == DisplaySettings.ModeNone)
                return DisplaySettings.ControlOff;
            if (itmCapable && itmEnabled)
                return DisplaySettings.ControlItm;
            return DisplaySettings.ControlLegacy;
        }
    }
}
