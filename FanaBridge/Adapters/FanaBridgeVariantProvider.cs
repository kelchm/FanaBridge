using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Reports the currently-attached Fanatec rim to SimHub's Control Mapper as an
    /// <see cref="IVariantProvider"/> variant string, so Control Mapper can key
    /// per-rim button mappings off <c>(VID, PID, Variant)</c> instead of
    /// <c>(VID, PID)</c> alone.
    ///
    /// Every Fanatec rim connects through one wheelbase USB endpoint and shares the
    /// base's product id, so DirectInput sees them all as the same controller —
    /// without a variant, swapping rims inherits the previous rim's mappings.
    /// SimHub's bundled providers handle this for gear the stale
    /// <c>SimHub.FanatecManaged.dll</c> recognizes; FanaBridge fills the gap for what
    /// it can't — Podium DD bases and newer wheels (e.g. ClubSport Formula V3) it has
    /// never heard of.
    ///
    /// <para><b>Identity vs display.</b> The variant is a stable, opaque-ish ID, not a
    /// display name: the rim's <em>stock-compatible</em> <c>"FS_WHEEL_SWTYPE_&lt;code&gt;"</c>
    /// string — the same identifier SimHub's own provider uses (or would use once its
    /// DLL learns the wheel). Emitting the SAME id as stock means a wheel mapped via
    /// FanaBridge shares ONE key with stock: no fragmentation, and the mapping keeps
    /// working if SimHub is later updated to recognize the wheel. The human-readable
    /// name is applied separately, as the controller's <c>CustomName</c> (see
    /// <see cref="ControlMapperBridge"/>), keeping the match key stable while the UI
    /// still shows something friendly.</para>
    ///
    /// <para><b>Registration.</b> <see cref="ControlMapperBridge"/> appends this provider
    /// AFTER the stock Fanatec provider. Control Mapper resolves with
    /// <c>FirstOrDefault(v =&gt; v != null)</c>, so stock wins for any wheel it
    /// recognizes and this provider answers only where stock returns <c>null</c> —
    /// filling the gap without disturbing anything stock already handles.</para>
    /// </summary>
    public class FanaBridgeVariantProvider : IVariantProvider
    {
        /// <summary>
        /// Fanatec USB vendor id (0x0EB7 = 3767). Matches the value the stock
        /// <c>FanatecVariantProvider</c> gates on, and
        /// <see cref="Transport.FanatecWheelbase.FANATEC_VENDOR_ID"/>.
        /// </summary>
        public const int FanatecVendorId = 0x0EB7;

        /// <summary>The prefix SimHub's <c>M_FS_WHEEL_SWTYPE</c> enum members carry; we
        /// reproduce the full member-name string so our ids match stock's byte-for-byte.</summary>
        private const string StockPrefix = "FS_WHEEL_SWTYPE_";

        /// <summary>
        /// FanaBridge wheel/hub codes equal stock's <c>M_FS_WHEEL_SWTYPE</c> member
        /// suffixes 1:1, except these. Codes stock never had (e.g. CSSWFORMV3, CSSWPVGT,
        /// CSLSWGT3, WHEELHUB) have no stock equivalent and are emitted as-is in stock
        /// format. Module codes (PBME/PBMR) already match stock's submodule suffixes.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> StockWheelSuffixOverrides =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "PSWBENT", "BENTLEY" },   // FanaBridge code -> stock enum member suffix
            };

        /// <summary>
        /// Raised when the resolved variant changes. The stock <c>VariantHelper</c> only
        /// subscribes to providers present when its list is first built, so a late-added
        /// provider's event isn't propagated automatically — the bridge drives Control
        /// Mapper re-enumeration directly off FanaBridge's <c>WheelChanged</c> signal.
        /// Kept for interface completeness and any future subscriber.
        /// </summary>
        public event EventHandler VariantChanged;

        private string _lastVariant;

        /// <summary>
        /// Control Mapper's entry point. Returns the stable per-rim id for the Fanatec
        /// vendor id when a wheel is detected; <c>null</c> otherwise (which lets the next
        /// provider, or no variant at all, decide).
        /// </summary>
        public string GetVariant(int vendorid, int productid)
        {
            if (vendorid != FanatecVendorId)
                return null;
            return ComputeCurrentVariant();
        }

        /// <summary>
        /// The variant id FanaBridge emits for the live wheel — stock-compatible
        /// <c>"FS_WHEEL_SWTYPE_&lt;code&gt;"</c> — or <c>null</c> when no base/wheel is
        /// present. Shared by <see cref="GetVariant"/> and the bridge.
        /// </summary>
        internal static string ComputeCurrentVariant()
        {
            var wheelbase = FanatecPlugin.Instance?.Wheelbase;
            if (wheelbase == null || !wheelbase.IsConnected || !wheelbase.WheelDetected)
                return null;
            return FormatStockVariant(wheelbase.WheelCode, wheelbase.ModuleCode);
        }

        /// <summary>
        /// The human-readable name for the live wheel (applied as the controller's
        /// <c>CustomName</c> for display), or <c>null</c> when no wheel is present.
        /// </summary>
        internal static string ComputeFriendlyName()
        {
            var wheelbase = FanatecPlugin.Instance?.Wheelbase;
            if (wheelbase == null || !wheelbase.IsConnected || !wheelbase.WheelDetected)
                return null;
            // Normalize on the short name FanaBridge shows elsewhere (the device's
            // DeviceDescriptor.Name = caps.ShortName ?? caps.Name). Cheap (no device
            // lookup) so it's safe per-frame. Falls back to a composed friendly name,
            // then the raw code. (The SimHub device's own name — incl. a user rename —
            // is preferred at stamp time; see ControlMapperBridge.StampFriendlyNames.)
            var caps = wheelbase.CurrentCapabilities;
            string shortName = caps?.ShortName ?? caps?.Name;
            if (!string.IsNullOrEmpty(shortName))
                return shortName;
            return FormatFriendlyName(wheelbase.WheelCode, wheelbase.ModuleCode);
        }

        /// <summary>
        /// Builds the stock-compatible variant id: <c>"FS_WHEEL_SWTYPE_&lt;wheel&gt;[_&lt;module&gt;]"</c>.
        /// Wheel codes map to stock enum suffixes 1:1 except
        /// <see cref="StockWheelSuffixOverrides"/>; codes stock never had are emitted
        /// as-is. Pure and side-effect-free for unit testing. Returns <c>null</c> for a
        /// missing wheel code. Hubs append the module; bare rims do not.
        /// </summary>
        internal static string FormatStockVariant(string wheelCode, string moduleCode)
        {
            if (string.IsNullOrEmpty(wheelCode))
                return null;
            string suffix = StockWheelSuffixOverrides.TryGetValue(wheelCode, out var mapped)
                ? mapped
                : wheelCode;
            return string.IsNullOrEmpty(moduleCode)
                ? StockPrefix + suffix
                : StockPrefix + suffix + "_" + moduleCode;
        }

        /// <summary>
        /// Builds the human-readable name (for <c>CustomName</c> / display): friendly
        /// wheel name, plus <c>" + "</c> friendly module for a hub. Falls back to the raw
        /// code when a name isn't in the tables. Pure and side-effect-free. Returns
        /// <c>null</c> for a missing wheel code.
        /// </summary>
        internal static string FormatFriendlyName(string wheelCode, string moduleCode)
        {
            if (string.IsNullOrEmpty(wheelCode))
                return null;
            string wheel = FanatecIdentity.FriendlyAttachment(wheelCode) ?? wheelCode;
            if (string.IsNullOrEmpty(moduleCode))
                return wheel;
            string module = FanatecIdentity.FriendlyModule(moduleCode) ?? moduleCode;
            return wheel + " + " + module;
        }

        /// <summary>
        /// True if <paramref name="candidate"/> is a name FanaBridge itself would have
        /// stamped for the currently-connected wheel — its short name
        /// (<c>caps.ShortName</c> / <c>caps.Name</c>) or the composed friendly name. Lets
        /// the Control Mapper stamp refresh a prior FanaBridge default (so a device rename
        /// propagates, and older composed stamps migrate to the short name) while never
        /// touching a name the user typed in Control Mapper.
        /// </summary>
        internal static bool IsFanaBridgeDefaultName(string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
                return false;
            var wheelbase = FanatecPlugin.Instance?.Wheelbase;
            if (wheelbase == null || !wheelbase.WheelDetected)
                return false;
            var caps = wheelbase.CurrentCapabilities;
            return NameEquals(candidate, caps?.ShortName)
                || NameEquals(candidate, caps?.Name)
                || NameEquals(candidate, FormatFriendlyName(wheelbase.WheelCode, wheelbase.ModuleCode));
        }

        private static bool NameEquals(string a, string b)
            => !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.Ordinal);

        /// <summary>
        /// Re-resolves the current variant and fires <see cref="VariantChanged"/> on a
        /// transition. Cheap; safe to call when no wheel is present.
        /// </summary>
        internal void Poll()
        {
            string current = ComputeCurrentVariant();
            if (current == _lastVariant)
                return;
            _lastVariant = current;
            try
            {
                VariantChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanaBridge: Control Mapper VariantChanged subscriber threw: " + ex.Message);
            }
        }
    }
}
