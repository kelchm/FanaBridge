using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FanatecManaged;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FanaBridge
{
    /// <summary>
    /// Standalone probe-based wizard dialog that discovers a wheel's LED and
    /// display capabilities by sending test signals to the hardware and asking
    /// the user to report what they observe.  Produces a <see cref="WheelProfile"/>
    /// JSON file saved to the user profile directory.
    /// </summary>
    public partial class WheelProfileWizardDialog : Window
    {
        private readonly FanatecPlugin _plugin;
        private FanatecDevice Device => _plugin.Device;

        // ── Step panels (order must match _stepTitles / _stepSubtitles) ──
        private StackPanel[] _steps;

        private int _currentStep;

        // ── Probe results ────────────────────────────────────────────────
        private bool _hasDisplay;
        private int _revCount;
        private int _flagCount;
        private int _colorCount;
        private ColorFormat _colorFormat = ColorFormat.Rgb565;
        private int _monoCount;

        // ── Input mapping ────────────────────────────────────────────────
        // Built during the input-mapping step: LED hwIndex → detected input id.
        // Uses SimHub's PluginManager.InputPressed event for reliable detection.
        private List<InputMappingEntry> _inputMappingLeds;
        private int _inputMappingIndex;
        private volatile CancellationTokenSource _blinkCts;
        private string _detectedInputId;
        private bool _listeningForInput;

        // ── Identity (from SDK) ──────────────────────────────────────────
        private readonly string _wheelType;
        private readonly string _moduleType;

        private static readonly string[] StepTitles =
        {
            "Welcome",
            "Display Detection",
            "Rev / RPM LED Detection",
            "Flag / Status LED Detection",
            "Button Colour LED Detection",
            "Green Channel Test",
            "Monochrome LED Detection",
            "Input Mapping",
            "Summary",
        };

        private static readonly string[] StepSubtitles =
        {
            "Create a hardware profile for an unsupported wheel",
            "Testing the 7-segment display",
            "Testing subcmd 0x00 — RPM / shift-indicator LEDs",
            "Testing subcmd 0x01 — flag / status LEDs",
            "Testing subcmd 0x02 — button backlight LEDs",
            "Determining the colour encoding format",
            "Testing subcmd 0x03 — intensity-only LEDs",
            "Map each LED to its physical button or encoder",
            "Review and save your new wheel profile",
        };

        // ─────────────────────────────────────────────────────────────────

        public WheelProfileWizardDialog(FanatecPlugin plugin)
        {
            InitializeComponent();

            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _steps = new[]
            {
                stepWelcome,
                stepDisplay,
                stepRevLeds,
                stepFlagLeds,
                stepColorLeds,
                stepColorFormat,
                stepMonoLeds,
                stepInputMapping,
                stepSummary,
            };

            // Pre-populate identity from the SDK
            var sdk = _plugin.SdkManager;
            _wheelType = WheelProfileStore.StripWheelPrefix(
                sdk.SteeringWheelType.ToString());
            _moduleType =
                sdk.SubModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                    ? null
                    : WheelProfileStore.StripModulePrefix(sdk.SubModuleType.ToString());

            txtWizWheelType.Text = _wheelType ?? "Unknown";
            txtWizModuleType.Text = _moduleType ?? "(none)";

            string defaultName = _wheelType ?? "Unknown Wheel";
            if (_moduleType != null)
                defaultName += " + " + _moduleType;
            txtProfileName.Text = defaultName;

            // Suspend normal SimHub LED output while the wizard is active
            _plugin.WizardActive = true;

            ShowStep(0);
        }

        // =====================================================================
        // Step navigation
        // =====================================================================

        private void ShowStep(int step)
        {
            _currentStep = step;

            for (int i = 0; i < _steps.Length; i++)
                _steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;

            txtStepTitle.Text = StepTitles[step];
            txtStepSubtitle.Text = StepSubtitles[step];

            btnBack.IsEnabled = step > 0;

            // Hide Next during input-mapping step (user advances per-LED via
            // Confirm/Skip) and on the final summary step.
            bool isInputStep = step == 7;
            bool isFinalStep = step == _steps.Length - 1;
            btnNext.Visibility = (!isInputStep && !isFinalStep)
                ? Visibility.Visible : Visibility.Collapsed;
            btnSave.Visibility = isFinalStep
                ? Visibility.Visible : Visibility.Collapsed;

            RunProbe(step);
        }

        /// <summary>Returns the next step index, skipping irrelevant steps.</summary>
        private int NextStep(int current)
        {
            int next = current + 1;
            // Skip colour-format step when there are no colour LEDs
            if (next == 5 && _colorCount == 0)
                next = 6;
            // Skip input-mapping step when there are no button/encoder LEDs
            if (next == 7 && _colorCount + _monoCount == 0)
                next = 8;
            return next;
        }

        /// <summary>Returns the previous step index, skipping irrelevant steps.</summary>
        private int PrevStep(int current)
        {
            int prev = current - 1;
            if (prev == 7 && _colorCount + _monoCount == 0)
                prev = 6;
            if (prev == 5 && _colorCount == 0)
                prev = 4;
            return prev;
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            ReadStepInput(_currentStep);
            int next = NextStep(_currentStep);
            if (next < _steps.Length)
                ShowStep(next);
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            int prev = PrevStep(_currentStep);
            if (prev >= 0)
                ShowStep(prev);
        }

        /// <summary>Captures the user's selection on the current step before navigating away.</summary>
        private void ReadStepInput(int step)
        {
            switch (step)
            {
                case 2:
                    if (panelRevCount.Visibility == Visibility.Visible)
                        _revCount = cboRevCount.SelectedIndex + 1;   // items are 1-based
                    break;
                case 3:
                    if (panelFlagCount.Visibility == Visibility.Visible)
                        _flagCount = cboFlagCount.SelectedIndex + 1;
                    break;
                case 4:
                    if (panelColorCount.Visibility == Visibility.Visible)
                        _colorCount = cboColorCount.SelectedIndex + 1;
                    break;
                case 6:
                    if (panelMonoCount.Visibility == Visibility.Visible)
                        _monoCount = cboMonoCount.SelectedIndex + 1;
                    break;
            }
        }

        // =====================================================================
        // Hardware probes
        // =====================================================================

        // NOTE: Automatic LED detection is not possible at the HID level.
        // The col03 interface is write-only (output reports). Fanatec firmware
        // silently ignores writes to non-existent LED slots — there is no
        // ACK/NAK, no input report, and no feature report on col03 that
        // reports back LED state. We must rely on the user to observe and
        // report what they see.

        /// <summary>Sends the test signal appropriate for the given step.</summary>
        private void RunProbe(int step)
        {
            ClearAllLeds();

            switch (step)
            {
                case 1: ProbeDisplay();    break;
                case 2: ProbeRevLeds();    break;
                case 3: ProbeFlagLeds();   break;
                case 4: ProbeColorLeds();  break;
                case 5: ProbeColorFormat(); break;
                case 6: ProbeMonoLeds();   break;
                case 7: BeginInputMapping(); break;
                case 8: BuildSummary();    break;
            }
        }

        /// <summary>Turns off every LED channel and clears the display.</summary>
        private void ClearAllLeds()
        {
            try
            {
                if (Device == null || !Device.IsConnected) return;
                Device.SetRevLedColors(new ushort[9]);
                Device.SetFlagLedColors(new ushort[3]);
                Device.SetButtonLedState(
                    new ushort[12],
                    new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE]);
                Device.ClearDisplay();
            }
            catch { /* best-effort */ }
        }

        private void ProbeDisplay()
        {
            try
            {
                Device?.SetDisplay(
                    SevenSegment.Digit8,
                    SevenSegment.Digit8,
                    SevenSegment.Digit8);
            }
            catch { }
        }

        private void ProbeRevLeds()
        {
            try
            {
                var colors = new ushort[9];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = ColorHelper.Colors.Red;
                Device?.SetRevLedColors(colors);
            }
            catch { }
        }

        private void ProbeFlagLeds()
        {
            try
            {
                var colors = new ushort[3];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = ColorHelper.Colors.Blue;
                Device?.SetFlagLedColors(colors);
            }
            catch { }
        }

        private void ProbeColorLeds()
        {
            try
            {
                var colors = new ushort[12];
                var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                for (int i = 0; i < 12; i++)
                {
                    colors[i] = ColorHelper.Colors.Green;
                    intensities[i] = 7;
                }
                Device?.SetButtonLedState(colors, intensities);
            }
            catch { }
        }

        private void ProbeMonoLeds()
        {
            try
            {
                // Colours all black → colour LEDs stay dark.
                // Intensity max on every slot → mono-only LEDs light up.
                var colors = new ushort[12]; // all 0x0000
                var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                for (int i = 0; i < intensities.Length; i++)
                    intensities[i] = 7;
                Device?.SetButtonLedState(colors, intensities);
            }
            catch { }
        }

        // ── Colour-format sub-test ───────────────────────────────────────
        //
        // Strategy: When this step activates, RunProbe immediately sends
        // "Test B" — green=128 encoded as RGB565.  This sets only bit 10.
        //
        //  • On true RGB565 hardware the LED appears green.
        //  • On RGB555 hardware the lower 5 green bits are all zero → dark/off.
        //
        // The user simply reports what they see RIGHT NOW:
        //  "LEDs are green" → RGB565
        //  "LEDs are OFF / wrong colour" → RGB555
        //
        // No A/B switching required — one glance is enough.

        private void ProbeColorFormat()
        {
            try
            {
                // Green = 128 → g6 = 32 (0x20).  Only bit 10 is set.
                int count = Math.Max(_colorCount, 1);
                var colors = new ushort[count];
                var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                ushort test = ColorHelper.RgbToRgb565(0, 128, 0);
                for (int i = 0; i < count; i++)
                {
                    colors[i] = test;
                    intensities[i] = 7;
                }
                Device?.SetButtonLedState(colors, intensities);
            }
            catch { }
        }

        private void ColorSame_Click(object sender, RoutedEventArgs e)
        {
            _colorFormat = ColorFormat.Rgb565;
            txtColorFormatResult.Text = "\u2713 Standard RGB565 — 6-bit green";
        }

        private void ColorDark_Click(object sender, RoutedEventArgs e)
        {
            _colorFormat = ColorFormat.Rgb555;
            txtColorFormatResult.Text = "\u2713 RGB555 — 5-bit green (MSB ignored)";
        }

        // ── Display probe answers ────────────────────────────────────────

        private void DisplayYes_Click(object sender, RoutedEventArgs e)
        {
            _hasDisplay = true;
            txtDisplayResult.Text = "\u2713 Display detected";
        }
        private void DisplayNotWorking_Click(object sender, RoutedEventArgs e)
        {
            // The wheel has a display but the probe didn't produce visible output.
            // We still record the display as present so the profile includes it.
            _hasDisplay = true;
            txtDisplayResult.Text = "\u2713 Display present (probe inconclusive)";
        }
        private void DisplayNo_Click(object sender, RoutedEventArgs e)
        {
            _hasDisplay = false;
            txtDisplayResult.Text = "\u2713 No display";
        }

        // ── Rev LED probe answers ────────────────────────────────────────

        private void RevSome_Click(object sender, RoutedEventArgs e)
        {
            panelRevCount.Visibility = Visibility.Visible;
            txtRevResult.Text = "";
        }

        private void RevNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _revCount = 0;
            panelRevCount.Visibility = Visibility.Collapsed;
            txtRevResult.Text = "\u2713 Rev LEDs present but probe inconclusive";
        }

        private void RevNone_Click(object sender, RoutedEventArgs e)
        {
            _revCount = 0;
            panelRevCount.Visibility = Visibility.Collapsed;
            txtRevResult.Text = "\u2713 No rev LEDs";
        }

        // ── Flag LED probe answers ───────────────────────────────────────

        private void FlagSome_Click(object sender, RoutedEventArgs e)
        {
            panelFlagCount.Visibility = Visibility.Visible;
            txtFlagResult.Text = "";
        }

        private void FlagNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _flagCount = 0;
            panelFlagCount.Visibility = Visibility.Collapsed;
            txtFlagResult.Text = "\u2713 Flag LEDs present but probe inconclusive";
        }

        private void FlagNone_Click(object sender, RoutedEventArgs e)
        {
            _flagCount = 0;
            panelFlagCount.Visibility = Visibility.Collapsed;
            txtFlagResult.Text = "\u2713 No flag LEDs";
        }

        // ── Color LED probe answers ──────────────────────────────────────

        private void ColorSome_Click(object sender, RoutedEventArgs e)
        {
            panelColorCount.Visibility = Visibility.Visible;
            txtColorResult.Text = "";
        }

        private void ColorNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _colorCount = 0;
            panelColorCount.Visibility = Visibility.Collapsed;
            txtColorResult.Text = "\u2713 Colour LEDs present but probe inconclusive";
        }

        private void ColorNone_Click(object sender, RoutedEventArgs e)
        {
            _colorCount = 0;
            panelColorCount.Visibility = Visibility.Collapsed;
            txtColorResult.Text = "\u2713 No colour LEDs";
        }

        // ── Mono LED probe answers ───────────────────────────────────────

        private void MonoNone_Click(object sender, RoutedEventArgs e)
        {
            _monoCount = 0;
            panelMonoCount.Visibility = Visibility.Collapsed;
            txtMonoResult.Text = "\u2713 No mono LEDs";
        }

        private void MonoSome_Click(object sender, RoutedEventArgs e)
        {
            panelMonoCount.Visibility = Visibility.Visible;
            txtMonoResult.Text = "";
        }

        // =====================================================================
        // Input Mapping (step 7)
        // =====================================================================
        //
        // For each colour + mono button/encoder LED we:
        //   1. Light ONLY that LED (distinctive colour).
        //   2. Subscribe to SimHub's PluginManager.InputPressed event.
        //   3. Wait for any button press or encoder rotation.
        //   4. Record the mapping as the LedDefinition.Input string.
        //
        // This data enables downstream ASTR profile generation and future
        // custom lighting / visualization features.

        /// <summary>Tracks one LED that needs an input mapping.</summary>
        private class InputMappingEntry
        {
            public LedChannel Channel;
            public int HwIndex;
            public string Label;
            /// <summary>Detected input ID, or null if skipped.</summary>
            public string InputId;
        }

        /// <summary>
        /// Builds the list of LEDs that need mapping and starts the
        /// per-LED flow.
        /// </summary>
        private void BeginInputMapping()
        {
            // Capture counts from previous steps
            ReadStepInput(6); // mono count

            _inputMappingLeds = new List<InputMappingEntry>();

            for (int i = 0; i < _colorCount; i++)
            {
                _inputMappingLeds.Add(new InputMappingEntry
                {
                    Channel = LedChannel.Color,
                    HwIndex = i,
                    Label = "Colour LED " + (i + 1),
                });
            }

            int monoStart = _colorCount;
            for (int i = 0; i < _monoCount; i++)
            {
                _inputMappingLeds.Add(new InputMappingEntry
                {
                    Channel = LedChannel.Mono,
                    HwIndex = monoStart + i,
                    Label = "Mono LED " + (i + 1),
                });
            }

            if (_inputMappingLeds.Count == 0)
            {
                // Nothing to map — jump straight to summary
                ShowStep(NextStep(7));
                return;
            }

            _inputMappingIndex = 0;
            ShowInputMappingLed();
        }

        /// <summary>
        /// Subscribe to SimHub's InputPressed event for reliable input detection.
        /// SimHub's JoystickPlugin already polls all devices every frame.
        /// </summary>
        private void StartListeningForInput()
        {
            StopListeningForInput();
            var pm = _plugin.PluginManager;
            if (pm == null) return;
            pm.InputPressed += OnInputPressed;
            _listeningForInput = true;
        }

        private void StopListeningForInput()
        {
            if (!_listeningForInput) return;
            var pm = _plugin.PluginManager;
            if (pm != null)
                pm.InputPressed -= OnInputPressed;
            _listeningForInput = false;
        }

        /// <summary>
        /// Called by SimHub on ANY button/encoder press across all devices.
        /// The input string is e.g. "JoystickPlugin.FANATEC_Wheel.Button3".
        /// We marshal to the UI thread and display it.
        /// Returns true to indicate the event was handled.
        /// </summary>
        private bool OnInputPressed(string input)
        {
            if (!_listeningForInput) return false;

            // Only accept inputs from a Fanatec device
            if (input == null ||
                input.IndexOf("FANATEC", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            // Marshal to UI thread
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_listeningForInput) return;
                StopListeningForInput();
                SetDetectedInput(input);
            }));

            return true;
        }

        private void CancelBlink()
        {
            var cts = _blinkCts;
            if (cts != null)
            {
                cts.Cancel();
                _blinkCts = null;
            }
        }

        /// <summary>
        /// Lights the current LED, waits for state to settle, then
        /// captures a baseline and starts polling for input changes.
        /// </summary>
        private void ShowInputMappingLed()
        {
            if (_inputMappingIndex >= _inputMappingLeds.Count)
            {
                // All LEDs mapped — move to summary
                StopListeningForInput();
                ShowStep(NextStep(7));
                return;
            }

            var entry = _inputMappingLeds[_inputMappingIndex];

            // Update UI
            txtInputProgress.Text = string.Format(
                "LED {0} of {1}", _inputMappingIndex + 1, _inputMappingLeds.Count);
            txtInputLedLabel.Text = entry.Label;
            txtInputLedDetail.Text = string.Format(
                "{0} channel, hardware index {1}", entry.Channel, entry.HwIndex);
            txtDetectedInput.Text = "Press a button or turn an encoder…";
            txtDetectedInput.Foreground = System.Windows.Media.Brushes.Gray;
            btnInputConfirm.IsEnabled = false;
            _detectedInputId = null;

            // Light only this LED
            ClearAllLeds();
            LightSingleLed(entry);

            // Start listening via SimHub
            StartListeningForInput();
        }

        /// <summary>Lights a single LED with a distinctive colour.</summary>
        private void LightSingleLed(InputMappingEntry entry)
        {
            try
            {
                if (entry.Channel == LedChannel.Color)
                {
                    var colors = new ushort[12];
                    var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                    colors[entry.HwIndex] = ColorHelper.Colors.Cyan;
                    intensities[entry.HwIndex] = 7;
                    Device?.SetButtonLedState(colors, intensities);
                }
                else if (entry.Channel == LedChannel.Mono)
                {
                    var colors = new ushort[12];
                    var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                    intensities[entry.HwIndex] = 7;
                    Device?.SetButtonLedState(colors, intensities);
                }
            }
            catch { /* best-effort */ }
        }

        private void StartInputPoll()
        {
            StartListeningForInput();
        }

        private void StopInputPoll()
        {
            StopListeningForInput();
        }

        private void SetDetectedInput(string inputId)
        {
            _detectedInputId = inputId;

            // Show a friendly label: strip the plugin prefix for display
            // e.g. "JoystickPlugin.FANATEC_Wheel.Button3" -> "FANATEC_Wheel.Button3"
            string display = inputId;
            int dot = inputId.IndexOf('.');
            if (dot >= 0 && dot < inputId.Length - 1)
                display = inputId.Substring(dot + 1);

            txtDetectedInput.Text = "\u2714 " + display;
            txtDetectedInput.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
            btnInputConfirm.IsEnabled = true;

            // Blink the LED on a thread-pool thread for consistent timing.
            // The initial OFF happens immediately on that thread, not here,
            // so the first visible change is near-instant.
            BlinkCurrentLed();
        }

        /// <summary>
        /// Turns off only the button LED for the current mapping entry.
        /// Much cheaper than <see cref="ClearAllLeds"/> because it skips
        /// rev LEDs, flag LEDs, and the display.
        /// </summary>
        private void ClearButtonLed()
        {
            try
            {
                Device?.SetButtonLedState(
                    new ushort[12],
                    new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE]);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Blinks the current LED on/off 3 times to confirm detection.
        /// Runs entirely on a thread-pool thread using Thread.Sleep for
        /// precise, UI-independent timing.  WizardActive is already true
        /// so the normal LED update loop won't interfere.
        /// Pattern: OFF, ON, OFF, ON, OFF, ON — ends with LED on.
        /// </summary>
        private void BlinkCurrentLed()
        {
            CancelBlink();

            if (_inputMappingIndex >= _inputMappingLeds.Count) return;
            var entry = _inputMappingLeds[_inputMappingIndex];

            var cts = new CancellationTokenSource();
            _blinkCts = cts;
            var token = cts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // 3 blink cycles: OFF-ON, OFF-ON, OFF-ON
                    for (int i = 0; i < 3 && !token.IsCancellationRequested; i++)
                    {
                        ClearButtonLed();
                        Thread.Sleep(100);
                        if (token.IsCancellationRequested) break;

                        LightSingleLed(entry);
                        Thread.Sleep(100);
                    }
                }
                catch { /* device may have disconnected */ }
            });
        }

        private void InputConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_detectedInputId == null) return;

            _inputMappingLeds[_inputMappingIndex].InputId = _detectedInputId;
            StopInputPoll();

            _inputMappingIndex++;
            ShowInputMappingLed();
        }

        private void InputSkip_Click(object sender, RoutedEventArgs e)
        {
            _inputMappingLeds[_inputMappingIndex].InputId = null;
            StopInputPoll();

            _inputMappingIndex++;
            ShowInputMappingLed();
        }

        private void InputRetry_Click(object sender, RoutedEventArgs e)
        {
            _detectedInputId = null;
            txtDetectedInput.Text = "Press a button or turn an encoder\u2026";
            txtDetectedInput.Foreground = System.Windows.Media.Brushes.Gray;
            btnInputConfirm.IsEnabled = false;

            // Re-light the LED and start listening again
            var entry = _inputMappingLeds[_inputMappingIndex];
            ClearAllLeds();
            LightSingleLed(entry);
            StartListeningForInput();
        }

        // =====================================================================
        // Summary & Save
        // =====================================================================

        private void BuildSummary()
        {
            ClearAllLeds();

            int total = _revCount + _flagCount + _colorCount + _monoCount;
            int mapped = 0;
            if (_inputMappingLeds != null)
                mapped = _inputMappingLeds.Count(m => m.InputId != null);

            string fmt =
                "Profile:       {0}\n" +
                "Wheel:         {1}\n" +
                "Module:        {2}\n\n" +
                "Display:       {3}\n" +
                "Rev LEDs:      {4}\n" +
                "Flag LEDs:     {5}\n" +
                "Colour LEDs:   {6}\n" +
                "Colour Format: {7}\n" +
                "Mono LEDs:     {8}\n\n" +
                "Total LEDs:    {9}\n" +
                "Input mapped:  {10} of {11}";

            txtSummary.Text = string.Format(fmt,
                txtProfileName.Text,
                _wheelType ?? "(unknown)",
                _moduleType ?? "(none)",
                _hasDisplay ? "Basic (7-segment)" : "None",
                _revCount,
                _flagCount,
                _colorCount,
                _colorCount > 0 ? _colorFormat.ToString() : "N/A",
                _monoCount,
                total,
                mapped,
                _colorCount + _monoCount);

            // Append mapping details
            if (_inputMappingLeds != null && mapped > 0)
            {
                txtSummary.Text += "\n\nMappings:";
                foreach (var m in _inputMappingLeds)
                {
                    if (m.InputId != null)
                        txtSummary.Text += "\n  " + m.Label + " → " + m.InputId;
                }
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var profile = BuildProfile();
                SaveProfile(profile);

                // Hot-reload: re-read profiles and force capability re-evaluation
                // so the new profile takes effect immediately without restarting SimHub.
                WheelProfileStore.Reload();
                _plugin.SdkManager.RefreshCapabilities();

                MessageBox.Show(
                    "Profile saved and loaded!\n\n" +
                    "Your new profile is active immediately — no restart required.",
                    "Profile Saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to save profile:\n" + ex.Message,
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            ClearAllLeds();
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            ClearAllLeds();
            StopListeningForInput();
            CancelBlink();

            // Re-enable normal SimHub LED output and force a full resend
            // so the device instance picks up where it left off.
            _plugin.WizardActive = false;
            _plugin.Device?.ForceDirty();

            base.OnClosed(e);
        }

        // =====================================================================
        // Profile construction
        // =====================================================================

        private WheelProfile BuildProfile()
        {
            string id = _wheelType ?? "UNKNOWN";
            if (_moduleType != null)
                id += "_" + _moduleType;

            var profile = new WheelProfile
            {
                Id = id,
                Name = txtProfileName.Text,
                ShortName = txtProfileName.Text.Length > 20
                    ? txtProfileName.Text.Substring(0, 20)
                    : txtProfileName.Text,
                Match = new ProfileMatch
                {
                    WheelType = _wheelType,
                    ModuleType = _moduleType,
                },
                Display = _hasDisplay ? "basic" : "none",
                Leds = new List<LedDefinition>(),
            };

            if (_colorCount > 0 && _colorFormat == ColorFormat.Rgb555)
                profile.ColorFormatRaw = "rgb555";

            // Rev LEDs — hwIndex 0..N-1
            for (int i = 0; i < _revCount; i++)
            {
                profile.Leds.Add(new LedDefinition
                {
                    Channel = LedChannel.Rev,
                    HwIndex = i,
                    Role = LedRole.Rev,
                    Label = "Rev LED " + (i + 1),
                });
            }

            // Flag LEDs — hwIndex 0..N-1
            for (int i = 0; i < _flagCount; i++)
            {
                profile.Leds.Add(new LedDefinition
                {
                    Channel = LedChannel.Flag,
                    HwIndex = i,
                    Role = LedRole.Flag,
                    Label = "Flag LED " + (i + 1),
                });
            }

            // Colour LEDs — hwIndex 0..N-1
            for (int i = 0; i < _colorCount; i++)
            {
                var led = new LedDefinition
                {
                    Channel = LedChannel.Color,
                    HwIndex = i,
                    Role = LedRole.Button,
                    Label = "Button LED " + (i + 1),
                };

                // Apply input mapping if available
                var mapping = _inputMappingLeds?.FirstOrDefault(
                    m => m.Channel == LedChannel.Color && m.HwIndex == i);
                if (mapping?.InputId != null)
                    led.Input = mapping.InputId;

                profile.Leds.Add(led);
            }

            // Mono LEDs — hw indices start after the colour slots
            int monoStart = _colorCount;
            for (int i = 0; i < _monoCount; i++)
            {
                var led = new LedDefinition
                {
                    Channel = LedChannel.Mono,
                    HwIndex = monoStart + i,
                    Role = LedRole.Indicator,
                    Label = "Mono LED " + (i + 1),
                };

                var mapping = _inputMappingLeds?.FirstOrDefault(
                    m => m.Channel == LedChannel.Mono && m.HwIndex == monoStart + i);
                if (mapping?.InputId != null)
                    led.Input = mapping.InputId;

                profile.Leds.Add(led);
            }

            return profile;
        }

        private static void SaveProfile(WheelProfile profile)
        {
            string userDir = WheelProfileStore.GetUserProfileDirectory();
            if (userDir == null)
                throw new InvalidOperationException("Could not determine user profile directory.");

            string fileName = profile.Id.ToLowerInvariant().Replace(' ', '-') + ".json";
            string filePath = Path.Combine(userDir, fileName);

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Converters =
                {
                    new Newtonsoft.Json.Converters.StringEnumConverter(
                        new CamelCaseNamingStrategy(), allowIntegerValues: false)
                },
            };

            string json = JsonConvert.SerializeObject(profile, settings);
            File.WriteAllText(filePath, json);

            SimHub.Logging.Current.Info(
                "WheelProfileWizard: Saved profile to " + filePath);
        }
    }
}
