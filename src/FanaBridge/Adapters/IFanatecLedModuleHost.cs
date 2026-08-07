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
        /// <summary>Whether this device has an LED editor at all.</summary>
        bool HasModule { get; }

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
        /// Tells the module whether anything can drive this device right now.
        /// </summary>
        /// <remarks>
        /// SimHub hides the LEDs tab's connection badge while this is false —
        /// the same thing it does for a device the user switched off, rather
        /// than claiming to be searching for hardware nobody is looking for.
        /// It also gates SimHub's own per-channel output on it.
        /// </remarks>
        void SetCanDrive(bool canDrive);

        /// <summary>
        /// Stops driving the wheel's LEDs: darkens them and lets go of the
        /// driver. Used when output stops while the hardware is still attached
        /// — otherwise the last frame stays lit and the LEDs tab keeps
        /// reporting a connection that is no longer there.
        /// </summary>
        void ClearOutput();

        /// <summary>Rebinds output to the current plugin generation.</summary>
        void RebindToCurrentGeneration();

        /// <summary>Tells the module the active profile may have changed.</summary>
        void HotSwapIfNeeded(Profiles.WheelCapabilities currentCaps);

        /// <summary>The module's dynamic button actions (brightness, etc.).</summary>
        IEnumerable<DynamicButtonAction> GetDynamicActions();

        /// <summary>Flushes module state on shutdown. Safe to call more than once.</summary>
        void FinalizeModule();
    }
}
