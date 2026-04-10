using FanaBridge.Shared.Conditions;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.Conditions
{
    public class ActivationConditionSerializationTests
    {
        [Fact]
        public void AlwaysActive_RoundTrips()
        {
            var original = new AlwaysActive();
            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ActivationCondition>(json);

            Assert.IsType<AlwaysActive>(result);
            Assert.Equal("AlwaysActive", result.Type);
        }

        [Fact]
        public void WhilePropertyTrue_RoundTrips()
        {
            var original = new WhilePropertyTrue
            {
                Property = "DataCorePlugin.GameData.PitLimiterOn",
                Invert = false,
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ActivationCondition>(json);

            var typed = Assert.IsType<WhilePropertyTrue>(result);
            Assert.Equal("DataCorePlugin.GameData.PitLimiterOn", typed.Property);
            Assert.False(typed.Invert);
        }

        [Fact]
        public void WhilePropertyTrue_WithInvert_RoundTrips()
        {
            var original = new WhilePropertyTrue
            {
                Property = "FanaBridge.Connected",
                Invert = true,
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ActivationCondition>(json);

            var typed = Assert.IsType<WhilePropertyTrue>(result);
            Assert.True(typed.Invert);
        }

        [Fact]
        public void OnValueChange_RoundTrips()
        {
            var original = new OnValueChange
            {
                Property = "DataCorePlugin.GameData.Gear",
                HoldMs = 2000,
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ActivationCondition>(json);

            var typed = Assert.IsType<OnValueChange>(result);
            Assert.Equal("DataCorePlugin.GameData.Gear", typed.Property);
            Assert.Equal(2000, typed.HoldMs);
        }

        [Fact]
        public void WhileExpressionTrue_RoundTrips()
        {
            var original = new WhileExpressionTrue
            {
                Expression = "[DataCorePlugin.GameData.Fuel] < 10",
            };

            string json = JsonConvert.SerializeObject(original);
            var result = JsonConvert.DeserializeObject<ActivationCondition>(json);

            var typed = Assert.IsType<WhileExpressionTrue>(result);
            Assert.Equal("[DataCorePlugin.GameData.Fuel] < 10", typed.Expression);
        }

        [Fact]
        public void UnknownType_ThrowsJsonSerializationException()
        {
            string json = "{\"Type\":\"Unknown\"}";
            Assert.Throws<JsonSerializationException>(
                () => JsonConvert.DeserializeObject<ActivationCondition>(json));
        }

        [Fact]
        public void NullCondition_DeserializesToNull()
        {
            string json = "null";
            var result = JsonConvert.DeserializeObject<ActivationCondition>(json);
            Assert.Null(result);
        }

        [Fact]
        public void TypeDiscriminator_AppearsInJson()
        {
            var original = new OnValueChange { Property = "test", HoldMs = 1000 };
            string json = JsonConvert.SerializeObject(original);

            Assert.Contains("\"Type\":\"OnValueChange\"", json);
        }
    }
}
