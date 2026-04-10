using System.Linq;
using FanaBridge.SegmentDisplay;
using FanaBridge.Shared.Conditions;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class SegmentLayerCatalogTests
    {
        [Fact]
        public void All_ContainsScreensAndOverlays()
        {
            Assert.Contains(SegmentLayerCatalog.All, l => l.Role == LayerRole.Screen);
            Assert.Contains(SegmentLayerCatalog.All, l => l.Role == LayerRole.Overlay);
        }

        [Fact]
        public void All_EveryCatalogEntryHasKey()
        {
            foreach (var layer in SegmentLayerCatalog.All)
                Assert.False(string.IsNullOrEmpty(layer.CatalogKey),
                    "Catalog entry missing CatalogKey: " + layer.Name);
        }

        [Fact]
        public void All_NoDuplicateKeys()
        {
            var keys = SegmentLayerCatalog.All.Select(l => l.CatalogKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Theory]
        [InlineData("Gear", LayerRole.Screen)]
        [InlineData("Speed", LayerRole.Screen)]
        [InlineData("GearChange", LayerRole.Overlay)]
        [InlineData("PitLimiter", LayerRole.Overlay)]
        [InlineData("YellowFlag", LayerRole.Overlay)]
        [InlineData("ShiftWarning", LayerRole.Overlay)]
        [InlineData("LowFuelAlt", LayerRole.Overlay)]
        public void All_KnownEntryHasExpectedRole(string key, LayerRole expectedRole)
        {
            var entry = SegmentLayerCatalog.All.FirstOrDefault(l => l.CatalogKey == key);
            Assert.NotNull(entry);
            Assert.Equal(expectedRole, entry.Role);
        }

        [Fact]
        public void CreateFromCatalog_ReturnsDeepCopy()
        {
            var copy = SegmentLayerCatalog.CreateFromCatalog("Gear");
            Assert.NotNull(copy);
            Assert.Equal("Gear", copy.CatalogKey);

            // Verify it's a distinct instance
            var original = SegmentLayerCatalog.All.First(l => l.CatalogKey == "Gear");
            Assert.NotSame(original, copy);
            Assert.NotSame(original.Condition, copy.Condition);
            Assert.NotSame(original.Content, copy.Content);
        }

        [Fact]
        public void CreateFromCatalog_UnknownKey_ReturnsNull()
        {
            Assert.Null(SegmentLayerCatalog.CreateFromCatalog("DoesNotExist"));
        }

        [Fact]
        public void CreateFromCatalog_NullKey_ReturnsNull()
        {
            Assert.Null(SegmentLayerCatalog.CreateFromCatalog(null));
        }

        [Fact]
        public void DefaultLayers_ReturnsOverlaysThenScreen()
        {
            var layers = SegmentLayerCatalog.DefaultLayers();
            Assert.True(layers.Count >= 2);

            // Last entry should be a screen
            Assert.Equal(LayerRole.Screen, layers.Last().Role);

            // Everything before should be overlays
            for (int i = 0; i < layers.Count - 1; i++)
                Assert.Equal(LayerRole.Overlay, layers[i].Role);
        }

        [Fact]
        public void DefaultLayers_AreDistinctInstances()
        {
            var layers = SegmentLayerCatalog.DefaultLayers();
            var layers2 = SegmentLayerCatalog.DefaultLayers();

            for (int i = 0; i < layers.Count; i++)
                Assert.NotSame(layers[i], layers2[i]);
        }

        [Fact]
        public void PitLimiter_HasBlinkEffect()
        {
            var pit = SegmentLayerCatalog.CreateFromCatalog("PitLimiter");
            Assert.NotNull(pit.Effects);
            Assert.Single(pit.Effects);
            Assert.IsType<BlinkEffect>(pit.Effects[0]);
        }

        [Fact]
        public void ShiftWarning_HasContinuousFlash()
        {
            var shift = SegmentLayerCatalog.CreateFromCatalog("ShiftWarning");
            Assert.NotNull(shift.Effects);
            var flash = Assert.IsType<FlashEffect>(shift.Effects[0]);
            Assert.Equal(0, flash.Count); // continuous
        }

        [Fact]
        public void LowFuelAlt_HasSequenceContent()
        {
            var fuel = SegmentLayerCatalog.CreateFromCatalog("LowFuelAlt");
            var seq = Assert.IsType<SequenceContent>(fuel.Content);
            Assert.Equal(2, seq.Items.Length);
        }
    }
}
