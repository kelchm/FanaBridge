using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Devices;
using FanaBridge.Settings;
using FanaBridge.Tests.TestDoubles;
using Xunit;

namespace FanaBridge.Tests.Plugin.Devices
{
    /// <summary>
    /// Device registration has to honour the user's profile override, because
    /// the profile it picks sizes the LED editor — and that size is fixed for a
    /// device instance's lifetime. Resolving the override only at runtime meant
    /// a wheel whose override changes its LED layout could never get an editor
    /// that matched, not even after a restart.
    ///
    /// These run against the real profile store, writing user profiles into the
    /// directory it reads from (next to the test binary) and pointing the
    /// settings reader at a temporary file.
    /// </summary>
    public class DeviceRegistryOverrideTests : IDisposable
    {
        private readonly string _userProfileDir;
        private readonly string _settingsPath;
        private readonly string _writtenProfile;
        private readonly List<string> _extraFiles = new List<string>();

        public DeviceRegistryOverrideTests()
        {
            _userProfileDir = WheelProfileStore.GetUserProfileDirectory();
            _writtenProfile = Path.Combine(_userProfileDir, "test-override-profile.json");
            _settingsPath = Path.Combine(
                Path.GetTempPath(), "fanabridge-test-settings-" + Guid.NewGuid().ToString("N") + ".json");
        }

