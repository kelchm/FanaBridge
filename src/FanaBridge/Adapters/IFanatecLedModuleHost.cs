using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// The LED settings module, behind an interface so persistence can be
    /// exercised without a running SimHub host.
    /// </summary>
    /// <remarks>
    /// SimHub's LED module reaches into the running host as soon as settings
    /// are applied, so the real implementation cannot run in a unit test. The
    /// persistence rules around it — what a save contains, what happens when a
    /// payload is rejected — are exactly what the settings-wipe incident was
    /// about, so they are the part that most needs test coverage.
    ///
    /// The host owns both the module and its manager, and is responsible for
    /// disposing them: the manager subscribes to a static USB-change event that
    /// only Dispose removes, and SimHub never disposes it for us.
    /// </remarks>
    internal interface IFanatecLedModuleHost : IDisposable
    {
        /// <summary>The LEDs tab, or null for a device without LEDs.</summary>
        Control EditControl { get; }

        /// <summary>
        /// Applies a settings document. Returns false when the module did not
        /// fully consume it, leaving the module partially populated.
        /// </summary>
        bool Apply(JObject source, bool isDefault);

        /// <summary>
        /// Serializes the module's current state. Throws if serialization
        /// fails — callers must not persist a partial document.
        /// </summary>
        JObject Capture(bool forTemplate, bool forDefaultSettings);

        /// <summary>Resets the module to its defaults.</summary>
        void LoadDefaults();

        /// <summary>Drives one frame of LED output.</summary>
        void Display();

        /// <summary>
        /// Tells the module whether anything can drive this device right now,
        /// and whether the wheel is actually there.
        /// </summary>
        /// <remarks>
        /// Both drive the LEDs tab's connection badge: it is hidden entirely
        /// while nothing can drive the device — what SimHub shows for a device
        /// the user switched off — and otherwise reports connected or
        /// searching. The module caches the second value and only refreshes it
        /// while it is driving output, which is exactly when it cannot notice
        /// the wheel leaving, so it has to be told.
        /// </remarks>
        void SetStatus(bool canDrive, bool connected);

        /// <summary>
        /// Stops driving the wheel's LEDs: darkens them and lets go of the
        /// driver.
        /// </summary>
        /// <remarks>
        /// Used both when output stops while the hardware is still attached —
        /// otherwise the last frame stays lit and the LEDs tab keeps reporting
        /// a connection that is no longer there — and when the plugin
        /// generation is replaced, where dropping the driver is what makes the
        /// next frame build one against the current hardware core.
        /// </remarks>
        void StopDriving();

        /// <summary>Tells the module the active profile may have changed.</summary>
        void HotSwapIfNeeded(Profiles.WheelCapabilities currentCaps);

        /// <summary>The module's dynamic button actions (brightness, etc.).</summary>
        IEnumerable<DynamicButtonAction> GetDynamicActions();
    }
}
