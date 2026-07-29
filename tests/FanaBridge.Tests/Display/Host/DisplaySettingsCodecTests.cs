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

        [Fact]
        public void Write_EmitsOnlyLiveSettings()
        {
            var settings = new DisplaySettings
            {
                DisplayControl = DisplaySettings.ControlLegacy,
                DisplayMode = "Speed",
                ItmEnabled = false,
            };
            var document = new JObject
            {
                ["displayControl"] = "Itm",
                ["displayMode"] = "Gear",
                ["itmEnabled"] = true,
            };

            DisplaySettingsCodec.Write(document, settings);

            Assert.Null(document["displayMode"]);
            Assert.Null(document["displayControl"]);
            Assert.Null(document["itmEnabled"]);
            Assert.NotNull(document["itmShowLapTotal"]);
            Assert.NotNull(document["itmShowPositionTotal"]);
            Assert.NotNull(document["itmDefaultPage"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WriteDefaults_EmitsOnlyLiveSettings(bool itmCapable)
        {
            var document = new JObject();

            DisplaySettingsCodec.WriteDefaults(document, itmCapable);

            Assert.Null(document["displayMode"]);
            Assert.Null(document["displayControl"]);
            Assert.Null(document["itmEnabled"]);
            Assert.Equal(DisplaySettings.DefaultShowLapTotal, (bool)document["itmShowLapTotal"]!);
            Assert.Equal(DisplaySettings.DefaultShowPositionTotal,
                (bool)document["itmShowPositionTotal"]!);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, (byte)document["itmDefaultPage"]!);
        }

        [Fact]
        public void WriteDefaults_NonItm_DoesNotFreezeLaterItmCapableRead()
        {
            var document = new JObject();
            DisplaySettingsCodec.WriteDefaults(document, itmCapable: false);
            Assert.Null(document["displayControl"]);

            var whenCapable = DisplaySettingsCodec.Read(document, itmCapable: true);

            Assert.Equal(DisplaySettings.ControlItm, whenCapable.DisplayControl);
            Assert.True(whenCapable.ItmActive);
            // Still resolve-on-read: the defaults blob is not rewritten by Read.
            Assert.Null(document["displayControl"]);
        }

        [Fact]
        public void Read_AbsentControl_RemigratesWhenCapsBecomeItmCapable()
        {
            // Pre-tristate blob: itmEnabled true + Gear, no displayControl.
            var source = new JObject
            {
                ["displayMode"] = "Gear",
                ["itmEnabled"] = true,
            };

            var nonItm = DisplaySettingsCodec.Read(source, itmCapable: false);
            Assert.Equal(DisplaySettings.ControlLegacy, nonItm.DisplayControl);
            Assert.False(nonItm.ItmActive);
            Assert.Null(source["displayControl"]);

            // Later caps resolve as ITM-capable: same blob remigrates to Itm.
            var itm = DisplaySettingsCodec.Read(source, itmCapable: true);
            Assert.Equal(DisplaySettings.ControlItm, itm.DisplayControl);
            Assert.True(itm.ItmActive);
            Assert.Null(source["displayControl"]);
        }

        [Fact]
        public void Read_StoredLegacy_IsHonoredEvenWhenItmCapable()
        {
            // Explicit user/store choice of Legacy (mirror may disagree) stays Legacy.
            var source = new JObject
            {
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["displayMode"] = "Gear",
                ["itmEnabled"] = true,
            };

            var result = DisplaySettingsCodec.Read(source, itmCapable: true);

            Assert.Equal(DisplaySettings.ControlLegacy, result.DisplayControl);
            Assert.False(result.ItmActive);
        }

        [Theory]
        [InlineData(DisplaySettings.ControlItm, "Gear", true, true)]
        [InlineData(DisplaySettings.ControlItm, DisplaySettings.ModeNone, true, true)]
        [InlineData(DisplaySettings.ControlLegacy, "Gear", false, true)]
        [InlineData(DisplaySettings.ControlLegacy, DisplaySettings.ModeNone, false, true)]
        [InlineData(DisplaySettings.ControlOff, "Gear", false, false)]
        [InlineData(DisplaySettings.ControlOff, DisplaySettings.ModeNone, false, false)]
        public void DerivedGates_FollowControlTruthTable(string control, string mode,
            bool itmActive, bool legacyPageActive)
        {
            // Phase 9a: LegacyPageActive is Off-only; ModeNone no longer gates the page
            // (empty legacy world = wire silence instead).
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
