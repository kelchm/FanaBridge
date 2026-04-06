using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class LayerCatalogTests
    {
        [Fact]
        public void HasBothConstantAndConditionalEntries()
        {
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.Constant);
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.OnChange);
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.WhileTrue);
        }

        [Fact]
        public void CreateFromCatalog_ReturnsCopy()
        {
            var a = LayerCatalog.CreateFromCatalog("Gear");
            var b = LayerCatalog.CreateFromCatalog("Gear");
            Assert.NotSame(a, b);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.CatalogKey, b.CatalogKey);
            Assert.True(a.IsEnabled);
            Assert.True(b.IsEnabled);
        }

        [Fact]
        public void FindByKey_ReturnsNull_ForUnknown()
        {
            Assert.Null(LayerCatalog.FindByKey("DoesNotExist"));
        }
    }
}
