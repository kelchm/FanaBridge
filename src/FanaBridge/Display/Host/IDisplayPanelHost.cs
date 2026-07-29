using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.Display.Runtime;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Everything the per-device Display tab needs from its device instance, as a
    /// typed seam: the device instance implements it (explicitly — none of this
    /// belongs on its public surface) and <see cref="IDevicePanelFactory.CreateDisplayPanel"/>
    /// receives it, so the panel's ONLY window into engine state is this interface.
    /// The snapshot accessor returns the immutable envelope published from the
    /// DataUpdate thread — safe to poll from the UI thread — and every config edit
    /// goes through the v2 apply members (never a direct field write).
    ///
    /// This is the display/settings/snapshot/config surface only. The two on-demand
    /// editor catalogs (property picker, mapped roles) are separate narrow contracts —
    /// <see cref="IDisplayPropertyCatalog"/> and <see cref="IMappedRoleCatalog"/> — so a
    /// view receives only the contracts it actually uses.
    ///
    /// </summary>
    internal interface IDisplayPanelHost
    {
        /// <summary>Which display surface this device has (gates the ITM UI).</summary>
        DisplayType DisplayType { get; }

        /// <summary>The ITM display-device id (page tables are per device id).</summary>
        byte ItmDeviceId { get; }

        /// <summary>
        /// Wheel code used by <c>CatalogLoader.TryResolve</c> on the apply path
        /// (same key the runtime stamps as composition <c>DeviceKey</c>). Null/empty
        /// when unknown — callers fail closed on catalog resolution.
        /// </summary>
        string WheelCode { get; }

        /// <summary>
        /// Attached display-module code for a hub/module composite. Catalog resolution
        /// prefers this identity and falls back to <see cref="WheelCode"/>.
        /// </summary>
        string ModuleCode { get; }

        /// <summary>
        /// The current v2 document, or null when none is live.
        /// Reference-stable between edits — never mutate; build a new document and
        /// pass it to <see cref="ApplyDisplayConfigV2"/>.
        /// </summary>
        DisplayConfigV2 GetDisplayConfigV2();

        /// <summary>
        /// Publishes a UI-built v2 document through Normalize. Null clears.
        /// </summary>
        void ApplyDisplayConfigV2(DisplayConfigV2 config);

        /// <summary>
        /// Compare-and-swap publish: normalizes <paramref name="config"/> and publishes
        /// only when the live document is still <paramref name="expected"/>. Returns
        /// false when another writer published between the caller's capture and this
        /// attempt (conflict — do not overwrite). True when the publish landed.
        /// </summary>
        bool TryApplyDisplayConfigV2(DisplayConfigV2 expected, DisplayConfigV2 config);

        /// <summary>The latest display envelope (ITM status line,
        /// values snapshot), or null while this device has nothing to show — poll it,
        /// never touch live engine state. Reference equality is the "anything new?"
        /// check; the parts' references gate per-part re-renders.</summary>
        DisplayPanelSnapshot Snapshot { get; }

    }
}
