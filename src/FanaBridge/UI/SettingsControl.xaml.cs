using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using FanaBridge.Adapters;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.Updater;
using SimHub.Plugins.Devices;
using Timer = System.Timers.Timer;

namespace FanaBridge.UI
{
    public partial class SettingsControl : UserControl
    {
        public FanatecPlugin Plugin { get; }

        /// <summary>Suppresses ComboBox SelectionChanged while we're programmatically populating.</summary>
        private bool _suppressProfileChange;

        /// <summary>
        /// Capabilities that the SimHub LED module was built from at startup.
        /// Used to detect whether a profile switch requires a restart (e.g.
        /// LED count or display type changed).
        /// </summary>
        private WheelCapabilities _bootCaps;
        private bool _restartPromptDismissed;

        /// <summary>
        /// DeviceTypeID the add-device prompt's button would add; null while
        /// the prompt is hidden or the add is blocked by a similar device.
        /// </summary>
        private string _promptDeviceTypeId;

        /// <summary>
        /// SimHub's device collection, watched while this control is loaded so
        /// the add-device prompt tracks devices added/removed outside this page.
        /// </summary>
        private ObservableCollection<DeviceInstance> _watchedDevices;

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(FanatecPlugin plugin) : this()
        {
            Plugin = plugin;
            DataContext = plugin.Settings;
            SetAboutInfo();

            // Subscribe/unsubscribe symmetrically so tab switches don't lose the handler
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void SetAboutInfo()
        {
            var versionText = FindName("txtPluginVersion") as TextBlock;
            if (versionText != null)
                versionText.Text = "FanaBridge " + BuildIdentity.Version;

            var buildText = FindName("txtBuildInfo") as TextBlock;
            if (buildText != null)
                buildText.Text = FormatBuildInfo(BuildIdentity.Configuration, BuildIdentity.CommitHash);
        }

        private static string FormatBuildInfo(string config, string commit)
        {
            if (config != null && commit != null)
                return $"{config} \u00b7 {commit}";
            if (config != null)
                return config;
            if (commit != null)
                return commit;
            return "\u2014";
        }

        private static string FormatCapabilities(WheelCapabilities caps)
        {
            var parts = new List<string>();

            if (caps.RevRgbCount > 0)
                parts.Add(caps.RevRgbCount + " rev RGB");
            if (caps.FlagRgbCount > 0)
                parts.Add(caps.FlagRgbCount + " flag RGB");
            if (caps.ButtonRgbCount > 0)
                parts.Add(caps.ButtonRgbCount + " button RGB");
            if (caps.ButtonAuxIntensityCount > 0)
                parts.Add(caps.ButtonAuxIntensityCount + " button aux");
            if (caps.LegacyRevOnOffCount > 0)
                parts.Add(caps.LegacyRevOnOffCount + " legacy rev on/off");
            if (caps.LegacyRevStripeCount > 0)
                parts.Add(caps.LegacyRevStripeCount + " legacy rev stripe");
            if (caps.LegacyRev3BitCount > 0)
                parts.Add(caps.LegacyRev3BitCount + " legacy rev 3-bit");
            if (caps.LegacyFlag3BitCount > 0)
                parts.Add(caps.LegacyFlag3BitCount + " legacy flag 3-bit");
            if (caps.HasEncoders)
                parts.Add("encoders");
            if (caps.Display != DisplayType.None)
                parts.Add("display: " + caps.Display.ToString().ToLowerInvariant());

            return parts.Count > 0 ? string.Join(", ", parts) : "None";
        }

        // =====================================================================
        // DEVICE CHAIN  (wheelbase › wheel/hub › module)
        //
        // A normal-user readout: friendly product names laid out as the physical
        // device stack. Empty slots disappear (a plain wheel shows no module
        // node). Unrecognized hardware falls back to the raw 0xNN byte so it is
        // still visible/reportable; the full codes + bytes live in the Copy
        // Debug Info report.
        // =====================================================================

        private enum LinkState { Connected, Connecting, Disconnected }

        private static readonly Brush DotConnected = MakeBrush(0x4C, 0xAF, 0x50);  // green
        private static readonly Brush DotConnecting = MakeBrush(0xE0, 0xA8, 0x00); // amber
        private static readonly Brush DotIdle = MakeBrush(0x99, 0x99, 0x99);       // gray
        private static readonly Brush DotError = MakeBrush(0xE0, 0x5A, 0x50);      // red — broken, needs the log

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void SetDot(LinkState state)
        {
            dotStatus.Fill =
                state == LinkState.Connected  ? DotConnected :
                state == LinkState.Connecting ? DotConnecting :
                                                DotIdle;
        }

        // Connected: the wheelbase node carries the dot + base name; the wheel and
        // module nodes follow what's attached (module only on a hub that has one).
        private void ShowConnectedChain(FanatecWheelbase wb)
        {
            SetDot(LinkState.Connected);
            txtBaseName.Text = ChainBaseText(wb);
            txtBaseCaption.Text = "Wheelbase";
            txtBaseCaption.ToolTip = null;

            connBaseWheel.Visibility = nodeWheel.Visibility = Visibility.Visible;
            if (wb.WheelDetected)
            {
                txtWheelChain.Text = ChainAttachmentText(wb);
                txtWheelKind.Text = wb.IsHub ? "Hub" : "Wheel";
                txtWheelKind.Visibility = Visibility.Visible;
            }
            else
            {
                txtWheelChain.Text = "(no wheel attached)";
                txtWheelKind.Visibility = Visibility.Collapsed;
            }

            bool showModule = wb.WheelDetected && wb.IsHub
                && (wb.ModuleCode != null || wb.ModuleWireCode != 0);
            connWheelModule.Visibility = nodeModule.Visibility =
                showModule ? Visibility.Visible : Visibility.Collapsed;
            if (showModule)
                txtModuleChain.Text = ChainModuleText(wb);
        }

        // Connecting / disconnected: only the wheelbase node shows — carrying the
        // dot, a headline, and an optional reason in its caption. Keeping the node
        // present means the panel height does not change between states.
        private void ShowBaseOnly(LinkState state, string headline, string caption)
        {
            SetDot(state);
            txtBaseName.Text = headline;
            txtBaseCaption.Text = string.IsNullOrEmpty(caption) ? "Wheelbase" : caption;
            txtBaseCaption.ToolTip = string.IsNullOrEmpty(caption) ? null : caption;

            connBaseWheel.Visibility = nodeWheel.Visibility = Visibility.Collapsed;
            connWheelModule.Visibility = nodeModule.Visibility = Visibility.Collapsed;
        }

        // Full-width wrapping line for the connection reason (e.g. "no col03 …"); the
        // device-chain caption is too narrow for it and would overflow the row.
        private void SetStatusDetail(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                txtStatusDetail.Text = "";
                txtStatusDetail.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtStatusDetail.Text = text;
                txtStatusDetail.Visibility = Visibility.Visible;
            }
        }

