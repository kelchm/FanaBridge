using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Profiles;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Owns everything a device persists: FanaBridge's own settings, the LED
    /// module's, and any part of the stored document this build doesn't
    /// recognise.
    /// </summary>
    /// <remarks>
    /// The one place that decides what a saved document contains: complete or
    /// refuse, typed snapshot + module projection + verbatim residual +
    /// profile-derived identity, all-or-nothing under one lock. The document
    /// rules, failure policy and their rationale live in
    /// docs/device-settings-lifecycle.md.
    /// </remarks>
    internal sealed class FanatecDeviceSettings
    {
        /// <summary>Roots this build owns and rewrites from typed state.</summary>
        private static readonly string[] TypedRoots =
        {
            "displayMode", "itmEnabled", "itmShowLapTotal",
            "itmShowPositionTotal", "itmDefaultPage", "encoderMode",
        };

        /// <summary>Roots derived from the device's profile rather than stored input.</summary>
        private static readonly string[] IdentityRoots = { "wheelType", "moduleType" };

        private readonly object _gate = new object();
        private readonly DeviceConfig _config;
        private readonly IFanatecLedModuleHost _host;

        private FanatecSettingsSnapshot _current = FanatecSettingsSnapshot.Defaults();
        private JObject _residual = new JObject();

        /// <summary>
        /// Set when the module rejected a payload, leaving it partially
        /// populated. While set, the device neither saves nor drives its LEDs —
        /// the stored file keeps the last complete copy until a later apply or
        /// an explicit reset makes the module trustworthy again.
        /// </summary>
        private bool _faulted;

        /// <summary>Raised after settings are committed, so open panels can refresh.</summary>
        public event EventHandler Changed;

        public FanatecDeviceSettings(DeviceConfig config, IFanatecLedModuleHost host)
        {
            _config = config;
            _host = host;
        }

        /// <summary>The current typed settings. Safe to read from any thread.</summary>
        public FanatecSettingsSnapshot Current
        {
            get { lock (_gate) return _current; }
        }

        /// <summary>Whether the module is in a state this device refuses to persist.</summary>
        public bool IsFaulted
        {
            get { lock (_gate) return _faulted; }
        }

        /// <summary>
        /// Applies a stored settings document. Throws when the module rejects
        /// it, leaving the previous typed and residual state untouched.
        /// </summary>
        public void Apply(JToken settings, bool isDefault)
        {
            if (!(settings is JObject source))
                throw new ArgumentException("Device settings must be a JSON object.", nameof(settings));

            // Parse outside the lock: this only reads the incoming document.
            var candidate = FanatecSettingsSnapshot.FromJson(source);

            lock (_gate)
            {
                if (!_host.Apply(source, isDefault))
                {
                    _faulted = true;
                    throw new InvalidOperationException(
                        "FanatecDeviceSettings[" + _config.Capabilities.Name + "]: the LED module " +
                        "rejected the stored settings, so they were not applied.");
                }

                // Ask the module what it now owns, so the residual keeps only
                // what nothing else will write back.
                JObject projection;
                try
                {
                    projection = _host.Capture(false, false);
                }
                catch
                {
                    // The module took the settings but cannot describe what it
                    // now holds, so there is no way to work out which parts of
                    // the document are still ours to keep. Committing either
                    // half would leave the device saving a mixture of the old
                    // and new state.
                    _faulted = true;
                    throw;
                }

                _residual = BuildResidual(source, projection);
                _current = candidate;
                _faulted = false;
            }

            RaiseChanged();
        }

        /// <summary>
        /// Composes the document to persist. Throws rather than return a
        /// partial one; SimHub leaves the existing file alone when a save
        /// fails, which is the outcome we want.
        /// </summary>
        public JObject Capture(bool forTemplate, bool forDefaultSettings)
        {
            lock (_gate)
            {
                if (_faulted)
                {
                    throw new InvalidOperationException(
                        "FanatecDeviceSettings[" + _config.Capabilities.Name + "]: refusing to save " +
                        "while the LED module holds settings it could not fully apply.");
                }

                // A failure here is transient (the editor may be mid-edit on
                // another thread), so it is not latched as a fault: the next
                // save tries again.
                var projection = _host.Capture(forTemplate, forDefaultSettings);

                // Start from what we could not interpret, so unknown settings
                // survive, then let everything authoritative overwrite it.
                var result = (JObject)_residual.DeepClone();

                foreach (var prop in projection.Properties())
                {
                    // A null channel means the module has no driver for it right
                    // now — not that the stored data should be deleted.
                    if (prop.Value == null || prop.Value.Type == JTokenType.Null)
                    {
                        if (result[prop.Name] == null)
                            result[prop.Name] = JValue.CreateNull();
                        continue;
                    }

                    result[prop.Name] = prop.Value.DeepClone();
                }

                _current.WriteTo(result);

                result["wheelType"] = _config.WheelCode ?? "";
                result["moduleType"] = _config.ModuleCode ?? "";

                return result;
            }
        }

        /// <summary>
        /// Resets to defaults. Unrecognised settings are kept: a reset of
        /// FanaBridge's own options has no business discarding another build's.
        /// </summary>
        public void LoadDefaults()
        {
            lock (_gate)
            {
                try
                {
                    _host.LoadDefaults();
                }
                catch
                {
                    // A reset that failed part way leaves the module holding a
                    // mixture of the old settings and the defaults. Persisting
                    // that would replace a good file with a state nobody chose,
                    // so the device stops saving until it is trustworthy again.
                    _faulted = true;
                    throw;
                }

                _current = FanatecSettingsSnapshot.Defaults();

                // A reset makes the module authoritative over its whole key
                // space again, including channels it currently has no driver
                // for — otherwise an old profile kept for one of those would
                // survive the reset and reappear if a later profile change gave
                // that channel a driver. Settings that belong to neither the
                // module nor this build are left alone: resetting FanaBridge's
                // options has no business discarding somebody else's.
                foreach (var root in ModuleOwnedRoots().Concat(TypedRoots).Concat(IdentityRoots))
                    _residual.Remove(root);

                _faulted = false;
            }

            RaiseChanged();
        }

        /// <summary>Replaces the display-related settings and notifies panels.</summary>
        public void UpdateDisplay(DisplaySettings display)
        {
            if (display == null)
                return;

            lock (_gate)
                _current = _current.WithDisplay(display);

            RaiseChanged();
        }

        /// <summary>Replaces the tuning-related settings and notifies panels.</summary>
        public void UpdateEncoderMode(string encoderMode)
        {
            lock (_gate)
            {
                if (string.Equals(_current.EncoderMode, encoderMode, StringComparison.Ordinal))
                    return;

                _current = _current.WithEncoderMode(encoderMode);
            }

            RaiseChanged();
        }

        /// <summary>
        /// Every root the module speaks for, whether or not it currently has
        /// anything to say for it. Read from the module itself rather than
        /// listed here, so it cannot drift from what SimHub actually emits.
        /// </summary>
        private IEnumerable<string> ModuleOwnedRoots()
        {
            try
            {
                return _host.Capture(false, false).Properties()
                    .Select(p => p.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                // Only reached on a reset the module has just accepted, so this
                // is unlikely; keeping the residual untouched is the safe way to
                // fail, since nothing here would delete data.
                SimHub.Logging.Current.Warn(
                    "FanatecDeviceSettings: could not read the module's settings after a " +
                    "reset, so unrecognised LED data was left as it was: " + ex.Message);
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Keeps only the parts of the stored document nothing else will write
        /// back. A channel the module currently reports as null stays here, so
        /// data for hardware this profile has no driver for is not deleted.
        /// </summary>
        private static JObject BuildResidual(JObject source, JObject projection)
        {
            var residual = (JObject)source.DeepClone();

            foreach (var root in TypedRoots.Concat(IdentityRoots))
                residual.Remove(root);

            foreach (var prop in projection.Properties())
            {
                if (prop.Value != null && prop.Value.Type != JTokenType.Null)
                    residual.Remove(prop.Name);
            }

            return residual;
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null)
                return;

            // Never let one subscriber's failure stop the others, or bubble
            // into the caller that just committed valid settings.
            foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler>())
            {
                try
                {
                    subscriber(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "FanatecDeviceSettings: a settings listener failed: " + ex.Message);
                }
            }
        }
    }
}
