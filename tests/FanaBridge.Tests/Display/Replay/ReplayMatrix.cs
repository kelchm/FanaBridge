using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanaBridge.Display.Rules;

namespace FanaBridge.Tests.Display.Replay
{
    // ── Axis enums (seam-map §4.5 + adjudication: ActionTriggered dropped) ─

    internal enum ReplayTarget
    {
        Page,
        SegmentScreen,
        Cycle,
        Special,
    }

    /// <summary>
    /// Condition kinds for the matrix. ActionTriggered is intentionally absent
    /// (adjudication FA2 — no honest v2 pair).
    /// </summary>
    internal enum ReplayCondition
    {
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        Equals,
        NotEquals,
        IsTrue,
        IsFalse,
        Changes,
        Increases,
        Decreases,
    }

    internal enum ReplayHold
    {
        WhileActive,
        ForDuration,
        UntilDismissed,
    }

    internal enum ReplayRuns
    {
        InGame,
        Idle,
        Always,
    }

    internal enum ReplayDevice
    {
        /// <summary>ITM device 3 (CSSWFORMV3 / PBME catalog envelope).</summary>
        Pbme,
        /// <summary>ITM device 4 (PSWBENT / provisional Bentley catalog).</summary>
        Bentley,
        /// <summary>Segment-only basic display (PSWBMW).</summary>
        SegmentOnly,
    }

    internal enum ReplayKnowledge
    {
        UnknownAtConnect,
        KnownPage,
    }

    internal enum ReplayPress
    {
        None,
        ManualPress,
        AdoptedPress,
        RejectOnRevert,
    }

    internal enum ReplayWire
    {
        Clean,
        DeclinedSends,
        LifecycleRecovery,
    }

    internal enum ReplayBlock
    {
        Anchored,
        Pairwise,
        KeptBehavior,
        Hysteresis,
    }

    /// <summary>One closed-matrix cell (axes + fixture id + optional known diffs).</summary>
    internal sealed class ReplayCell
    {
        public ReplayCell(
            string id,
            ReplayBlock block,
            ReplayDevice device,
            ReplayTarget target,
            ReplayCondition condition,
            ReplayHold hold,
            ReplayRuns runs,
            ReplayKnowledge knowledge,
            ReplayPress press,
            ReplayWire wire,
            string? unrepresentableReason = null,
            IReadOnlyList<KnownDiff>? knownDiffs = null,
            string? keptBehaviorName = null,
            double? hysteresis = null,
            string? notes = null)
        {
            Id = id;
            Block = block;
            Device = device;
            Target = target;
            Condition = condition;
            Hold = hold;
            Runs = runs;
            Knowledge = knowledge;
            Press = press;
            Wire = wire;
            UnrepresentableReason = unrepresentableReason;
            KnownDiffs = knownDiffs ?? Array.Empty<KnownDiff>();
            KeptBehaviorName = keptBehaviorName;
            Hysteresis = hysteresis;
            Notes = notes;
        }

        public string Id { get; }
        public ReplayBlock Block { get; }
        public ReplayDevice Device { get; }
        public ReplayTarget Target { get; }
        public ReplayCondition Condition { get; }
        public ReplayHold Hold { get; }
        public ReplayRuns Runs { get; }
        public ReplayKnowledge Knowledge { get; }
        public ReplayPress Press { get; }
        public ReplayWire Wire { get; }
        public string? UnrepresentableReason { get; }
        public IReadOnlyList<KnownDiff> KnownDiffs { get; }
        public string? KeptBehaviorName { get; }
        public double? Hysteresis { get; }
        public string? Notes { get; }

        public bool IsRepresentable => UnrepresentableReason == null;

        /// <summary>Axis log line for every cell (required by charter).</summary>
        public string AxesLog => string.Format(
            CultureInfo.InvariantCulture,
            "id={0} block={1} device={2} target={3} condition={4} hold={5} runs={6} knowledge={7} press={8} wire={9} hyst={10} kept={11}",
            Id, Block, Device, Target, Condition, Hold, Runs, Knowledge, Press, Wire,
            Hysteresis?.ToString(CultureInfo.InvariantCulture) ?? "-",
            KeptBehaviorName ?? "-");

        public string WheelCode => Device switch
        {
            ReplayDevice.Pbme => "CSSWFORMV3",
            ReplayDevice.Bentley => "PSWBENT",
            ReplayDevice.SegmentOnly => "PSWBMW",
            _ => "CSSWFORMV3",
        };

