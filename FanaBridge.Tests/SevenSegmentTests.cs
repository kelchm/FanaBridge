using FanaBridge.Core;
using Xunit;

namespace FanaBridge.Tests
{
    public class SevenSegmentTests
    {
        [Theory]
        [InlineData('0', SevenSegment.Digit0)]
        [InlineData('9', SevenSegment.Digit9)]
        [InlineData('-', SevenSegment.Dash)]
        [InlineData(' ', SevenSegment.Blank)]
        [InlineData('A', SevenSegment.A)]
        [InlineData('a', SevenSegment.A)] // case-insensitive
        public void CharToSegment_KnownChars_ReturnCorrectCode(char input, byte expected)
        {
            Assert.Equal(expected, SevenSegment.CharToSegment(input));
        }

        [Fact]
        public void CharToSegment_UnknownChar_ReturnsBlank()
        {
            Assert.Equal(SevenSegment.Blank, SevenSegment.CharToSegment('@'));
        }
    }
}
