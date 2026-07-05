using FanaBridge.Adapters;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.UI;
using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FanaBridge
{
    [PluginDescription("Fanatec wheel LED and display control via HID")]
    [PluginAuthor("kelchm")]
    [PluginName("FanaBridge")]
    public class FanatecPlugin : IPlugin, IDataPlugin, IWPFSettingsV2, IReusable
    {
        /// <summary>
        /// Singleton reference so DeviceInstance wrappers can access the shared
        /// hardware without owning their own HID connections.
        /// Set during Init(), cleared during FinalizePlugin().
        /// </summary>
        public static FanatecPlugin Instance { get; private set; }

        public FanatecPluginSettings Settings { get; set; }

        /// <summary>
        /// True once the hardware core (wheelbase, encoders, connection monitor)
        /// has been built. SimHub restarts its plugin manager IN-PROCESS on every
        /// game change; because this plugin implements <see cref="IReusable"/>,
        /// SimHub adopts this same instance into the new manager and calls
        /// <see cref="Init"/> again. The core must be built exactly once and
        /// survive those restarts — SimHub's DevicesPlugin keeps our
        /// DeviceInstances alive across them, and they drive output through this
        /// instance's encoders (issue #37). Per-manager registrations
        /// (AttachDelegate/AddEvent) still re-run on every Init.
        /// </summary>
        private bool _coreInitialized;

        private FanatecWheelbase _wheelbase;
        private ConnectionMonitor _connectionMonitor;
        private FanatecTuningController _tuning;
        private LedEncoder _leds;
        private LegacyLedEncoder _legacyLeds;
        private DisplayEncoder _display;
        private ItmEncoder _itm;

        /// <summary>
        /// Experimental Control Mapper integration bridge. Lazily constructed
        /// the first time the feature is enabled (so a missing SimHub Control
        /// Mapper type can't affect plugin load), reconciled to
        /// <see cref="FanatecPluginSettings.EnableControlMapperIntegration"/> each
        /// frame in <see cref="DataUpdate"/>.
        /// </summary>
        // volatile: written on the DataUpdate thread (UpdateControlMapperIntegration),
        // read in the WheelChanged handler, which may fire from any thread. Ensures the
        // handler observes the constructed bridge; a missed read self-heals next tick.
        private volatile FanaBridge.Adapters.ControlMapperBridge _controlMapperBridge;

        /// <summary>Frame counter that throttles the Control Mapper reconcile (see <see cref="UpdateControlMapperIntegration"/>).</summary>
        private int _cmReconcileTick;

        // Live registry of this plugin's DeviceInstances (each adds itself lazily,
        // removes itself on End) so the Control Mapper integration can read the
        // connected wheel's SimHub device name — the user's rename if set, else the
        // short name — and label the mapped controller consistently with the Devices view.
        private readonly object _deviceInstancesLock = new object();
        private readonly List<Adapters.FanatecWheelDeviceInstance> _deviceInstances =
            new List<Adapters.FanatecWheelDeviceInstance>();

        /// <summary>Fired when connection status or wheel identity changes. May fire from any thread.</summary>
        public event Action StateChanged;

        /// <summary>
        /// When true, device instances skip all LED and display output so the
        /// profile wizard can send probe signals without being overwritten by
        /// SimHub's frame-by-frame updates.  Set by the wizard dialog.
        /// </summary>
        public bool WizardActive { get; set; }

        /// <summary>Whether the Fanatec device is currently connected (for UI binding).</summary>
        public bool IsDeviceConnected => _connectionMonitor?.IsConnected == true;

        /// <summary>
        /// When disconnected, a short reason for it (the latest connect-attempt
        /// failure, else the latest runtime disconnect reason); null while
        /// connected. Surfaced on the Status row and in the diagnostics capture.
        /// </summary>
        public string StatusDetail =>
            IsDeviceConnected
                ? null
                : (_wheelbase?.LastConnectError ?? _connectionMonitor?.LastDisconnectReason);

        /// <summary>Name of the connected device (for UI binding).</summary>
        public string DeviceName => _wheelbase?.ProductName ?? "Not connected";

        /// <summary>Name of the currently detected steering wheel.</summary>
        public string WheelName => _wheelbase?.DisplayName ?? "Unknown";

        /// <summary>Current wheel capabilities (for UI binding).</summary>
        public WheelCapabilities CurrentCapabilities => _wheelbase?.CurrentCapabilities ?? WheelCapabilities.None;

        /// <summary>
        /// Resolves the capabilities a specific device descriptor should use.
        /// Returns the live, currently-active capabilities (which respect any
        /// user override) only when the connected wheel actually matches this
        /// <paramref name="config"/>; otherwise returns the config's own
        /// registration capabilities. <see cref="CurrentCapabilities"/> is
        /// global, so unrelated device instances must NOT consume it directly —
        /// this is the single guard that prevents the connected wheel's caps
        /// from leaking into every descriptor.
        /// </summary>
        public WheelCapabilities ResolveCapsFor(DeviceConfig config)
        {
            if (config == null)
                return WheelCapabilities.None;

            var wheelbase = _wheelbase;
            var current = CurrentCapabilities;
            if (current?.Profile != null && wheelbase != null
                && config.MatchesAttachment(
                    wheelbase.WheelDetected, wheelbase.WheelCode, wheelbase.ModuleCode))
            {
                return current;
            }

            return config.Capabilities ?? WheelCapabilities.None;
        }

        /// <summary>Called by each <see cref="Adapters.FanatecWheelDeviceInstance"/> so the
        /// plugin can read the connected wheel's SimHub device name for the Control Mapper
        /// integration. Idempotent.</summary>
        internal void RegisterDeviceInstance(Adapters.FanatecWheelDeviceInstance instance)
        {
            if (instance == null) return;
            lock (_deviceInstancesLock)
                if (!_deviceInstances.Contains(instance))
                    _deviceInstances.Add(instance);
        }

        /// <summary>Removes a DeviceInstance from the registry (on its End).</summary>
        internal void UnregisterDeviceInstance(Adapters.FanatecWheelDeviceInstance instance)
        {
            if (instance == null) return;
            lock (_deviceInstancesLock)
                _deviceInstances.Remove(instance);
        }

        /// <summary>
        /// The SimHub device display name for the currently-connected wheel — the user's
        /// device rename if they set one, otherwise the same short name shown elsewhere in
        /// FanaBridge (<c>DeviceDescriptor.Name</c> = caps.ShortName). Null when no device
        /// instance reports Connected. Used by the Control Mapper integration so the mapped
        /// controller is labelled consistently with the Devices view.
        /// </summary>
        public string GetConnectedWheelDisplayName()
        {
            if (_wheelbase == null || !_wheelbase.WheelDetected) return null;
            lock (_deviceInstancesLock)
            {
                foreach (var inst in _deviceInstances)
                {
                    try
                    {
                        if (inst.GetDeviceState() == SimHub.Plugins.Devices.DeviceState.Connected)
                        {
                            string name = inst.MainDisplayName;
                            if (!string.IsNullOrEmpty(name)) return name;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// Whether Control Mapper's "Recognize Individual Wheels" is on (the setting the
        /// integration depends on), or null if it can't be determined. Read-only; used by
        /// the settings UI to flag the case where the integration is enabled but inert.
        /// </summary>
        public bool? IsControlMapperRecognizingIndividualWheels()
        {
            try
            {
                var bridge = _controlMapperBridge ?? new FanaBridge.Adapters.ControlMapperBridge();
                return bridge.IsRecognizeIndividualWheelsOn(PluginManager);
            }
            catch { return null; }
        }

        /// <summary>The connected wheelbase — used by DeviceInstance wrappers to query wheel identity.</summary>
        // ARCHITECTURE: single-device assumption. When multi-device support lands
        // (pedals/shifter/SRM), this becomes a DeviceManager owning a collection;
        // don't add callers that bake in "exactly one base". See the device-
        // architecture direction note (Connection / IIdentitySource / Device).
        public FanatecWheelbase Wheelbase => _wheelbase;

        /// <summary>Shared HID transport — used by DeviceInstance wrappers for hardware I/O.</summary>
        public IDeviceTransport Transport => _wheelbase?.Transport;

        /// <summary>Shared LED encoder (col03) — used by DeviceInstance LED drivers and wizard.</summary>
        public LedEncoder Leds => _leds;

        /// <summary>Shared legacy LED encoder (col01) — used by DeviceInstance LED drivers for legacy/RevStripe wheels.</summary>
        public LegacyLedEncoder LegacyLeds => _legacyLeds;

        /// <summary>Shared display encoder — used by DeviceInstance display managers and wizard.</summary>
        public DisplayEncoder Display => _display;

        /// <summary>Shared ITM encoder (col03) — used by DeviceInstance ITM display drivers.</summary>
        public ItmEncoder Itm => _itm;

        /// <summary>Shared tuning controller — used by TuningSettingsPanel for encoder config.</summary>
        public FanatecTuningController Tuning => _tuning;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => new BitmapImage(new Uri(
            "pack://application:,,,/FanaBridge;component/Resources/Images/plugin-icon.png"));

        public string LeftMenuTitle => "FanaBridge";

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info(_coreInitialized
                ? "FanaBridge: Init starting (plugin manager restart — reusing hardware core)"
                : "FanaBridge: Init starting");

            if (!_coreInitialized)
            {
                InitializeCore();
                _coreInitialized = true;
            }

            // Everything below registers with the CURRENT plugin manager, which
            // is a fresh object on every in-process restart — so it re-runs on
            // every Init, against the stable core built above.

            // --- Properties ---
            this.AttachDelegate("FanaBridge.Connected", () => _connectionMonitor.IsConnected);
            this.AttachDelegate("FanaBridge.DeviceName", () => _wheelbase.ProductName ?? "Not connected");
            this.AttachDelegate("FanaBridge.BaseName", () =>
                _wheelbase.IsConnected ? (_wheelbase.BaseCode ?? "Unknown") : "Not connected");
            this.AttachDelegate("FanaBridge.WheelName", () => _wheelbase.DisplayName);
            this.AttachDelegate("FanaBridge.ModuleName", () => _wheelbase.ModuleCode ?? "None");
            this.AttachDelegate("FanaBridge.DisplayName", () => _wheelbase.DisplayName);
            this.AttachDelegate("FanaBridge.IsHub", () => _wheelbase.IsHub);
            this.AttachDelegate("FanaBridge.WheelDetected", () => _wheelbase.WheelDetected);
            this.AttachDelegate("FanaBridge.WheelCode", () => _wheelbase.WheelCode ?? "");
            this.AttachDelegate("FanaBridge.WheelWireCode", () => (int)_wheelbase.WheelWireCode);
            this.AttachDelegate("FanaBridge.ModuleType", () => _wheelbase.ModuleCode ?? "");
            this.AttachDelegate("FanaBridge.Capabilities.ButtonLedCount", () => _wheelbase.CurrentCapabilities.ButtonLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonRgbCount", () => _wheelbase.CurrentCapabilities.ButtonRgbCount);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonAuxIntensityCount", () => _wheelbase.CurrentCapabilities.ButtonAuxIntensityCount);
            this.AttachDelegate("FanaBridge.Capabilities.TotalLedCount", () => _wheelbase.CurrentCapabilities.AllLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.DisplayType", () => _wheelbase.CurrentCapabilities.Display.ToString());

            // The friendly per-rim name FanaBridge applies as the Control Mapper
            // controller's CustomName (e.g. "Podium Hub + Button Module Rally"); empty
            // when no wheel is detected. Lets a user watch per-rim identity live on a
            // dashboard while testing the experimental Control Mapper integration.
            // Cheap (no reflection) — it only reads live wheel state.
            this.AttachDelegate("FanaBridge.ControlMapperVariant",
                () => Adapters.FanaBridgeVariantProvider.ComputeFriendlyName() ?? "");

            // --- Events ---
            this.AddEvent("DeviceConnected");
            this.AddEvent("DeviceDisconnected");
            this.AddEvent("WheelChanged");

            SimHub.Logging.Current.Info(
                $"FanaBridge: Init complete, connected={_connectionMonitor.IsConnected}");
        }

        /// <summary>
        /// One-time construction of the hardware core: wheelbase + transport,
        /// encoders, tuning controller, connection monitor, and the event
        /// subscriptions between them. Runs on the first <see cref="Init"/> only;
        /// in-process plugin manager restarts (game changes) reuse all of it.
        /// The handlers subscribed here resolve the plugin manager at fire time
        /// (via <c>this.TriggerEvent</c>), so they stay correct across restarts.
        /// </summary>
        private void InitializeCore()
        {
            Settings = this.ReadCommonSettings<FanatecPluginSettings>(
                "FanaBridgeSettings",
                () => new FanatecPluginSettings());

            // The wheelbase owns the HID transport and reads the FF 08 identity
            // report through it (no SimHub.FanatecManaged.dll). Encoders share
            // that same transport for hardware I/O.
            _wheelbase = new FanatecWheelbase();
            var transport = _wheelbase.Transport;
            _leds = new LedEncoder(transport);
            _legacyLeds = new LegacyLedEncoder(transport);
            _display = new DisplayEncoder(transport);
            _itm = new ItmEncoder(transport);
            _tuning = new FanatecTuningController(
                transport,
                msg => SimHub.Logging.Current.Warn(msg),
                msg => SimHub.Logging.Current.Info(msg));

            // Wire up profile override resolution from plugin settings
            _wheelbase.ProfileOverrideResolver = (matchKey) =>
            {
                if (string.IsNullOrEmpty(matchKey))
                    return null;
                if (Settings.ProfileOverrides != null
                    && Settings.ProfileOverrides.TryGetValue(matchKey, out var overrideId))
                    return overrideId;
                return null;
            };

            _connectionMonitor = new ConnectionMonitor(
                _wheelbase, TryConnect,
                msg => SimHub.Logging.Current.Warn(msg),
                msg => SimHub.Logging.Current.Info(msg));

            _connectionMonitor.Connected += () =>
            {
                this.TriggerEvent("DeviceConnected");
                StateChanged?.Invoke();
            };

            _connectionMonitor.Disconnected += () =>
            {
                this.TriggerEvent("DeviceDisconnected");
                StateChanged?.Invoke();
            };

            _wheelbase.WheelChanged += (manager) =>
            {
                SimHub.Logging.Current.Info("FanaBridge: Wheel changed to " + manager.DisplayName);

                // The physical rim just changed — firmware resets LED state
                // but our dirty-tracking arrays still hold the old instance's
                // last output.  Force a full resend on the next frame so the
                // new DeviceInstance's first write always reaches hardware.
                _leds.ForceDirty();
                _legacyLeds.ForceDirty();

                this.TriggerEvent("WheelChanged");
                StateChanged?.Invoke();

                // Tell Control Mapper to re-read the variant for the just-swapped
                // rim (no-op unless the experimental bridge is registered).
                _controlMapperBridge?.RequestReEnumerate();
            };

            // Publish the singleton only now that every shared field is
            // constructed. DeviceInstance wrappers reach back through
            // Instance.Wheelbase / Instance.Transport, so exposing it earlier
            // would let them observe a half-built plugin (null Wheelbase).
            Instance = this;

            // Attempt initial connection
            _connectionMonitor.TryInitialConnect();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Reconcile the Control Mapper bridge first — it must run regardless
            // of connection or game state (the user maps controllers outside of
            // a running game), so it sits ahead of the early returns below.
            UpdateControlMapperIntegration();

            if (!_connectionMonitor.Update())
                return;

            if (!data.GameRunning || data.NewData == null)
                return;
        }

        /// <summary>
        /// Reconciles the experimental Control Mapper integration bridge to the
        /// current setting. Registers (lazily constructing the bridge) when
        /// enabled, unregisters when disabled — live, no restart. Throttled so it
        /// isn't doing reflection work every single frame; the bridge itself is
        /// idempotent, so a coarse cadence is fine and keeps it responsive to the
        /// user toggling the setting or Control Mapper's "Recognize Individual
        /// Wheels".
        /// </summary>
        private void UpdateControlMapperIntegration()
        {
            // ~every 30 frames (about twice a second at typical update rates).
            if (++_cmReconcileTick < 30)
                return;
            _cmReconcileTick = 0;

            try
            {
                if (Settings.EnableControlMapperIntegration)
                {
                    if (_controlMapperBridge == null)
                        _controlMapperBridge = new FanaBridge.Adapters.ControlMapperBridge();
                    _controlMapperBridge.EnsureRegistered(PluginManager);
                    // Stamp a friendly CustomName on the connected wheel's source(s) so
                    // the UI shows a readable name while the match key stays the stable id.
                    _controlMapperBridge.StampFriendlyNames();
                }
                else
                {
                    _controlMapperBridge?.Unregister();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanaBridge: Control Mapper integration error: " + ex.Message);
            }
        }

        public void End(PluginManager pluginManager)
        {
            // Called on EVERY plugin manager stop — including the IN-PROCESS
            // restart SimHub performs on each game change — not just app exit.
            // SimHub's DevicesPlugin keeps our DeviceInstances alive across
            // those restarts and they keep driving output through this
            // instance's encoders, so the hardware core must stay up here
            // (issue #37: disposing it left every surviving DeviceInstance
            // writing into a dead transport until SimHub was restarted).
            // Real teardown lives in FinalizePlugin(), which SimHub calls only
            // at application exit or when the plugin leaves the active set.
            SimHub.Logging.Current.Info("FanaBridge: End (plugin manager stopping; hardware core stays up)");

            // The Control Mapper provider registration stays put: Control Mapper's
            // plugin is IReusable too, so both sides survive the manager restart
            // and unregistering here would only churn CM's controller list (and
            // briefly resolve FanaBridge-only wheels to no variant). Final removal
            // happens in FinalizePlugin(); the per-frame reconcile handles the
            // settings toggle.

            // Guarded because the host wraps End() and FinalizePlugin() in one
            // try/catch — a settings-persistence failure here must not cancel
            // the hardware teardown at application exit.
            try
            {
                this.SaveCommonSettings("FanaBridgeSettings", Settings);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanaBridge: Failed to save settings on End: " + ex.Message);
            }
        }

        /// <summary>
        /// Final teardown — SimHub calls this (after <see cref="End"/>) at
        /// application exit, or when the plugin is dropped from the active set
        /// (e.g. disabled by the user). Only here is it safe to dispose the
        /// hardware core: no further plugin manager will adopt this instance.
        /// </summary>
        public void FinalizePlugin()
        {
            SimHub.Logging.Current.Info("FanaBridge: FinalizePlugin (final teardown)");

            // Unpublish FIRST: device DataUpdate frames can still be in flight
            // (the host doesn't join them on a manager restart), and they must
            // observe a null Instance — not a core mid-disposal.
            if (ReferenceEquals(Instance, this))
                Instance = null;

            try { _controlMapperBridge?.Unregister(); }
            catch (Exception ex) { SimHub.Logging.Current.Debug("FanaBridge: CM unregister on finalize: " + ex.Message); }

            if (_connectionMonitor?.IsConnected == true)
            {
                try
                {
                    _display.ClearDisplay();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"FanaBridge: Cleanup error: {ex.Message}");
                }
            }

            _wheelbase?.Dispose();
        }

        /// <summary>
        /// Forces a disconnect and immediate reconnect attempt. Called from UI.
        /// </summary>
        public void ForceReconnect()
        {
            // ConnectionMonitor.ForceReconnect() already fires Connected/Disconnected,
            // which invoke StateChanged via the event subscriptions set up in Init().
            _connectionMonitor.ForceReconnect();
        }

        /// <summary>
        /// Builds a read-only, GitHub-ready diagnostics snapshot of current device
        /// detection (HID interface inventory + decoded identity + raw FF 08 frame).
        /// Called from the settings UI's "Copy Debug Info" link. Sends nothing
        /// to the device — it only re-enumerates the bus and formats held state.
        /// </summary>
        public string BuildDiagnosticsReport()
        {
            // Build the read-only Control Mapper snapshot first so it can be embedded
            // inside the report's code fence as a trailing feature section (not dangling
            // after it). It lets a user confirm, on their own hardware, whether the stock
            // Fanatec provider identifies their base (variant non-null => FanaBridge is
            // masked) or not (variant null => FanaBridge fills the gap). Uses a throwaway
            // bridge when the feature is off so the diagnostic is always available; it
            // only reads, never registers.
            string controlMapperSection;
            try
            {
                var bridge = _controlMapperBridge ?? new FanaBridge.Adapters.ControlMapperBridge();
                controlMapperSection = bridge.DescribeResolution(PluginManager);
            }
            catch (Exception ex)
            {
                controlMapperSection = "Control Mapper integration (diagnostic)" + Environment.NewLine
                    + "  diagnostic error: " + ex.Message;
            }

            var report = DiagnosticsReport.Build(
                _wheelbase, IsDeviceConnected, StatusDetail, BuildIdentity.Full, controlMapperSection);

            return report;
        }

        /// <summary>
        /// Persists the current <see cref="Settings"/> to SimHub's storage.
        /// Called from the settings UI when profile overrides change.
        /// </summary>
        public void SaveSettings()
        {
            this.SaveCommonSettings("FanaBridgeSettings", Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        private bool TryConnect()
        {
            try
            {
                bool connected;
                if (Settings.ProductIdOverride != 0)
                {
                    SimHub.Logging.Current.Info($"FanaBridge: Using PID override 0x{Settings.ProductIdOverride:X4}");
                    connected = _wheelbase.Connect(Settings.ProductIdOverride);
                }
                else
                {
                    connected = _wheelbase.AutoConnect();
                }

                if (!connected)
                    return false;

                SimHub.Logging.Current.Info($"FanaBridge: Connected to {_wheelbase.ProductName} (PID 0x{_wheelbase.ConnectedProductId:X4})");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"FanaBridge: Connection failed: {ex.Message}");
                return false;
            }
        }
    }
}
