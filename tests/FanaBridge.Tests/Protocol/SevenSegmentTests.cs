using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Protocol
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

        // ── EncodeWithDots (shared fold for the scroll path) ─────────────

        [Fact]
        public void EncodeWithDots_FoldsDotOntoPreviousChar()
        {
            var encoded = SevenSegment.EncodeWithDots("1.2");

            Assert.Equal(2, encoded.Count);
            Assert.Equal((byte)(SevenSegment.Digit1 | SevenSegment.Dot), encoded[0]);
            Assert.Equal(SevenSegment.Digit2, encoded[1]);
        }

        [Fact]
        public void EncodeWithDots_CommaFoldsLikeDot_AndTrailingDotIsKept()
        {
            // Unbounded encoding keeps a trailing dot; DisplayText uses the same fold
            // capped at 3 positions (including a trailing fold onto position 3).
            var encoded = SevenSegment.EncodeWithDots("1,23.");

            Assert.Equal(3, encoded.Count);
            Assert.Equal((byte)(SevenSegment.Digit1 | SevenSegment.Dot), encoded[0]);
            Assert.Equal((byte)(SevenSegment.Digit3 | SevenSegment.Dot), encoded[2]);
        }

        [Fact]
        public void EncodeWithDots_LeadingDot_BecomesItsOwnSegment()
        {
            // Nothing to fold onto — blank position with the dot lit.
            var encoded = SevenSegment.EncodeWithDots(".5");

            Assert.Equal(2, encoded.Count);
            Assert.Equal(SevenSegment.Dot, encoded[0]);
            Assert.Equal(SevenSegment.Digit5, encoded[1]);
        }

        [Fact]
        public void EncodeWithDots_ConsecutiveDots_EachIsBlankDotPosition()
        {
            var encoded = SevenSegment.EncodeWithDots("...");
            Assert.Equal(3, encoded.Count);
            Assert.All(encoded, b => Assert.Equal(SevenSegment.Dot, b));
        }

        [Fact]
        public void EncodeWithDots_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(SevenSegment.EncodeWithDots(""));
            Assert.Empty(SevenSegment.EncodeWithDots(null));
        }
    }
}
