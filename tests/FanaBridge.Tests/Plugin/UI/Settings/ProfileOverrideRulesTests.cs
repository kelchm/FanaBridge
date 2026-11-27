using System;
using System.Collections.Generic;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.UI.Settings;
using Xunit;

namespace FanaBridge.Tests.Plugin.UI.Settings
{
    public class ProfileOverrideRulesTests
    {
        private static WheelProfile Profile(string id, ProfileSource source) =>
            new WheelProfile { Id = id, Source = source };

        private static Dictionary<string, string> Overrides(params (string key, string value)[] entries)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in entries)
                d[key] = value;
            return d;
        }

        // ── Picking a profile ─────────────────────────────────────────────

        [Fact]
        public void PickingTheAutomaticChoice_RemovesTheOverride()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            ProfileOverrideRules.ApplySelection(
                overrides, "PSWBMW", "PSWBMW:BuiltIn", Profile("PSWBMW", ProfileSource.BuiltIn));

            Assert.False(overrides.ContainsKey("PSWBMW"));
        }

        [Fact]
        public void PickingSomethingElse_StoresTheOverride()
        {
            var overrides = Overrides();

            ProfileOverrideRules.ApplySelection(
                overrides, "PSWBMW", "my-bmw:User", Profile("PSWBMW", ProfileSource.BuiltIn));

            Assert.Equal("my-bmw:User", overrides["PSWBMW"]);
        }

        [Fact]
        public void PickingTheBareBuiltIn_OverAnAutomaticLedProfile_StoresTheOverride()
        {
            // The resolver picks a user LED profile over the bare built-in hub
            // profile automatically, so choosing the bare one is an explicit pin
            // that must persist (it used to be dropped as "the default").
            var overrides = Overrides(("PHUB", "my-hub-leds:User"));

            ProfileOverrideRules.ApplySelection(
                overrides, "PHUB", "PHUB:BuiltIn", Profile("my-hub-leds", ProfileSource.User));

            Assert.Equal("PHUB:BuiltIn", overrides["PHUB"]);
        }

        [Fact]
        public void Selection_WithoutAMatchKey_ChangesNothing()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            ProfileOverrideRules.ApplySelection(overrides, null!, "x:User", null!);

            Assert.Equal("my-bmw:User", overrides["PSWBMW"]);
        }

        // ── Deleting a profile ────────────────────────────────────────────

        [Fact]
        public void Delete_ClearsTheOverrideThatNamedTheDeletedProfile()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            Assert.True(ProfileOverrideRules.ClearAfterDelete(overrides, "PSWBMW", "my-bmw:User"));
            Assert.False(overrides.ContainsKey("PSWBMW"));
        }

        [Fact]
        public void Delete_KeepsAPinToAnotherProfile()
        {
            var overrides = Overrides(("PSWBMW", "PSWBMW:BuiltIn"));

            Assert.False(ProfileOverrideRules.ClearAfterDelete(overrides, "PSWBMW", "my-bmw:User"));
            Assert.Equal("PSWBMW:BuiltIn", overrides["PSWBMW"]);
        }

        [Fact]
        public void Delete_OnlyTouchesTheDeletedProfilesOwnAttachment()
        {
            // The wheel can change while the confirmation dialog is open; the
            // other attachment's pin must survive.
            var overrides = Overrides(("PSWBMW", "my-bmw:User"), ("CSSWFORMV3", "my-v3:User"));

            ProfileOverrideRules.ClearAfterDelete(overrides, "PSWBMW", "my-bmw:User");

            Assert.Equal("my-v3:User", overrides["CSSWFORMV3"]);
        }

        [Fact]
        public void Delete_WithoutAMatchKey_ChangesNothing()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            Assert.False(ProfileOverrideRules.ClearAfterDelete(overrides, null!, "my-bmw:User"));
            Assert.Equal("my-bmw:User", overrides["PSWBMW"]);
        }
    }
}
