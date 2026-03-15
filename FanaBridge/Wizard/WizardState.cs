using System.Collections.Generic;
using System.Linq;
using FanaBridge.Core;

namespace FanaBridge.Wizard
{
    // ── Wizard sections (replace magic step numbers) ─────────────────────

    /// <summary>
    /// Named sections of the wheel profile wizard.
    /// Navigation logic uses these instead of raw integer indices.
    /// </summary>
    public enum WizardSection
    {
        Welcome,
        DisplayDiscovery,
        RevDiscovery,
        FlagDiscovery,
        ColorDiscovery,
        ColorFormatDiscovery,
        MonoDiscovery,
        InputMapping,
        Summary,
    }

    // ── Input-mapping sub-phases ─────────────────────────────────────────

    /// <summary>
    /// Phases within the input-mapping section.  Each LED walks through
    /// a subset of these depending on what type of input is detected.
    /// </summary>
    public enum MappingPhase
    {
        /// <summary>LED is lit, waiting for any input event.</summary>
        WaitingForInput,
        /// <summary>Collecting unique inputs during the classification window.</summary>
        Classifying,
        /// <summary>Relative encoder detected — showing confirmation panel.</summary>
        RelativeConfirm,
        /// <summary>Absolute encoder detected — collecting all positions.</summary>
        AbsoluteCapture,
        /// <summary>Classification complete — auto-advancing to next LED.</summary>
        Completed,
    }

    // ── Probe answer (shared across discovery sections) ──────────────────

    /// <summary>
    /// The three possible outcomes for any LED/display probe question.
    /// </summary>
    public enum ProbeAnswer
    {
        /// <summary>User hasn't answered yet.</summary>
        Unanswered,
        /// <summary>"Yes — I see it" (hardware present and working).</summary>
        Detected,
        /// <summary>"I have one, but the test didn't work" (present, inconclusive).</summary>
        Inconclusive,
        /// <summary>"My wheel doesn't have this" (not present).</summary>
        NotPresent,
    }

    // ── Per-section state classes ────────────────────────────────────────

    /// <summary>State for a simple yes/no detection section (Display).</summary>
    public class DisplayProbeState
    {
        public ProbeAnswer Answer { get; set; }
        public bool HasDisplay => Answer == ProbeAnswer.Detected || Answer == ProbeAnswer.Inconclusive;
        public bool IsAnswered => Answer != ProbeAnswer.Unanswered;
    }

    /// <summary>State for an LED detection section that also captures a count.</summary>
    public class LedProbeState
    {
        public ProbeAnswer Answer { get; set; }
        public int Count { get; set; }
        public bool HasLeds => Count > 0;
        public bool IsAnswered => Answer != ProbeAnswer.Unanswered;

        /// <summary>
        /// True when the section has enough data to continue:
        /// answered, and if user said "detected" we also need a count > 0.
        /// </summary>
        public bool CanContinue =>
            Answer == ProbeAnswer.NotPresent ||
            Answer == ProbeAnswer.Inconclusive ||
            (Answer == ProbeAnswer.Detected && Count > 0);
    }

    /// <summary>State for the color-format discrimination step.</summary>
    public class ColorFormatProbeState
    {
        public ProbeAnswer Answer { get; set; }
        public ColorFormat Format { get; set; } = ColorFormat.Rgb565;
        public bool IsAnswered => Answer != ProbeAnswer.Unanswered;
    }

    // ── Input-mapping entry ──────────────────────────────────────────────

    /// <summary>Tracks one LED that needs an input mapping.</summary>
    public class InputMappingEntry
    {
        public LedChannel Channel { get; set; }
        public int HwIndex { get; set; }
        public string Label { get; set; }

        // --- Captured data ---
        public bool IsEncoder { get; set; }
        public string ButtonInputId { get; set; }
        public string RelativeCW { get; set; }
        public string RelativeCCW { get; set; }
        public List<string> AbsoluteInputs { get; set; }

        public bool HasAny =>
            ButtonInputId != null ||
            RelativeCW != null ||
            (AbsoluteInputs != null && AbsoluteInputs.Count > 0);
    }

    /// <summary>State for the entire input-mapping section.</summary>
    public class InputMappingState
    {
        public List<InputMappingEntry> Leds { get; set; } = new List<InputMappingEntry>();
        public int CurrentIndex { get; set; }
        public MappingPhase Phase { get; set; }
        public EncoderMode? EncoderMode { get; set; }

        /// <summary>Unique input IDs collected during auto-classification.</summary>
        public List<string> ClassifyInputs { get; set; } = new List<string>();

        public InputMappingEntry CurrentEntry =>
            (CurrentIndex >= 0 && CurrentIndex < Leds.Count) ? Leds[CurrentIndex] : null;

        public bool IsComplete => CurrentIndex >= Leds.Count;
        public int MappedCount => Leds.Count(m => m.HasAny);
    }

    // ── Main wizard state ────────────────────────────────────────────────

    /// <summary>
    /// Single source of truth for the wizard's state.
    /// The dialog code-behind renders this state and dispatches user actions
    /// that update it.  Navigation, completion, and probe decisions all
    /// derive from this object — not from UI visibility or control values.
    /// </summary>
    public class WizardState
    {
        // ── Identity (from SDK, set once at construction) ────────────────
        public string WheelType { get; set; }
        public string ModuleType { get; set; }
        public string ProfileName { get; set; }

