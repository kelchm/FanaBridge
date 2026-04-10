using FanaBridge.SegmentDisplay;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class SegmentEffectSerializationTests
    {
        [Fact]
        public void BlinkEffect_RoundTrips()
        {
            var original = new BlinkEffect { OnMs = 400, OffMs = 200 };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<SegmentEffect>(json);

            var typed = Assert.IsType<BlinkEffect>(result);
            Assert.Equal(400, typed.OnMs);
            Assert.Equal(200, typed.OffMs);
        }

        [Fact]
        public void BlinkEffect_DefaultValues()
        {
            var effect = new BlinkEffect();
            Assert.Equal(500, effect.OnMs);
            Assert.Equal(500, effect.OffMs);
            Assert.Equal("Blink", effect.Label);
        }

        [Fact]
        public void FlashEffect_RoundTrips()
        {
            var original = new FlashEffect { Count = 5, RateMs = 100 };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<SegmentEffect>(json);

            var typed = Assert.IsType<FlashEffect>(result);
            Assert.Equal(5, typed.Count);
            Assert.Equal(100, typed.RateMs);
        }

        [Fact]
        public void FlashEffect_Label_ShowsCount()
        {
            var effect = new FlashEffect { Count = 3 };
            Assert.Equal("Flash 3x", effect.Label);
        }

        [Fact]
        public void FlashEffect_ContinuousLabel()
        {
            var effect = new FlashEffect { Count = 0 };
            Assert.Equal("Flash", effect.Label);
        }

        [Fact]
        public void UnknownEffectType_Throws()
        {
            string json = "{\"Type\":\"Sparkle\"}";
            Assert.Throws<JsonSerializationException>(
                () => JsonConvert.DeserializeObject<SegmentEffect>(json));
        }
    }
}