        public void Dispose()
        {
            PersistedPluginSettings.SettingsPathResolver = PersistedPluginSettings.DefaultSettingsPath;
            TryDelete(_writtenProfile);
            TryDelete(_settingsPath);
            foreach (var extra in _extraFiles)
                TryDelete(extra);
            WheelProfileStore.Reload();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Writes a user profile matching CSLSWGT3 — a built-in that has a
        /// display but no LEDs — that adds LEDs to it.
        /// </summary>
        private void WriteLedAddingOverrideForDisplayOnlyWheel()
        {
            File.WriteAllText(_writtenProfile, @"{
              ""schemaVersion"": 2,
              ""id"": ""CSLSWGT3_LEDTEST"",
              ""name"": ""GT3 with LEDs (test)"",
              ""shortName"": ""GT3 LED test"",
              ""match"": { ""wheelType"": ""CSLSWGT3"" },
              ""display"": ""basic"",
              ""leds"": [
                { ""channel"": ""revRgb"", ""hwIndex"": 0, ""role"": ""rev"", ""label"": ""Rev 1"" },
                { ""channel"": ""revRgb"", ""hwIndex"": 1, ""role"": ""rev"", ""label"": ""Rev 2"" },
                { ""channel"": ""revRgb"", ""hwIndex"": 2, ""role"": ""rev"", ""label"": ""Rev 3"" }
              ]
            }");
            WheelProfileStore.Reload();
        }

        private void WriteSettings(string matchKey, string overrideKey)
        {
            File.WriteAllText(_settingsPath,
                "{ \"ProfileOverrides\": { \"" + matchKey + "\": \"" + overrideKey + "\" } }");
            PersistedPluginSettings.SettingsPathResolver = () => _settingsPath;
        }

        private static DeviceConfig ConfigFor(string deviceTypeId) =>
            FanatecDevicesRegistry.BuildConfigs()
                .FirstOrDefault(c => c.DeviceTypeId == deviceTypeId);

        [Fact]
        public void NoSettingsFile_LeavesTheBuiltInProfileInPlace()
        {
            PersistedPluginSettings.SettingsPathResolver =
                () => Path.Combine(Path.GetTempPath(), "fanabridge-does-not-exist.json");

            var config = ConfigFor("Fanatec_CSLSWGT3");

            Assert.NotNull(config);
            Assert.Equal(ProfileSource.BuiltIn, config.Profile.Source);
        }

        [Fact]
        public void Override_AddingLedsToADisplayOnlyWheel_IsApplied()
        {
            // The case that made deferring this a regression: without it the
            // device is frozen as display-only and can never gain an editor.
            WriteLedAddingOverrideForDisplayOnlyWheel();
            var overridden = WheelProfileStore.GetById("CSLSWGT3_LEDTEST");
            Assert.NotNull(overridden);
            WriteSettings("CSLSWGT3", WheelProfileStore.MakeOverrideKey(overridden));

            var config = ConfigFor("Fanatec_CSLSWGT3");

            Assert.NotNull(config);
            Assert.Equal("CSLSWGT3_LEDTEST", config.Profile.Id);
            Assert.Equal(3, config.Capabilities.AllLedCount);
        }

        [Fact]
        public void Override_KeepsTheDeviceTypeIdStable()
        {
            // The settings file on disk is keyed by DeviceTypeID; an override
            // that renamed the device would orphan it.
            WriteLedAddingOverrideForDisplayOnlyWheel();
            var overridden = WheelProfileStore.GetById("CSLSWGT3_LEDTEST");
            WriteSettings("CSLSWGT3", WheelProfileStore.MakeOverrideKey(overridden));

            Assert.Single(FanatecDevicesRegistry.BuildConfigs(),
                c => c.DeviceTypeId == "Fanatec_CSLSWGT3");
        }

        [Fact]
        public void Override_ForADifferentWheel_IsIgnored()
        {
            // A stored override whose profile matches another wheel must not
            // be able to change this device's identity.
            var otherWheel = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(otherWheel);
            WriteSettings("CSLSWGT3", WheelProfileStore.MakeOverrideKey(otherWheel));

            var config = ConfigFor("Fanatec_CSLSWGT3");

            Assert.NotNull(config);
            // Compare profile ids to profile ids: the other wheel's *code* need
            // not equal its profile id, and comparing against the code would
            // pass whether or not the override was wrongly applied.
            Assert.NotEqual(otherWheel.Id, config.Profile.Id);
            Assert.Equal("CSLSWGT3", config.WheelCode);
        }

        [Fact]
        public void Override_WithoutTheAttachedModule_IsIgnored()
        {
            // The bare hub's profile must not be applied to the hub + module
            // device, at registration any more than at runtime.
            WriteSettings("PHUB_PBMR", "PHUB:BuiltIn");

            var config = ConfigFor("Fanatec_PHUB_PBMR");

            Assert.NotNull(config);
            Assert.Equal("PHUB_PBMR", config.Profile.Id);
        }

        [Fact]
        public void BareBuiltIn_DoesNotBeatAUserLedProfile_AtRegistrationOrAtRuntime()
        {
            // The built-in PHUB is bare and named after the attachment, but a bare
            // profile never wins automatically over one that can drive something,
            // so the user's LED profile backs the device and drives the hub alike.
            using var profiles = new TempUserProfiles();
            profiles.Write("fb81-hub-leds.json", "PHUB_FB81", "PHUB");
            profiles.Reload();
            PersistedPluginSettings.SettingsPathResolver =
                () => Path.Combine(Path.GetTempPath(), "fanabridge-does-not-exist.json");

            var withoutOverride = ConfigFor("Fanatec_PHUB");
            Assert.NotNull(withoutOverride);
            Assert.Equal("PHUB_FB81", withoutOverride.Profile.Id);
            Assert.Same(withoutOverride.Profile, WheelProfileStore.FindByWheelType("PHUB"));

            WriteSettings("PHUB", "PHUB_FB81:User");

            var config = ConfigFor("Fanatec_PHUB");
            Assert.NotNull(config);
            Assert.Equal("PHUB_FB81", config.Profile.Id);
        }

        [Fact]
        public void Override_ToTheBareBuiltIn_RegistersTheBareProfile_AsBefore()
        {
            // An explicit pin to the bare built-in is the user's choice: it sized
            // the editor (to nothing) before, and still does.
            using var profiles = new TempUserProfiles();
            profiles.Write("fb81-hub-leds.json", "PHUB_FB81", "PHUB", revLeds: 4);
            profiles.Reload();
            WriteSettings("PHUB", "PHUB:BuiltIn");

            var config = ConfigFor("Fanatec_PHUB");

            Assert.NotNull(config);
            Assert.Equal("PHUB", config.Profile.Id);
            Assert.Equal(ProfileSource.BuiltIn, config.Profile.Source);
            Assert.Equal(0, config.Capabilities.AllLedCount);
        }

        [Fact]
        public void Override_ToALowercaseCodedProfile_RegistersTheCanonicalId()
        {
            // Match codes are canonicalized on load, so pinning a hand-edited
            // lowercase profile no longer renames the device.
            using var profiles = new TempUserProfiles();
            profiles.Write("fb81-lower.json", "pswbmw-custom", "pswbmw");
            profiles.Reload();
            WriteSettings("PSWBMW", "pswbmw-custom:User");

            var config = ConfigFor("Fanatec_PSWBMW");

            Assert.NotNull(config);
            Assert.Equal("pswbmw-custom", config.Profile.Id);
            Assert.Null(ConfigFor("Fanatec_pswbmw"));
        }

        [Fact]
        public void BuiltInPin_OverASameIdUserProfile_KeepsTheBuiltInEverywhere()
        {
            // A real rig's setup: the built-in pinned over a user profile with the
            // same id, alongside other user profiles for the same attachment.
            using var profiles = new TempUserProfiles();
            profiles.Write("PHUB_PBME-v2.json", "PHUB_PBME", "PHUB", "PBME", revLeds: 4);
            profiles.Write("PHUB_PBME_LEGACY_REV.json", "PHUB_PBME_LEGACY_REV", "PHUB", "PBME", revLeds: 2);
            profiles.Reload();
            WriteSettings("PHUB_PBME", "PHUB_PBME:BuiltIn");

            var config = ConfigFor("Fanatec_PHUB_PBME");

            Assert.NotNull(config);
            Assert.Equal("PHUB_PBME", config.Profile.Id);
            Assert.Equal(ProfileSource.BuiltIn, config.Profile.Source);
            Assert.Same(config.Profile,
                WheelProfileStore.FindByWheelType("PHUB", "PBME", "PHUB_PBME:BuiltIn"));
        }

        [Fact]
        public void UnresolvableOverride_LeavesTheDeviceUsable()
        {
            WriteSettings("CSLSWGT3", "NoSuchProfile:BuiltIn");

            var config = ConfigFor("Fanatec_CSLSWGT3");

            Assert.NotNull(config);
            Assert.Equal(ProfileSource.BuiltIn, config.Profile.Source);
        }

        [Fact]
        public void CorruptSettingsFile_FallsBackToSimHubsBackup()
        {
            // SimHub itself recovers settings from its rolling backups. If
            // registration did not, it would size the LED editor from the
            // built-in profile while the running plugin used the override.
            WriteLedAddingOverrideForDisplayOnlyWheel();
            var overridden = WheelProfileStore.GetById("CSLSWGT3_LEDTEST");
            WriteSettings("CSLSWGT3", WheelProfileStore.MakeOverrideKey(overridden));

            var backupDir = Path.Combine(Path.GetDirectoryName(_settingsPath), "_Backups");
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir,
                Path.GetFileNameWithoutExtension(_settingsPath) + "_b1.json");
            File.Copy(_settingsPath, backup, overwrite: true);
            _extraFiles.Add(backup);

            File.WriteAllText(_settingsPath, "{ truncated...");

            var config = ConfigFor("Fanatec_CSLSWGT3");

            Assert.NotNull(config);
            Assert.Equal("CSLSWGT3_LEDTEST", config.Profile.Id);
        }

