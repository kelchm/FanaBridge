using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The Pages &amp; fields editor's edit model (<see cref="DisplayPagesEditModel"/>):
    /// page pills from the device table, selection state, mapping read/write commit
    /// shape, provenance, format choices, and reset-to-default. Plain functions — no
    /// SimHub, no UI thread.
    /// </summary>
    public class DisplayPagesEditModelTests
    {
        private const byte Device3 = 3;   // standard six-page set
        private const byte Device4 = 4;   // Bentley — no Car Settings, renumbered 1–5

        private static DisplayCustomizationConfig Load(string json)
            => DisplayConfigSerializer.Load(json, _ => { });

        private static DisplayCustomizationConfig WithFuelOverride()
            => Load("{ \"schemaVersion\": 1, \"fieldMappings\": { "
                + "\"5\": { \"source\": { \"kind\": \"simHubProperty\", "
                + "\"name\": \"DataCorePlugin.GameData.Fuel\" }, \"format\": \"bare\" } } }");

        // ── Page pills ───────────────────────────────────────────────────

        [Fact]
        public void PagePills_StandardDevice_SixPillsInWireOrder()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            var pills = model.PagePills();

            Assert.Equal(6, pills.Count);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, pills.Select(p => p.Wire).ToArray());
            Assert.Equal(ItmPage.LapInfo, pills[0].Page);
            Assert.Equal(ItmPage.Legacy, pills[5].Page);
            Assert.True(pills[5].IsLegacy);
            Assert.Equal("Lap Info", pills[0].Name);
            // First editable page is selected on open.
            Assert.True(pills[0].IsSelected);
            Assert.Equal(1, pills.Count(p => p.IsSelected));
        }

        [Fact]
        public void PagePills_Bentley_FivePills_NoCarSettings_Renumbered()
        {
            var model = new DisplayPagesEditModel(null, Device4);
            var pills = model.PagePills();

            Assert.Equal(5, pills.Count);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, pills.Select(p => p.Wire).ToArray());
            Assert.DoesNotContain(pills, p => p.Page == ItmPage.CarSettings);
            Assert.Equal(ItmPage.LapTimes, pills[2].Page);   // wire 3 on Bentley
            Assert.Equal(ItmPage.Legacy, pills[4].Page);
            Assert.Equal(5, pills[4].Wire);
        }

        [Fact]
        public void SelectPage_UpdatesPillSelection_AndResetsParam()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            Assert.Equal(ItmPage.LapInfo, model.SelectedPage);
            Assert.Equal(ItmParam.Lap, model.SelectedParamId);   // first remappable on LapInfo

            model.SelectPage(ItmPage.FuelErsDrs);
            Assert.Equal(ItmPage.FuelErsDrs, model.SelectedPage);
            Assert.Equal(ItmParam.Fuel, model.SelectedParamId);
            Assert.True(model.PagePills().Single(p => p.Page == ItmPage.FuelErsDrs).IsSelected);
        }

        [Fact]
        public void SelectPage_Legacy_ClearsParam_AndIsLegacyPage()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SelectPage(ItmPage.Legacy);
            Assert.True(model.IsLegacyPage);
            Assert.Null(model.SelectedParamId);
            Assert.Null(model.Inspector());
        }

        // ── Selection ────────────────────────────────────────────────────

        [Fact]
        public void SelectParam_OnPage_UpdatesInspector()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SelectParam(ItmParam.Position);
            Assert.Equal(ItmParam.Position, model.SelectedParamId);
            var insp = model.Inspector();
            Assert.NotNull(insp);
            Assert.Equal(ItmParam.Position, insp.ParamId);
            Assert.Equal(FieldProvenance.Default, insp.Provenance);
            Assert.False(insp.IsLocked);
            Assert.Equal(BuiltInProperties.Position, insp.SourceName);
        }

        [Fact]
        public void SelectParam_EngineMapping_IsLocked_NoPropertyRemap()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SelectPage(ItmPage.CarSettings);
            model.SelectParam(ItmParam.EngineMapping);
            var insp = model.Inspector();
            Assert.True(insp.IsLocked);
            Assert.False(insp.ShowResetToDefault);
            Assert.Equal(FieldProvenance.Default, insp.Provenance);
        }

        // ── Mapping read/write round-trip ────────────────────────────────

        [Fact]
        public void SetSource_CreatesFreshDocument_WithFieldMapping()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            var cfg = model.SetSource(ItmParam.Fuel, PropertyKind.SimHubProperty,
                "DataCorePlugin.GameData.Fuel");

            Assert.NotNull(cfg);
            Assert.Same(cfg, model.Config);
            Assert.True(cfg.FieldMappings.ContainsKey(ItmParam.Fuel));
            var m = cfg.FieldMappings[ItmParam.Fuel];
            Assert.Equal(PropertyKind.SimHubProperty, m.Source.Kind);
            Assert.Equal("DataCorePlugin.GameData.Fuel", m.Source.Name);
            Assert.Equal(FieldProvenance.ThisWheel, model.ProvenanceOf(ItmParam.Fuel));
        }

        [Fact]
        public void SetSource_KeepsExistingFormat_CopiesFieldMappingsDict()
        {
            var start = WithFuelOverride();
            var model = new DisplayPagesEditModel(start, Device3);
            var originalDict = start.FieldMappings;

            var cfg = model.SetSource(ItmParam.Fuel, PropertyKind.BuiltIn,
                BuiltInProperties.FuelPercent);

            Assert.NotSame(start, cfg);
            Assert.NotSame(originalDict, cfg.FieldMappings);
            Assert.Equal(FieldFormats.Bare, cfg.FieldMappings[ItmParam.Fuel].Format);
            Assert.Equal(BuiltInProperties.FuelPercent,
                cfg.FieldMappings[ItmParam.Fuel].Source.Name);
            // Original document untouched.
            Assert.Equal("DataCorePlugin.GameData.Fuel",
                start.FieldMappings[ItmParam.Fuel].Source.Name);
        }

        [Fact]
        public void SetSource_Gear_Rejected_ConfigUnchanged()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            var cfg = model.SetSource(ItmParam.Gear, PropertyKind.BuiltIn,
                BuiltInProperties.Gear);
            Assert.Null(cfg);
            Assert.Null(model.Config);
        }

        [Fact]
        public void SetFormat_CreatesMappingWithDefaultSource()
        {
            // Fuel has no Show*Total mirror — bare is a real non-default format and
            // must persist as a mapping with the built-in default source.
            var model = new DisplayPagesEditModel(null, Device3);
            var cfg = model.SetFormat(ItmParam.Fuel, FieldFormats.Bare);

            Assert.NotNull(cfg);
            var m = cfg.FieldMappings[ItmParam.Fuel];
            Assert.Equal(FieldFormats.Bare, m.Format);
            Assert.Equal(PropertyKind.BuiltIn, m.Source.Kind);
            Assert.Equal(BuiltInProperties.Fuel, m.Source.Name);
            Assert.Equal(FieldProvenance.ThisWheel, model.ProvenanceOf(ItmParam.Fuel));
        }

        [Fact]
        public void SetFormat_FamilyDefault_WithNoSourceOverride_DropsMapping()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SetFormat(ItmParam.Fuel, FieldFormats.Bare);
            Assert.True(model.HasMapping(ItmParam.Fuel));

            var cfg = model.SetFormat(ItmParam.Fuel, FieldFormats.WithTotal);
            Assert.False(cfg.FieldMappings.ContainsKey(ItmParam.Fuel));
            Assert.Equal(FieldProvenance.Default, model.ProvenanceOf(ItmParam.Fuel));
        }

        [Fact]
        public void SetSource_DefaultSource_NoNonDefaultFormat_PrunesMapping()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SetSource(ItmParam.Fuel, PropertyKind.SimHubProperty, "Custom.Fuel");
            Assert.True(model.HasMapping(ItmParam.Fuel));

            // Re-pick the exact built-in default with no format override → drop.
            var cfg = model.SetSource(ItmParam.Fuel, PropertyKind.BuiltIn,
                BuiltInProperties.Fuel);
            Assert.False(cfg.FieldMappings.ContainsKey(ItmParam.Fuel));
            Assert.Equal(FieldProvenance.Default, model.ProvenanceOf(ItmParam.Fuel));
        }

        [Fact]
        public void SetSource_DefaultSource_KeepsNonDefaultFormat()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SetFormat(ItmParam.Fuel, FieldFormats.Bare);
            Assert.True(model.HasMapping(ItmParam.Fuel));

            // Source already default; re-setting it must keep the bare format mapping.
            var cfg = model.SetSource(ItmParam.Fuel, PropertyKind.BuiltIn,
                BuiltInProperties.Fuel);
            Assert.True(cfg.FieldMappings.ContainsKey(ItmParam.Fuel));
            Assert.Equal(FieldFormats.Bare, cfg.FieldMappings[ItmParam.Fuel].Format);
        }

        [Fact]
        public void SetFormat_Disallowed_NoOp()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            var cfg = model.SetFormat(ItmParam.Speed, FieldFormats.Bare);
            Assert.Null(cfg);
            Assert.Null(model.Config);
        }

        [Fact]
        public void ResetToDefault_RemovesMapping()
        {
            var model = new DisplayPagesEditModel(WithFuelOverride(), Device3);
            Assert.Equal(FieldProvenance.ThisWheel, model.ProvenanceOf(ItmParam.Fuel));

            var cfg = model.ResetToDefault(ItmParam.Fuel);
            Assert.False(cfg.FieldMappings.ContainsKey(ItmParam.Fuel));
            Assert.Equal(FieldProvenance.Default, model.ProvenanceOf(ItmParam.Fuel));
            var insp = model.Inspector();
            // After reset, inspector for selected field still works (default first param).
            Assert.NotNull(model.Inspector());
        }

        [Fact]
        public void ResetToDefault_NoMapping_NoOp()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            var cfg = model.ResetToDefault(ItmParam.Fuel);
            Assert.Null(cfg);
        }

        // ── Provenance / format choices ──────────────────────────────────

        [Fact]
        public void Provenance_DefaultVsThisWheel_NoGlobal()
        {
            var model = new DisplayPagesEditModel(WithFuelOverride(), Device3);
            Assert.Equal(FieldProvenance.ThisWheel, model.ProvenanceOf(ItmParam.Fuel));
            Assert.Equal(FieldProvenance.Default, model.ProvenanceOf(ItmParam.Lap));
        }

        [Fact]
        public void FormatChoices_TotalAndTempFamilies()
        {
            var total = DisplayPagesEditModel.FormatChoicesFor(ItmParam.Fuel);
            Assert.Equal(2, total.Count);
            Assert.Equal(FieldFormats.WithTotal, total[0].Id);
            Assert.Equal(FieldFormats.Bare, total[1].Id);

            var temp = DisplayPagesEditModel.FormatChoicesFor(ItmParam.OilTemp);
            Assert.Equal(2, temp.Count);
            Assert.Equal(FieldFormats.Unit, temp[0].Id);
            Assert.Equal(FieldFormats.Bare, temp[1].Id);

            Assert.Empty(DisplayPagesEditModel.FormatChoicesFor(ItmParam.Speed));
            Assert.Empty(DisplayPagesEditModel.FormatChoicesFor(ItmParam.Gear));
        }

        [Fact]
        public void EffectiveFormat_SourceOverrideDefaultsBare()
        {
            var model = new DisplayPagesEditModel(null, Device3);
            model.SetSource(ItmParam.Fuel, PropertyKind.SimHubProperty, "Custom.Fuel");
            // No explicit format → bare (mapper rule mirrored in the UI).
            Assert.Equal(FieldFormats.Bare, model.EffectiveFormatId(ItmParam.Fuel));
        }

        [Fact]
        public void EffectiveFormat_ShowLapTotalFalse_NoMapping_ShowsBare()
        {
            var model = new DisplayPagesEditModel(null, Device3,
                showLapTotal: false, showPositionTotal: true);
            Assert.Equal(FieldFormats.Bare, model.EffectiveFormatId(ItmParam.Lap));
            Assert.Equal(FieldFormats.WithTotal, model.EffectiveFormatId(ItmParam.Position));
            Assert.Equal(FieldFormats.WithTotal, model.EffectiveFormatId(ItmParam.Fuel));
        }

        [Fact]
        public void SetFormat_LapWithTotal_WhenShowLapTotalFalse_Prunes_PostMirrorResolvesWithTotal()
        {
            // Choosing "With total" while the migrated toggle is false must not leave
            // an unrecoverable bare state: SetFormat anticipates the view's Show*Total
            // mirror and prunes; after the mirror (toggle=true) the default is withTotal.
            var model = new DisplayPagesEditModel(null, Device3,
                showLapTotal: false, showPositionTotal: true);
            Assert.Equal(FieldFormats.Bare, model.EffectiveFormatId(ItmParam.Lap));

            var cfg = model.SetFormat(ItmParam.Lap, FieldFormats.WithTotal);
            Assert.False(cfg?.FieldMappings?.ContainsKey(ItmParam.Lap) ?? true);
            Assert.Equal(FieldProvenance.Default, model.ProvenanceOf(ItmParam.Lap));

            // Simulate the view's boolean mirror + rebuild.
            var after = new DisplayPagesEditModel(cfg, Device3,
                showLapTotal: true, showPositionTotal: true);
            Assert.Equal(FieldFormats.WithTotal, after.EffectiveFormatId(ItmParam.Lap));
        }

        [Fact]
        public void SetFormat_LapBare_Prunes_PostMirrorResolvesBare()
        {
            var model = new DisplayPagesEditModel(null, Device3,
                showLapTotal: true, showPositionTotal: true);
            var cfg = model.SetFormat(ItmParam.Lap, FieldFormats.Bare);
            // Lap/Position format lives in the toggle after mirror — no mapping.
            Assert.False(cfg?.FieldMappings?.ContainsKey(ItmParam.Lap) ?? true);

            var after = new DisplayPagesEditModel(cfg, Device3,
                showLapTotal: false, showPositionTotal: true);
            Assert.Equal(FieldFormats.Bare, after.EffectiveFormatId(ItmParam.Lap));
        }

        [Fact]
        public void FormatMirrorsShowTotal_OnlyLapAndPosition()
        {
            Assert.True(DisplayPagesEditModel.FormatMirrorsShowTotal(ItmParam.Lap));
            Assert.True(DisplayPagesEditModel.FormatMirrorsShowTotal(ItmParam.Position));
            Assert.False(DisplayPagesEditModel.FormatMirrorsShowTotal(ItmParam.Fuel));
            Assert.False(DisplayPagesEditModel.FormatMirrorsShowTotal(ItmParam.OilTemp));
            Assert.True(DisplayPagesEditModel.ShowTotalFromFormat(FieldFormats.WithTotal));
            Assert.False(DisplayPagesEditModel.ShowTotalFromFormat(FieldFormats.Bare));
        }

        [Fact]
        public void Inspector_ShowReset_OnlyWhenThisWheel()
        {
            var model = new DisplayPagesEditModel(WithFuelOverride(), Device3);
            model.SelectPage(ItmPage.FuelErsDrs);
            model.SelectParam(ItmParam.Fuel);
            Assert.True(model.Inspector().ShowResetToDefault);

            model.SelectParam(ItmParam.ErsLevel);
            Assert.False(model.Inspector().ShowResetToDefault);
        }

        [Fact]
        public void Commit_CarriesItmRulesByReference()
        {
            var start = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } } ] } }");
            var model = new DisplayPagesEditModel(start, Device3);
            var cfg = model.SetSource(ItmParam.Fuel, PropertyKind.BuiltIn,
                BuiltInProperties.FuelPercent);

            Assert.NotSame(start, cfg);
            Assert.Same(start.Itm, cfg.Itm);           // carried by reference
            Assert.Same(start.Legacy, cfg.Legacy);
            Assert.NotSame(start.FieldMappings, cfg.FieldMappings);
        }

        [Fact]
        public void DefaultSource_PinsBuiltInNames()
        {
            Assert.Equal(BuiltInProperties.CurrentLap,
                DisplayPagesEditModel.DefaultSource(ItmParam.Lap).Name);
            Assert.Equal(BuiltInProperties.OilTemperature,
                DisplayPagesEditModel.DefaultSource(ItmParam.OilTemp).Name);
            Assert.Equal(PropertyKind.BuiltIn,
                DisplayPagesEditModel.DefaultSource(ItmParam.Fuel).Kind);
        }
    }
}
