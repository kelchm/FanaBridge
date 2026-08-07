using System;
using FanaBridge.Updater;
using Xunit;

namespace FanaBridge.Tests.Updater
{
    public class UpdateVersionTests
    {
        [Theory]
        [InlineData("0.6.0", "0.6.0", null)]
        [InlineData("v0.7.0", "0.7.0", null)]
        [InlineData("V0.7.0", "0.7.0", null)]
        [InlineData("0.6.0-preview", "0.6.0", "preview")]
        [InlineData("1.2", "1.2", null)]
        public void TryParse_AcceptsWellFormed(string text, string numeric, string? suffix)
        {
            Assert.True(UpdateVersion.TryParse(text, out UpdateVersion v));
            Assert.Equal(numeric, v.Numeric.ToString());
            Assert.Equal(suffix, v.Suffix);
            Assert.Equal(suffix == null ? numeric : numeric + "-" + suffix, v.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1")]
        [InlineData("abc")]
        [InlineData("1.-2.0")]
        [InlineData("v")]
        [InlineData("-preview")]
        [InlineData("0.6.0-")]
        public void TryParse_RejectsGarbage(string? text)
        {
            Assert.False(UpdateVersion.TryParse(text, out _));
        }

        [Fact]
        public void OneTwo_Equals_OneTwoZero()
        {
            Assert.True(UpdateVersion.TryParse("1.2", out UpdateVersion a));
            Assert.True(UpdateVersion.TryParse("1.2.0", out UpdateVersion b));
            Assert.Equal(0, a.CompareTo(b));
            Assert.True(a.Equals(b));
        }

        [Fact]
        public void Ordering_NumericAndSuffix()
        {
            Assert.True(UpdateVersion.TryParse("0.10.0", out UpdateVersion v10));
            Assert.True(UpdateVersion.TryParse("0.9.0", out UpdateVersion v9));
            Assert.True(v10.CompareTo(v9) > 0);

            Assert.True(UpdateVersion.TryParse("0.6.1", out UpdateVersion a));
            Assert.True(UpdateVersion.TryParse("0.6.0", out UpdateVersion b));
            Assert.True(a.CompareTo(b) > 0);

            Assert.True(UpdateVersion.TryParse("0.6.0", out UpdateVersion rel));
            Assert.True(UpdateVersion.TryParse("0.6.0-preview", out UpdateVersion prev));
            Assert.True(rel.CompareTo(prev) > 0);

            Assert.True(UpdateVersion.TryParse("0.6.0-preview", out UpdateVersion p1));
            Assert.True(UpdateVersion.TryParse("0.6.0-PREVIEW", out UpdateVersion p2));
            Assert.Equal(0, p1.CompareTo(p2));
        }
    }
}
