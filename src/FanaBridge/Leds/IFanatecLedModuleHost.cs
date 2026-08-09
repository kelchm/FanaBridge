using System;
using System.Collections.Generic;
using System.Windows.Controls;
using FanaBridge.Core.Devices.Profiles;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;

namespace FanaBridge.Leds
{
    /// <summary>
    /// The LED settings module, behind an interface so persistence is testable
    /// (the real module reaches into the running host as soon as settings are
    /// applied). The host owns module and manager, including their disposal —
    /// both subscribe to static events only Dispose removes.
    /// </summary>
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

        /// <summary>
        /// Resets channel profiles to defaults. Module-level values (brightness
        /// etc.) survive — SimHub's own reset behaves the same.
        /// </summary>
        void LoadDefaults();

        /// <summary>Drives one frame of LED output.</summary>
        void Display();

        /// <summary>
        /// Drives the LEDs tab's connection badge: hidden while nothing can
        /// drive the device, else connected/searching. The module cannot work
        /// the second value out itself — it only refreshes while driving.
        /// </summary>
        void SetStatus(bool canDrive, bool connected);

        /// <summary>
        /// Darkens the LEDs and lets go of the driver — on output stopping and
        /// on generation replacement (dropping the driver is the rebind).
        /// </summary>
        void StopDriving();

        /// <summary>Tells the module the active profile may have changed.</summary>
        void HotSwapIfNeeded(WheelCapabilities currentCaps);

        /// <summary>The module's dynamic button actions (brightness, etc.).</summary>
        IEnumerable<DynamicButtonAction> GetDynamicActions();
    }
}