        public byte ItmDeviceId => Device switch
        {
            ReplayDevice.Pbme => 3,
            ReplayDevice.Bentley => 4,
            _ => 0,
        };

        public bool IsItmDevice => Device != ReplayDevice.SegmentOnly;
    }

    /// <summary>
    /// Closed matrix as code (seam-map §4.5 + adjudication hysteresis block + full
    /// pairwise per OQ-5). ActionTriggered cells are never emitted.
    /// </summary>
    internal static class ReplayMatrix
    {
        // Baseline B = (pbme, page, greaterThan, forDuration, inGame, known, none, clean)
        public static readonly ReplayDevice BaselineDevice = ReplayDevice.Pbme;
        public static readonly ReplayTarget BaselineTarget = ReplayTarget.Page;
        public static readonly ReplayCondition BaselineCondition = ReplayCondition.GreaterThan;
        public static readonly ReplayHold BaselineHold = ReplayHold.ForDuration;
        public static readonly ReplayRuns BaselineRuns = ReplayRuns.InGame;
        public static readonly ReplayKnowledge BaselineKnowledge = ReplayKnowledge.KnownPage;
        public static readonly ReplayPress BaselinePress = ReplayPress.None;
        public static readonly ReplayWire BaselineWire = ReplayWire.Clean;

        private static readonly ReplayTarget[] Targets =
        {
            ReplayTarget.Page, ReplayTarget.SegmentScreen, ReplayTarget.Cycle, ReplayTarget.Special,
        };

        private static readonly ReplayCondition[] Conditions =
        {
            ReplayCondition.LessThan, ReplayCondition.LessOrEqual,
            ReplayCondition.GreaterThan, ReplayCondition.GreaterOrEqual,
            ReplayCondition.Equals, ReplayCondition.NotEquals,
            ReplayCondition.IsTrue, ReplayCondition.IsFalse,
            ReplayCondition.Changes, ReplayCondition.Increases, ReplayCondition.Decreases,
        };

        private static readonly ReplayHold[] Holds =
        {
            ReplayHold.WhileActive, ReplayHold.ForDuration, ReplayHold.UntilDismissed,
        };

        private static readonly ReplayRuns[] Runs =
        {
            ReplayRuns.InGame, ReplayRuns.Idle, ReplayRuns.Always,
        };

        private static readonly ReplayDevice[] Devices =
        {
            ReplayDevice.Pbme, ReplayDevice.Bentley, ReplayDevice.SegmentOnly,
        };

        private static readonly ReplayKnowledge[] Knowledge =
        {
            ReplayKnowledge.UnknownAtConnect, ReplayKnowledge.KnownPage,
        };

        private static readonly ReplayPress[] Presses =
        {
            ReplayPress.None, ReplayPress.ManualPress, ReplayPress.AdoptedPress,
            ReplayPress.RejectOnRevert,
        };

        private static readonly ReplayWire[] Wires =
        {
            ReplayWire.Clean, ReplayWire.DeclinedSends, ReplayWire.LifecycleRecovery,
        };

        /// <summary>Level operators that take a value and support hysteresis bands.</summary>
        private static readonly ReplayCondition[] HysteresisOperators =
        {
            ReplayCondition.LessThan, ReplayCondition.LessOrEqual,
            ReplayCondition.GreaterThan, ReplayCondition.GreaterOrEqual,
            ReplayCondition.Equals, ReplayCondition.NotEquals,
        };

        private static readonly object Gate = new object();
        private static IReadOnlyList<ReplayCell>? _all;

        public static IReadOnlyList<ReplayCell> All()
        {
            lock (Gate)
            {
                if (_all != null)
                    return _all;
                _all = BuildAll();
                return _all;
            }
        }

        public static IEnumerable<object[]> AllTheoryData()
            => All().Select(c => new object[] { c.Id });

        public static ReplayCell ById(string id)
            => All().First(c => string.Equals(c.Id, id, StringComparison.Ordinal));

        private static List<ReplayCell> BuildAll()
        {
            var byId = new Dictionary<string, ReplayCell>(StringComparer.Ordinal);
            void Add(ReplayCell cell)
            {
                if (byId.ContainsKey(cell.Id))
                    return; // dedupe pairwise against anchored
                byId[cell.Id] = MarkUnrepresentable(cell);
            }

            // (a) OFAT + baseline
            Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, BaselineCondition,
                BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));

