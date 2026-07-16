using System;
using FanaBridge.Display;
using FanaBridge.Profiles;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Everything the per-device Display tab needs from its device instance, bundled so
    /// <see cref="IDevicePanelFactory.CreateDisplayPanel"/> stays a one-argument seam as
    /// the tab grows. Built by <see cref="FanatecWheelDeviceInstance.GetSettingsControls"/>;
    /// the panel must treat the delegates as its ONLY window into engine state — the
    /// getters return volatile snapshots safe to read from the UI thread, and every
    /// config edit goes through <see cref="ApplyConfig"/> (never a direct field write).
    /// </summary>
    internal sealed class DisplayPanelContext
    {
        /// <summary>The device's live display settings — the panel mutates this instance
        /// directly and fires <see cref="SettingsChanged"/>, exactly the old Screen
        /// panel's contract.</summary>
        public DisplaySettings DisplaySettings { get; set; }

        /// <summary>Which display surface this device has (gates the ITM UI).</summary>
        public DisplayType DisplayType { get; set; }

        /// <summary>The ITM display-device id (page tables are per device id).</summary>
        public byte ItmDeviceId { get; set; }

        /// <summary>The current customization config snapshot, or null when none is
        /// active. Reference-stable between edits — never mutate the returned instance;
        /// build a new document and pass it to <see cref="ApplyConfig"/>.</summary>
        public Func<DisplayCustomizationConfig> GetConfig { get; set; }

        /// <summary>Publishes a UI-built config through the device's normalization path
        /// (<see cref="FanatecWheelDeviceInstance.ApplyDisplayConfig"/>). The frame path
        /// rebuilds the rule stack; SimHub persists via GetSettings on its schedule.</summary>
        public Action<DisplayCustomizationConfig> ApplyConfig { get; set; }

        /// <summary>The latest rule-stack snapshot, or null while no customization is
        /// active — poll it, never touch live engine state.</summary>
        public Func<DisplayRuleSnapshot> GetSnapshot { get; set; }

        /// <summary>The ITM lifecycle status line, or null when this device isn't
        /// driving an ITM display.</summary>
        public Func<string> GetItmStatus { get; set; }

        /// <summary>Fired by the panel after any user settings change, so the instance
        /// syncs <see cref="DisplaySettings"/> back into its persisted JObject — the
        /// same callback contract the old Screen panel had.</summary>
        public Action SettingsChanged { get; set; }
    }
}