        [Fact]
        public void HealthySettingsFileWithoutOverrides_IsNotSecondGuessedByABackup()
        {
            // Backups are for files that cannot be read. A file with no
            // overrides in it is a healthy state -- most obviously after the
            // settings file was reset while _Backups survived -- and walking
            // past it would resurrect an override the live settings no longer
            // carry, sizing the LED editor from a ghost for the session.
            WriteLedAddingOverrideForDisplayOnlyWheel();
            var overridden = WheelProfileStore.GetById("CSLSWGT3_LEDTEST");
            WriteSettings("CSLSWGT3", WheelProfileStore.MakeOverrideKey(overridden));

            var backupDir = Path.Combine(Path.GetDirectoryName(_settingsPath), "_Backups");
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir,
                Path.GetFileNameWithoutExtension(_settingsPath) + "_b1.json");
            File.Copy(_settingsPath, backup, overwrite: true);
            _extraFiles.Add(backup);

            // A fresh, parseable file with no ProfileOverrides at all.
            File.WriteAllText(_settingsPath, "{ \"EnableTuning\": false }");

            var config = ConfigFor("Fanatec_CSLSWGT3");

            Assert.NotNull(config);
            Assert.Equal(ProfileSource.BuiltIn, config.Profile.Source);
        }

        [Fact]
        public void UnreadableSettingsFile_DoesNotBreakRegistration()
        {
            File.WriteAllText(_settingsPath, "{ this is not json");
            PersistedPluginSettings.SettingsPathResolver = () => _settingsPath;

            // Registration must still produce devices — failing here would hide
            // every Fanatec device from SimHub.
            Assert.NotEmpty(FanatecDevicesRegistry.BuildConfigs());
        }
    }
}
