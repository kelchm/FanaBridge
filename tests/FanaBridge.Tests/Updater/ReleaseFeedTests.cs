using FanaBridge.Updater;
using Xunit;

namespace FanaBridge.Tests.Updater
{
    public class ReleaseFeedTests
    {
        private const string HappyJson = @"{
  ""tag_name"": ""v0.7.0"",
  ""html_url"": ""https://github.com/example/FanaBridge/releases/tag/v0.7.0"",
  ""assets"": [
    {
      ""name"": ""notes.txt"",
      ""browser_download_url"": ""https://example.com/notes.txt"",
      ""size"": 12,
      ""digest"": ""sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa""
    },
    {
      ""name"": ""FanaBridge-0.7.0.zip"",
      ""browser_download_url"": ""https://github.com/example/FanaBridge/releases/download/v0.7.0/FanaBridge-0.7.0.zip"",
      ""size"": 12345,
      ""digest"": ""sha256:9715EFCE0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567""
    }
  ]
}";

        [Fact]
        public void Parse_HappyPath_ExtractsAssetAndLowercasesDigest()
        {
            ReleaseInfo? info = ReleaseFeed.Parse(HappyJson, out string? error);
            Assert.Null(error);
            Assert.NotNull(info);
            Assert.Equal("v0.7.0", info!.TagName);
            Assert.Equal("0.7.0", info.Version);
            Assert.Equal("https://github.com/example/FanaBridge/releases/tag/v0.7.0", info.HtmlUrl);
            Assert.Equal("FanaBridge-0.7.0.zip", info.ZipName);
            Assert.Equal(
                "https://github.com/example/FanaBridge/releases/download/v0.7.0/FanaBridge-0.7.0.zip",
                info.ZipUrl);
            Assert.Equal(12345, info.ZipSizeBytes);
            Assert.Equal("9715efce0123456789abcdef0123456789abcdef0123456789abcdef01234567", info.DigestHex);
            Assert.True(info.CanSelfInstall);
            Assert.Null(info.InstallBlockedReason);
        }

        [Fact]
        public void Parse_WrongAssetName_NotifyOnly()
        {
            string json = @"{
  ""tag_name"": ""v0.7.0"",
  ""html_url"": ""https://example.com/r"",
  ""assets"": [{
    ""name"": ""FanaBridge-0.7.0-win.zip"",
    ""browser_download_url"": ""https://example.com/z.zip"",
    ""size"": 1,
    ""digest"": ""sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa""
  }]
}";
            ReleaseInfo? info = ReleaseFeed.Parse(json, out string? error);
            Assert.Null(error);
            Assert.NotNull(info);
            Assert.False(info!.CanSelfInstall);
            Assert.NotNull(info.InstallBlockedReason);
            Assert.Contains("FanaBridge-0.7.0.zip", info.InstallBlockedReason);
            Assert.Null(info.ZipName);
        }

        [Fact]
        public void Parse_MissingDigest_NotifyOnly()
        {
            string json = @"{
  ""tag_name"": ""v0.7.0"",
  ""html_url"": ""https://example.com/r"",
  ""assets"": [{
    ""name"": ""FanaBridge-0.7.0.zip"",
    ""browser_download_url"": ""https://example.com/z.zip"",
    ""size"": 1
  }]
}";
            ReleaseInfo? info = ReleaseFeed.Parse(json, out string? error);
            Assert.Null(error);
            Assert.NotNull(info);
            Assert.False(info!.CanSelfInstall);
            Assert.Contains("digest", info.InstallBlockedReason, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("sha256:xyz")]
        [InlineData("sha256:abcd")]
        [InlineData("md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("sha256:9715efce0123456789abcdef0123456789abcdef0123456789abcdef012345")] // 62 hex
        public void Parse_MalformedDigest_NotifyOnly(string digest)
        {
            string json = @"{
  ""tag_name"": ""v0.7.0"",
  ""html_url"": ""https://example.com/r"",
  ""assets"": [{
    ""name"": ""FanaBridge-0.7.0.zip"",
    ""browser_download_url"": ""https://example.com/z.zip"",
    ""size"": 1,
    ""digest"": """ + digest + @"""
  }]
}";
            ReleaseInfo? info = ReleaseFeed.Parse(json, out string? error);
            Assert.Null(error);
            Assert.NotNull(info);
            Assert.False(info!.CanSelfInstall);
            Assert.Null(info.DigestHex);
        }

        [Fact]
        public void Parse_MalformedJson_ReturnsError()
        {
            ReleaseInfo? info = ReleaseFeed.Parse("{ not json", out string? error);
            Assert.Null(info);
            Assert.NotNull(error);
        }

        [Fact]
        public void Parse_MissingTagName_ReturnsError()
        {
            string json = @"{ ""html_url"": ""https://example.com/r"", ""assets"": [] }";
            ReleaseInfo? info = ReleaseFeed.Parse(json, out string? error);
            Assert.Null(info);
            Assert.NotNull(error);
            Assert.Contains("tag_name", error, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
