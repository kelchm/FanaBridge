using FanaBridge.Display.Drivers;
using FanaBridge.Display.Rules;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Pins FieldDefaults (the UI's paramId → built-in-default table) to the two
    /// tables it mirrors: every entry must be a known BuiltInProperties name AND a
    /// param the mapper actually has a built-in encoder for.
    /// </summary>
    public class FieldDefaultsTests
    {
        [Fact]
        public void EveryDefault_IsKnownBuiltIn_AndMapperEncodable()
        {
            var mapper = new ItmTelemetryMapper();
            foreach (ushort paramId in FieldDefaults.MappedParams)
            {
                Assert.True(
                    FieldDefaults.TryGetBuiltInDefault(paramId, out string name));
                Assert.True(
                    BuiltInProperties.IsKnown(name),
                    $"param {paramId}: '{name}' is not a known built-in");
                Assert.True(
                    mapper.HasEncoder(paramId),
                    $"param {paramId} has a default but no built-in encoder");
            }
        }

        [Fact]
        public void UnknownParam_HasNoDefault()
        {
            Assert.False(FieldDefaults.TryGetBuiltInDefault(9999, out _));
        }
    }
}
