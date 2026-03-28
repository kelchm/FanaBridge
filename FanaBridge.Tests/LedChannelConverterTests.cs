using FanaBridge.Profiles;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests
{
    public class LedChannelConverterTests
    {
        private static LedChannel Deserialize(string channelName)
        {
            string json = "{\"channel\":\"" + channelName + "\",\"hwIndex\":0,\"role\":\"rev\",\"label\":\"test\"}";
            return JsonConvert.DeserializeObject<LedDefinition>(json).Channel;
        }

        // ── V2 names (standard enum parse) ────────────────────────────

        [Theory]
        [InlineData("revRgb", LedChannel.RevRgb)]
        [InlineData("flagRgb", LedChannel.FlagRgb)]
        [InlineData("buttonRgb", LedChannel.ButtonRgb)]
        [InlineData("buttonAuxIntensity", LedChannel.ButtonAuxIntensity)]
        [InlineData("legacyRevOnOff", LedChannel.LegacyRevOnOff)]
        [InlineData("legacyRevStripe", LedChannel.LegacyRevStripe)]
        [InlineData("legacyRev3Bit", LedChannel.LegacyRev3Bit)]
        [InlineData("legacyFlag3Bit", LedChannel.LegacyFlag3Bit)]
        public void V2Names_DeserializeCorrectly(string name, LedChannel expected)
        {
            Assert.Equal(expected, Deserialize(name));
        }

        // ── V1 legacy names ───────────────────────────────────────────

        [Theory]
        [InlineData("rev", LedChannel.RevRgb)]
        [InlineData("flag", LedChannel.FlagRgb)]
        [InlineData("color", LedChannel.ButtonRgb)]
        [InlineData("mono", LedChannel.ButtonAuxIntensity)]
        public void V1Names_MapToV2Equivalents(string v1Name, LedChannel expected)
        {
            Assert.Equal(expected, Deserialize(v1Name));
        }
    }
}