        // ── Current section ──────────────────────────────────────────────
        public WizardSection Section { get; set; } = WizardSection.Welcome;

        // ── Per-section state ────────────────────────────────────────────
        public DisplayProbeState Display { get; } = new DisplayProbeState();
        public LedProbeState Rev { get; } = new LedProbeState();
        public LedProbeState Flag { get; } = new LedProbeState();
        public LedProbeState Color { get; } = new LedProbeState();
        public ColorFormatProbeState ColorFormat { get; } = new ColorFormatProbeState();
        public LedProbeState Mono { get; } = new LedProbeState();
        public InputMappingState Mapping { get; } = new InputMappingState();

        // ── Navigation ───────────────────────────────────────────────────

        /// <summary>The ordered list of all possible sections.</summary>
        private static readonly WizardSection[] AllSections =
        {
            WizardSection.Welcome,
            WizardSection.DisplayDiscovery,
            WizardSection.RevDiscovery,
            WizardSection.FlagDiscovery,
            WizardSection.ColorDiscovery,
            WizardSection.ColorFormatDiscovery,
            WizardSection.MonoDiscovery,
            WizardSection.InputMapping,
            WizardSection.Summary,
        };

        /// <summary>Whether the current section has enough data to advance.</summary>
        public bool CanGoNext
        {
            get
            {
                switch (Section)
                {
                    case WizardSection.Welcome: return true;
                    case WizardSection.DisplayDiscovery: return Display.IsAnswered;
                    case WizardSection.RevDiscovery: return Rev.CanContinue;
                    case WizardSection.FlagDiscovery: return Flag.CanContinue;
                    case WizardSection.ColorDiscovery: return Color.CanContinue;
                    case WizardSection.ColorFormatDiscovery: return ColorFormat.IsAnswered;
                    case WizardSection.MonoDiscovery: return Mono.CanContinue;
                    case WizardSection.InputMapping: return Mapping.IsComplete;
                    case WizardSection.Summary: return false; // Save, not Next
                    default: return false;
                }
            }
        }

        public bool CanGoBack => Section != WizardSection.Welcome;

        /// <summary>Whether the given section should be shown (not skipped).</summary>
        public bool IsSectionRelevant(WizardSection section)
        {
            switch (section)
            {
                case WizardSection.ColorFormatDiscovery:
                    return Color.Count > 0;
                case WizardSection.InputMapping:
                    return Color.Count + Mono.Count > 0;
                default:
                    return true;
            }
        }

        /// <summary>Returns the next relevant section after <paramref name="current"/>.</summary>
        public WizardSection? GetNextSection(WizardSection current)
        {
            int idx = System.Array.IndexOf(AllSections, current);
            for (int i = idx + 1; i < AllSections.Length; i++)
            {
                if (IsSectionRelevant(AllSections[i]))
                    return AllSections[i];
            }
            return null;
        }

        /// <summary>Returns the previous relevant section before <paramref name="current"/>.</summary>
        public WizardSection? GetPrevSection(WizardSection current)
        {
            int idx = System.Array.IndexOf(AllSections, current);
            for (int i = idx - 1; i >= 0; i--)
            {
                if (IsSectionRelevant(AllSections[i]))
                    return AllSections[i];
            }
            return null;
        }

        // ── Section metadata ─────────────────────────────────────────────

        public static string GetTitle(WizardSection section)
        {
            switch (section)
            {
                case WizardSection.Welcome: return "Welcome";
                case WizardSection.DisplayDiscovery: return "Display Detection";
                case WizardSection.RevDiscovery: return "Rev / RPM LED Detection";
                case WizardSection.FlagDiscovery: return "Flag / Status LED Detection";
                case WizardSection.ColorDiscovery: return "Button Color LED Detection";
                case WizardSection.ColorFormatDiscovery: return "Green Channel Test";
                case WizardSection.MonoDiscovery: return "Monochrome LED Detection";
                case WizardSection.InputMapping: return "Input Mapping";
                case WizardSection.Summary: return "Summary";
                default: return "";
            }
        }

        public static string GetSubtitle(WizardSection section)
        {
            switch (section)
            {
                case WizardSection.Welcome: return "Create a hardware profile for an unsupported wheel";
                case WizardSection.DisplayDiscovery: return "Testing the 7-segment display";
                case WizardSection.RevDiscovery: return "Testing subcmd 0x00 \u2014 RPM / shift-indicator LEDs";
                case WizardSection.FlagDiscovery: return "Testing subcmd 0x01 \u2014 flag / status LEDs";
                case WizardSection.ColorDiscovery: return "Testing subcmd 0x02 \u2014 button backlight LEDs";
                case WizardSection.ColorFormatDiscovery: return "Determining the color encoding format";
                case WizardSection.MonoDiscovery: return "Testing subcmd 0x03 \u2014 intensity-only LEDs";
                case WizardSection.InputMapping: return "Map each LED to its physical button or encoder";
                case WizardSection.Summary: return "Review and save your new wheel profile";
                default: return "";
            }
        }
    }
}
