using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using FanaBridge.Profiles;
using FanaBridge.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// The real LED settings module, built from a device's registered
    /// capabilities rather than from whatever the runtime currently reports.
    /// </summary>
    /// <remarks>
    /// Construction touches no hardware and needs no plugin: SimHub's module
    /// only allocates its editor state, and the channel drivers appear when
    /// settings are applied. That is what lets a device keep a working editor,
    /// and keep serializing a complete settings document, while FanaBridge is
    /// disabled — the situation in which saves used to erase LED data.
    ///
    /// Sizing comes from the registered capabilities because SimHub fixes the
    /// editor's slot count for the module's lifetime; see
    /// FanatecDevicesRegistry, which resolves the user's profile override at
    /// registration so those capabilities already reflect it.
    /// </remarks>
    internal sealed class FanatecLedModuleHost : IFanatecLedModuleHost
    {
        private readonly LedModuleSettings<FanatecLedManager> _module;
        private readonly FanatecLedManager _manager;
        private bool _finalized;
        private bool _disposed;

        public bool HasModule => true;

        public FanatecLedModuleHost(
            DeviceConfig config,
            Func<FanatecPlugin> pluginResolver,
            Func<WheelCapabilities> currentCapabilities)
        {
            var caps = config.Capabilities;
            _manager = new FanatecLedManager(config, pluginResolver);

            try
            {
                var options = new LedModuleOptions
                {
                    DeviceName = caps.ShortName ?? caps.Name,
                    LedCount = caps.RevFlagCount,
                    ButtonsCount = caps.ButtonLedCount,
                    EncodersCount = 0,  // all non-rev/flag LEDs are "buttons" in SimHub
                    RawLedCount = caps.AllLedCount,
                    LedDriver = _manager,
                    EnableBrightnessSection = true,
                    ShowConnectionStatus = true,
                    VID = FanatecWheelbase.FANATEC_VENDOR_ID,

                    // Wheels whose LEDs can't render the picker's range get a note
                    // in the LEDs tab. The picker stays stock — constraining it
                    // would break gradients and imported profiles without fixing
                    // anything, since those never pass through it. The notice
                    // resolves capabilities when the tab is opened, so it reflects
                    // the wheel that is actually connected.
                    ExtraSettingsControlFactory =
                        _ => new UI.LedColorLimitationNotice(currentCapabilities),
                };

                _module = new LedModuleSettings<FanatecLedManager>(options)
                {
                    IsEmbedded = true,
                    IsEnabled = true,
                };
            }
            catch
            {
                // The manager has already subscribed to a static event; drop it
                // rather than leak it when the module cannot be built.
                _manager.Dispose();
                throw;
            }

            SimHub.Logging.Current.Info(
                "FanatecLedModuleHost[" + caps.Name + "]: LED module created (" +
                "revRgb=" + caps.RevRgbCount + ", flagRgb=" + caps.FlagRgbCount +
                ", buttonRgb=" + caps.ButtonRgbCount +
                ", buttonAuxIntensity=" + caps.ButtonAuxIntensityCount +
                ", total=" + caps.AllLedCount + ")");
        }

        public Control EditControl => _module.EditControl;

        public bool Apply(JObject source, bool isDefault)
        {
            // Module-level state is written before the channels, so a channel
            // failure would otherwise leave brightness and friends carrying
            // values from a payload that was refused. Nothing resets those
            // afterwards — LoadDefaults only rebuilds channel drivers — so a
            // rejected apply has to put them back itself.
            var moduleLevelBefore = JToken.FromObject(_module);
            var driversBefore = ChannelDrivers();

            try
            {
                // Restore module-level state (brightness, IndividualLEDsMode, etc.)
                // before passing channel profiles, matching LedModuleDevice.SetSettings.
                var moduleToken = source["ledModuleSettings"];
                if (moduleToken != null)
                    JsonConvert.PopulateObject(moduleToken.ToString(), _module);

                // Per-channel profile data (leds, buttons, raw, …)
                var dict = source.Properties().ToDictionary(p => p.Name, p => p.Value);
                _module.SetSettings(dict, isDefault);

                DisposeReplacedDrivers(driversBefore);
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecLedModuleHost: applying LED settings failed: " + ex.Message);

                TryRestoreModuleLevelState(moduleLevelBefore);
                DisposeReplacedDrivers(driversBefore);
                return false;
            }
        }

        private void TryRestoreModuleLevelState(JToken snapshot)
        {
            try
            {
                JsonConvert.PopulateObject(snapshot.ToString(), _module);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecLedModuleHost: could not restore module state after a " +
                    "failed apply: " + ex.Message);
            }
        }

        /// <summary>
        /// The module's per-channel drivers. Each subscribes to a static update
        /// event and only its own disposal unsubscribes, so every one that gets
        /// replaced has to be disposed or it keeps being called forever.
        /// </summary>
        private IDisposable[] ChannelDrivers() => new IDisposable[]
        {
            _module.LedsDriver,
            _module.ButtonsDriver,
            _module.EncodersDriver,
            _module.RawDriver,
            _module.MatrixDriver,
        };

        private void DisposeReplacedDrivers(IDisposable[] before)
        {
            var after = ChannelDrivers();

            foreach (var driver in before)
            {
                if (driver == null || after.Any(a => ReferenceEquals(a, driver)))
                    continue;

                try
                {
                    driver.Dispose();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "FanatecLedModuleHost: disposing a replaced LED channel failed: " +
                        ex.Message);
                }
            }
        }

        public JObject Capture(bool forTemplate, bool forDefaultSettings)
        {
            var result = new JObject
            {
                // The module object itself (brightness, IndividualLEDsMode, …),
                // matching how LedModuleDevice persists it.
                ["ledModuleSettings"] = JToken.FromObject(_module),
            };

            // Per-channel profile data. SimHub emits every channel key, using
            // null for channels it has no driver for.
            var channels = _module.GetSettings(forTemplate, forDefaultSettings);
            if (channels != null)
            {
                foreach (var kvp in channels)
                    result[kvp.Key] = kvp.Value ?? JValue.CreateNull();
            }

            return result;
        }

        public void LoadDefaults()
        {
            var driversBefore = ChannelDrivers();
            try
            {
                _module.LoadDefaults();
            }
            finally
            {
                DisposeReplacedDrivers(driversBefore);
            }
        }

        public void Display() => _module.Display();

        public void ClearOutput()
        {
            // Only a driver that was actually built can blank anything; there is
            // nothing lit to blank when the runtime never produced one.
            try
            {
                (_manager.GetDriverInstance() as FanatecLedDriver)?.Clear();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecLedModuleHost: clearing LED output failed: " + ex.Message);
            }
        }

        public void RebindToCurrentGeneration() => _manager.Close();

        public void HotSwapIfNeeded(WheelCapabilities currentCaps) =>
            _manager.HotSwapIfNeeded(currentCaps);

        public IEnumerable<DynamicButtonAction> GetDynamicActions() =>
            _module.GetDynamicActions() ?? Enumerable.Empty<DynamicButtonAction>();

        public void FinalizeModule()
        {
            if (_finalized)
                return;

            _finalized = true;
            _module.FinalizeModule();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                FinalizeModule();
            }
            finally
            {
                // Must run even if flushing threw: these are the only calls that
                // remove subscriptions to static events -- the manager's to USB
                // changes, and each channel driver's to LED updates -- and
                // nothing in SimHub will do it for us.
                foreach (var driver in ChannelDrivers())
                {
                    try { driver?.Dispose(); }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn(
                            "FanatecLedModuleHost: disposing an LED channel failed: " + ex.Message);
                    }
                }

                (_manager.GetDriverInstance() as IDisposable)?.Dispose();
                _manager.Dispose();
            }
        }
    }
}
