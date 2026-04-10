using FanaBridge.SegmentDisplay;
using FanaBridge.Shared.Conditions;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class SegmentDisplayLayerSerializationTests
    {
        [Fact]
        public void FullLayer_RoundTrips()
        {
            var original = new SegmentDisplayLayer
            {
                Name = "Low Fuel",
                CatalogKey = "LowFuel",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.FuelAlertActive",
                },
                Content = new FixedTextContent { Text = "LOW" },
                Alignment = AlignmentType.Center,
                Overflow = OverflowType.Auto,
                ScrollSpeedMs = 250,
                Effects = new SegmentEffect[]
                {
                    new BlinkEffect { OnMs = 500, OffMs = 500 },
                },
                ShowWhenRunning = true,
                ShowWhenIdle = false,
            };

            string json = JsonConvert.SerializeObject(original, Formatting.Indented);
            var result = JsonConvert.DeserializeObject<SegmentDisplayLayer>(json);

            Assert.Equal("Low Fuel", result.Name);
            Assert.Equal("LowFuel", result.CatalogKey);
            Assert.Equal(LayerRole.Overlay, result.Role);

            var condition = Assert.IsType<WhilePropertyTrue>(result.Condition);
            Assert.Equal("DataCorePlugin.GameData.FuelAlertActive", condition.Property);

            var content = Assert.IsType<FixedTextContent>(result.Content);
            Assert.Equal("LOW", content.Text);

            Assert.Equal(AlignmentType.Center, result.Alignment);
            Assert.Single(result.Effects);
            Assert.IsType<BlinkEffect>(result.Effects[0]);
        }

        [Fact]
        public void MinimalLayer_RoundTrips()
        {
            var original = new SegmentDisplayLayer
            {
                Name = "Gear",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<SegmentDisplayLayer>(json);

            Assert.Equal("Gear", result.Name);
            Assert.Null(result.CatalogKey);
            Assert.IsType<AlwaysActive>(result.Condition);
            Assert.IsType<PropertyContent>(result.Content);
        }

        [Fact]
        public void IsEnabled_TrueWhenRunning()
        {
            var layer = new SegmentDisplayLayer { ShowWhenRunning = true, ShowWhenIdle = false };
            Assert.True(layer.IsEnabled);
        }

        [Fact]
        public void IsEnabled_TrueWhenIdle()
        {
            var layer = new SegmentDisplayLayer { ShowWhenRunning = false, ShowWhenIdle = true };
            Assert.True(layer.IsEnabled);
        }

        [Fact]
        public void IsEnabled_FalseWhenNeither()
        {
            var layer = new SegmentDisplayLayer { ShowWhenRunning = false, ShowWhenIdle = false };
            Assert.False(layer.IsEnabled);
        }

        [Fact]
        public void IsCustom_TrueWhenNoCatalogKey()
        {
            var layer = new SegmentDisplayLayer { CatalogKey = null };
            Assert.True(layer.IsCustom);
        }

        [Fact]
        public void IsCustom_FalseWhenHasCatalogKey()
        {
            var layer = new SegmentDisplayLayer { CatalogKey = "Gear" };
            Assert.False(layer.IsCustom);
        }
    }
}
