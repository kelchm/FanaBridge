using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Host;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Spec §9b — pre-epic settings → v2 direct migrator (bake-on-sight, marker, idempotent).
    /// Mode-content oracle = <see cref="Host.LegacyModeMigrationTests"/> (kinds, names, layers).
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

        // ── displayMode → hosted content (oracle: LegacyModeMigrationTests) ─

        [Theory]
        [InlineData("Gear", ContentKind.Gear, "Gear")]
        [InlineData("Speed", ContentKind.Speed, "Speed")]
        [InlineData("TotallyBogus", ContentKind.Gear, "Gear")]
        [InlineData("", ContentKind.Gear, "Gear")]
        public void Bake_Mode_SynthesizesHostedBase_ShapeIdNameKind(
            string mode, ContentKind kind, string label)
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: mode,
                itmCapable: false);

            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            var page = Assert.Single(doc.Pages);
            Assert.Equal(PageEntryKind.HostedPage, page.Kind);
            Assert.False(string.IsNullOrEmpty(page.Id));
            Assert.Equal(32, page.Id.Length); // Guid "N"
            Assert.Equal(label, page.Name);
            Assert.Equal(kind, page.Base.Content.Kind);
            Assert.Null(page.Layers);
            Assert.Equal(PageRefKind.HostedPage, doc.Priority.Rest.InSessionPage.Kind);
            Assert.Equal(page.Id, doc.Priority.Rest.InSessionPage.Id);
        }

        [Fact]
        public void Bake_NullMode_SynthesizesGear()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: null,
                itmCapable: false);

            var page = Assert.Single(doc.Pages);
            Assert.Equal(ContentKind.Gear, page.Base.Content.Kind);
            Assert.Equal("Gear", page.Name);
        }

        [Fact]
        public void Bake_GearAndSpeed_BaseSpeed_OverlayGear_OnChangeHold()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: "GearAndSpeed",
                itmCapable: false);

            var page = Assert.Single(doc.Pages);
            Assert.Equal("Speed", page.Name);
            Assert.Equal(ContentKind.Speed, page.Base.Content.Kind);

            var layer = Assert.Single(page.Layers);
            Assert.Equal(32, layer.Id.Length);
            Assert.Equal("Gear", layer.Name);
            Assert.Equal(ContentKind.Gear, layer.Content.Kind);
            Assert.Equal(ValueSourceKind.BuiltIn, layer.Condition.Source.Kind);
            Assert.Equal(BuiltInProperties.Gear, layer.Condition.Source.Name);
            Assert.Null(layer.Condition.Operator); // edge lives on lifetime
            Assert.Equal(LifetimeKind.OnChange, layer.Lifetime.Kind);
            Assert.Equal(LegacyValueFormatter.GearOverlayMs, layer.Lifetime.DurationMs);
            Assert.Equal(RunsWhen.InGame, layer.Runs); // default eligibility
        }

        [Fact]
        public void Bake_GearUpshiftBrackets_BaseGear_OverlayBrackets_RedlineWhileTrue()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: "GearUpshiftBrackets",
                itmCapable: false);

            var page = Assert.Single(doc.Pages);
            Assert.Equal("Gear", page.Name);
            Assert.Equal(ContentKind.Gear, page.Base.Content.Kind);

            var layer = Assert.Single(page.Layers);
            Assert.Equal(32, layer.Id.Length);
            Assert.Equal("Gear (brackets)", layer.Name);
            Assert.Equal(ContentKind.GearBrackets, layer.Content.Kind);
            Assert.Equal(ConditionOperator.IsTrue, layer.Condition.Operator);
            Assert.Equal(ValueSourceKind.BuiltIn, layer.Condition.Source.Kind);
            Assert.Equal(BuiltInProperties.RedlineReached, layer.Condition.Source.Name);
            Assert.Equal(LifetimeKind.WhileTrue, layer.Lifetime.Kind);
            Assert.Equal(RunsWhen.InGame, layer.Runs);
        }

        [Fact]
        public void Bake_ModeNone_NoHostedPages_StillMarked()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: DisplaySettings.ModeNone,
                itmCapable: true);

            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            Assert.Empty(doc.Pages);
            Assert.Equal(PageRefKind.ItmPage, doc.Priority.Rest.InSessionPage.Kind);
        }

        [Fact]
        public void Bake_FreshDefaults_SynthesizeGear_NotSilence()
        {
            // Fresh/default mode synthesizes a Gear page.
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: DisplaySettings.DefaultMode,
                itmCapable: true);

            var page = Assert.Single(doc.Pages);
            Assert.Equal("Gear", page.Name);
            Assert.Equal(ContentKind.Gear, page.Base.Content.Kind);
            Assert.Equal("lapInfo", doc.Priority.Rest.InSessionPage.CatalogPageId);
        }

        // ── itmDefaultPage → rest.inSessionPage (ITM-capable only) ────────

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
                displayMode: DisplaySettings.ModeNone,
                itmCapable: true,
                log: warnings.Add);

            Assert.Null(doc.Priority.Rest.InSessionPage);
            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            Assert.Single(warnings);
            Assert.Contains("itmDefaultPage 99", warnings[0]);
            Assert.Contains("rest.inSessionPage omitted", warnings[0]);
        }

        // ── Segment-only: no ITM entries ───────────────────────────────────

        [Fact]
        public void Bake_SegmentOnly_NoItmRest_ModeContentStillBakes()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy,
                itmDefaultPage: 3,
                itmDeviceId: 3,
                displayMode: "Speed",
                itmCapable: false);

            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            Assert.Equal(SettingsMode.LegacyOnly, doc.Settings.Mode);

            var page = Assert.Single(doc.Pages);
            Assert.Equal(PageEntryKind.HostedPage, page.Kind);
            Assert.Equal(ContentKind.Speed, page.Base.Content.Kind);
            Assert.Equal(PageRefKind.HostedPage, doc.Priority.Rest.InSessionPage.Kind);
            Assert.Equal(page.Id, doc.Priority.Rest.InSessionPage.Id);

            // Schema law: no ITM-anything on segment-only documents.
            Assert.DoesNotContain(doc.Pages, p => p.Kind == PageEntryKind.ItmPage);
            Assert.Null(doc.Priority.Rest.InSessionPage.CatalogPageId);
        }

        [Fact]
        public void Bake_SegmentOnly_ModeNone_NoPages_NoItmRest()
        {
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlOff,
                itmDefaultPage: 1,
                itmDeviceId: 3,
                displayMode: DisplaySettings.ModeNone,
                itmCapable: false);

            Assert.True(PreEpicSettingsMigrator.HasMarker(doc));
            Assert.Empty(doc.Pages);
            Assert.Null(doc.Priority.Rest.InSessionPage);
        }

        // ── Mapping surface is closed ─────────────────────────────────────

        [Fact]
        public void Bake_DoesNotReadShowTotalsOrOtherUnlistedKeys()
        {
            // Only control + page + mode + display-kind input. Show-totals are ignored.
            var doc = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlLegacy,
                itmDefaultPage: 2,
                displayMode: "Gear",
                itmCapable: true);

            Assert.Equal(SettingsMode.LegacyOnly, doc.Settings.Mode);
            Assert.Equal("fuelErsDrs", doc.Priority.Rest.InSessionPage.CatalogPageId);
            Assert.Single(doc.Pages); // Gear hosted
            Assert.Empty(doc.Cycles);
            Assert.Empty(doc.Fields);
            Assert.Empty(doc.WheelScreen.Rules);
            Assert.Empty(doc.Priority.Rows);
        }

        [Fact]
        public void Bake_RoundTripsMarkerAndModeContentThroughSerializer()
        {
            var baked = PreEpicSettingsMigrator.Bake(
                DisplaySettings.ControlItm,
                DisplaySettings.DefaultItmDefaultPage,
                displayMode: "Speed",
                itmCapable: true);
            string json = DisplayConfigV2Serializer.Save(baked);
            var reloaded = DisplayConfigV2Serializer.Load(json, _ => { });

            Assert.True(PreEpicSettingsMigrator.HasMarker(reloaded));
            Assert.Equal(
                PageRefKind.ItmPage,
                reloaded.Priority.Rest.InSessionPage.Kind);
            Assert.Equal("lapInfo", reloaded.Priority.Rest.InSessionPage.CatalogPageId);
            var page = Assert.Single(reloaded.Pages);
            Assert.Equal(ContentKind.Speed, page.Base.Content.Kind);
            Assert.Equal("Speed", page.Name);
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
