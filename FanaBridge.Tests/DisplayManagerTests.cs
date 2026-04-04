using System.Linq;
using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class DisplayManagerTests
    {
        // ── TruncateTo3 ─────────────────────────────────────────────────

        [Theory]
        [InlineData("123", "123")]
        [InlineData("12345", "123")]
        [InlineData("", "")]
        [InlineData("AB", "AB")]
        public void TruncateTo3_TruncatesAtThreeDisplayChars(string input, string expected)
        {
            Assert.Equal(expected, FanatecDisplayManager.TruncateTo3(input));
        }

        [Fact]
        public void TruncateTo3_DotsDoNotCountAsDisplayChars()
        {
            Assert.Equal("1.23", FanatecDisplayManager.TruncateTo3("1.234"));
        }

        [Fact]
        public void TruncateTo3_NullReturnsEmpty()
        {
            Assert.Equal("", FanatecDisplayManager.TruncateTo3(null));
        }

        // ── MigrateFromLegacy ───────────────────────────────────────────

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

        // ── CreateDefault ───────────────────────────────────────────────

        [Fact]
        public void CreateDefault_HasLayers()
        {
            var settings = DisplaySettings.CreateDefault();
            Assert.True(settings.Layers.Count > 0);
            Assert.Contains(settings.Layers, l => l.CatalogKey == "Gear");
        }

        // ── LayerCatalog ────────────────────────────────────────────────

        [Fact]
        public void LayerCatalog_HasBothConstantAndConditionalEntries()
        {
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.Constant);
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.OnChange);
            Assert.Contains(LayerCatalog.All, l => l.Mode == DisplayLayerMode.WhileTrue);
        }

        [Fact]
        public void LayerCatalog_CreateFromCatalog_ReturnsCopy()
        {
            var a = LayerCatalog.CreateFromCatalog("Gear");
            var b = LayerCatalog.CreateFromCatalog("Gear");
            Assert.NotSame(a, b);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.CatalogKey, b.CatalogKey);
        }

        [Fact]
        public void LayerCatalog_FindByKey_ReturnsNull_ForUnknown()
        {
            Assert.Null(LayerCatalog.FindByKey("DoesNotExist"));
        }

        // ── DisplayLayer ────────────────────────────────────────────────

        [Fact]
        public void DisplayLayer_IsGearFormat_MatchesEnum()
        {
            var layer = new DisplayLayer { DisplayFormat = DisplayFormat.Gear };
            Assert.True(layer.IsGearFormat);
            layer.DisplayFormat = DisplayFormat.Number;
            Assert.False(layer.IsGearFormat);
        }

        [Fact]
        public void DisplayLayer_ModeLabel_ReturnsCorrectStrings()
        {
            var layer = new DisplayLayer { Mode = DisplayLayerMode.Constant };
            Assert.Equal("ALWAYS", layer.ModeLabel);
            layer.Mode = DisplayLayerMode.OnChange;
            Assert.Equal("ON CHANGE", layer.ModeLabel);
            layer.Mode = DisplayLayerMode.WhileTrue;
            Assert.Equal("WHILE TRUE", layer.ModeLabel);
        }

        [Fact]
        public void DisplayLayer_TimingLabel_OnlyForOnChange()
        {
            var layer = new DisplayLayer { Mode = DisplayLayerMode.OnChange, DurationMs = 2000 };
            Assert.Equal("2s", layer.TimingLabel);
            layer.Mode = DisplayLayerMode.Constant;
            Assert.Equal("", layer.TimingLabel);
        }

        [Fact]
        public void DisplayLayer_PropertyChanged_Fires()
        {
            var layer = new DisplayLayer();
            string changedProp = null;
            layer.PropertyChanged += (s, e) => changedProp = e.PropertyName;
            layer.Name = "Test";
            Assert.Equal("Name", changedProp);
        }

        // ── EncodeText ──────────────────────────────────────────────────

        [Fact]
        public void EncodeText_DotFoldsIntoPredecessor()
        {
            var encoded = FanatecDisplayManager.EncodeText("1.2");
            Assert.Equal(2, encoded.Count);
            Assert.True((encoded[0] & 0x80) != 0); // dot bit set on first char
        }

        [Fact]
        public void EncodeText_EmptyReturnsEmpty()
        {
            Assert.Empty(FanatecDisplayManager.EncodeText(""));
            Assert.Empty(FanatecDisplayManager.EncodeText(null));
        }
    }
}
