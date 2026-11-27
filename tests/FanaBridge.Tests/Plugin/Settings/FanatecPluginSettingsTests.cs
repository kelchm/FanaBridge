using System.Collections.Generic;
using FanaBridge.Settings;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.Plugin.Settings
{
    public class FanatecPluginSettingsTests
    {
        // The registration-time reader (PersistedPluginSettings) matches override
        // keys case-insensitively; the running plugin must too, or a hand-edited
        // key resolves differently at registration and at runtime.

        [Fact]
        public void ProfileOverrides_AssignedDictionary_MatchesKeysCaseInsensitively()
        {
            var settings = new FanatecPluginSettings
            {
                ProfileOverrides = new Dictionary<string, string> { ["pswbmw"] = "X:User" },
            };

            Assert.True(settings.ProfileOverrides.TryGetValue("PSWBMW", out var value));
            Assert.Equal("X:User", value);
        }

        [Fact]
        public void ProfileOverrides_Deserialized_MatchesKeysCaseInsensitively()
        {
            var settings = JsonConvert.DeserializeObject<FanatecPluginSettings>(
                "{ \"ProfileOverrides\": { \"pswbmw\": \"X:User\" } }");

            Assert.NotNull(settings);
            Assert.Equal("X:User", settings!.ProfileOverrides["PSWBMW"]);
        }

        [Fact]
        public void ProfileOverrides_AssignedNull_IsAnEmptyDictionary()
        {
            var settings = new FanatecPluginSettings { ProfileOverrides = null };

            Assert.NotNull(settings.ProfileOverrides);
            Assert.Empty(settings.ProfileOverrides);
        }
    }
}
