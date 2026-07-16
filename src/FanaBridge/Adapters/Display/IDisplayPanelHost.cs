using System.Collections.Generic;
using FanaBridge.Customization;
using FanaBridge.Profiles;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Everything the per-device Display tab needs from its device instance, as a
    /// typed seam: the device instance implements it (explicitly — none of this
    /// belongs on its public surface) and <see cref="IDevicePanelFactory.CreateDisplayPanel"/>
    /// receives it, so the panel's ONLY window into engine state is this interface.
    /// The snapshot accessor returns the immutable envelope published from the
    /// DataUpdate thread — safe to poll from the UI thread — and every config edit
    /// goes through <see cref="ApplyDisplayConfig"/> (never a direct field write).
    /// </summary>
    internal interface IDisplayPanelHost
    {
        /// <summary>The device's live display settings — the panel mutates this
        /// instance directly and calls <see cref="NotifySettingsChanged"/>, exactly
        /// the old Screen panel's contract.</summary>
        DisplaySettings DisplaySettings { get; }

        /// <summary>Which display surface this device has (gates the ITM UI).</summary>
        DisplayType DisplayType { get; }

        /// <summary>The ITM display-device id (page tables are per device id).</summary>
        byte ItmDeviceId { get; }

        /// <summary>The current customization config snapshot, or null when none is
        /// active. Reference-stable between edits — never mutate the returned
        /// instance; build a new document and pass it to
        /// <see cref="ApplyDisplayConfig"/>.</summary>
        DisplayCustomizationConfig GetDisplayConfig();

        /// <summary>Publishes a UI-built config through the device's normalization
        /// path. The frame path rebuilds the rule stack; SimHub persists via
        /// GetSettings on its schedule.</summary>
        void ApplyDisplayConfig(DisplayCustomizationConfig config);

        /// <summary>The latest display envelope (ITM status line, rule snapshot,
        /// values snapshot), or null while this device has nothing to show — poll it,
        /// never touch live engine state. Reference equality is the "anything new?"
        /// check; the parts' references gate per-part re-renders.</summary>
        DisplayPanelSnapshot Snapshot { get; }

        /// <summary>Called by the panel after any user settings change, so the device
        /// instance syncs <see cref="DisplaySettings"/> back into its persisted
        /// settings — the same callback contract the old Screen panel had.</summary>
        void NotifySettingsChanged();

        /// <summary>Every SimHub property name the picker can offer, from
        /// <c>PluginManager.GetAllPropertiesNames()</c>. Defensively wrapped (null plugin
        /// manager or an exception yields an empty list). Fetched on demand when the
        /// picker opens — never per frame; the list can hold thousands of names.</summary>
        IReadOnlyList<string> GetAllPropertyNames();

        /// <summary>Control Mapper roles for THIS rim, for the mapped-control add flow.
        /// Reads the wheel's own button→role mappings when present (marked
        /// <see cref="MappedRolesSource.MappedOnThisWheel"/>), else falls back to the
        /// sanctioned role catalog (<see cref="MappedRolesSource.AllRoles"/>), else empty
        /// (<see cref="MappedRolesSource.None"/>) — so the UI can hint "mapped on this
        /// wheel" vs "all roles". Read-only; no Control Mapper writes anywhere. Fetched on
        /// demand (dropdown open).</summary>
        MappedRoles GetMappedRoles();
    }
}
