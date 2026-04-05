using System.Linq;
using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class DisplaySettingsTests
    {
        [Fact]
        public void MigrateFromLegacy_Gear_HasGearConstantLayer()
        {
            var settings = DisplaySettings.MigrateFromLegacy("Gear");
            var gearLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Gear" && l.Mode == DisplayLayerMode.Constant && l.IsEnabled);
            Assert.NotNull(gearLayer);
        }

        [Fact]
        public void MigrateFromLegacy_Speed_HasSpeedConstantLayer()
        {
            var settings = DisplaySettings.MigrateFromLegacy("Speed");
            var speedLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Speed" && l.Mode == DisplayLayerMode.Constant && l.IsEnabled);
            Assert.NotNull(speedLayer);
        }

        [Fact]
        public void MigrateFromLegacy_GearAndSpeed_HasSpeedAndGearChangeOverlay()
        {
            var settings = DisplaySettings.MigrateFromLegacy("GearAndSpeed");
            var speedLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Speed" && l.IsEnabled);
            var gearOverlay = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "GearChange" && l.Mode == DisplayLayerMode.OnChange && l.IsEnabled);
            Assert.NotNull(speedLayer);
            Assert.NotNull(gearOverlay);
        }

        [Fact]
        public void MigrateFromLegacy_UnknownDefaultsToGear()
        {
            var settings = DisplaySettings.MigrateFromLegacy("SomethingWeird");
            var gearLayer = settings.Layers.FirstOrDefault(l =>
                l.CatalogKey == "Gear" && l.IsEnabled);
            Assert.NotNull(gearLayer);
        }

        [Fact]
        public void CreateDefault_HasLayers()
        {
            var settings = DisplaySettings.CreateDefault();
            Assert.True(settings.Layers.Count > 0);
            Assert.Contains(settings.Layers, l => l.CatalogKey == "Gear");
        }
    }
}