        // Reflects the Control Mapper integration's hard dependency — SimHub's own
        // "Recognize Individual Wheels" — so an enabled-but-inert feature isn't a silent
        // no-op. Shown only when the integration is enabled; the amber warning is the
        // only attention state, the "on" confirmation stays muted, and an indeterminate
        // state (Control Mapper not loaded / internals unavailable) stays hidden.
        private void UpdateControlMapperStatus()
        {
            if (Plugin == null || txtControlMapperStatus == null) return;

            if (!Plugin.Settings.EnableControlMapperIntegration)
            {
                txtControlMapperStatus.Visibility = Visibility.Collapsed;
                return;
            }

            // Given-up beats everything: the checkbox is on but the integration is
            // dead (a SimHub update changed the internals the bridge reflects
            // into). Without this branch that state renders exactly like "Control
            // Mapper not installed" — invisible — and the first symptom users see
            // is per-rim mappings silently not following the rim.
            if (Plugin.IsControlMapperIntegrationGivenUp)
            {
                txtControlMapperStatus.Text =
                    "Control Mapper integration unavailable — SimHub internals changed "
                    + "(see the SimHub log). Mappings still work, but won't follow rim changes.";
                txtControlMapperStatus.Foreground = DotError;
                txtControlMapperStatus.Visibility = Visibility.Visible;
                return;
            }

            bool? riw = Plugin.IsControlMapperRecognizingIndividualWheels();
            if (riw == false)
            {
                txtControlMapperStatus.Text =
                    "Control Mapper's \"Recognize Individual Wheels\" is off — turn it on "
                    + "(Control Mapper → Settings) for per-rim mapping to take effect.";
                txtControlMapperStatus.Foreground = DotConnecting; // amber — needs action
                txtControlMapperStatus.Visibility = Visibility.Visible;
            }
            else if (riw == true)
            {
                txtControlMapperStatus.Text = "Active — Control Mapper is recognizing individual wheels.";
                txtControlMapperStatus.Foreground = DotIdle;       // muted — all good
                txtControlMapperStatus.Visibility = Visibility.Visible;
            }
            else
            {
                txtControlMapperStatus.Visibility = Visibility.Collapsed; // unknown — stay quiet
            }
        }

        // friendly name → code → raw byte, so an unmapped device still shows.
        private static string ChainBaseText(FanatecWheelbase wb)
            => wb.BaseFriendlyName
            ?? wb.BaseCode
            ?? (wb.BaseType != 0 ? string.Format("Unknown Base (0x{0:X2})", wb.BaseType) : "Unknown Base");

        private static string ChainAttachmentText(FanatecWheelbase wb)
            => wb.AttachmentFriendlyName
            ?? wb.WheelCode
            ?? (wb.WheelWireCode == 0xFF
                ? "Unrecognized (0xFF)"
                : string.Format("Unrecognized (0x{0:X2})", wb.WheelWireCode));

        private static string ChainModuleText(FanatecWheelbase wb)
            => wb.ModuleFriendlyName
            ?? wb.ModuleCode
            ?? string.Format("Unknown (0x{0:X2})", wb.ModuleWireCode);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Plugin.StateChanged += OnPluginStateChanged;
            Plugin.UpdateStateChanged += OnUpdateStateChanged;

            // Capture the capabilities the LED module was built from at startup.
            // Only set once — tab reloads must not clobber the baseline.
            if (_bootCaps == null)
                _bootCaps = Plugin.CurrentCapabilities;

            // The ITM lifecycle moves without StateChanged firing (page switches,
            // recovery rungs, game exits) — poll its status row while visible.
            if (_itmStatusTimer == null)
            {
                _itmStatusTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _itmStatusTimer.Tick += (s, a) => UpdateItmStatus();
            }
            _itmStatusTimer.Start();

            UpdateStatus();

            // Events only cover FUTURE transitions — the startup update check
            // normally finishes long before this page is first opened, so render
            // the current snapshot immediately.
            UpdateUpdateBanner();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopScroll();
            StopPromptRetry();
            UnwatchSimHubDevices();
            _itmStatusTimer?.Stop();
            Plugin.StateChanged -= OnPluginStateChanged;
            Plugin.UpdateStateChanged -= OnUpdateStateChanged;
        }

        private System.Windows.Threading.DispatcherTimer _itmStatusTimer;

