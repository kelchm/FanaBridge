using System;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Display;
using FanaBridge.Settings;
using FanaBridge.Tests.TestDoubles;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Plugin.Settings
{
    /// <summary>
    /// What a device writes to its settings file.
    ///
    /// SimHub rewrites that file wholesale from one call, with no merge against
    /// what is already there, so anything missing is erased. These pin the rules
    /// that keep a save complete: unknown settings survive, channels the module
    /// currently has no driver for are not deleted, identity comes from the
    /// device's profile, and a module that could not take its settings blocks
    /// the save instead of writing a partial one.
    /// </summary>
    public class FanatecDeviceSettingsTests
    {
        private static DeviceConfig ConfigFor(string wheelCode)
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            return new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile),
            };
        }

        private static FanatecDeviceSettings SettingsWith(
            FakeLedModuleHost host, string wheelCode = "PSWBMW") =>
            new FanatecDeviceSettings(ConfigFor(wheelCode), host);

        private static JObject StoredDocument() => new JObject
        {
            ["ledModuleSettings"] = new JObject { ["Brightness"] = 80.0 },
            ["leds"] = new JObject { ["activeProfileId"] = "profile-abc" },
            ["buttons"] = new JObject { ["activeProfileId"] = "buttons-1" },
            // Channels this wheel has no driver for, but whose stored data must
            // outlive a save that cannot describe them.
            ["encoders"] = new JObject { ["activeProfileId"] = "encoders-kept" },
            ["matrix"] = JValue.CreateNull(),
            ["raw"] = new JObject { ["activeProfileId"] = "raw-1" },
            ["wheelType"] = "PSWBMW",
            ["moduleType"] = "",
            ["displayMode"] = "Speed",
            ["itmEnabled"] = false,
            ["itmShowLapTotal"] = false,
            ["itmShowPositionTotal"] = true,
            ["itmDefaultPage"] = 3,
            ["encoderMode"] = "mode-b",
            // Written by a build that knows something this one doesn't.
            ["futureExtension"] = new JObject { ["nested"] = "keep-me" },
        };

        [Fact]
        public void StoredDocument_SurvivesARoundTrip()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            var stored = StoredDocument();

            settings.Apply(stored, isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.True(JToken.DeepEquals(stored, saved),
                "a save must reproduce the stored document, got: " + saved);
        }

        [Fact]
        public void SavingTwice_IsStable()
        {
            var settings = SettingsWith(new FakeLedModuleHost());

            settings.Apply(StoredDocument(), isDefault: false);
            var first = settings.Capture(false, false);

            var reloaded = SettingsWith(new FakeLedModuleHost());
            reloaded.Apply(first, isDefault: false);
            var second = reloaded.Capture(false, false);

            Assert.True(JToken.DeepEquals(first, second),
                "reloading a saved document must produce the same document again");
        }

        [Fact]
        public void UnknownRoots_AreWrittenBackUntouched()
        {
            var settings = SettingsWith(new FakeLedModuleHost());

            settings.Apply(StoredDocument(), isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.Equal("keep-me", (string?)saved["futureExtension"]?["nested"]);
        }

        [Fact]
        public void ChannelsWithNoDriver_KeepTheirStoredData()
        {
            // The module reports "encoders" as null because this wheel has no
            // encoder driver. That must not delete what is stored for it.
            var settings = SettingsWith(new FakeLedModuleHost("leds", "buttons", "raw"));

            settings.Apply(StoredDocument(), isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.Equal("encoders-kept", (string?)saved["encoders"]?["activeProfileId"]);
        }

        [Fact]
        public void ChannelsWithADriver_AreWrittenFromTheModule()
        {
            var host = new FakeLedModuleHost("leds", "buttons", "raw");
            var settings = SettingsWith(host);

            settings.Apply(StoredDocument(), isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.Equal("profile-abc", (string?)saved["leds"]?["activeProfileId"]);
        }

        [Fact]
        public void NeverStoredChannels_StayNull()
        {
            var settings = SettingsWith(new FakeLedModuleHost("leds", "buttons", "raw"));

            settings.Apply(StoredDocument(), isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.Equal(JTokenType.Null, saved["matrix"]?.Type);
        }

        [Fact]
        public void Identity_ComesFromTheProfile_NotTheStoredDocument()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            var stored = StoredDocument();
            stored["wheelType"] = "SOMETHING-ELSE";

            settings.Apply(stored, isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.Equal("PSWBMW", (string?)saved["wheelType"]);
        }

        [Fact]
        public void AbsentEncoderMode_StaysAbsent()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            var stored = StoredDocument();
            stored.Remove("encoderMode");

            settings.Apply(stored, isDefault: false);
            var saved = settings.Capture(false, false);

            Assert.Null(saved["encoderMode"]);
        }

        [Fact]
        public void EncoderMode_RoundTripsAndCanBeChanged()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            settings.Apply(StoredDocument(), isDefault: false);

            Assert.Equal("mode-b", (string?)settings.Capture(false, false)["encoderMode"]);

            settings.UpdateEncoderMode("mode-c");

            Assert.Equal("mode-c", (string?)settings.Capture(false, false)["encoderMode"]);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void EveryFlagCombination_ProducesACompleteDocument(
            bool forTemplate, bool forDefaultSettings)
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            settings.Apply(StoredDocument(), isDefault: false);

            var saved = settings.Capture(forTemplate, forDefaultSettings);

            Assert.NotNull(saved["ledModuleSettings"]);
            Assert.Equal("keep-me", (string?)saved["futureExtension"]?["nested"]);
            Assert.Equal("PSWBMW", (string?)saved["wheelType"]);
        }

        // ── Failure handling ───────────────────────────────────────────────

        [Fact]
        public void RejectedSettings_AreNotCommitted()
        {
            var host = new FakeLedModuleHost();
            var settings = SettingsWith(host);
            settings.Apply(StoredDocument(), isDefault: false);

            var replacement = StoredDocument();
            replacement["displayMode"] = "Gear";
            host.AcceptSettings = false;

            Assert.Throws<InvalidOperationException>(
                () => settings.Apply(replacement, isDefault: false));

            // The rejected values must not have replaced the committed ones.
            Assert.Equal("Speed", settings.Current.DisplayMode);
        }

        [Fact]
        public void AfterARejectedApply_SavingIsRefused()
        {
            var host = new FakeLedModuleHost { AcceptSettings = false };
            var settings = SettingsWith(host);

            Assert.Throws<InvalidOperationException>(
                () => settings.Apply(StoredDocument(), isDefault: false));

            Assert.True(settings.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => settings.Capture(false, false));
        }

        [Fact]
        public void ALaterSuccessfulApply_ClearsTheRefusal()
        {
            var host = new FakeLedModuleHost { AcceptSettings = false };
            var settings = SettingsWith(host);
            Assert.Throws<InvalidOperationException>(
                () => settings.Apply(StoredDocument(), isDefault: false));

            host.AcceptSettings = true;
            settings.Apply(StoredDocument(), isDefault: false);

            Assert.False(settings.IsFaulted);
            Assert.NotNull(settings.Capture(false, false));
        }

        [Fact]
        public void DefaultsAfterARejectedApply_ClearTheRefusal()
        {
            var host = new FakeLedModuleHost { AcceptSettings = false };
            var settings = SettingsWith(host);
            Assert.Throws<InvalidOperationException>(
                () => settings.Apply(StoredDocument(), isDefault: false));

            settings.LoadDefaults();

            Assert.False(settings.IsFaulted);
            Assert.NotNull(settings.Capture(false, false));
        }

        [Fact]
        public void AnApplyThatCannotBeReconciled_BlocksSaving()
        {
            // The module took the settings but cannot describe what it now
            // holds, so there is no way to tell which parts of the document are
            // still ours to keep. Saving either half would persist a mixture.
            var host = new FakeLedModuleHost();
            var settings = SettingsWith(host);
            settings.Apply(StoredDocument(), isDefault: false);

            host.ThrowOnCapture = true;
            Assert.Throws<InvalidOperationException>(
                () => settings.Apply(StoredDocument(), isDefault: false));

            host.ThrowOnCapture = false;
            Assert.True(settings.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => settings.Capture(false, false));
        }

        [Fact]
        public void AFailedCapture_IsNotLatched()
        {
            // Serialization can fail transiently while the editor is mid-edit;
            // latching that would block every later save.
            var host = new FakeLedModuleHost();
            var settings = SettingsWith(host);
            settings.Apply(StoredDocument(), isDefault: false);

            host.ThrowOnCapture = true;
            Assert.Throws<InvalidOperationException>(() => settings.Capture(false, false));

            host.ThrowOnCapture = false;
            Assert.False(settings.IsFaulted);
            Assert.NotNull(settings.Capture(false, false));
        }

        [Fact]
        public void FailedDefaults_LeaveTheCommittedSettingsAlone()
        {
            var host = new FakeLedModuleHost { ThrowOnDefaults = true };
            var settings = SettingsWith(host);
            settings.Apply(StoredDocument(), isDefault: false);

            Assert.Throws<InvalidOperationException>(() => settings.LoadDefaults());

            Assert.Equal("Speed", settings.Current.DisplayMode);
        }

        [Fact]
        public void APartlyAppliedReset_BlocksSaving()
        {
            // A reset that failed part way leaves the module holding neither the
            // old settings nor the defaults; saving that would replace a good
            // file with a state nobody chose.
            var host = new FakeLedModuleHost
            {
                ThrowOnDefaults = true,
                MutateBeforeThrowingOnDefaults = true,
            };
            var settings = SettingsWith(host);
            settings.Apply(StoredDocument(), isDefault: false);

            Assert.Throws<InvalidOperationException>(() => settings.LoadDefaults());

            Assert.True(settings.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => settings.Capture(false, false));
        }

        [Fact]
        public void NonObjectSettings_AreRejected()
        {
            var settings = SettingsWith(new FakeLedModuleHost());

            Assert.Throws<ArgumentException>(
                () => settings.Apply(new JArray(), isDefault: false));
        }

        // ── Defaults ───────────────────────────────────────────────────────

        [Fact]
        public void Defaults_ResetOwnSettingsButKeepUnknownOnes()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            settings.Apply(StoredDocument(), isDefault: false);

            settings.LoadDefaults();
            var saved = settings.Capture(false, false);

            Assert.Equal(DisplaySettings.DefaultMode, (string?)saved["displayMode"]);
            Assert.Null(saved["encoderMode"]);
            // A reset of FanaBridge's options has no business discarding
            // settings that belong to something else.
            Assert.Equal("keep-me", (string?)saved["futureExtension"]?["nested"]);
        }

        [Fact]
        public void Defaults_ClearLedDataForChannelsWithNoDriver()
        {
            // "encoders" is kept through ordinary saves because the module has
            // no driver to describe it — but a reset is explicit, and leaving it
            // would resurrect the old profile if a later profile change gave
            // that channel a driver.
            var settings = SettingsWith(new FakeLedModuleHost("leds", "buttons", "raw"));
            settings.Apply(StoredDocument(), isDefault: false);
            Assert.Equal("encoders-kept",
                (string?)settings.Capture(false, false)["encoders"]?["activeProfileId"]);

            settings.LoadDefaults();
            var saved = settings.Capture(false, false);

            // The module reports the channel as empty, and nothing stored
            // overrides that any more.
            Assert.Equal(JTokenType.Null, saved["encoders"]?.Type);
            // Settings that are nobody's business but their own still survive.
            Assert.Equal("keep-me", (string?)saved["futureExtension"]?["nested"]);
        }

        // ── Notifications ──────────────────────────────────────────────────

        [Fact]
        public void Committing_NotifiesListeners()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            var notifications = 0;
            settings.Changed += (_, __) => notifications++;

            settings.Apply(StoredDocument(), isDefault: false);
            settings.LoadDefaults();

            Assert.Equal(2, notifications);
        }

        [Fact]
        public void ARejectedApply_NotifiesNobody()
        {
            var settings = SettingsWith(new FakeLedModuleHost { AcceptSettings = false });
            var notified = false;
            settings.Changed += (_, __) => notified = true;

            Assert.Throws<InvalidOperationException>(
                () => settings.Apply(StoredDocument(), isDefault: false));

            Assert.False(notified);
        }

        [Fact]
        public void AFailingListener_DoesNotBreakTheCommit()
        {
            var settings = SettingsWith(new FakeLedModuleHost());
            settings.Changed += (_, __) => throw new InvalidOperationException("panel blew up");
            var reached = false;
            settings.Changed += (_, __) => reached = true;

            settings.Apply(StoredDocument(), isDefault: false);

            Assert.True(reached);
            Assert.NotNull(settings.Capture(false, false));
        }
    }
}
