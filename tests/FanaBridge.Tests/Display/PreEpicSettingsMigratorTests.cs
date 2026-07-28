using System.Collections.Generic;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Host;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Spec §9b — pre-epic settings → v2 direct migrator (bake-on-sight, marker, idempotent).
    /// </summary>
    public class PreEpicSettingsMigratorTests
    {
        // ── Bake once + marker ────────────────────────────────────────────

        [Fact]
        public void Bake_StampsMigratedFromMarker()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm, DisplaySettings.DefaultItmDefaultPage);

            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            Assert.Equal(
                PreEpicSettingsMigrator.MarkerValue,
                (string)doc.ExtensionData![PreEpicSettingsMigrator.MarkerKey]!);
        }

        [Fact]
        public void Apply_ExistingV2_NeverOverwritten()
        {
            var authored = new DisplayConfigV2();
            authored.Settings.Mode = SettingsMode.LegacyOnly;
            authored.ProfileId = "keep-me";

            var result = PreEpicSettingsMigrator.Apply(
                authored,
                DisplaySettings.ControlItm,
                itmDefaultPage: 5);

            Assert.Same(authored, result);
            Assert.Equal(SettingsMode.LegacyOnly, result.Settings.Mode);
            Assert.Equal("keep-me", result.ProfileId);
            Assert.False(PreEpicSettingsMigrator.HasMarker(result));
        }

        [Fact]
        public void Apply_Idempotent_SecondCallReturnsSameDocument()
        {
            var first = PreEpicSettingsMigrator.Apply(
                existingV2: null,
                DisplaySettings.ControlItm,
                DisplaySettings.DefaultItmDefaultPage);
            Assert.True(PreEpicSettingsMigrator.HasMarker(first));

            var second = PreEpicSettingsMigrator.Apply(
                first,
                DisplaySettings.ControlOff,
                itmDefaultPage: 5);

            Assert.Same(first, second);
            Assert.Equal(SettingsMode.On, second.Settings.Mode);
            Assert.Equal(
                "lapInfo",
                second.Priority.Rest.InSessionPage.CatalogPageId);
        }

        // ── Mode / displayControl → settings.mode ─────────────────────────

        [Theory]
        [InlineData("Itm", SettingsMode.On)]
        [InlineData("itm", SettingsMode.On)]
        [InlineData("Legacy", SettingsMode.LegacyOnly)]
        [InlineData("legacy", SettingsMode.LegacyOnly)]
        [InlineData("Off", SettingsMode.Off)]
        [InlineData("off", SettingsMode.Off)]
        [InlineData("", SettingsMode.On)]
        [InlineData("UnknownControl", SettingsMode.On)]
        public void Bake_MapsDisplayControlToSettingsMode(
            string displayControl, SettingsMode expected)
        {
            var doc = PreEpicSettingsMigrator.Bake(
                displayControl, DisplaySettings.DefaultItmDefaultPage);
            Assert.Equal(expected, doc.Settings.Mode);
            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
        }

        [Fact]
        public void Bake_NullDisplayControl_MapsToOn()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                displayControl: null, DisplaySettings.DefaultItmDefaultPage);
            Assert.Equal(SettingsMode.On, doc.Settings.Mode);
            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
        }

        // ── itmDefaultPage → rest.inSessionPage ───────────────────────────

        [Fact]
        public void Bake_DefaultItmDefaultPage_RestIsLapInfo()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm, DisplaySettings.DefaultItmDefaultPage);

            var rest = doc.Priority.Rest.InSessionPage;
            Assert.NotNull(rest);
            Assert.Equal(PageRefKind.ItmPage, rest.Kind);
            Assert.Equal("lapInfo", rest.CatalogPageId);
        }

        [Theory]
        [InlineData(1, "lapInfo")]
        [InlineData(2, "fuelErsDrs")]
        [InlineData(3, "carSettings")]
        [InlineData(4, "lapTimes")]
        [InlineData(5, "tyreTemps")]
        [InlineData(6, "legacy")]
        public void Bake_PresentItmDefaultPage_MapsViaStandardCatalog(
            byte wirePage, string catalogPageId)
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm, wirePage, itmDeviceId: 1);

            Assert.Equal(PageRefKind.ItmPage, doc.Priority.Rest.InSessionPage.Kind);
            Assert.Equal(catalogPageId, doc.Priority.Rest.InSessionPage.CatalogPageId);
            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
        }

        [Fact]
        public void Bake_BentleyDevice_RenumberedPages()
        {
            // Bentley (device 4): wire 3 = LapTimes (no CarSettings).
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm, itmDefaultPage: 3, itmDeviceId: 4);

            Assert.Equal("lapTimes", doc.Priority.Rest.InSessionPage.CatalogPageId);
        }

        [Fact]
        public void Bake_UnresolvableItmDefaultPage_OmitsRest_Logs_StillMarked()
        {
            var warnings = new List<string>();
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm,
                itmDefaultPage: 99,
                itmDeviceId: 1,
                warnings.Add);

            Assert.Null(doc.Priority.Rest.InSessionPage);
            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            Assert.Single(warnings);
            Assert.Contains("itmDefaultPage 99", warnings[0]);
            Assert.Contains("rest.inSessionPage omitted", warnings[0]);
        }

        // ── Mapping surface is closed ─────────────────────────────────────

        [Fact]
        public void Bake_DoesNotReadOtherPreEpicKeys()
        {
            // displayMode / show totals are deliberately ignored — only control + page.
            // A baked document has no pages/cycles/fields/wheelScreen content.
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy, itmDefaultPage: 2);

            Assert.Equal(SettingsMode.LegacyOnly, doc.Settings.Mode);
            Assert.Equal("fuelErsDrs", doc.Priority.Rest.InSessionPage.CatalogPageId);
            Assert.Empty(doc.Pages);
            Assert.Empty(doc.Cycles);
            Assert.Empty(doc.Fields);
            Assert.Empty(doc.WheelScreen.Rules);
            Assert.Empty(doc.Priority.Rows);
        }

        [Fact]
        public void Bake_RoundTripsMarkerThroughSerializer()
        {
            var baked = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm, DisplaySettings.DefaultItmDefaultPage);
            string json = DisplayConfigV2Serializer.Save(baked);
            var reloaded = DisplayConfigV2Serializer.Load(json, _ => { });

            Assert.True(PreEpicSettingsMigrator.HasMarker(reloaded));
            Assert.Equal(
                PageRefKind.ItmPage,
                reloaded.Priority.Rest.InSessionPage.Kind);
            Assert.Equal("lapInfo", reloaded.Priority.Rest.InSessionPage.CatalogPageId);
        }

        // ── Pure map helpers ──────────────────────────────────────────────

        [Theory]
        [InlineData("Itm", SettingsMode.On)]
        [InlineData("Legacy", SettingsMode.LegacyOnly)]
        [InlineData("Off", SettingsMode.Off)]
        public void MapControlToMode_TriState(string control, SettingsMode expected)
            => Assert.Equal(expected, PreEpicSettingsMigrator.MapControlToMode(control));

        [Fact]
        public void TryResolveItmPage_UnknownWire_False()
        {
            Assert.False(
                PreEpicSettingsMigrator.TryResolveItmPage(0, itmDeviceId: 1, out _));
            Assert.False(
                PreEpicSettingsMigrator.TryResolveItmPage(99, itmDeviceId: 1, out _));
        }
    }
}
