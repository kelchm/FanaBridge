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
    ///
    /// Architecture: The dialog is a thin view layer over <see cref="WizardState"/>,
    /// which is the single source of truth for all probe results, navigation,
    /// and input-mapping data.  User actions update the model immediately;
    /// the view re-renders from the model.  Probes always run when entering
    /// a section so that the hardware state matches the visible step,
    /// regardless of forward or backward navigation.
    /// </summary>
    public partial class WheelProfileWizardDialog : Window
    {
        private readonly FanatecPlugin _plugin;
        private FanatecDevice Device => _plugin.Device;

        // ── State model (single source of truth) ─────────────────────────
        private readonly WizardState _state = new WizardState();

        // ── Section → XAML panel lookup ──────────────────────────────────
        private Dictionary<WizardSection, UIElement> _panels;

        // ── Async hardware helpers ───────────────────────────────────────
        private volatile CancellationTokenSource _probeCts;
        private volatile CancellationTokenSource _blinkCts;
        private readonly ManualResetEventSlim _blinkDone = new ManualResetEventSlim(true);
        private bool _listeningForInput;
        private Action<string> _inputHandler;
        private DispatcherTimer _classifyTimer;

        // ─────────────────────────────────────────────────────────────────

        public WheelProfileWizardDialog(FanatecPlugin plugin)
        {
            InitializeComponent();

            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _panels = new Dictionary<WizardSection, UIElement>
            {
                { WizardSection.Welcome,              stepWelcome },
                { WizardSection.DisplayDiscovery,     stepDisplay },
                { WizardSection.RevDiscovery,         stepRevLeds },
                { WizardSection.FlagDiscovery,        stepFlagLeds },
                { WizardSection.ColorDiscovery,       stepColorLeds },
                { WizardSection.ColorFormatDiscovery, stepColorFormat },
                { WizardSection.MonoDiscovery,        stepMonoLeds },
                { WizardSection.InputMapping,         stepInputMapping },
                { WizardSection.Summary,              stepSummary },
            };

            // Pre-populate identity from the SDK
            var sdk = _plugin.SdkManager;
            _state.WheelType = WheelProfileStore.StripWheelPrefix(
                sdk.SteeringWheelType.ToString());
            _state.ModuleType =
                sdk.SubModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                    ? null
                    : WheelProfileStore.StripModulePrefix(sdk.SubModuleType.ToString());

            txtWizWheelType.Text = _state.WheelType ?? "Unknown";
            txtWizModuleType.Text = _state.ModuleType ?? "(none)";

            string defaultName = _state.WheelType ?? "Unknown Wheel";
            if (_state.ModuleType != null)
                defaultName += " + " + _state.ModuleType;
            txtProfileName.Text = defaultName;
            _state.ProfileName = defaultName;

            // Commit profile name changes immediately
            txtProfileName.TextChanged += (s, ev) => _state.ProfileName = txtProfileName.Text;

            // Commit count selections immediately (not deferred to navigation)
            cboRevCount.SelectionChanged += (s, ev) =>
            {
                if (_state.Rev.Answer == ProbeAnswer.Detected)
                    _state.Rev.Count = cboRevCount.SelectedIndex + 1;
                SyncNextButton();
            };
            cboFlagCount.SelectionChanged += (s, ev) =>
            {
                if (_state.Flag.Answer == ProbeAnswer.Detected)
                    _state.Flag.Count = cboFlagCount.SelectedIndex + 1;
                SyncNextButton();
            };
            cboColorCount.SelectionChanged += (s, ev) =>
            {
                if (_state.Color.Answer == ProbeAnswer.Detected)
                    _state.Color.Count = cboColorCount.SelectedIndex + 1;
                SyncNextButton();
            };
            cboMonoCount.SelectionChanged += (s, ev) =>
            {
                if (_state.Mono.Answer == ProbeAnswer.Detected)
                    _state.Mono.Count = cboMonoCount.SelectedIndex + 1;
                SyncNextButton();
            };

            // Suspend normal SimHub LED output while the wizard is active
            _plugin.WizardActive = true;

            NavigateToSection(WizardSection.Welcome);
        }

        // =====================================================================
        // Section navigation
        // =====================================================================

        /// <summary>
        /// Transitions the wizard to a named section.  Updates panel visibility,
        /// header text, and button state.  Always runs the section's hardware
        /// probe so LEDs/display match the visible step.
        /// </summary>
        private void NavigateToSection(WizardSection section)
        {
            // Tear down ALL async work from the previous section before
            // touching anything else.  Each call is a no-op when idle.
            StopClassifyTimer();
            StopListeningForInput();
            CancelBlink();
            CancelProbeBlink();

            _state.Section = section;

            // Show only the target panel
            foreach (var kv in _panels)
                kv.Value.Visibility = kv.Key == section
                    ? Visibility.Visible : Visibility.Collapsed;

            txtStepTitle.Text = WizardState.GetTitle(section);
            txtStepSubtitle.Text = WizardState.GetSubtitle(section);

            btnBack.IsEnabled = _state.CanGoBack;

            bool isInputStep = section == WizardSection.InputMapping;
            bool isFinalStep = section == WizardSection.Summary;
            btnNext.Visibility = (!isInputStep && !isFinalStep)
                ? Visibility.Visible : Visibility.Collapsed;
            btnSave.Visibility = isFinalStep
                ? Visibility.Visible : Visibility.Collapsed;

            SyncNextButton();

            RunProbe(section);
        }

        /// <summary>Enables/disables the Next button based on the current section state.</summary>
        private void SyncNextButton()
        {
            btnNext.IsEnabled = _state.CanGoNext;
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (!_state.CanGoNext) return;
            var next = _state.GetNextSection(_state.Section);
            if (next.HasValue)
                NavigateToSection(next.Value);
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            var prev = _state.GetPrevSection(_state.Section);
            if (prev.HasValue)
                NavigateToSection(prev.Value);
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

        /// <summary>
        /// Sets the hardware to the correct state for the given section.
        /// Probes are idempotent LED/display signals — always safe to re-run.
        /// We must keep hardware in sync with the visible section so the user
        /// sees the right LEDs whether navigating forward or back.
        /// </summary>
        private void RunProbe(WizardSection section)
        {
            switch (section)
            {
                case WizardSection.Welcome:
                    // Don't touch hardware on Welcome — no probe to show.
                    // Avoids clearing a display test pattern if the user
                    // navigates back to edit the profile name.
                    break;

                case WizardSection.DisplayDiscovery:
                    ProbeDisplay();
                    break;

                case WizardSection.RevDiscovery:
                    ProbeRevLeds();
                    break;

                case WizardSection.FlagDiscovery:
                    ProbeFlagLeds();
                    break;

                case WizardSection.ColorDiscovery:
                    ProbeColorLeds();
                    break;

                case WizardSection.ColorFormatDiscovery:
                    ProbeColorFormat();
                    break;

                case WizardSection.MonoDiscovery:
                    ProbeMonoLeds();
                    break;

                case WizardSection.InputMapping:
                    BeginInputMapping();
                    break;

                case WizardSection.Summary:
                    BuildSummary();
                    break;
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
            // Clear the display — the "888" test pattern from the previous
            // display-detection step should not linger.
            try { Device?.ClearDisplay(); } catch { }

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
            // Colors all black → color LEDs stay dark.
            // Blink intensity high/low so mono-only LEDs are hard to miss.
            CancelProbeBlink();
            var cts = new CancellationTokenSource();
            _probeCts = cts;
            var token = cts.Token;

            // Start with LEDs on
            var intensities = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
            for (int i = 0; i < intensities.Length; i++)
                intensities[i] = 7;
            SetAllLeds(buttonIntensities: intensities);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        Thread.Sleep(400);
                        if (token.IsCancellationRequested) break;

                        // Dim
                        var off = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                        SetAllLeds(buttonIntensities: off);

                        Thread.Sleep(400);
                        if (token.IsCancellationRequested) break;

                        // Bright
                        var on = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];
                        for (int i = 0; i < on.Length; i++)
                            on[i] = 7;
                        SetAllLeds(buttonIntensities: on);
                    }
                }
                catch { /* device disconnect */ }
            });
        }

        private void CancelProbeBlink()
        {
            var cts = _probeCts;
            if (cts != null)
            {
                cts.Cancel();
                _probeCts = null;
            }
        }

        // ── Color-format sub-test ────────────────────────────────────────
        //
        // Strategy: When this section activates, the probe sends green=128
        // encoded as RGB565.  This sets only bit 10.
        //
        //  • On true RGB565 hardware the LED appears green.
        //  • On RGB555 hardware the lower 5 green bits are all zero → dark/off.
        //
        // The user simply reports what they see:
        //  "LEDs are still green" → RGB565
        //  "LEDs are OFF or a completely wrong color" → RGB555
        //
        // No A/B switching required — one glance is enough.

        private void ProbeColorFormat()
        {
            try
            {
                // Green = 128 → g6 = 32 (0x20).  Only bit 10 is set.
                int count = Math.Max(_state.Color.Count, 1);
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

        // ── Answer helpers ─────────────────────────────────────────────

        /// <summary>
        /// Highlights the chosen button and dims siblings.  Call after
        /// updating the state model — does NOT modify state itself.
        /// </summary>
        private void HighlightChoice(object sender)
        {
            if (sender is Button btn && btn.Parent is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Button sibling)
                    {
                        sibling.Opacity = sibling == btn ? 1.0 : 0.45;
                        sibling.BorderBrush = sibling == btn
                            ? new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44))
                            : new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
                    }
                }
            }
        }

        // ── Display probe answers ────────────────────────────────────────

        private void DisplayYes_Click(object sender, RoutedEventArgs e)
        {
            _state.Display.Answer = ProbeAnswer.Detected;
            txtDisplayResult.Text = "\u2713 Display detected";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void DisplayNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _state.Display.Answer = ProbeAnswer.Inconclusive;
            txtDisplayResult.Text = "\u2713 Display present (probe inconclusive)";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void DisplayNo_Click(object sender, RoutedEventArgs e)
        {
            _state.Display.Answer = ProbeAnswer.NotPresent;
            txtDisplayResult.Text = "\u2713 No display";
            HighlightChoice(sender);
            SyncNextButton();
        }

        // ── Rev LED probe answers ────────────────────────────────────────

        private void RevSome_Click(object sender, RoutedEventArgs e)
        {
            _state.Rev.Answer = ProbeAnswer.Detected;
            panelRevCount.Visibility = Visibility.Visible;
            cboRevCount.SelectedIndex = 8; // default to 9
            _state.Rev.Count = 9;
            txtRevResult.Text = "";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void RevNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _state.Rev.Answer = ProbeAnswer.Inconclusive;
            _state.Rev.Count = 0;
            panelRevCount.Visibility = Visibility.Collapsed;
            txtRevResult.Text = "\u2713 Rev LEDs present but probe inconclusive";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void RevNone_Click(object sender, RoutedEventArgs e)
        {
            _state.Rev.Answer = ProbeAnswer.NotPresent;
            _state.Rev.Count = 0;
            panelRevCount.Visibility = Visibility.Collapsed;
            txtRevResult.Text = "\u2713 No rev LEDs";
            HighlightChoice(sender);
            SyncNextButton();
        }

        // ── Flag LED probe answers ───────────────────────────────────────

        private void FlagSome_Click(object sender, RoutedEventArgs e)
        {
            _state.Flag.Answer = ProbeAnswer.Detected;
            panelFlagCount.Visibility = Visibility.Visible;
            cboFlagCount.SelectedIndex = 5; // default to 6
            _state.Flag.Count = 6;
            txtFlagResult.Text = "";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void FlagNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _state.Flag.Answer = ProbeAnswer.Inconclusive;
            _state.Flag.Count = 0;
            panelFlagCount.Visibility = Visibility.Collapsed;
            txtFlagResult.Text = "\u2713 Flag LEDs present but probe inconclusive";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void FlagNone_Click(object sender, RoutedEventArgs e)
        {
            _state.Flag.Answer = ProbeAnswer.NotPresent;
            _state.Flag.Count = 0;
            panelFlagCount.Visibility = Visibility.Collapsed;
            txtFlagResult.Text = "\u2713 No flag LEDs";
            HighlightChoice(sender);
            SyncNextButton();
        }

        // ── Color LED probe answers ──────────────────────────────────────

        private void ColorSome_Click(object sender, RoutedEventArgs e)
        {
            _state.Color.Answer = ProbeAnswer.Detected;
            panelColorCount.Visibility = Visibility.Visible;
            cboColorCount.SelectedIndex = 11; // default to 12
            _state.Color.Count = 12;
            txtColorResult.Text = "";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void ColorNotWorking_Click(object sender, RoutedEventArgs e)
        {
            _state.Color.Answer = ProbeAnswer.Inconclusive;
            _state.Color.Count = 0;
            panelColorCount.Visibility = Visibility.Collapsed;
            txtColorResult.Text = "\u2713 Color LEDs present but probe inconclusive";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void ColorNone_Click(object sender, RoutedEventArgs e)
        {
            _state.Color.Answer = ProbeAnswer.NotPresent;
            _state.Color.Count = 0;
            panelColorCount.Visibility = Visibility.Collapsed;
            txtColorResult.Text = "\u2713 No color LEDs";
            HighlightChoice(sender);
            SyncNextButton();
        }

        // ── Color format answers ─────────────────────────────────────────

        private void ColorSame_Click(object sender, RoutedEventArgs e)
        {
            _state.ColorFormat.Answer = ProbeAnswer.Detected;
            _state.ColorFormat.Format = ColorFormat.Rgb565;
            txtColorFormatResult.Text = "\u2713 Standard RGB565 \u2014 6-bit green";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void ColorDark_Click(object sender, RoutedEventArgs e)
        {
            _state.ColorFormat.Answer = ProbeAnswer.Detected;
            _state.ColorFormat.Format = ColorFormat.Rgb555;
            txtColorFormatResult.Text = "\u2713 RGB555 \u2014 5-bit green (MSB ignored)";
            HighlightChoice(sender);
            SyncNextButton();
        }

        // ── Mono LED probe answers ───────────────────────────────────────

        private void MonoSome_Click(object sender, RoutedEventArgs e)
        {
            _state.Mono.Answer = ProbeAnswer.Detected;
            panelMonoCount.Visibility = Visibility.Visible;
            cboMonoCount.SelectedIndex = 0;
            _state.Mono.Count = 1;
            txtMonoResult.Text = "";
            HighlightChoice(sender);
            SyncNextButton();
        }

        private void MonoNone_Click(object sender, RoutedEventArgs e)
        {
            _state.Mono.Answer = ProbeAnswer.NotPresent;
            _state.Mono.Count = 0;
            panelMonoCount.Visibility = Visibility.Collapsed;
            txtMonoResult.Text = "\u2713 No mono LEDs";
            HighlightChoice(sender);
            SyncNextButton();
        }

        // =====================================================================
        // Input Mapping
        // =====================================================================
        //
        // For each color + mono button/encoder LED we:
        //   1. Light ONLY that LED (distinctive color).
        //   2. Subscribe to SimHub's PluginManager.InputPressed event.
        //   3. Wait for any button press or encoder rotation.
        //   4. Record the mapping in the state model.
        //
        // The mapping section is its own mini state machine driven by
        // InputMappingState and MappingPhase in the state model.

        /// <summary>
        /// Builds the list of LEDs that need mapping and starts the
        /// per-LED flow.
        /// </summary>
        private void BeginInputMapping()
        {
            var mapping = _state.Mapping;

            // Only build the LED list once (re-entering from Back keeps it)
            if (mapping.Leds.Count == 0)
            {
                for (int i = 0; i < _state.Color.Count; i++)
                {
                    mapping.Leds.Add(new InputMappingEntry
                    {
                        Channel = LedChannel.Color,
                        HwIndex = i,
                        Label = "Color LED " + (i + 1),
                    });
                }

                int monoStart = _state.Color.Count;
                for (int i = 0; i < _state.Mono.Count; i++)
                {
                    mapping.Leds.Add(new InputMappingEntry
                    {
                        Channel = LedChannel.Mono,
                        HwIndex = monoStart + i,
                        Label = "Mono LED " + (i + 1),
                    });
                }
            }

            if (mapping.Leds.Count == 0)
            {
                // Nothing to map — jump straight to summary
                var next = _state.GetNextSection(WizardSection.InputMapping);
                if (next.HasValue)
                    NavigateToSection(next.Value);
                return;
            }

            // Read the current encoder mode from the device (best-effort).
            if (mapping.EncoderMode == null)
            {
                mapping.EncoderMode = Device?.ReadEncoderMode();
                SimHub.Logging.Current.Info(
                    "WheelProfileWizard: Encoder mode = " +
                    (mapping.EncoderMode?.ToString() ?? "unknown"));
            }

            // When re-entering via Back after all LEDs were mapped, show the
            // last LED instead of bouncing forward to Summary again.
            if (mapping.IsComplete && mapping.Leds.Count > 0)
                mapping.CurrentIndex = mapping.Leds.Count - 1;

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
            // Wait for the blink thread to finish so its last HID write
            // can't race with the next LightSingleLed call.
            _blinkDone.Wait(500);
        }

        /// <summary>
        /// Lights the current LED and enters the auto-classification flow:
        /// collects inputs for a short window then classifies automatically.
        /// </summary>
        private void ShowInputMappingLed()
        {
            var mapping = _state.Mapping;

            if (mapping.IsComplete)
            {
                // All LEDs mapped — move to summary
                StopListeningForInput();
                var next = _state.GetNextSection(WizardSection.InputMapping);
                if (next.HasValue)
                    NavigateToSection(next.Value);
                return;
            }

            var entry = mapping.CurrentEntry;
            mapping.Phase = MappingPhase.WaitingForInput;

            // Update UI
            txtInputProgress.Text = string.Format(
                "LED {0} of {1}", mapping.CurrentIndex + 1, mapping.Leds.Count);
            txtInputLedLabel.Text = entry.Label;
            txtInputLedDetail.Text = string.Format(
                "{0} channel, hardware index {1}", entry.Channel, entry.HwIndex);

            // Reset detection state
            txtDetectedInput.Text = "Waiting for input\u2026";
            txtDetectedInput.Foreground = System.Windows.Media.Brushes.Gray;
            mapping.ClassifyInputs = new List<string>();
            StopClassifyTimer();

            // Hide phase-specific panels
            panelEncoderModeBanner.Visibility = Visibility.Collapsed;
            panelRelativeCapture.Visibility = Visibility.Collapsed;
            panelAbsoluteCapture.Visibility = Visibility.Collapsed;

            // Prev enabled only when not on first LED
            btnInputPrev.IsEnabled = mapping.CurrentIndex > 0;

            // Cancel any running blink to prevent ThreadPool race on
            // the shared HID report buffer.
            CancelBlink();

            // Set ALL channels in one pass — mirrors the production frame
            // loop.  Rev/flag go off, button lights the target LED only.
            LightSingleLed(entry);

            // Listen — auto-classification handler collects inputs
            _inputHandler = OnAutoClassifyInput;
            StartListeningForInput();
        }

        /// <summary>
        /// Auto-classification handler: collects unique input IDs during
        /// a short window and classifies as button or encoder automatically.
        /// </summary>
        private void OnAutoClassifyInput(string input)
        {
            var mapping = _state.Mapping;
            var inputs = mapping.ClassifyInputs;

            if (!inputs.Contains(input))
                inputs.Add(input);

            if (inputs.Count == 1)
            {
                mapping.Phase = MappingPhase.Classifying;

                // First input detected — show it and start the timer
                txtDetectedInput.Text = "\u2714 " + FormatInputDisplay(input);
                txtDetectedInput.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
                BlinkCurrentLed();

                _classifyTimer = new DispatcherTimer();
                _classifyTimer.Interval = TimeSpan.FromMilliseconds(800);
                _classifyTimer.Tick += OnClassifyTimerTick;
                _classifyTimer.Start();
            }
            else if (inputs.Count == 2)
            {
                // Second unique input — likely an encoder
                bool isAbsolute = mapping.EncoderMode == EncoderMode.Pulse ||
                                  mapping.EncoderMode == EncoderMode.Constant;

                if (isAbsolute)
                {
                    UpdateAutoClassifyStatus();
                }
                else
                {
                    // Relative/auto/unknown — two IDs = both directions
                    StopClassifyTimer();
                    StopListeningForInput();
                    ClassifyAsRelativeEncoder();
                }
            }
            else
            {
                // 3+ unique inputs (absolute encoder positions)
                UpdateAutoClassifyStatus();
            }
        }

        private void UpdateAutoClassifyStatus()
        {
            int n = _state.Mapping.ClassifyInputs.Count;
            txtDetectedInput.Text = "\u2714 " + n + " unique input" +
                (n == 1 ? "" : "s") + " detected";
            txtDetectedInput.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
        }

        private void OnClassifyTimerTick(object sender, EventArgs e)
        {
            StopClassifyTimer();
            StopListeningForInput();

            var mapping = _state.Mapping;
            int n = mapping.ClassifyInputs.Count;
            if (n == 0) return;

            bool isAbsolute = mapping.EncoderMode == EncoderMode.Pulse ||
                              mapping.EncoderMode == EncoderMode.Constant;

            if (n == 1)
            {
                ClassifyAsButton();
            }
            else if (n == 2 && !isAbsolute)
            {
                ClassifyAsRelativeEncoder();
            }
            else
            {
                ClassifyAsAbsoluteEncoder();
            }
        }

        private void StopClassifyTimer()
        {
            if (_classifyTimer != null)
            {
                _classifyTimer.Stop();
                _classifyTimer.Tick -= OnClassifyTimerTick;
                _classifyTimer = null;
            }
        }

        private void ClassifyAsButton()
        {
            var mapping = _state.Mapping;
            var entry = mapping.CurrentEntry;
            entry.IsEncoder = false;
            entry.ButtonInputId = mapping.ClassifyInputs[0];
            mapping.Phase = MappingPhase.Completed;
            AdvanceToNextLed();
        }

        private void ClassifyAsRelativeEncoder()
        {
            var mapping = _state.Mapping;
            var entry = mapping.CurrentEntry;
            var inputs = mapping.ClassifyInputs;

            entry.IsEncoder = true;
            entry.RelativeCW = inputs[0];
            entry.RelativeCCW = inputs.Count > 1 ? inputs[1] : null;
            mapping.Phase = MappingPhase.RelativeConfirm;

            ShowEncoderModeBanner();

            panelRelativeCapture.Visibility = Visibility.Visible;
            txtRelativePrompt.Text = "Direction 1: " + FormatInputDisplay(inputs[0]);

            if (inputs.Count > 1)
            {
                txtRelativeStatus.Text = "\u2714 Direction 2: " + FormatInputDisplay(inputs[1]);
                txtRelativeStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
                btnRelativeConfirm.IsEnabled = true;
            }
            else
            {
                txtRelativeStatus.Text = "Only one direction detected";
                txtRelativeStatus.Foreground = System.Windows.Media.Brushes.Orange;
                btnRelativeConfirm.IsEnabled = true;
            }
        }

        private void ClassifyAsAbsoluteEncoder()
        {
            var mapping = _state.Mapping;
            var entry = mapping.CurrentEntry;

            entry.IsEncoder = true;
            entry.AbsoluteInputs = new List<string>(mapping.ClassifyInputs);
            mapping.Phase = MappingPhase.AbsoluteCapture;

            ShowEncoderModeBanner();
            BeginAbsoluteCapture();
        }

        /// <summary>Lights a single LED with a distinctive color.
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
        // Encoder mode banner
        // -----------------------------------------------------------------

        private void ShowEncoderModeBanner()
        {
            string modeLabel;
            switch (_state.Mapping.EncoderMode)
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
        // Relative encoder confirmation
        // -----------------------------------------------------------------

        private void RelativeConfirm_Click(object sender, RoutedEventArgs e)
        {
            _state.Mapping.Phase = MappingPhase.Completed;
            AdvanceToNextLed();
        }

        // -----------------------------------------------------------------
        // Absolute encoder capture (all positions)
        // -----------------------------------------------------------------

        private void BeginAbsoluteCapture()
        {
            var entry = _state.Mapping.CurrentEntry;
            if (entry.AbsoluteInputs == null)
                entry.AbsoluteInputs = new List<string>();

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
                if (!entry.AbsoluteInputs.Contains(input))
                {
                    entry.AbsoluteInputs.Add(input);
                    int n = entry.AbsoluteInputs.Count;
                    txtAbsoluteCount.Text = n + " position" + (n == 1 ? "" : "s") + " detected";
                    txtAbsoluteCount.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x44, 0xDD, 0x44));
                    btnAbsoluteDone.IsEnabled = true;
                }
            };
            StartListeningForInput();
        }

        private void AbsoluteDone_Click(object sender, RoutedEventArgs e)
        {
            StopListeningForInput();
            _state.Mapping.Phase = MappingPhase.Completed;
            AdvanceToNextLed();
        }

        // -----------------------------------------------------------------
        // Input-mapping navigation helpers
        // -----------------------------------------------------------------

        private void AdvanceToNextLed()
        {
            StopClassifyTimer();
            StopListeningForInput();
            _state.Mapping.CurrentIndex++;
            ShowInputMappingLed();
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

            var entry = _state.Mapping.CurrentEntry;
            if (entry == null) return;

            _blinkDone.Reset();
            var cts = new CancellationTokenSource();
            _blinkCts = cts;
            var token = cts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
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
                finally
                {
                    _blinkDone.Set();
                }
            });
        }

        private void InputSkip_Click(object sender, RoutedEventArgs e)
        {
            StopClassifyTimer();
            StopListeningForInput();
            _state.Mapping.CurrentIndex++;
            ShowInputMappingLed();
        }

        private void InputRetry_Click(object sender, RoutedEventArgs e)
        {
            StopClassifyTimer();
            StopListeningForInput();

            var entry = _state.Mapping.CurrentEntry;
            if (entry != null)
            {
                entry.ButtonInputId = null;
                entry.RelativeCW = null;
                entry.RelativeCCW = null;
                entry.AbsoluteInputs = null;
                entry.IsEncoder = false;
            }

            ShowInputMappingLed();
        }

        private void InputPrev_Click(object sender, RoutedEventArgs e)
        {
            StopClassifyTimer();
            StopListeningForInput();
            CancelBlink();

            if (_state.Mapping.CurrentIndex > 0)
            {
                _state.Mapping.CurrentIndex--;
                ShowInputMappingLed();
            }
        }

        // =====================================================================
        // Summary & Save
        // =====================================================================

        private void BuildSummary()
        {
            ClearAllLeds();

            var s = _state;
            int revCount = s.Rev.Count;
            int flagCount = s.Flag.Count;
            int colorCount = s.Color.Count;
            int monoCount = s.Mono.Count;
            int total = revCount + flagCount + colorCount + monoCount;
            int mapped = s.Mapping.MappedCount;

            string fmt =
                "Profile:       {0}\n" +
                "Wheel:         {1}\n" +
                "Module:        {2}\n\n" +
                "Display:       {3}\n" +
                "Rev LEDs:      {4}\n" +
                "Flag LEDs:     {5}\n" +
                "Color LEDs:    {6}\n" +
                "Color Format:  {7}\n" +
                "Mono LEDs:     {8}\n\n" +
                "Total LEDs:    {9}\n" +
                "Input mapped:  {10} of {11}";

            txtSummary.Text = string.Format(fmt,
                s.ProfileName,
                s.WheelType ?? "(unknown)",
                s.ModuleType ?? "(none)",
                s.Display.HasDisplay ? "Basic (7-segment)" : "None",
                revCount,
                flagCount,
                colorCount,
                colorCount > 0 ? s.ColorFormat.Format.ToString() : "N/A",
                monoCount,
                total,
                mapped,
                colorCount + monoCount);

            // Append mapping details
            if (mapped > 0)
            {
                txtSummary.Text += "\n\nMappings:";
                foreach (var m in s.Mapping.Leds)
                {
                    if (!m.HasAny) continue;
                    txtSummary.Text += "\n  " + m.Label;
                    if (m.ButtonInputId != null)
                        txtSummary.Text += " \u2192 " + FormatInputDisplay(m.ButtonInputId);
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
                    "Your new profile is active immediately \u2014 no restart required.",
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
            CancelProbeBlink();
            StopClassifyTimer();
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
            var s = _state;
            string id = s.WheelType ?? "UNKNOWN";
            if (s.ModuleType != null)
                id += "_" + s.ModuleType;

            var profile = new WheelProfile
            {
                Id = id,
                Name = s.ProfileName,
                ShortName = s.ProfileName.Length > 20
                    ? s.ProfileName.Substring(0, 20)
                    : s.ProfileName,
                Match = new ProfileMatch
                {
                    WheelType = s.WheelType,
                    ModuleType = s.ModuleType,
                },
                Display = s.Display.HasDisplay ? "basic" : "none",
                Leds = new List<LedDefinition>(),
            };

            int colorCount = s.Color.Count;
            int monoCount = s.Mono.Count;

            if (colorCount > 0 && s.ColorFormat.Format == ColorFormat.Rgb555)
                profile.ColorFormatRaw = "rgb555";

            // Rev LEDs — hwIndex 0..N-1
            for (int i = 0; i < s.Rev.Count; i++)
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
            for (int i = 0; i < s.Flag.Count; i++)
            {
                profile.Leds.Add(new LedDefinition
                {
                    Channel = LedChannel.Flag,
                    HwIndex = i,
                    Role = LedRole.Flag,
                    Label = "Flag LED " + (i + 1),
                });
            }

            // Color LEDs — hwIndex 0..N-1
            for (int i = 0; i < colorCount; i++)
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

            // Mono LEDs — hw indices start after the color slots
            int monoStart = colorCount;
            for (int i = 0; i < monoCount; i++)
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
            var entry = _state.Mapping.Leds.FirstOrDefault(
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
