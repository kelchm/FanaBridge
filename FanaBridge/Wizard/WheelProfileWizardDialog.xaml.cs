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
        /// <summary>Callback invoked on the UI thread when an input is detected.</summary>
        private Action<string> _inputHandler;
        /// <summary>Encoder mode read from the device at the start of input mapping.</summary>
        private EncoderMode? _encoderMode;

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
            switch (step)
            {
                case 0: SetAllLedsOff();   break;
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

        /// <summary>
        /// Sets all LED channels to the desired state in one pass, mirroring
        /// the production frame loop.  Null parameters default to all-off.
        /// Dirty tracking skips unchanged channels automatically.
        /// </summary>
        /// <remarks>
        /// HACK: The 1 ms sleeps between writes work around a firmware timing
        /// issue — back-to-back col03 reports sent without any gap are
        /// intermittently dropped by the device.  In production this never
        /// occurs because writes are spaced by the ~16 ms frame interval.
        /// A proper fix would be a rate-limited serial write queue in
        /// <see cref="FanatecDevice"/>, but that's overkill while only the
        /// wizard triggers rapid multi-channel writes.
        /// </remarks>
        private void SetAllLeds(
            ushort[] revColors = null,
            ushort[] flagColors = null,
            ushort[] buttonColors = null,
            byte[] buttonIntensities = null)
        {
            try
            {
                if (Device == null || !Device.IsConnected) return;
                Device.SetRevLedColors(revColors ?? new ushort[9]);
                Thread.Sleep(1);
                Device.SetFlagLedColors(flagColors ?? new ushort[6]);
                Thread.Sleep(1);
                Device.SetButtonLedState(
                    buttonColors ?? new ushort[12],
                    buttonIntensities ?? new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE]);
            }
            catch { /* best-effort */ }
        }

        /// <summary>Turns off every LED channel and clears the display.</summary>
        private void SetAllLedsOff()
        {
            SetAllLeds();
            try { Device?.ClearDisplay(); } catch { }
        }

        /// <summary>Turns off every LED channel and clears the display.
        /// Used for cleanup on cancel/close/summary.</summary>
        private void ClearAllLeds()
        {
            SetAllLedsOff();
        }

        private void ProbeDisplay()
        {
            SetAllLeds(); // all LEDs off
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
            var colors = new ushort[9];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = ColorHelper.Colors.Red;
            SetAllLeds(revColors: colors);
        }

        private void ProbeFlagLeds()
        {
            var colors = new ushort[6];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = ColorHelper.Colors.Blue;
            SetAllLeds(flagColors: colors);
        }

        private void ProbeColorLeds()
        {
            var colors = new ushort[12];
            var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
            for (int i = 0; i < 12; i++)
            {
                colors[i] = ColorHelper.Colors.Green;
                intensities[i] = 7;
            }
            SetAllLeds(buttonColors: colors, buttonIntensities: intensities);
        }

        private void ProbeMonoLeds()
        {
            // Colours all black → colour LEDs stay dark.
            // Intensity max on every slot → mono-only LEDs light up.
            var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
            for (int i = 0; i < intensities.Length; i++)
                intensities[i] = 7;
            SetAllLeds(buttonIntensities: intensities);
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
                var colors = new ushort[12];
                var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                ushort test = ColorHelper.RgbToRgb565(0, 128, 0);
                for (int i = 0; i < count; i++)
                {
                    colors[i] = test;
                    intensities[i] = 7;
                }
                SetAllLeds(buttonColors: colors, buttonIntensities: intensities);
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

            // --- Captured data ---
            /// <summary>True if user classified this as an encoder.</summary>
            public bool IsEncoder;
            /// <summary>Button input ID (non-encoder), or null.</summary>
            public string ButtonInputId;
            /// <summary>Relative encoder CW input, or null.</summary>
            public string RelativeCW;
            /// <summary>Relative encoder CCW input, or null.</summary>
            public string RelativeCCW;
            /// <summary>Absolute encoder position inputs, or null.</summary>
            public List<string> AbsoluteInputs;

            /// <summary>True when at least one input has been captured.</summary>
            public bool HasAny =>
                ButtonInputId != null ||
                RelativeCW != null ||
                (AbsoluteInputs != null && AbsoluteInputs.Count > 0);
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

            // Read the current encoder mode from the device (best-effort).
            // This informs which capture flow we show for encoder LEDs.
            _encoderMode = Device?.ReadEncoderMode();
            SimHub.Logging.Current.Info(
                "WheelProfileWizard: Encoder mode = " + (_encoderMode?.ToString() ?? "unknown"));

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
        /// We marshal to the UI thread and dispatch to the current _inputHandler.
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
                _inputHandler?.Invoke(input);
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
        /// Lights the current LED and enters Phase 1: waiting for the
        /// first input to arrive so we can classify it.
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

            // Reset detection state
            txtDetectedInput.Text = "Press a button or turn an encoder\u2026";
            txtDetectedInput.Foreground = System.Windows.Media.Brushes.Gray;
            _detectedInputId = null;

            // Show Phase 1 panels, hide others
            panelInputActions.Visibility = Visibility.Visible;
            panelInputType.Visibility = Visibility.Collapsed;
            panelEncoderModeBanner.Visibility = Visibility.Collapsed;
            panelRelativeCapture.Visibility = Visibility.Collapsed;
            panelAbsoluteCapture.Visibility = Visibility.Collapsed;

            // Cancel any running blink to prevent ThreadPool race on
            // the shared HID report buffer.
            CancelBlink();

            // Set ALL channels in one pass — mirrors the production frame
            // loop.  Rev/flag go off, button lights the target LED only.
            LightSingleLed(entry);

            // Listen — Phase 1: first detection shows type-choice panel
            _inputHandler = OnFirstInputDetected;
            StartListeningForInput();
        }

        /// <summary>
        /// Phase 1 handler: first input detected — show it and ask for type.
        /// </summary>
        private void OnFirstInputDetected(string input)
        {
            StopListeningForInput();
            _detectedInputId = input;

            txtDetectedInput.Text = "\u2714 " + FormatInputDisplay(input);
            txtDetectedInput.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));

            BlinkCurrentLed();

            // Show type classification, hide Phase 1 buttons
            panelInputActions.Visibility = Visibility.Collapsed;
            panelInputType.Visibility = Visibility.Visible;
        }

        /// <summary>Lights a single LED with a distinctive colour.
        /// Sets ALL channels (rev/flag off) to mirror production pattern.</summary>
        private void LightSingleLed(InputMappingEntry entry)
        {
            try
            {
                var colors = new ushort[12];
                var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];

                if (entry.Channel == LedChannel.Color)
                {
                    colors[entry.HwIndex] = ColorHelper.Colors.Cyan;
                    intensities[entry.HwIndex] = 7;
                }
                else if (entry.Channel == LedChannel.Mono)
                {
                    intensities[entry.HwIndex] = 7;
                }

                SetAllLeds(buttonColors: colors, buttonIntensities: intensities);
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

        /// <summary>Strips the JoystickPlugin prefix for display.</summary>
        private static string FormatInputDisplay(string inputId)
        {
            if (inputId == null) return "";
            int dot = inputId.IndexOf('.');
            return (dot >= 0 && dot < inputId.Length - 1)
                ? inputId.Substring(dot + 1)
                : inputId;
        }

        // -----------------------------------------------------------------
        // Phase 2: Type classification handlers
        // -----------------------------------------------------------------

        private void InputTypeButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = _inputMappingLeds[_inputMappingIndex];
            entry.IsEncoder = false;
            entry.ButtonInputId = _detectedInputId;
            AdvanceToNextLed();
        }

        private void InputTypeEncoder_Click(object sender, RoutedEventArgs e)
        {
            var entry = _inputMappingLeds[_inputMappingIndex];
            entry.IsEncoder = true;

            // Show the encoder mode banner
            ShowEncoderModeBanner();

            // Route to the appropriate capture flow based on the
            // device's current encoder mode.
            bool isAbsolute = _encoderMode == EncoderMode.Pulse ||
                              _encoderMode == EncoderMode.Constant;

            if (isAbsolute)
            {
                // First detected input is one absolute position
                entry.AbsoluteInputs = new List<string> { _detectedInputId };
                BeginAbsoluteCapture();
            }
            else
            {
                // Relative / Encoder / Auto / unknown — capture CW then CCW
                BeginRelativeCapture(entry, _detectedInputId);
            }
        }

        private void ShowEncoderModeBanner()
        {
            string modeLabel;
            switch (_encoderMode)
            {
                case EncoderMode.Encoder:  modeLabel = "Relative (Encoder)"; break;
                case EncoderMode.Pulse:    modeLabel = "Absolute (Pulse)"; break;
                case EncoderMode.Constant: modeLabel = "Absolute (Constant)"; break;
                case EncoderMode.Auto:     modeLabel = "Auto"; break;
                default:                   modeLabel = "Unknown"; break;
            }
            txtEncoderMode.Text = "\u2699 Encoder mode: " + modeLabel;
            panelEncoderModeBanner.Visibility = Visibility.Visible;
        }

        // -----------------------------------------------------------------
        // Phase 3a: Relative encoder capture (CW then CCW)
        // -----------------------------------------------------------------

        private void BeginRelativeCapture(InputMappingEntry entry, string firstInput)
        {
            entry.RelativeCW = firstInput;
            entry.RelativeCCW = null;

            panelInputType.Visibility = Visibility.Collapsed;
            panelRelativeCapture.Visibility = Visibility.Visible;

            txtRelativePrompt.Text = "CW detected: " + FormatInputDisplay(firstInput) +
                "\nNow rotate the OTHER direction.";
            txtRelativeStatus.Text = "Waiting for counter-clockwise input\u2026";
            txtRelativeStatus.Foreground = System.Windows.Media.Brushes.Gray;
            btnRelativeConfirm.IsEnabled = false;

            _inputHandler = input =>
            {
                StopListeningForInput();
                entry.RelativeCCW = input;
                txtRelativeStatus.Text = "\u2714 CCW: " + FormatInputDisplay(input);
                txtRelativeStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
                btnRelativeConfirm.IsEnabled = true;
                BlinkCurrentLed();
            };
            StartListeningForInput();
        }

        private void RelativeConfirm_Click(object sender, RoutedEventArgs e)
        {
            // Relative capture done — advance to next LED
            AdvanceToNextLed();
        }

        // -----------------------------------------------------------------
        // Phase 3b: Absolute encoder capture (all positions)
        // -----------------------------------------------------------------

        private void BeginAbsoluteCapture()
        {
            var entry = _inputMappingLeds[_inputMappingIndex];
            if (entry.AbsoluteInputs == null)
                entry.AbsoluteInputs = new List<string>();

            panelInputType.Visibility = Visibility.Collapsed;
            panelRelativeCapture.Visibility = Visibility.Collapsed;
            panelAbsoluteCapture.Visibility = Visibility.Visible;

            int already = entry.AbsoluteInputs.Count;
            txtAbsolutePrompt.Text = "Slowly rotate through ALL positions, then click Done.";
            txtAbsoluteCount.Text = already > 0
                ? already + " position" + (already == 1 ? "" : "s") + " detected"
                : "0 positions detected";
            txtAbsoluteCount.Foreground = already > 0
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44))
                : System.Windows.Media.Brushes.Gray;
            btnAbsoluteDone.IsEnabled = already > 0;

            _inputHandler = input =>
            {
                // Accumulate unique inputs in detection order
                if (!entry.AbsoluteInputs.Contains(input))
                {
                    entry.AbsoluteInputs.Add(input);
                    int n = entry.AbsoluteInputs.Count;
                    txtAbsoluteCount.Text = n + " position" + (n == 1 ? "" : "s") + " detected";
                    txtAbsoluteCount.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
                    btnAbsoluteDone.IsEnabled = true;
                }
                // Keep listening for more positions (don't stop)
            };
            StartListeningForInput();
        }

        private void AbsoluteDone_Click(object sender, RoutedEventArgs e)
        {
            StopListeningForInput();
            AdvanceToNextLed();
        }



        // -----------------------------------------------------------------
        // Navigation helpers
        // -----------------------------------------------------------------

        private void AdvanceToNextLed()
        {
            StopInputPoll();
            _inputMappingIndex++;
            ShowInputMappingLed();
        }

        private void SetDetectedInput(string inputId)
        {
            // Legacy helper kept for backward compat in case anything calls it
            _detectedInputId = inputId;
            txtDetectedInput.Text = "\u2714 " + FormatInputDisplay(inputId);
            txtDetectedInput.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
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
            // Currently unused — type-specific buttons handle confirmation.
            // Kept in case XAML still references it.
            AdvanceToNextLed();
        }

        private void InputSkip_Click(object sender, RoutedEventArgs e)
        {
            StopInputPoll();
            _inputMappingIndex++;
            ShowInputMappingLed();
        }

        private void InputRetry_Click(object sender, RoutedEventArgs e)
        {
            StopListeningForInput();
            _detectedInputId = null;

            // Reset the current entry
            var entry = _inputMappingLeds[_inputMappingIndex];
            entry.ButtonInputId = null;
            entry.RelativeCW = null;
            entry.RelativeCCW = null;
            entry.AbsoluteInputs = null;
            entry.IsEncoder = false;

            // Re-show from Phase 1
            ShowInputMappingLed();
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
                mapped = _inputMappingLeds.Count(m => m.HasAny);

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
                    if (!m.HasAny) continue;
                    txtSummary.Text += "\n  " + m.Label;
                    if (m.ButtonInputId != null)
                        txtSummary.Text += " → " + FormatInputDisplay(m.ButtonInputId);
                    if (m.RelativeCW != null || m.RelativeCCW != null)
                        txtSummary.Text += " [rel: " +
                            FormatInputDisplay(m.RelativeCW) + " / " +
                            FormatInputDisplay(m.RelativeCCW) + "]";
                    if (m.AbsoluteInputs != null && m.AbsoluteInputs.Count > 0)
                        txtSummary.Text += " [abs: " + m.AbsoluteInputs.Count + " pos]";
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

                ApplyInputMapping(led, LedChannel.Color, i);
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

                ApplyInputMapping(led, LedChannel.Mono, monoStart + i);
                profile.Leds.Add(led);
            }

            return profile;
        }

        /// <summary>
        /// Copies wizard-captured input data into a <see cref="LedDefinition"/>.
        /// Produces an <see cref="InputMapping"/> when any inputs were captured,
        /// and also sets the legacy <see cref="LedDefinition.Input"/> for backward compat.
        /// </summary>
        private void ApplyInputMapping(LedDefinition led, LedChannel channel, int hwIndex)
        {
            var entry = _inputMappingLeds?.FirstOrDefault(
                m => m.Channel == channel && m.HwIndex == hwIndex);
            if (entry == null || !entry.HasAny) return;

            // Update role if we learned this is an encoder
            if (entry.IsEncoder)
                led.Role = LedRole.Encoder;

            var mapping = new InputMapping();

            if (!entry.IsEncoder && entry.ButtonInputId != null)
            {
                mapping.Button = entry.ButtonInputId;
                led.Input = entry.ButtonInputId; // legacy compat
            }

            if (entry.RelativeCW != null || entry.RelativeCCW != null)
            {
                mapping.Relative = new List<string>();
                if (entry.RelativeCW != null) mapping.Relative.Add(entry.RelativeCW);
                if (entry.RelativeCCW != null) mapping.Relative.Add(entry.RelativeCCW);
                led.Input = led.Input ?? entry.RelativeCW; // legacy compat
            }

            if (entry.AbsoluteInputs != null && entry.AbsoluteInputs.Count > 0)
            {
                mapping.Absolute = entry.AbsoluteInputs;
                led.Input = led.Input ?? entry.AbsoluteInputs[0]; // legacy compat
            }

            if (mapping.HasAny)
                led.InputMapping = mapping;
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
