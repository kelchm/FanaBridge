using System.Linq;
using FanaBridge.SegmentDisplay;
using FanaBridge.Shared.Conditions;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class SegmentDisplaySettingsMigrationTests
    {
        [Fact]
        public void MigrateFromLegacy_Gear_CreatesSingleGearScreen()
        {
            var settings = SegmentDisplaySettings.MigrateFromLegacy("Gear");

            Assert.Single(settings.Layers);
            Assert.Equal(LayerRole.Screen, settings.Layers[0].Role);
            Assert.Equal("Gear", settings.Layers[0].CatalogKey);
            Assert.IsType<AlwaysActive>(settings.Layers[0].Condition);

            var content = Assert.IsType<PropertyContent>(settings.Layers[0].Content);
            Assert.Equal(SegmentFormat.Gear, content.Format);
        }

        [Fact]
        public void MigrateFromLegacy_Speed_CreatesSingleSpeedScreen()
        {
            var settings = SegmentDisplaySettings.MigrateFromLegacy("Speed");

            Assert.Single(settings.Layers);
            Assert.Equal("Speed", settings.Layers[0].CatalogKey);

            var content = Assert.IsType<PropertyContent>(settings.Layers[0].Content);
            Assert.Equal(SegmentFormat.Number, content.Format);
        }

        [Fact]
        public void MigrateFromLegacy_GearAndSpeed_CreatesOverlayAndScreen()
        {
            var settings = SegmentDisplaySettings.MigrateFromLegacy("GearAndSpeed");

            Assert.Equal(2, settings.Layers.Count);
            Assert.Equal(LayerRole.Overlay, settings.Layers[0].Role);
            Assert.Equal(LayerRole.Screen, settings.Layers[1].Role);
        }

        [Fact]
        public void MigrateFromLegacy_GearAndSpeed_OverlayHas2000msHold()
        {
            var settings = SegmentDisplaySettings.MigrateFromLegacy("GearAndSpeed");

            var condition = Assert.IsType<OnValueChange>(settings.Layers[0].Condition);
            Assert.Equal(2000, condition.HoldMs);
            Assert.Equal("DataCorePlugin.GameData.Gear", condition.Property);
        }

        [Fact]
        public void MigrateFromLegacy_UnknownMode_DefaultsToGear()
        {
            var settings = SegmentDisplaySettings.MigrateFromLegacy("SomeUnknownMode");

            Assert.Single(settings.Layers);
            Assert.Equal("Gear", settings.Layers[0].CatalogKey);
        }

        [Fact]
        public void MigrateFromLegacy_Null_DefaultsToGear()
        {
            var settings = SegmentDisplaySettings.MigrateFromLegacy(null);

            Assert.Single(settings.Layers);
            Assert.Equal("Gear", settings.Layers[0].CatalogKey);
        }

        [Fact]
        public void CreateDefault_HasExpectedLayers()
        {
            var settings = SegmentDisplaySettings.CreateDefault();

            Assert.Equal(2, settings.Layers.Count);
            Assert.Equal(LayerRole.Overlay, settings.Layers[0].Role);
            Assert.Equal(LayerRole.Screen, settings.Layers[1].Role);
        }

        [Fact]
        public void CreateDefault_SettingsRoundTripThroughJson()
        {
            var original = SegmentDisplaySettings.CreateDefault();

            string json = JsonConvert.SerializeObject(original, Formatting.Indented);
            var result = JsonConvert.DeserializeObject<SegmentDisplaySettings>(json);

            Assert.Equal(original.Layers.Count, result.Layers.Count);

            for (int i = 0; i < original.Layers.Count; i++)
            {
                Assert.Equal(original.Layers[i].Name, result.Layers[i].Name);
                Assert.Equal(original.Layers[i].Role, result.Layers[i].Role);
                Assert.Equal(original.Layers[i].CatalogKey, result.Layers[i].CatalogKey);
                Assert.Equal(original.Layers[i].Condition.Type, result.Layers[i].Condition.Type);
                Assert.Equal(original.Layers[i].Content.Type, result.Layers[i].Content.Type);
            }
        }
    }
}
