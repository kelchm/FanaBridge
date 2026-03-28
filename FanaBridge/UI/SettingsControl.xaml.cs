using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanatecManaged;
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
            var assembly = typeof(FanatecPlugin).Assembly;

            var version = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "Unknown";

            var config = GetAssemblyMetadata(assembly, "BuildConfiguration");
            var commit = GetAssemblyMetadata(assembly, "CommitHash");

            var versionText = FindName("txtPluginVersion") as TextBlock;
            if (versionText != null)
                versionText.Text = $"FanaBridge {version}";

            var buildText = FindName("txtBuildInfo") as TextBlock;
            if (buildText != null)
                buildText.Text = FormatBuildInfo(config, commit);
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

        private static string GetAssemblyMetadata(Assembly assembly, string key)
        {
            return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Plugin.StateChanged += OnPluginStateChanged;

            // Capture the capabilities the LED module was built from at startup
            _bootCaps = Plugin.CurrentCapabilities;

            UpdateStatus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopScroll();
            Plugin.StateChanged -= OnPluginStateChanged;
        }

        private void OnPluginStateChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        private void UpdateStatus()
        {
            if (Plugin == null) return;

            bool connected = Plugin.IsDeviceConnected;

            if (!connected)
            {
                txtStatus.Text = "Disconnected";
                txtWheelName.Text = "—";
                txtCapabilities.Text = "—";
                borderUnverifiedAlert.Visibility = Visibility.Collapsed;
                UpdateProfilePicker(null, null, null);
                return;
            }

            var caps = Plugin.CurrentCapabilities;
            bool identified = caps.Name != null;

            // Resolve wheel/module codes from the SDK — available even when no profile exists
            var sdk = Plugin.SdkManager;
            string wheelCode = WheelProfileStore.StripWheelPrefix(sdk.SteeringWheelType.ToString());
            string moduleCode = sdk.SubModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                ? null
                : WheelProfileStore.StripModulePrefix(sdk.SubModuleType.ToString());

            if (!identified)
            {
                txtStatus.Text = "Connected — " + Plugin.WheelName;
                txtWheelName.Text = "—";
                txtCapabilities.Text = "—";
                borderUnverifiedAlert.Visibility = Visibility.Collapsed;
                // Still show the panel so the wizard button is accessible for unsupported wheels
                UpdateProfilePicker(wheelCode, moduleCode, null);
                return;
            }

            txtStatus.Text = "Connected";
            txtWheelName.Text = Plugin.WheelName;
            txtCapabilities.Text = string.Format("{0} button RGB, {1} button aux intensity, {2} rev RGB, {3} flag RGB, Display: {4}",
                caps.ButtonRgbCount,
                caps.ButtonAuxIntensityCount,
                caps.RevRgbCount,
                caps.FlagRgbCount,
                caps.Display);

            // Show unverified profile banner if applicable
            borderUnverifiedAlert.Visibility = caps.Verified
                ? Visibility.Collapsed
                : Visibility.Visible;

            UpdateProfilePicker(wheelCode, moduleCode, caps);
        }

        // =====================================================================
        // PROFILE PICKER
        // =====================================================================

        private void UpdateProfilePicker(
            string wheelCode, string moduleCode, WheelCapabilities activeCaps)
        {
            if (wheelCode == null)
            {
                // No identified wheel — hide picker
                panelProfilePicker.Visibility = Visibility.Collapsed;
                txtProfileHint.Visibility = Visibility.Visible;
                txtProfileHint.Text = "Connect a wheel to manage profiles.";
                return;
            }

            txtProfileHint.Visibility = Visibility.Collapsed;
            panelProfilePicker.Visibility = Visibility.Visible;

            // Build the match key (same format as the profile ID: "WHEELTYPE_MODULE")
            string matchKey = wheelCode;
            if (moduleCode != null)
                matchKey += "_" + moduleCode;

            // Get ALL profiles that match this wheel (built-in + user, even duplicates)
            var all = WheelProfileStore.FindAllForWheel(wheelCode, moduleCode);

            // Determine which profile auto-resolution would pick (no override)
            var autoResolved = WheelProfileStore.FindByWheelType(wheelCode, moduleCode, overrideId: null);
            string autoOverrideKey = autoResolved != null
                ? WheelProfileStore.MakeOverrideKey(autoResolved)
                : null;

            // Current override (if any) from settings
            string currentOverride = null;
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
            var sdk = Plugin.SdkManager;
            if (!sdk.WheelDetected) return;

            string wheelCode = WheelProfileStore.StripWheelPrefix(sdk.SteeringWheelType.ToString());
            string moduleCode = sdk.SubModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                ? null
                : WheelProfileStore.StripModulePrefix(sdk.SubModuleType.ToString());
            string matchKey = wheelCode;
            if (moduleCode != null)
                matchKey += "_" + moduleCode;

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
            sdk.RefreshCapabilities();

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
            var sdk = Plugin.SdkManager;
            string wheelCode = WheelProfileStore.StripWheelPrefix(sdk.SteeringWheelType.ToString());
            string moduleCode = sdk.SubModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                ? null
                : WheelProfileStore.StripModulePrefix(sdk.SubModuleType.ToString());
            string matchKey = wheelCode;
            if (moduleCode != null)
                matchKey += "_" + moduleCode;

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
            sdk.RefreshCapabilities();
            UpdateRestartNotice();
            UpdateStatus();
        }

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            Plugin?.ForceReconnect();
            UpdateStatus(); // immediate since we're already on UI thread
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

            string text = txtDisplayTest.Text;
            if (string.IsNullOrEmpty(text)) text = "---";

            // Encode with dot-folding to see how many display positions we need
            var encoded = EncodeText(text);

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

        /// <summary>
        /// Encode a string to 7-segment bytes, folding dots/commas onto the previous character.
        /// </summary>
        private static List<byte> EncodeText(string text)
        {
            var encoded = new List<byte>();
            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && encoded.Count > 0)
                    encoded[encoded.Count - 1] |= SevenSegment.Dot;
                else
                    encoded.Add(SevenSegment.CharToSegment(ch));
            }
            return encoded;
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
