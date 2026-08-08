using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanaBridge.Devices.Profiles;
using FanaBridge.Plugin.Devices;
using FanaBridge.Plugin.Settings;
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
