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
        public void PickingTheAutomaticChoice_RemovesTheOverride_WithoutMutatingInput()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            var next = ProfileOverrideRules.ApplySelection(
                overrides, "PSWBMW", "PSWBMW:BuiltIn", Profile("PSWBMW", ProfileSource.BuiltIn));

            Assert.False(next.ContainsKey("PSWBMW"));
            Assert.Equal("my-bmw:User", overrides["PSWBMW"]);
        }

        [Fact]
        public void PickingSomethingElse_StoresTheOverride_WithoutMutatingInput()
        {
            var overrides = Overrides();

            var next = ProfileOverrideRules.ApplySelection(
                overrides, "PSWBMW", "my-bmw:User", Profile("PSWBMW", ProfileSource.BuiltIn));

            Assert.Equal("my-bmw:User", next["PSWBMW"]);
            Assert.Empty(overrides);
        }

        [Fact]
        public void PickingTheBareBuiltIn_OverAnAutomaticLedProfile_StoresTheOverride()
        {
            // The resolver picks a user LED profile over the bare built-in hub
            // profile automatically, so choosing the bare one is an explicit pin
            // that must persist (it used to be dropped as "the default").
            var overrides = Overrides(("PHUB", "my-hub-leds:User"));

            var next = ProfileOverrideRules.ApplySelection(
                overrides, "PHUB", "PHUB:BuiltIn", Profile("my-hub-leds", ProfileSource.User));

            Assert.Equal("PHUB:BuiltIn", next["PHUB"]);
            Assert.Equal("my-hub-leds:User", overrides["PHUB"]);
        }

        [Fact]
        public void Selection_WithoutAMatchKey_ChangesNothing()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            var next = ProfileOverrideRules.ApplySelection(overrides, null!, "x:User", null!);

            Assert.Equal("my-bmw:User", next["PSWBMW"]);
            Assert.NotSame(overrides, next);
        }

        [Fact]
        public void ApplyPickerChoice_WritesToChoicesOwnAttachment_LeavingOtherAttachmentsUntouched()
        {
            var overrides = Overrides(("CSSWFORMV2", "my-formv2:User"));
            var choice = new ProfilePickerChoice("PSWBMW", null, "my-bmw:User");

            var next = ProfileOverrideRules.ApplyPickerChoice(
                overrides, choice, Profile("PSWBMW", ProfileSource.BuiltIn));

            Assert.Equal("my-bmw:User", next["PSWBMW"]);
            Assert.Equal("my-formv2:User", next["CSSWFORMV2"]);
            Assert.False(overrides.ContainsKey("PSWBMW"));
        }

        [Fact]
        public void ApplyPickerChoice_WithoutMatchKey_ChangesNothing()
        {
            var overrides = Overrides(("CSSWFORMV2", "my-formv2:User"));
            var choice = new ProfilePickerChoice(null, null, "x:User");

            var next = ProfileOverrideRules.ApplyPickerChoice(overrides, choice, null);

            Assert.Equal("my-formv2:User", next["CSSWFORMV2"]);
            Assert.Single(next);
            Assert.NotSame(overrides, next);
        }

        [Fact]
        public void WithOverride_PinsMatchKey_WithoutMutatingInput()
        {
            var overrides = Overrides(("CSSWFORMV3", "my-v3:User"));

            var next = ProfileOverrideRules.WithOverride(overrides, "PSWBMW", "my-bmw:User");

            Assert.Equal("my-bmw:User", next["PSWBMW"]);
            Assert.Equal("my-v3:User", next["CSSWFORMV3"]);
            Assert.False(overrides.ContainsKey("PSWBMW"));
        }

        // ── Deleting a profile ────────────────────────────────────────────

        [Fact]
        public void Delete_ClearsTheOverrideThatNamedTheDeletedProfile_WithoutMutatingInput()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            var next = ProfileOverrideRules.ClearAfterDelete(
                overrides, "PSWBMW", "my-bmw:User", out bool removed);

            Assert.True(removed);
            Assert.False(next.ContainsKey("PSWBMW"));
            Assert.Equal("my-bmw:User", overrides["PSWBMW"]);
        }

        [Fact]
        public void Delete_KeepsAPinToAnotherProfile()
        {
            var overrides = Overrides(("PSWBMW", "PSWBMW:BuiltIn"));

            var next = ProfileOverrideRules.ClearAfterDelete(
                overrides, "PSWBMW", "my-bmw:User", out bool removed);

            Assert.False(removed);
            Assert.Equal("PSWBMW:BuiltIn", next["PSWBMW"]);
        }

        [Fact]
        public void Delete_OnlyTouchesTheDeletedProfilesOwnAttachment()
        {
            // The wheel can change while the confirmation dialog is open; the
            // other attachment's pin must survive.
            var overrides = Overrides(("PSWBMW", "my-bmw:User"), ("CSSWFORMV3", "my-v3:User"));

            var next = ProfileOverrideRules.ClearAfterDelete(
                overrides, "PSWBMW", "my-bmw:User", out bool removed);

            Assert.True(removed);
            Assert.False(next.ContainsKey("PSWBMW"));
            Assert.Equal("my-v3:User", next["CSSWFORMV3"]);
        }

        [Fact]
        public void Delete_WithoutAMatchKey_ChangesNothing()
        {
            var overrides = Overrides(("PSWBMW", "my-bmw:User"));

            var next = ProfileOverrideRules.ClearAfterDelete(
                overrides, null!, "my-bmw:User", out bool removed);

            Assert.False(removed);
            Assert.Equal("my-bmw:User", next["PSWBMW"]);
        }
    }
}