            foreach (var t in Targets)
                if (t != BaselineTarget)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, t, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));
            foreach (var c in Conditions)
                if (c != BaselineCondition)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, c,
                        BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));
            foreach (var h in Holds)
                if (h != BaselineHold)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, BaselineCondition,
                        h, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));
            foreach (var r in Runs)
                if (r != BaselineRuns)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, BaselineCondition,
                        BaselineHold, r, BaselineKnowledge, BaselinePress, BaselineWire));
            foreach (var d in Devices)
                if (d != BaselineDevice)
                    Add(Make(ReplayBlock.Anchored, d, BaselineTarget, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));
            foreach (var k in Knowledge)
                if (k != BaselineKnowledge)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, BaselineCondition,
                        BaselineHold, BaselineRuns, k, BaselinePress, BaselineWire));
            foreach (var p in Presses)
                if (p != BaselinePress)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, p, BaselineWire));
            foreach (var w in Wires)
                if (w != BaselineWire)
                    Add(Make(ReplayBlock.Anchored, BaselineDevice, BaselineTarget, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, w));

            // (b) Targeted pairwise (full block; OQ-5)
            // target × hold
            foreach (var t in Targets)
                foreach (var h in Holds)
                    Add(Make(ReplayBlock.Pairwise, BaselineDevice, t, BaselineCondition,
                        h, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));
            // condition-family × runs (families: Level, Edge, Bool — ActionTriggered dropped)
            foreach (var fam in ConditionFamilies())
                foreach (var r in Runs)
                    Add(Make(ReplayBlock.Pairwise, BaselineDevice, BaselineTarget, fam,
                        BaselineHold, r, BaselineKnowledge, BaselinePress, BaselineWire));
            // press × knowledge
            foreach (var p in Presses)
                foreach (var k in Knowledge)
                    Add(Make(ReplayBlock.Pairwise, BaselineDevice, BaselineTarget, BaselineCondition,
                        BaselineHold, BaselineRuns, k, p, BaselineWire));
            // device × target
            foreach (var d in Devices)
                foreach (var t in Targets)
                    Add(Make(ReplayBlock.Pairwise, d, t, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, BaselineWire));
            // wire × press
            foreach (var w in Wires)
                foreach (var p in Presses)
                    Add(Make(ReplayBlock.Pairwise, BaselineDevice, BaselineTarget, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, p, w));
            // target × wire
            foreach (var t in Targets)
                foreach (var w in Wires)
                    Add(Make(ReplayBlock.Pairwise, BaselineDevice, t, BaselineCondition,
                        BaselineHold, BaselineRuns, BaselineKnowledge, BaselinePress, w));

            // (c) Named kept-behavior cells
            foreach (var kept in KeptBehaviorCells())
                Add(kept);

            // (d) Hysteresis boundary block (adjudication MAJOR) — PBME only
            foreach (var op in HysteresisOperators)
            {
                Add(Make(
                    ReplayBlock.Hysteresis,
                    ReplayDevice.Pbme,
                    ReplayTarget.Page,
                    op,
                    ReplayHold.WhileActive,
                    ReplayRuns.InGame,
                    ReplayKnowledge.KnownPage,
                    ReplayPress.None,
                    ReplayWire.Clean,
                    hysteresis: 2.0,
                    notes: "below→enter→within→exit→above + exact-boundary"));
            }

            return byId.Values
                .OrderBy(c => c.Block)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// One representative per condition family for pairwise family×runs.
        /// Level=GreaterThan (baseline), Bool=IsTrue, Edge=Changes.
        /// </summary>
        private static IEnumerable<ReplayCondition> ConditionFamilies()
        {
            yield return ReplayCondition.GreaterThan; // level
            yield return ReplayCondition.IsTrue;      // bool-level
            yield return ReplayCondition.Changes;     // edge
        }

        private static IEnumerable<ReplayCell> KeptBehaviorCells()
        {
            // Game-start axis: named kept-behavior cell in EVERY device column (RISK-5).
            foreach (var d in Devices)
            {
                yield return Make(
                    ReplayBlock.KeptBehavior,
                    d,
                    d == ReplayDevice.SegmentOnly ? ReplayTarget.SegmentScreen : ReplayTarget.Page,
                    BaselineCondition,
                    BaselineHold,
                    BaselineRuns,
                    BaselineKnowledge,
                    BaselinePress,
                    BaselineWire,
                    keptName: "game-start-manual-reset");
            }

            // Remaining named kept-behaviors from seam-map §4.5 (c).
            string[] names =
            {
                "supersede-retired-untilDismissed-resumes",
                "dismissal-law-generalization",
                "itm-special-outranks-legacy-special",
                "reject-uncommanded-fresh-fight",
                "reject-uncommanded-in-window-reassert",
                "reject-uncommanded-exhausted-surrender",
                "wheel-screen-release-reclaim-ordering",
                "blank-compile-three-row-split",
                "unknown-page-at-connect-propagation",
                "cycle-free-run-resume",
                "walk-wrap-over-removed-members",
                "param-budget-at-16",
                "param-budget-at-17",
                "hysteresis-boundary-x-declined-send-x-cycle-flip",
                "wheel-change-x-reject-fight-x-keepalive",
                "config-reload-mid-crossing-x-wheel-screen-hold",
                "suffix-blink-v2-only", // named NEW-behavior (v2-only; not silent normalize)
            };

            foreach (var name in names)
            {
                var target = name.StartsWith("cycle", StringComparison.Ordinal)
                    ? ReplayTarget.Cycle
                    : name.Contains("special")
                        ? ReplayTarget.Special
                        : name.Contains("wheel-screen") || name.Contains("blank-compile")
                            ? ReplayTarget.SegmentScreen
                            : ReplayTarget.Page;
                var press = name.StartsWith("reject-uncommanded", StringComparison.Ordinal)
                    ? ReplayPress.RejectOnRevert
                    : ReplayPress.None;
                var knowledge = name.Contains("unknown-page")
                    ? ReplayKnowledge.UnknownAtConnect
                    : ReplayKnowledge.KnownPage;
                var wire = name.Contains("declined") || name.Contains("keepalive")
                    ? ReplayWire.DeclinedSends
                    : name.Contains("lifecycle") || name.Contains("wheel-change")
                        ? ReplayWire.LifecycleRecovery
                        : ReplayWire.Clean;
                var hold = name.Contains("untilDismissed") || name.Contains("supersede")
                    ? ReplayHold.UntilDismissed
                    : BaselineHold;

                yield return Make(
                    ReplayBlock.KeptBehavior,
                    ReplayDevice.Pbme,
                    target,
                    BaselineCondition,
                    hold,
                    BaselineRuns,
                    knowledge,
                    press,
                    wire,
                    keptName: name);
            }
        }

        private static ReplayCell Make(
            ReplayBlock block,
            ReplayDevice device,
            ReplayTarget target,
            ReplayCondition condition,
            ReplayHold hold,
            ReplayRuns runs,
            ReplayKnowledge knowledge,
            ReplayPress press,
            ReplayWire wire,
            string? keptName = null,
            double? hysteresis = null,
            string? notes = null)
        {
            string id = BuildId(device, target, condition, hold, runs, knowledge, press, wire,
                hysteresis, keptName);
            return new ReplayCell(
                id, block, device, target, condition, hold, runs, knowledge, press, wire,
                knownDiffs: null,
                keptBehaviorName: keptName,
                hysteresis: hysteresis,
                notes: notes);
        }

        public static string BuildId(
            ReplayDevice device,
            ReplayTarget target,
            ReplayCondition condition,
            ReplayHold hold,
            ReplayRuns runs,
            ReplayKnowledge knowledge,
            ReplayPress press,
            ReplayWire wire,
            double? hysteresis = null,
            string? keptName = null)
        {
            string baseId = string.Format(
                CultureInfo.InvariantCulture,
                "e8-{0}-{1}-{2}-{3}-{4}-{5}-{6}-{7}",
                Kebab(device),
                Kebab(target),
                Kebab(condition),
                Kebab(hold),
                Kebab(runs),
                Kebab(knowledge),
                Kebab(press),
                Kebab(wire));
            if (hysteresis.HasValue)
                baseId += "-hyst" + hysteresis.Value.ToString("0", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(keptName))
                baseId += "--" + keptName!.Replace('_', '-');
            return baseId.ToLowerInvariant();
        }

        private static string Kebab(object value)
        {
            string s = value.ToString() ?? "";
            // camelCase enum spellings for readability in fixture ids
            if (s.Length == 0)
                return s;
            var chars = new List<char>(s.Length + 4);
            chars.Add(char.ToLowerInvariant(s[0]));
            for (int i = 1; i < s.Length; i++)
            {
                if (char.IsUpper(s[i]))
                {
                    chars.Add('-');
                    chars.Add(char.ToLowerInvariant(s[i]));
                }
                else
                    chars.Add(s[i]);
            }
            return new string(chars.ToArray());
        }

        private static ReplayCell MarkUnrepresentable(ReplayCell cell)
        {
            string? reason = null;

            // Segment-only cannot drive ITM page/cycle targets honestly on the wire.
            if (cell.Device == ReplayDevice.SegmentOnly
                && (cell.Target == ReplayTarget.Page || cell.Target == ReplayTarget.Cycle))
            {
                reason = "segment-only device has no ITM surface for page/cycle targets";
            }

            // Reject-on-revert needs an ITM lifecycle (page announce path).
            if (cell.Device == ReplayDevice.SegmentOnly
                && cell.Press == ReplayPress.RejectOnRevert)
            {
                reason = "reject-ON revert requires ITM page announce path";
            }

            // Edge + WhileActive is coerced in both engines; still representable (coerced).
            // ActionTriggered never appears (matrix omits it).

            // Param-budget kept cells need ITM field path.
            if (cell.KeptBehaviorName != null
                && cell.KeptBehaviorName.StartsWith("param-budget", StringComparison.Ordinal)
                && cell.Device == ReplayDevice.SegmentOnly)
            {
                reason = "param budget is an ITM field path";
            }

            if (reason == null)
                return cell;

            return new ReplayCell(
                cell.Id, cell.Block, cell.Device, cell.Target, cell.Condition, cell.Hold,
                cell.Runs, cell.Knowledge, cell.Press, cell.Wire,
                unrepresentableReason: reason,
                knownDiffs: cell.KnownDiffs,
                keptBehaviorName: cell.KeptBehaviorName,
                hysteresis: cell.Hysteresis,
                notes: cell.Notes);
        }

        // ── Mapping helpers for fixture factory ───────────────────────────

        public static ConditionKind ToV1Condition(ReplayCondition c) => c switch
        {
            ReplayCondition.LessThan => ConditionKind.LessThan,
            ReplayCondition.LessOrEqual => ConditionKind.LessOrEqual,
            ReplayCondition.GreaterThan => ConditionKind.GreaterThan,
            ReplayCondition.GreaterOrEqual => ConditionKind.GreaterOrEqual,
            ReplayCondition.Equals => ConditionKind.Equals,
            ReplayCondition.NotEquals => ConditionKind.NotEquals,
            ReplayCondition.IsTrue => ConditionKind.IsTrue,
            ReplayCondition.IsFalse => ConditionKind.IsFalse,
            ReplayCondition.Changes => ConditionKind.Changes,
            ReplayCondition.Increases => ConditionKind.Increases,
            ReplayCondition.Decreases => ConditionKind.Decreases,
            _ => ConditionKind.GreaterThan,
        };

        public static TargetKind ToV1Target(ReplayTarget t) => t switch
        {
            ReplayTarget.Page => TargetKind.Page,
            ReplayTarget.SegmentScreen => TargetKind.SegmentScreen,
            ReplayTarget.Cycle => TargetKind.Cycle,
            ReplayTarget.Special => TargetKind.Special,
            _ => TargetKind.Page,
        };

        public static HoldKind ToV1Hold(ReplayHold h) => h switch
        {
            ReplayHold.WhileActive => HoldKind.WhileActive,
            ReplayHold.ForDuration => HoldKind.ForDuration,
            ReplayHold.UntilDismissed => HoldKind.UntilDismissed,
            _ => HoldKind.ForDuration,
        };

        public static RuleEligibility ToV1Runs(ReplayRuns r) => r switch
        {
            ReplayRuns.InGame => RuleEligibility.InGame,
            ReplayRuns.Idle => RuleEligibility.Idle,
            ReplayRuns.Always => RuleEligibility.Always,
            _ => RuleEligibility.InGame,
        };

        public static bool IsEdge(ReplayCondition c)
            => c == ReplayCondition.Changes
            || c == ReplayCondition.Increases
            || c == ReplayCondition.Decreases;

        public static bool IsBoolLevel(ReplayCondition c)
            => c == ReplayCondition.IsTrue || c == ReplayCondition.IsFalse;
    }
}
