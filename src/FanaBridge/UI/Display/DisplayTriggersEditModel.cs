using System;
using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The editable form of one trigger rule, the shape the expanded editor binds to and
    /// the shape <see cref="DisplayTriggersEditModel"/> builds a fresh
    /// <see cref="DisplayRule"/> from. Plain fields on purpose — it is a scratch draft, not
    /// a config object; only committed drafts become rules (and only then get a new id).
    /// A degraded rule is never turned into a draft (it is not editable); the model carries
    /// it through untouched.
    /// </summary>
    internal sealed class RuleEdit
    {
        /// <summary>Existing rule id, or null for a new rule (the model assigns a GUID).</summary>
        public string Id;

        public string Name;

        public bool Enabled = true;

        // WHEN
        public PropertyKind SourceKind = PropertyKind.SimHubProperty;
        public string SourceName;
        public ConditionKind Operator;
        public double? Value;
        public double? Hysteresis;

        // SHOW
        public TargetKind TargetKind = TargetKind.Page;
        public ItmPage? Page;
        public ItmPage? PageA;
        public ItmPage? PageB;
        public int AlternatePeriodMs = RuleTarget.DefaultAlternatePeriodMs;

        /// <summary>A legacy-screen target's screen id, carried through untouched. The v1
        /// SHOW dropdown does not author legacy targets (P3 owns that), but a rule that
        /// already targets a legacy screen must keep its id across an unrelated field edit.</summary>
        public string ScreenId;

        // HOLD (Unknown means "let the model pick the condition family's default")
        public HoldKind Hold = HoldKind.Unknown;
        public int HoldDurationMs = HoldSpec.DefaultDurationMs;

        // ELIGIBLE
        public RuleEligibility Eligibility = RuleEligibility.InGame;
    }

    /// <summary>One row of the Triggers editor list, ready for the XAML to draw: the
    /// collapsed row language (rank, label, live-state chip + countdown, eligibility) plus
    /// the affordance flags (draggable / expandable) and the degraded / base markers.</summary>
    internal sealed class TriggerRowModel
    {
        /// <summary>The rule this row edits, or null for the pinned base row.</summary>
        public string RuleId;

        /// <summary>"1".."n" for rules, "★" for the base row.</summary>
        public string Rank;

        /// <summary>Row label (<see cref="DisplayRuleFormatter.Label"/>), or "Always → &lt;base&gt;".</summary>
        public string Label;

        /// <summary>Live-state chip ("on screen"/"waiting"/…/"base"), merged from the snapshot by id.</summary>
        public string Chip = "";

        /// <summary>Hold countdown ("4s"), only while on screen with a timed hold.</summary>
        public string Seconds;

        /// <summary>The winning rule — green accent.</summary>
        public bool OnScreen;

        /// <summary>Disabled, ineligible, or degraded — the row renders dimmed.</summary>
        public bool Muted;

        /// <summary>Loaded from a newer version this build can't honor: shown muted with a
        /// "created by a newer version" hint, reorderable and removable but not editable.</summary>
        public bool Degraded;

        /// <summary>The user's own enable toggle (independent of <see cref="Degraded"/>).</summary>
        public bool Enabled;

        /// <summary>Eligibility chip text ("In-game"/"Idle"/"Any time"); empty for base / degraded.</summary>
        public string Eligibility = "";

        /// <summary>The pinned "Always" row — dashed, last, not draggable, not expandable.</summary>
        public bool IsBase;

        /// <summary>Whether the drag handle reorders this row (every rule row; not the base).</summary>
        public bool Draggable;

        /// <summary>Whether the chevron opens an editor (every non-degraded rule row).</summary>
        public bool Expandable;
    }

    /// <summary>
    /// The testable core of the Triggers editor: it holds the working
    /// <see cref="DisplayCustomizationConfig"/> and turns every user action
    /// (add / update / reorder / remove / enable) into a NEW document, never mutating the
    /// applied snapshot. Documents are immutable-after-load, so each mutation builds fresh
    /// instances for whatever it changes and carries everything else — including rules that
    /// loaded degraded — through by reference, so their serialized text (the EnumText
    /// round-trip guarantee) survives the editor untouched. The caller applies the returned
    /// document through <c>host.ApplyDisplayConfig</c>; the normalize-and-publish path
    /// rebuilds the engines. No SimHub or WPF here — a sibling of
    /// <see cref="DisplayOverviewRender"/>.
    /// </summary>
    internal sealed class DisplayTriggersEditModel
    {
        private readonly byte _itmDeviceId;
        private readonly ItmPageTable _pageTable;      // this device's page set, one source of truth
        private DisplayCustomizationConfig _config;   // working copy; never mutated in place

        // The device's default-page wire (DisplaySettings.ItmDefaultPage). Used only to
        // resolve the EFFECTIVE base a new rule's default target must avoid, so a new rule
        // doesn't default to the page the display already rests on when the config pins no
        // base of its own. 1 (Lap Info, the settings default) for callers that don't supply it.
        private readonly byte _defaultWirePage;

        /// <summary>Starts from the host's current config (null / empty is an empty rule
        /// list — creating the first rule creates the document). <paramref name="defaultWirePage"/>
        /// is the device's ItmDefaultPage setting, letting the new-rule default target avoid the
        /// effective base even when the config pins none.</summary>
        public DisplayTriggersEditModel(DisplayCustomizationConfig current, byte itmDeviceId,
            byte defaultWirePage = 1)
        {
            _config = current;
            _itmDeviceId = itmDeviceId;
            _pageTable = ItmPageTable.ForDevice(itmDeviceId);
            _defaultWirePage = defaultWirePage;
        }

        /// <summary>The current working document (null until the first rule is added to an
        /// empty start).</summary>
        public DisplayCustomizationConfig Config => _config;

        /// <summary>The ITM rules in priority order (empty when there is no document yet).</summary>
        public IReadOnlyList<DisplayRule> Rules
            => _config?.Itm?.Rules ?? (IReadOnlyList<DisplayRule>)Array.Empty<DisplayRule>();

        // ── The mapped-control mapping (hardware-verified) ────────────────

        /// <summary>The live SimHub property a Control Mapper role publishes under —
        /// <c>InputStatus.ControlMapperPlugin.&lt;role&gt;</c> (verified on hardware). The
        /// mapped-control add flow points a rule's condition source at this name.</summary>
        public static string MappedControlPropertyName(string role)
            => "InputStatus.ControlMapperPlugin." + role;

        // ── Draft factories (the two add-trigger flows) ───────────────────

        /// <summary>A blank telemetry draft: the caller fills WHEN (property/operator/value)
        /// and SHOW. Defaults to a greater-than level test against a page target.</summary>
        public RuleEdit NewTelemetryDraft()
            => new RuleEdit
            {
                SourceKind = PropertyKind.SimHubProperty,
                Operator = ConditionKind.GreaterThan,
                TargetKind = TargetKind.Page,
                Page = DefaultTargetPage(),
                Eligibility = RuleEligibility.InGame,
            };

        /// <summary>A mapped-control draft for <paramref name="role"/>: a
        /// <see cref="PropertyKind.SimHubProperty"/> source at
        /// <see cref="MappedControlPropertyName"/> with the <c>isTrue</c> + <c>whileActive</c>
        /// defaults, eligible any time (a wheel button should also work at idle).</summary>
        public RuleEdit NewMappedControlDraft(string role)
            => new RuleEdit
            {
                SourceKind = PropertyKind.SimHubProperty,
                SourceName = MappedControlPropertyName(role),
                Operator = ConditionKind.IsTrue,
                Hold = HoldKind.WhileActive,
                TargetKind = TargetKind.Page,
                Page = DefaultTargetPage(),
                Eligibility = RuleEligibility.Any,
            };

        /// <summary>Loads an existing (non-degraded) rule into an editable draft.</summary>
        public static RuleEdit ToDraft(DisplayRule rule)
        {
            var e = new RuleEdit
            {
                Id = rule.Id,
                Name = rule.Name,
                Enabled = rule.Enabled,
                Eligibility = rule.Eligible,
            };
            if (rule.When != null)
            {
                e.Operator = rule.When.Kind;
                e.Value = rule.When.Value;
                e.Hysteresis = rule.When.Hysteresis;
                if (rule.When.Source != null)
                {
                    e.SourceKind = rule.When.Source.Kind;
                    e.SourceName = rule.When.Source.Name;
                }
            }
            if (rule.Show != null)
            {
                e.TargetKind = rule.Show.Kind;
                e.Page = rule.Show.Page;
                e.PageA = rule.Show.PageA;
                e.PageB = rule.Show.PageB;
                e.AlternatePeriodMs = rule.Show.PeriodMs;
                e.ScreenId = rule.Show.ScreenId;
            }
            if (rule.Hold != null)
            {
                e.Hold = rule.Hold.Kind;
                e.HoldDurationMs = rule.Hold.DurationMs;
            }
            return e;
        }

        // ── Mutations (each returns the NEW document) ─────────────────────

        /// <summary>Appends a new rule built from <paramref name="draft"/> (a GUID is
        /// assigned) at the lowest priority (bottom of the list, above the base row).</summary>
        public DisplayCustomizationConfig AddRule(RuleEdit draft)
        {
            var rules = CurrentRules();
            rules.Add(BuildRule(draft, forceNewId: true));
            return Commit(rules);
        }

        /// <summary>Replaces the rule whose id matches <paramref name="draft"/>'s with one
        /// rebuilt from the draft (its id and position are kept). No-op returning the
        /// current config when the id is unknown.</summary>
        public DisplayCustomizationConfig UpdateRule(RuleEdit draft)
        {
            var rules = CurrentRules();
            int i = IndexOf(rules, draft.Id);
            if (i < 0)
                return _config;
            rules[i] = BuildRule(draft, forceNewId: false);
            return Commit(rules);
        }

        /// <summary>Moves the rule <paramref name="delta"/> places (−1 = up / higher
        /// priority, +1 = down), clamped to the list. Order is priority.</summary>
        public DisplayCustomizationConfig MoveRule(string id, int delta)
        {
            var rules = CurrentRules();
            int from = IndexOf(rules, id);
            if (from < 0 || delta == 0)
                return _config;
            int to = from + delta;
            if (to < 0) to = 0;
            if (to > rules.Count - 1) to = rules.Count - 1;
            if (to == from)
                return _config;
            var rule = rules[from];
            rules.RemoveAt(from);
            rules.Insert(to, rule);
            return Commit(rules);
        }

        /// <summary>Removes the rule (works on degraded rules too — reorderable and
        /// removable, just not editable). No-op when the id is unknown.</summary>
        public DisplayCustomizationConfig RemoveRule(string id)
        {
            var rules = CurrentRules();
            int i = IndexOf(rules, id);
            if (i < 0)
                return _config;
            rules.RemoveAt(i);
            return Commit(rules);
        }

        /// <summary>Flips a rule's user enable toggle without disturbing anything else on
        /// it — a fresh clone with the same serialized fields, so the id and every raw
        /// value survive.</summary>
        public DisplayCustomizationConfig SetRuleEnabled(string id, bool enabled)
        {
            var rules = CurrentRules();
            int i = IndexOf(rules, id);
            if (i < 0)
                return _config;
            rules[i] = CloneRuleWithEnabled(rules[i], enabled);
            return Commit(rules);
        }

        // ── Row model ─────────────────────────────────────────────────────

        /// <summary>
        /// The editor rows in priority order, then the pinned base row last. Live state is
        /// merged in by rule id from <paramref name="snapshot"/> (so poll re-renders can
        /// patch chips without rebuilding), degraded rules render as muted non-expandable
        /// rows, and the base row's page name follows the running stack's own resolution
        /// exactly as the Overview does.
        /// </summary>
        public IReadOnlyList<TriggerRowModel> Rows(DisplayRuleSnapshot snapshot, byte defaultWirePage)
        {
            var rows = new List<TriggerRowModel>();
            var rules = Rules;

            Dictionary<string, DisplayRuleRow> live = null;
            if (snapshot?.ItmRules != null)
            {
                live = new Dictionary<string, DisplayRuleRow>(StringComparer.Ordinal);
                foreach (var r in snapshot.ItmRules)
                    if (r.RuleId != null)
                        live[r.RuleId] = r;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                var row = new TriggerRowModel
                {
                    RuleId = rule.Id,
                    Rank = (i + 1).ToString(),
                    Label = DisplayRuleFormatter.Label(rule),
                    Enabled = rule.Enabled,
                    Degraded = rule.DegradedAtLoad,
                    Draggable = true,
                    Expandable = !rule.DegradedAtLoad,
                    Eligibility = rule.DegradedAtLoad ? "" : EligibilityLabel(rule.Eligible),
                };
                if (live != null && rule.Id != null && live.TryGetValue(rule.Id, out var state))
                {
                    var chip = DisplayOverviewRender.StateChip(state.Status, state.RemainingMs);
                    row.Chip = chip.Chip;
                    row.Seconds = chip.Seconds;
                    row.OnScreen = chip.OnScreen;
                    row.Muted = chip.Muted;
                }
                if (rule.DegradedAtLoad)
                    row.Muted = true;      // always dimmed, regardless of live state
                rows.Add(row);
            }

            rows.Add(new TriggerRowModel
            {
                Rank = "★",
                Label = "Always → " + DisplayOverviewRender.BasePageName(
                    snapshot, _config, _itmDeviceId, defaultWirePage),
                Chip = "base",
                IsBase = true,
                Draggable = false,
                Expandable = false,
            });
            return rows;
        }

        // ── UI option mappings (operator / hold / eligibility / pages) ────

        /// <summary>The operator dropdown, in the mock's order. The mapped-control edge
        /// (ActionTriggered) is not user-selectable — mapped control uses <c>isTrue</c> on
        /// the role property.</summary>
        public static readonly IReadOnlyList<ConditionKind> Operators = Array.AsReadOnly(new[]
        {
            ConditionKind.LessThan, ConditionKind.LessOrEqual,
            ConditionKind.GreaterThan, ConditionKind.GreaterOrEqual,
            ConditionKind.Equals, ConditionKind.NotEquals,
            ConditionKind.IsTrue, ConditionKind.IsFalse,
            ConditionKind.Changes, ConditionKind.Increases, ConditionKind.Decreases,
        });

        public static string OperatorLabel(ConditionKind kind)
        {
            switch (kind)
            {
                case ConditionKind.LessThan: return "less than";
                case ConditionKind.LessOrEqual: return "less or equal";
                case ConditionKind.GreaterThan: return "greater than";
                case ConditionKind.GreaterOrEqual: return "greater or equal";
                case ConditionKind.Equals: return "equals";
                case ConditionKind.NotEquals: return "not equals";
                case ConditionKind.IsTrue: return "is true";
                case ConditionKind.IsFalse: return "is false";
                case ConditionKind.Changes: return "changes";
                case ConditionKind.Increases: return "increases";
                case ConditionKind.Decreases: return "decreases";
                case ConditionKind.ActionTriggered: return "action triggered";
                default: return kind.ToString();
            }
        }

        /// <summary>The operator dropdown for a rule already using <paramref name="current"/>:
        /// the standard eleven, but with <paramref name="current"/> prepended when it is a
        /// valid-but-unlisted kind (a loaded <see cref="ConditionKind.ActionTriggered"/>
        /// rule). Without this the combo would fall back to the first item and mislabel an
        /// event-triggered rule as "less than".</summary>
        public static IReadOnlyList<ConditionKind> OperatorOptionsFor(ConditionKind current)
        {
            if (current == ConditionKind.Unknown)
                return Operators;
            foreach (var op in Operators)
                if (op == current)
                    return Operators;
            var list = new List<ConditionKind>(Operators.Count + 1) { current };
            list.AddRange(Operators);
            return list;
        }

        /// <summary>Whether a draft is complete enough to commit without the load-time
        /// validator degrading it into a locked "newer version" row: it has a source, and a
        /// finite comparison value whenever the operator needs one. Both the add flow and
        /// every in-place field edit gate on this, so a momentarily-empty VALUE box (or an
        /// operator just switched to a comparison) never turns a user's own valid rule into
        /// a degraded one.</summary>
        public static bool IsCommittable(RuleEdit draft)
        {
            if (draft == null || string.IsNullOrEmpty(draft.SourceName))
                return false;
            if (draft.Operator.RequiresValue())
            {
                if (draft.Value == null)
                    return false;
                if (double.IsNaN(draft.Value.Value) || double.IsInfinity(draft.Value.Value))
                    return false;
            }
            return true;
        }

        /// <summary>The hold options (While active is level-kinds only — the caller hides it
        /// for edge/event conditions, matching the validator's coercion).</summary>
        public static readonly IReadOnlyList<HoldKind> Holds = Array.AsReadOnly(new[]
        {
            HoldKind.WhileActive, HoldKind.ForDuration, HoldKind.Indefinite,
        });

        public static string HoldLabel(HoldKind kind)
        {
            switch (kind)
            {
                case HoldKind.WhileActive: return "While active";
                case HoldKind.ForDuration: return "For duration";
                case HoldKind.Indefinite: return "Indefinite";
                default: return kind.ToString();
            }
        }

        /// <summary>The eligibility segmented control, in the mock's order.</summary>
        public static readonly IReadOnlyList<RuleEligibility> Eligibilities = Array.AsReadOnly(new[]
        {
            RuleEligibility.InGame, RuleEligibility.Idle, RuleEligibility.Any,
        });

        public static string EligibilityLabel(RuleEligibility eligibility)
        {
            switch (eligibility)
            {
                case RuleEligibility.InGame: return "In-game";
                case RuleEligibility.Idle: return "Idle";
                case RuleEligibility.Any: return "Any time";
                default: return eligibility.ToString();
            }
        }

        /// <summary>The single-page SHOW options this device offers (legacy page excluded —
        /// legacy targets are a later phase). Content identities, resolved to wire numbers
        /// at the edge.</summary>
        public IReadOnlyList<ItmPage> PageOptions()
        {
            var result = new List<ItmPage>();
            foreach (var p in _pageTable.Pages)
                if (p.Page != ItmPage.Legacy)
                    result.Add(p.Page);
            return result;
        }

        // ── Internals ─────────────────────────────────────────────────────

        // A fresh working list of the current rules. Rule INSTANCES are shared (they are
        // immutable by convention and we never mutate them — every edit builds a fresh
        // rule); only the list is new, so a mutation can never reach back into the applied
        // snapshot, and a degraded rule carried through keeps its exact serialized text.
        private List<DisplayRule> CurrentRules()
            => new List<DisplayRule>(Rules);

        // Build the NEW document around the mutated rule list, carrying everything else
        // (schema, profile hook, base page, the whole legacy set, field mappings) forward
        // by reference. The caller applies it; ApplyDisplayConfig re-normalizes.
        private DisplayCustomizationConfig Commit(List<DisplayRule> rules)
        {
            var src = _config;
            var cfg = new DisplayCustomizationConfig
            {
                SchemaVersion = src?.SchemaVersion ?? DisplayCustomizationConfig.CurrentSchemaVersion,
                ProfileId = src?.ProfileId,
                Itm = new ItmRuleSet
                {
                    Rules = rules,
                    BasePageRaw = src?.Itm?.BasePageRaw,
                },
                Legacy = src?.Legacy ?? new LegacyRuleSet(),
                FieldMappings = src?.FieldMappings ?? new Dictionary<ushort, FieldMapping>(),
            };
            _config = cfg;
            return cfg;
        }

        private static int IndexOf(List<DisplayRule> rules, string id)
        {
            if (id == null)
                return -1;
            for (int i = 0; i < rules.Count; i++)
                if (string.Equals(rules[i].Id, id, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private DisplayRule BuildRule(RuleEdit e, bool forceNewId)
        {
            string id = forceNewId || string.IsNullOrEmpty(e.Id)
                ? Guid.NewGuid().ToString("N")
                : e.Id;

            var when = new RuleCondition
            {
                Kind = e.Operator,
                Source = new PropertySpec { Kind = e.SourceKind, Name = e.SourceName },
                Value = e.Operator.RequiresValue() ? e.Value : null,
                Hysteresis = e.Operator.IsLevel() ? e.Hysteresis : null,
            };

            var show = new RuleTarget { Kind = e.TargetKind };
            switch (e.TargetKind)
            {
                case TargetKind.Page:
                    show.Page = e.Page;
                    break;
                case TargetKind.Alternate:
                    show.PageA = e.PageA;
                    show.PageB = e.PageB;
                    show.PeriodMs = e.AlternatePeriodMs;
                    break;
                case TargetKind.LegacyScreen:
                    // The v1 SHOW dropdown does not author legacy targets (P3 owns that),
                    // but a rule loaded targeting a legacy screen keeps its id through an
                    // edit of any other field.
                    show.ScreenId = e.ScreenId;
                    break;
            }

            // Hold: an unset draft takes the condition family's natural default (the same
            // choice the validator would make), so a freshly-added telemetry level rule
            // holds while active and an edge rule holds for a duration.
            HoldKind holdKind = e.Hold;
            if (holdKind == HoldKind.Unknown)
                holdKind = e.Operator.IsLevel() ? HoldKind.WhileActive : HoldKind.ForDuration;
            var hold = new HoldSpec { Kind = holdKind, DurationMs = e.HoldDurationMs };

            return new DisplayRule
            {
                Id = id,
                Name = e.Name,
                Enabled = e.Enabled,
                When = when,
                Show = show,
                Hold = hold,
                Eligible = e.Eligibility,
            };
        }

        // A byte-faithful copy with Enabled flipped: copies every serialized (*Raw) field
        // verbatim so a value only a future version understands survives the toggle. Used
        // only for the user's enable switch, which the editor shows on non-degraded rules.
        private static DisplayRule CloneRuleWithEnabled(DisplayRule rule, bool enabled)
        {
            var clone = new DisplayRule
            {
                Id = rule.Id,
                Name = rule.Name,
                Enabled = enabled,
                EligibleRaw = rule.EligibleRaw,
            };
            if (rule.When != null)
                clone.When = new RuleCondition
                {
                    KindRaw = rule.When.KindRaw,
                    Value = rule.When.Value,
                    Hysteresis = rule.When.Hysteresis,
                    Source = rule.When.Source == null ? null : new PropertySpec
                    {
                        KindRaw = rule.When.Source.KindRaw,
                        Name = rule.When.Source.Name,
                    },
                };
            if (rule.Show != null)
                clone.Show = new RuleTarget
                {
                    KindRaw = rule.Show.KindRaw,
                    PageRaw = rule.Show.PageRaw,
                    ScreenId = rule.Show.ScreenId,
                    PageARaw = rule.Show.PageARaw,
                    PageBRaw = rule.Show.PageBRaw,
                    PeriodMs = rule.Show.PeriodMs,
                };
            if (rule.Hold != null)
                clone.Hold = new HoldSpec
                {
                    KindRaw = rule.Hold.KindRaw,
                    DurationMs = rule.Hold.DurationMs,
                };
            return clone;
        }

        // A sensible default target for a new rule: the first page this device offers that
        // isn't the resting base page (so a new rule shows something other than the base),
        // else the first page.
        private ItmPage DefaultTargetPage()
        {
            // Avoid the EFFECTIVE base — the page the display actually rests on. When the
            // config pins no base, that is the identity at the device's default-page wire
            // (ItmDefaultPage), not an assumed Lap Info; resolving through the page table
            // matches what the running stack and the "Always →" row show.
            ItmPage? configuredBase = _config?.Itm != null && _config.Itm.BasePageRaw != null
                ? _config.Itm.BasePage
                : (ItmPage?)null;
            ItmPage effectiveBase = _pageTable.ResolveBase(configuredBase, _defaultWirePage).Identity;
            ItmPage first = ItmPage.LapInfo;
            bool haveFirst = false;
            foreach (var p in _pageTable.Pages)
            {
                if (p.Page == ItmPage.Legacy)
                    continue;
                if (!haveFirst) { first = p.Page; haveFirst = true; }
                if (p.Page != effectiveBase)
                    return p.Page;
            }
            return first;
        }
    }
}