        // ITM display row + co-driver warning. Self-contained (hides itself when no ITM
        // display is being driven), safe on every connection state.
        private void UpdateItmStatus()
        {
            if (Plugin == null || panelItmStatus == null) return;

            string itm = null;
            string warn = null;
            try
            {
                itm = Plugin.ItmStatus;
                warn = itm != null ? Plugin.ItmCoDriverWarning : null;
            }
            catch { }

            panelItmStatus.Visibility = itm == null ? Visibility.Collapsed : Visibility.Visible;
            txtItmStatus.Text = itm ?? "—";
            // Only carry the warning glyph when there is a warning — a collapsed element still
            // exposes its Text to automation/accessibility trees, so don't leave a stray "⚠".
            txtItmCoDriver.Text = warn == null ? "" : "⚠  " + warn;
            txtItmCoDriver.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnPluginStateChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        private void UpdateStatus()
        {
            if (Plugin == null) return;

            // Independent of device connection (Control Mapper is configured with or
            // without a wheel attached), so it runs ahead of the connection returns.
            UpdateControlMapperStatus();

            // Also self-contained (hides itself while disconnected), so it runs
            // ahead of the early returns below and stays correct on every path.
            UpdateAddDevicePrompt();
            UpdateItmStatus();

            if (!Plugin.IsDeviceConnected)
            {
                ShowBaseOnly(LinkState.Disconnected, "Not Connected", null);
                SetStatusDetail(Plugin.StatusDetail);
                txtCapabilities.Text = "—";
                borderUnverifiedAlert.Visibility = Visibility.Collapsed;
                UpdateProfilePicker(false, null, null, null);
                return;
            }

            var wheelbase = Plugin.Wheelbase;

            // Connected, but the FF 08 identity hasn't been committed yet (the base
            // is still being read). Show the transitional state rather than a
            // misleading "Unknown Base" flash.
            if (!wheelbase.HasIdentity)
            {
                ShowBaseOnly(LinkState.Connecting, "Connecting…", null);
                SetStatusDetail(null);
                txtCapabilities.Text = "—";
                borderUnverifiedAlert.Visibility = Visibility.Collapsed;
                UpdateProfilePicker(false, null, null, null);
                return;
            }

            var caps = Plugin.CurrentCapabilities;
            bool identified = caps.Name != null;

            // The chain shows what's attached whether or not a profile matched.
            ShowConnectedChain(wheelbase);
            SetStatusDetail(null);

            txtCapabilities.Text = identified ? FormatCapabilities(caps) : "—";

            // Unverified-profile banner only applies once a profile is matched.
            borderUnverifiedAlert.Visibility = (identified && !caps.Verified)
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Pass caps only when identified; an unrecognized-but-attached wheel
            // still shows the picker so the New Profile Wizard stays reachable.
            UpdateProfilePicker(
                wheelbase.WheelDetected, wheelbase.WheelCode, wheelbase.ModuleCode,
                identified ? caps : null);
        }

        // =====================================================================
        // ADD-DEVICE PROMPT
        //
        // LEDs and display output only start once the user adds the wheel's
        // device entry in SimHub — a step many users miss after installing the
        // plugin. Whenever the attached wheel resolves to a registered
        // descriptor with no added device yet, show a banner with a one-click
        // add. Added-ness lives in SimHub's Devices plugin, so besides
        // StateChanged this also watches SimHub's device collection while the
        // page is visible.
        // =====================================================================

        private void UpdateAddDevicePrompt()
        {
            _promptDeviceTypeId = null;

            var wheelbase = Plugin?.Wheelbase;
            if (wheelbase == null || !Plugin.IsDeviceConnected || !wheelbase.HasIdentity
                || !wheelbase.WheelDetected)
            {
                // Every way out of these states (connect/disconnect, identity
                // commit) raises StateChanged, so no re-poll is needed.
                StopPromptRetry();
                borderAddDeviceAlert.Visibility = Visibility.Collapsed;
                return;
            }

            if (!wheelbase.IdentityStable)
            {
                // The unstable→stable edge is silent when the identity settles
                // back UNCHANGED (nothing commits, so no StateChanged) — re-poll
                // until stable or the banner could stay hidden indefinitely
                // after an update landed inside the settle window.
                ArmPromptRetry();
                borderAddDeviceAlert.Visibility = Visibility.Collapsed;
                return;
            }

            StopPromptRetry();

            var devices = SimHubDevicesGateway.Resolve(Plugin.PluginManager);
            if (devices != null)
                WatchSimHubDevices(devices);

            var config = FanatecDevicesRegistry.FindConfigForAttachment(
                wheelbase.WheelDetected, wheelbase.WheelCode, wheelbase.ModuleCode);

            // Stay quiet when there is nothing addable: no matching registered
            // config, a config whose descriptor SimHub doesn't know (a profile
            // created this session — the restart notice owns that message), or
            // the device is already added.
            if (config == null || devices == null
                || !SimHubDevicesGateway.HasDescriptor(devices, config.DeviceTypeId)
                || SimHubDevicesGateway.IsDeviceAdded(devices, config.DeviceTypeId))
            {
                borderAddDeviceAlert.Visibility = Visibility.Collapsed;
                return;
            }

            string name = config.Capabilities?.ShortName ?? config.Capabilities?.Name ?? "This device";

            txtAddDeviceTitle.Text = name + " isn't added to SimHub yet";
            txtAddDeviceDetail.Text = "SimHub only sends output to devices in its device list.";
            btnAddDevice.IsEnabled = true;
            _promptDeviceTypeId = config.DeviceTypeId;

            borderAddDeviceAlert.Visibility = Visibility.Visible;
        }

        private async void BtnAddDevice_Click(object sender, RoutedEventArgs e)
        {
            string deviceTypeId = _promptDeviceTypeId;
            if (Plugin == null || deviceTypeId == null)
                return;

            var devices = SimHubDevicesGateway.Resolve(Plugin.PluginManager);
            if (devices == null)
                return;

            // Re-check right before adding — a double-add would end in SimHub's
            // instance-cap error dialog.
            if (SimHubDevicesGateway.IsDeviceAdded(devices, deviceTypeId))
            {
                UpdateStatus();
                return;
            }

            btnAddDevice.IsEnabled = false;
            try
            {
                // Sanctioned add path: wires extensions, default settings and
                // Init like a manual add; autoName skips the naming dialog. On
                // success SimHub switches to its Devices page so the user sees
                // the new entry. Returns null if the user cancelled or SimHub
                // refused; the add isn't persisted until SaveSettings.
                var instance = await devices.ShowAddDevice(
                    this, deviceTypeId, requestName: false, autoName: true);
                if (instance != null)
                {
                    devices.SaveSettings();
                    SimHub.Logging.Current.Info(
                        "FanaBridge: Added SimHub device " + deviceTypeId + " from the settings prompt");
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanaBridge: Failed to add SimHub device " + deviceTypeId + ": " + ex.Message);
            }
            finally
            {
                btnAddDevice.IsEnabled = true;
                UpdateStatus();
            }
        }

        // Re-polls the prompt while the wheel identity is settling; interval is
        // comfortably above the settler's 200 ms quiet window. Created lazily,
        // stopped whenever the identity is readable again or the page unloads.
        private DispatcherTimer _promptRetryTimer;

        private void ArmPromptRetry()
        {
            if (!IsLoaded)
                return;

            if (_promptRetryTimer == null)
            {
                _promptRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _promptRetryTimer.Tick += (s, e) => UpdateAddDevicePrompt();
            }

            _promptRetryTimer.Start();
        }

        private void StopPromptRetry()
        {
            _promptRetryTimer?.Stop();
        }

        // SimHub raises no event of its own when the user adds or removes a
        // device, and Plugin.StateChanged doesn't fire for it either — watching
        // the (public) device collection keeps the banner honest without
        // polling. Subscribed lazily on first prompt evaluation, dropped on
        // Unloaded, symmetric across tab switches.
        private void WatchSimHubDevices(DevicesPlugin devices)
        {
            // A StateChanged racing OnUnloaded can queue an UpdateStatus that
            // runs after the unsubscribes; without this guard it would
            // re-subscribe on a control SimHub has already discarded, pinning
            // it (and re-running the prompt) for the rest of the app's life.
            if (!IsLoaded)
                return;

            var collection = devices?.DevicesPluginSettings?.Devices;
            if (collection == null || ReferenceEquals(_watchedDevices, collection))
                return;

            UnwatchSimHubDevices();
            _watchedDevices = collection;
            _watchedDevices.CollectionChanged += OnSimHubDevicesChanged;
        }

        private void UnwatchSimHubDevices()
        {
            if (_watchedDevices == null)
                return;

            _watchedDevices.CollectionChanged -= OnSimHubDevicesChanged;
            _watchedDevices = null;
        }

        private void OnSimHubDevicesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateAddDevicePrompt));
        }

        // =====================================================================
        // PROFILE PICKER
        // =====================================================================

        private void UpdateProfilePicker(
            bool wheelDetected, string wheelCode, string moduleCode, WheelCapabilities activeCaps)
        {
            if (!wheelDetected)
            {
                // Nothing attached — hide picker entirely.
                panelProfilePicker.Visibility = Visibility.Collapsed;
                txtProfileHint.Visibility = Visibility.Visible;
                txtProfileHint.Text = "Connect a wheel to manage profiles.";
                return;
            }

            txtProfileHint.Visibility = Visibility.Collapsed;
            panelProfilePicker.Visibility = Visibility.Visible;

            // A detected-but-unrecognized wheel (wire byte not in the decode
            // tables) has a null code, so there's no profile to match — but the
            // panel must stay visible so the New Profile Wizard is reachable and
            // the user can create one. The store lookups below are null-safe and
            // yield an empty list / null, landing on the "no profile" state.
            string matchKey = wheelCode != null
                ? WheelProfileStore.MakeMatchKey(wheelCode, moduleCode)
                : null;

            // Get ALL profiles that match this wheel (built-in + user, even duplicates)
            var all = WheelProfileStore.FindAllForWheel(wheelCode, moduleCode);

            // Determine which profile auto-resolution would pick (no override)
            var autoResolved = WheelProfileStore.FindByWheelType(wheelCode, moduleCode, overrideId: null);
            string autoOverrideKey = autoResolved != null
                ? WheelProfileStore.MakeOverrideKey(autoResolved)
                : null;

            // Current override (if any) from settings
            string currentOverride = null;
            if (matchKey != null)
                Plugin.Settings.ProfileOverrides?.TryGetValue(matchKey, out currentOverride);

            if (all.Count == 0)
            {
                // No profile for this wheel — show amber alert, hide combo
                borderNoProfileAlert.Visibility = Visibility.Visible;
                txtMultipleProfilesHint.Visibility = Visibility.Collapsed;
                panelProfileCombo.Visibility = Visibility.Collapsed;
            }
            else if (all.Count == 1)
            {
                // Single profile — show it in the combo so users get confirmation it loaded
                borderNoProfileAlert.Visibility = Visibility.Collapsed;
                txtMultipleProfilesHint.Visibility = Visibility.Collapsed;
                panelProfileCombo.Visibility = Visibility.Visible;
            }
            else
            {
                // Multiple profiles — show picker with explanation
                borderNoProfileAlert.Visibility = Visibility.Collapsed;
                txtMultipleProfilesHint.Visibility = Visibility.Visible;
                panelProfileCombo.Visibility = Visibility.Visible;
            }

            // Populate combo (even if hidden, keeps logic simple)
            _suppressProfileChange = true;
            try
            {
                cboProfile.Items.Clear();
                int selectedIndex = 0;

                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    string overrideKey = WheelProfileStore.MakeOverrideKey(p);
                    string sourceLabel = p.Source == ProfileSource.BuiltIn
                        ? "\ud83d\udce6 Built-in"
                        : "\ud83d\udcdd " + System.IO.Path.GetFileName(p.SourcePath ?? "Custom");
                    string label = p.Name + "  [" + sourceLabel + "]";

                    var item = new ComboBoxItem
                    {
                        Content = label,
                        Tag = overrideKey,
                    };
                    cboProfile.Items.Add(item);

                    // Select: explicit override wins, otherwise the auto-resolved one
                    if (!string.IsNullOrEmpty(currentOverride))
                    {
                        if (string.Equals(overrideKey, currentOverride, StringComparison.OrdinalIgnoreCase))
                            selectedIndex = i;
                    }
                    else
                    {
                        if (string.Equals(overrideKey, autoOverrideKey, StringComparison.OrdinalIgnoreCase))
                            selectedIndex = i;
                    }
                }

                cboProfile.SelectedIndex = selectedIndex;
            }
            finally
            {
                _suppressProfileChange = false;
            }

            // Update source display and delete button state
            UpdateProfileSourceDisplay(activeCaps);
        }

        private void UpdateProfileSourceDisplay(WheelCapabilities caps)
        {
            if (caps == null || caps.ProfileSource == null)
            {
                txtProfileSource.Visibility = Visibility.Collapsed;
                txtProfileSource.Text = "";
                btnDeleteProfile.IsEnabled = false;
                txtContributeProfile.Visibility = Visibility.Collapsed;
                return;
            }
            txtProfileSource.Visibility = Visibility.Visible;

            bool isCustom = caps.ProfileSource == ProfileSource.User;

            if (isCustom)
            {
                string fileName = caps.ProfileSourcePath != null
                    ? System.IO.Path.GetFileName(caps.ProfileSourcePath)
                    : "(unknown)";
                txtProfileSource.Text = "Custom profile \u2014 " + fileName;
                btnDeleteProfile.IsEnabled = true;
            }
            else
            {
                txtProfileSource.Text = "Built-in profile";
                btnDeleteProfile.IsEnabled = false;
            }

            // Show contribute callout only for custom profiles
            txtContributeProfile.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CboProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileChange || Plugin == null) return;

            var selected = cboProfile.SelectedItem as ComboBoxItem;
            if (selected == null) return;

            string overrideKey = selected.Tag as string;
            if (string.IsNullOrEmpty(overrideKey)) return;

            // Build match key for current wheel
            var wheelbase = Plugin.Wheelbase;
            if (!wheelbase.WheelDetected) return;

            string wheelCode = wheelbase.WheelCode;
            string moduleCode = wheelbase.ModuleCode;
            string matchKey = WheelProfileStore.MakeMatchKey(wheelCode, moduleCode);

            // No match key (e.g. detected but unrecognized wheel, WheelCode null)
            // means there is no identity to attach an override to — nothing to persist.
            if (string.IsNullOrEmpty(matchKey)) return;

            // Check if the selected profile is the one auto-resolution would pick
            var autoResolved = WheelProfileStore.FindByWheelType(wheelCode, moduleCode, overrideId: null);
            string autoOverrideKey = autoResolved != null
                ? WheelProfileStore.MakeOverrideKey(autoResolved)
                : null;
            bool isDefault = string.Equals(overrideKey, autoOverrideKey, StringComparison.OrdinalIgnoreCase);

            if (isDefault)
            {
                // No need to persist an override — default resolution already picks this
                Plugin.Settings.ProfileOverrides.Remove(matchKey);
            }
            else
            {
                Plugin.Settings.ProfileOverrides[matchKey] = overrideKey;
            }

            // Persist settings and re-resolve capabilities
            Plugin.SaveSettings();
            wheelbase.RefreshCapabilities();

            // Show restart notice if the device name changed from what SimHub registered
            UpdateRestartNotice();
        }

        private void UpdateRestartNotice()
        {
            var caps = Plugin?.CurrentCapabilities;
            if (caps?.Name == null)
            {
                txtRestartNotice.Visibility = Visibility.Collapsed;
                return;
            }

            // Check if capabilities changed in a way that requires restart
            string restartReason = caps.GetRestartReason(_bootCaps);
            if (restartReason != null)
            {
                txtRestartNotice.Visibility = Visibility.Visible;
                if (!_restartPromptDismissed)
                    PromptRestart(restartReason);
                return;
            }

            // Check if the device name changed (cosmetic — doesn't need
            // restart for functionality, but the Devices list is stale)
            string currentName = caps.ShortName ?? caps.Name;
            string bootName = _bootCaps?.ShortName ?? _bootCaps?.Name;
            bool nameChanged = bootName != null
                && !string.Equals(currentName, bootName, StringComparison.OrdinalIgnoreCase);

            txtRestartNotice.Visibility = nameChanged
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void PromptRestart(string reason)
        {
            var result = System.Windows.MessageBox.Show(
                reason + ".\n\n" +
                "LED and display output has switched immediately, but the SimHub " +
                "LED editor and device list need a restart to update.\n\n" +
                "Restart SimHub now?",
                "Restart Required",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.No)
            {
                _restartPromptDismissed = true;
                return;
            }

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                Plugin.PluginManager?.RequestApplicationExit(restart: true);
            }
        }

        // =====================================================================
        // PROFILE DELETION
        // =====================================================================

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var caps = Plugin?.CurrentCapabilities;
            if (caps?.Profile == null || caps.ProfileSource != ProfileSource.User)
                return;

            string profileId = caps.Profile.Id;
            string fileName = caps.ProfileSourcePath != null
                ? System.IO.Path.GetFileName(caps.ProfileSourcePath)
                : profileId;

            var result = MessageBox.Show(
                "Delete custom profile \"" + caps.Profile.Name + "\"?\n\n" +
                "File: " + fileName + "\n\n" +
                "This cannot be undone. If a built-in profile exists for this " +
                "wheel, it will be used instead.",
                "Delete Profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // Remove any override for this profile
            var wheelbase = Plugin.Wheelbase;
            string wheelCode = wheelbase.WheelCode;
            string moduleCode = wheelbase.ModuleCode;
            string matchKey = WheelProfileStore.MakeMatchKey(wheelCode, moduleCode);

            // A null/empty key (unrecognized wheel) can never have been stored as
            // an override key, so there is nothing to remove — and Dictionary
            // throws on a null key.
            if (!string.IsNullOrEmpty(matchKey))
                Plugin.Settings.ProfileOverrides.Remove(matchKey);

            // Delete from disk and store
            bool deleted = WheelProfileStore.DeleteUserProfile(profileId);
            if (!deleted)
            {
                MessageBox.Show("Failed to delete profile.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Re-resolve and update UI
            Plugin.SaveSettings();
            wheelbase.RefreshCapabilities();
            UpdateRestartNotice();
            UpdateStatus();
        }

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            // After reconnecting, the base is connected but not yet identified, so
            // UpdateStatus naturally shows the amber "Connecting…" state until the
            // identity commits (~200 ms later) and it goes green.
            Plugin?.ForceReconnect();
            UpdateStatus();
        }

        private void BtnCopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            string report;
            try
            {
                report = Plugin?.BuildDiagnosticsReport();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanaBridge: Failed to build debug info: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(report))
                return;

            try
            {
                Clipboard.SetText(report);
                FlashCopied(sender as Hyperlink);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanaBridge: Failed to copy debug info to clipboard: " + ex.Message);
            }
        }

        private const string CopyLinkLabel = "Copy Debug Info";
        private DispatcherTimer _copyFlashTimer;

        // Briefly confirm the copy on the link itself, then restore its label.
        // Restores a constant label (not the captured inlines) and cancels any
        // pending flash, so a rapid second click can't leave it stuck on "Copied!".
        private void FlashCopied(Hyperlink link)
        {
            if (link == null) return;

            _copyFlashTimer?.Stop();
            link.Inlines.Clear();
            link.Inlines.Add("Copied!");

            _copyFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _copyFlashTimer.Tick += (s, args) =>
            {
                _copyFlashTimer.Stop();
                link.Inlines.Clear();
                link.Inlines.Add(CopyLinkLabel);
            };
            _copyFlashTimer.Start();
        }

        private void BtnOpenProfilesFolder_Click(object sender, RoutedEventArgs e)
        {
            string userDir = WheelProfileStore.GetUserProfileDirectory();
            if (userDir != null)
                Process.Start(new ProcessStartInfo { FileName = userDir, UseShellExecute = true });
        }

        private void BtnContributeProfile_Click(object sender, RoutedEventArgs e)
        {
            var caps = Plugin?.CurrentCapabilities;
            if (caps?.Profile == null || caps.ProfileSource != ProfileSource.User)
                return;

            var profile = caps.Profile;
            string fileName = !string.IsNullOrEmpty(caps.ProfileSourcePath)
                ? System.IO.Path.GetFileName(caps.ProfileSourcePath)
                : profile.Id + ".json";

            // Open a pre-filled GitHub issue
            string title = Uri.EscapeDataString("Wheel profile: " + profile.Id);
            string label = Uri.EscapeDataString("wheel profile");
            string body = Uri.EscapeDataString(
                "## Wheel Profile Submission\n\n" +
                "**Wheel:** " + (profile.Match?.WheelType ?? "Unknown") + "\n" +
                "**Module:** " + (profile.Match?.ModuleType ?? "None") + "\n\n" +
                "Please drag and drop `" + fileName + "` into this issue.\n" +
                "You can find it via **Open Profiles Folder** in the FanaBridge settings.");

            string url = "https://github.com/kelchm/FanaBridge/issues/new" +
                         "?title=" + title + "&labels=" + label + "&body=" + body;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanaBridge: Failed to open GitHub: " + ex.Message);
            }
        }

        private void BtnReportIssue_Click(object sender, RoutedEventArgs e)
        {
            var caps = Plugin?.CurrentCapabilities;
            string profileId = caps?.Profile?.Id ?? "unknown";
            string title = Uri.EscapeDataString("Feedback: " + profileId + " profile");
            string label = Uri.EscapeDataString("wheel profile");
            string body = Uri.EscapeDataString(
                "## Profile Feedback\n\n" +
                "**Profile:** " + profileId + "\n" +
                "**Wheel:** " + (caps?.Name ?? "Unknown") + "\n\n" +
                "Please describe your experience:\n" +
                "- Did the LEDs work correctly?\n" +
                "- Did the display work correctly?\n" +
                "- Any issues or unexpected behavior?\n");

            string url = "https://github.com/kelchm/FanaBridge/issues/new" +
                         "?title=" + title + "&labels=" + label + "&body=" + body;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanaBridge: Failed to open GitHub: " + ex.Message);
            }
        }

        private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"FanaBridge: Failed to open repository link: {ex.Message}");
            }

            e.Handled = true;
        }

        // =====================================================================
        // WHEEL PROFILE WIZARD
        // =====================================================================

        private void BtnCreateProfile_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || !Plugin.IsDeviceConnected)
            {
                MessageBox.Show(
                    "Please connect a Fanatec device first.",
                    "Not Connected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dialog = new WheelProfileWizardDialog(Plugin);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();

            // The wizard calls Reload() + RefreshCapabilities() before closing.
            // Refresh the UI now that the dialog is dismissed so the new
            // profile shows up immediately in the picker.
            UpdateStatus();
            UpdateRestartNotice();
        }

        // =====================================================================
        // FEATURE FLAGS
        // =====================================================================

        private void ChkEnableTuning_Changed(object sender, RoutedEventArgs e)
        {
            Plugin?.SaveSettings();
        }

        private void ChkEnableUpdateCheck_Changed(object sender, RoutedEventArgs e)
        {
            // Live: the daily re-check timer re-reads the setting on each fire;
            // the manual link works regardless.
            Plugin?.SaveSettings();
        }

        // =====================================================================
        // SELF-UPDATER  (banner + About affordances)
        //
        // Pure view over UpdateService's immutable snapshots: every render
        // derives the whole banner + About line from the current snapshot, so
        // events and the on-load render can never disagree. The service owns
        // all sequencing (terminal ReadyToRestart, no re-entrancy) — this code
        // never decides what is allowed, it only displays and forwards clicks.
        // =====================================================================

        private static readonly Brush UpdateBlueBg = HexBrush("#1A4488CC");
        private static readonly Brush UpdateBlueBorder = HexBrush("#4488CC");
        private static readonly Brush UpdateBlueText = HexBrush("#AADDFF");
        private static readonly Brush UpdateAmberBg = HexBrush("#1AFFCC00");
        private static readonly Brush UpdateAmberBorder = HexBrush("#FFCC00");
        private static readonly Brush UpdateAmberText = HexBrush("#FFEEBB");

        private static Brush HexBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private void OnUpdateStateChanged()
        {
            // May fire from the update check's background thread; the control
            // can also unload before the queued render runs — hence the guard.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsLoaded)
                    UpdateUpdateBanner();
            }));
        }

        private void UpdateUpdateBanner()
        {
            var snapshot = Plugin?.Updates?.Snapshot;
            if (snapshot == null)
            {
                borderUpdateAlert.Visibility = Visibility.Collapsed;
                return;
            }

            var release = snapshot.Release;
            switch (snapshot.Phase)
            {
                case UpdatePhase.Idle:
                    txtUpdateCheckResult.Text = "";
                    borderUpdateAlert.Visibility = Visibility.Collapsed;
                    break;

                case UpdatePhase.Checking:
                    txtUpdateCheckResult.Text = "Checking…";
                    borderUpdateAlert.Visibility = Visibility.Collapsed;
                    break;

                case UpdatePhase.UpToDate:
                    txtUpdateCheckResult.Text = "You're up to date (" + BuildIdentity.Version + ").";
                    borderUpdateAlert.Visibility = Visibility.Collapsed;
                    break;

                case UpdatePhase.CheckFailed:
                    txtUpdateCheckResult.Text = "Check failed: " + (snapshot.FailureDetail ?? "unknown error");
                    borderUpdateAlert.Visibility = Visibility.Collapsed;
                    break;

                case UpdatePhase.UpdateAvailable:
                    // The About line reports the manual check's outcome and
                    // points at the banner — the actionable UI — rather than
                    // duplicating it at the bottom of the page.
                    txtUpdateCheckResult.Text = "FanaBridge " + release.Version
                        + " is available — see above.";
                    StyleUpdateBanner(failed: false);
                    runUpdateHeadline.Text = "Update available: FanaBridge " + release.Version;
                    if (release.CanSelfInstall)
                    {
                        SetUpdateDetail(null);
                        btnUpdateNow.Content = "Update";
                    }
                    else
                    {
                        SetUpdateDetail("One-click update isn't available for this release ("
                            + release.InstallBlockedReason + ") — install it manually from the release page.");
                        btnUpdateNow.Content = "Open release page";
                    }
                    btnUpdateNow.IsEnabled = true;
                    borderUpdateAlert.Visibility = Visibility.Visible;
                    break;

                case UpdatePhase.Downloading:
                case UpdatePhase.Applying:
                    // These states (and the two below) are outcomes of banner
                    // interaction, not of the manual check link — the banner
                    // owns their messaging, the About line stays quiet.
                    txtUpdateCheckResult.Text = "";
                    StyleUpdateBanner(failed: false);
                    runUpdateHeadline.Text = "Updating to FanaBridge " + release?.Version;
                    SetUpdateDetail("Downloading and installing…");
                    btnUpdateNow.Content = "Update";
                    btnUpdateNow.IsEnabled = false;
                    borderUpdateAlert.Visibility = Visibility.Visible;
                    break;

                case UpdatePhase.ReadyToRestart:
                    txtUpdateCheckResult.Text = "";
                    StyleUpdateBanner(failed: false);
                    runUpdateHeadline.Text = "Update installed — restart SimHub to finish updating to FanaBridge "
                        + release?.Version + ".";
                    SetUpdateDetail(null);
                    btnUpdateNow.Content = "Restart SimHub";
                    btnUpdateNow.IsEnabled = true;
                    borderUpdateAlert.Visibility = Visibility.Visible;
                    OfferUpdateRestartOnce();
                    break;

                case UpdatePhase.Failed:
                    txtUpdateCheckResult.Text = "";
                    StyleUpdateBanner(failed: true);
                    runUpdateHeadline.Text = "Automatic update failed";
                    SetUpdateDetail(ComposeUpdateFailureDetail(snapshot));
                    btnUpdateNow.Content = "Open release page";
                    btnUpdateNow.IsEnabled = true;
                    borderUpdateAlert.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void SetUpdateDetail(string detail)
        {
            txtUpdateDetail.Text = detail ?? "";
            txtUpdateDetail.Visibility = string.IsNullOrEmpty(detail)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void StyleUpdateBanner(bool failed)
        {
            borderUpdateAlert.Background = failed ? UpdateAmberBg : UpdateBlueBg;
            borderUpdateAlert.BorderBrush = failed ? UpdateAmberBorder : UpdateBlueBorder;
            txtUpdateTitle.Foreground = failed ? UpdateAmberText : UpdateBlueText;
            txtUpdateDetail.Foreground = failed ? UpdateAmberText : UpdateBlueText;
        }

        private static string ComposeUpdateFailureDetail(UpdateSnapshot snapshot)
        {
            string detail = snapshot.FailureDetail ?? "Unknown error.";
            if (snapshot.AccessDenied)
                return detail + " SimHub's folder isn't writable by your user account — download the "
                    + "release zip and copy FanaBridge.dll (and the DevicesLogos images) next to "
                    + "SimHub.exe manually.";
            return detail + " You can install manually: download the release zip and copy its files "
                + "next to SimHub.exe.";
        }

        private void OfferUpdateRestartOnce()
        {
            // The banner keeps its Restart button, so declining here isn't
            // final — this just avoids re-prompting on every render. The flag
            // lives on the plugin: SimHub creates a fresh control per page open
            // while ReadyToRestart persists for the whole process.
            if (Plugin.UpdateRestartPromptShown)
                return;
            Plugin.UpdateRestartPromptShown = true;

            var result = MessageBox.Show(
                "FanaBridge has been updated. Restart SimHub now to load the new version?",
                "Update Installed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                Plugin.PluginManager?.RequestApplicationExit(restart: true);
        }

        private void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            var updates = Plugin?.Updates;
            var snapshot = updates?.Snapshot;
            if (snapshot == null)
                return;

            switch (snapshot.Phase)
            {
                case UpdatePhase.UpdateAvailable when snapshot.Release?.CanSelfInstall == true:
                    // Fire-and-forget: phase transitions drive the UI, and the
                    // service ignores re-entrant calls, so a double-click is safe.
                    // Routed via the plugin so FinalizePlugin's cancel covers it.
                    _ = Plugin.ApplyUpdateAsync();
                    break;

                case UpdatePhase.UpdateAvailable:
                case UpdatePhase.Failed:
                    OpenReleasePage(snapshot);
                    break;

                case UpdatePhase.ReadyToRestart:
                    Plugin.PluginManager?.RequestApplicationExit(restart: true);
                    break;
            }
        }

        private void LnkReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            OpenReleasePage(Plugin?.Updates?.Snapshot);
        }

        private void OpenReleasePage(UpdateSnapshot snapshot)
        {
            const string fallback = "https://github.com/kelchm/FanaBridge/releases";

            // The feed's html_url is remote data handed to the shell — only
            // launch real web URLs, never other schemes.
            string url = snapshot?.Release?.HtmlUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                url = fallback;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanaBridge: Failed to open release page: " + ex.Message);
            }
        }

        private async void LnkCheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            var updates = Plugin?.Updates;
            if (updates == null)
            {
                txtUpdateCheckResult.Text = "Updater unavailable.";
                return;
            }

            txtUpdateCheckResult.Text = "Checking…";
            // Routed via the plugin so FinalizePlugin's cancel covers it.
            try { await Plugin.CheckForUpdatesAsync(); }
            catch { /* CheckAsync converts failures to states */ }

            // A debounced/no-op check fires no Changed event, which would leave
            // "Checking…" on screen — re-render from the snapshot regardless.
            if (IsLoaded)
                UpdateUpdateBanner();
        }

        private void ChkEnableControlMapperIntegration_Changed(object sender, RoutedEventArgs e)
        {
            // Persist the flag; FanatecPlugin.DataUpdate reconciles the Control
            // Mapper bridge (register / unregister) on its next tick — live, no
            // restart needed.
            Plugin?.SaveSettings();
            UpdateControlMapperStatus();
        }

        // =====================================================================
        // DISPLAY TEST — scroll support
        // =====================================================================

        private const int SCROLL_SPEED_MIN = 50;
        private const int SCROLL_SPEED_MAX = 1000;
        private const int SCROLL_SPEED_DEFAULT = 250;

        private Timer _scrollTimer;
        private List<byte> _scrollFrames;
        private int _scrollPos;

        private void TxtScrollSpeed_LostFocus(object sender, RoutedEventArgs e)
        {
            txtScrollSpeed.Text = ClampScrollSpeed().ToString();
        }

        private int ClampScrollSpeed()
        {
            int ms;
            if (!int.TryParse(txtScrollSpeed.Text, out ms))
                return SCROLL_SPEED_DEFAULT;
            return Math.Max(SCROLL_SPEED_MIN, Math.Min(SCROLL_SPEED_MAX, ms));
        }

        private void BtnSendDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || !Plugin.IsDeviceConnected) return;

            StopScroll();

            // Take ownership of the 7-segment display: with a game running, the
            // per-frame gear/speed drive would otherwise overwrite the test text
            // (and its buffer fills would race ours). StopScroll is the single
            // release point (Clear / Stop Scroll / unload / disconnect all funnel
            // through it) — the game driver then blanks and repaints live.
            Plugin.DisplayTestActive = true;

            string text = txtDisplayTest.Text;
            if (string.IsNullOrEmpty(text)) text = "---";

            // Encode with dot-folding to see how many display positions we need
            var encoded = SevenSegment.EncodeWithDots(text);

            if (encoded.Count <= 3)
            {
                // Fits on the display — just send it
                SimHub.Logging.Current.Info($"SettingsControl: Sending display text \"{text}\"");
                Plugin.Display.DisplayText(text);
                return;
            }

            // Longer text — scroll it
            SimHub.Logging.Current.Info($"SettingsControl: Scrolling display text \"{text}\"");
            StartScroll(encoded);
        }

        private void BtnStopScroll_Click(object sender, RoutedEventArgs e)
        {
            StopScroll();
            if (Plugin != null && Plugin.IsDeviceConnected)
                Plugin.Display.ClearDisplay();
        }

        private void BtnClearDisplay_Click(object sender, RoutedEventArgs e)
        {
            StopScroll();
            if (Plugin == null || !Plugin.IsDeviceConnected) return;
            Plugin.Display.ClearDisplay();
        }

        private void StartScroll(List<byte> encoded)
        {
            // Pad with 3 blanks on each side so the text slides in and out
            _scrollFrames = new List<byte>();
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.AddRange(encoded);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollPos = 0;

            int delayMs = ClampScrollSpeed();
            txtScrollSpeed.Text = delayMs.ToString();

            _scrollTimer = new Timer(delayMs);
            _scrollTimer.AutoReset = true;
            _scrollTimer.Elapsed += ScrollTick;
            _scrollTimer.Start();

            btnStopScroll.Visibility = Visibility.Visible;
        }

        private void ScrollTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            var frames = _scrollFrames;
            if (Plugin == null || !Plugin.IsDeviceConnected || frames == null)
            {
                Dispatcher.BeginInvoke(new Action(StopScroll));
                return;
            }

            int pos = _scrollPos;
            if (pos > frames.Count - 3)
            {
                pos = 0;
                _scrollPos = 0;
            }

            Plugin.Display.SetDisplay(
                frames[pos],
                frames[pos + 1],
                frames[pos + 2]);

            _scrollPos = pos + 1;
        }

        private void StopScroll()
        {
            // Single release point for display-test ownership: every way a test
            // ends (Clear, Stop Scroll, a new Send, page unload, disconnect
            // auto-stop) funnels through here. The device instance blanks and
            // repaints the live gear/speed on the released edge.
            if (Plugin != null)
                Plugin.DisplayTestActive = false;

            if (_scrollTimer != null)
            {
                _scrollTimer.Stop();
                _scrollTimer.Elapsed -= ScrollTick;
                _scrollTimer.Dispose();
                _scrollTimer = null;
            }
            _scrollFrames = null;
            _scrollPos = 0;

            if (btnStopScroll != null)
                btnStopScroll.Visibility = Visibility.Collapsed;
        }

    }
}
