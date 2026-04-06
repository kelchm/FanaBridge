using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class DisplayLayerTests
    {
        [Fact]
        public void IsGearFormat_MatchesEnum()
        {
            var layer = new DisplayLayer { DisplayFormat = DisplayFormat.Gear };
            Assert.True(layer.IsGearFormat);
            layer.DisplayFormat = DisplayFormat.Number;
            Assert.False(layer.IsGearFormat);
        }

        [Fact]
        public void ModeLabel_ReturnsCorrectStrings()
        {
            var layer = new DisplayLayer { Mode = DisplayLayerMode.Constant };
            Assert.Equal("ALWAYS", layer.ModeLabel);
            layer.Mode = DisplayLayerMode.OnChange;
            Assert.Equal("ON CHANGE", layer.ModeLabel);
            layer.Mode = DisplayLayerMode.WhileTrue;
            Assert.Equal("WHILE TRUE", layer.ModeLabel);
        }

        [Fact]
        public void TimingLabel_OnlyForOnChange()
        {
            var layer = new DisplayLayer { Mode = DisplayLayerMode.OnChange, DurationMs = 2000 };
            Assert.Equal("2s", layer.TimingLabel);
            layer.Mode = DisplayLayerMode.Constant;
            Assert.Equal("", layer.TimingLabel);
        }

        [Fact]
        public void PropertyChanged_Fires()
        {
            var layer = new DisplayLayer();
            string changedProp = null;
            layer.PropertyChanged += (s, e) => changedProp = e.PropertyName;
            layer.Name = "Test";
            Assert.Equal("Name", changedProp);
        }
    }
}
