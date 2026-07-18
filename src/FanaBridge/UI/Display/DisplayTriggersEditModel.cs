using System;
using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

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

        // SHOW — draft TargetKind is only ever Page, Cycle, or (carried) LegacyScreen;
        // Alternate loads as a 2-page Cycle and re-saves as Cycle if the user edits it.
        public TargetKind TargetKind = TargetKind.Page;
        public ItmPage? Page;
        /// <summary>Resolved pages for a <see cref="TargetKind.Cycle"/> draft (order is
        /// rotation order). Never holds null entries — a draft is only built from a
        /// non-degraded rule.</summary>
        public List<ItmPage> CyclePages = new List<ItmPage>();
        public int CyclePeriodMs = RuleTarget.DefaultCyclePeriodMs;

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
                // Alternate is a parse alias of Cycle: the draft always holds Cycle so an
                // edit re-saves as kind "cycle" + pages (design decision 2). Untouched
                // Alternate rules never pass through ToDraft/BuildRule, so they keep their
                // original kind byte-for-byte.
                if (rule.Show.Kind == TargetKind.Alternate || rule.Show.Kind == TargetKind.Cycle)
                {
                    e.TargetKind = TargetKind.Cycle;
                    e.CyclePages = new List<ItmPage>();
                    var pages = rule.Show.CyclePages;
                    if (pages != null)
                    {
                        for (int i = 0; i < pages.Count; i++)
                            if (pages[i] != null)
                                e.CyclePages.Add(pages[i].Value);
                    }
                    e.CyclePeriodMs = rule.Show.PeriodMs;
                }
                else
                {
                    e.TargetKind = rule.Show.Kind;
                    e.Page = rule.Show.Page;
                    e.ScreenId = rule.Show.ScreenId;
                }
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

        /// <summary>
        /// Applies a "Run this trigger" choice (plan B6): <see cref="RunDisabled"/> flips the
        /// rule off while leaving its serialized eligibility (<c>EligibleRaw</c>) untouched, so
        /// re-enabling restores the prior scope; any scope id turns the rule on AND sets that
        /// eligibility. Byte-faithful otherwise — a rule only toggled/re-enabled to its prior
        /// scope round-trips identically. No-op when the id is unknown.
        /// </summary>
        public DisplayCustomizationConfig SetRun(string id, string runId)
        {
            var rules = CurrentRules();
            int i = IndexOf(rules, id);
            if (i < 0)
                return _config;
            if (string.Equals(runId, RunDisabled, StringComparison.Ordinal))
                rules[i] = CloneRuleWithRun(rules[i], enabled: false, eligibility: null);
            else
            {
                RuleEligibility scope =
                    string.Equals(runId, RunIdle, StringComparison.Ordinal) ? RuleEligibility.Idle :
                    string.Equals(runId, RunAny, StringComparison.Ordinal) ? RuleEligibility.Any :
                    RuleEligibility.InGame;
                rules[i] = CloneRuleWithRun(rules[i], enabled: true, eligibility: scope);
            }
            return Commit(rules);
        }

        /// <summary>Sets the ITM base ("Always") page, editing <see cref="ItmRuleSet.BasePage"/>
        /// while carrying every rule and the rest of the document through unchanged.</summary>
        public DisplayCustomizationConfig SetBasePage(ItmPage page)
            => Commit(CurrentRules(), EnumText.Write(page));

        /// <summary>
        /// The base page currently in effect for this device — the configured base when the
        /// config pins one AND this device offers it, else the identity at the device's
        /// default-page wire. Drives the footer page dropdown's selection so it agrees with
        /// the "Always →" row.
        /// </summary>
        public ItmPage EffectiveBasePage(byte defaultWirePage)
        {
            ItmPage? configuredBase = _config?.Itm != null && _config.Itm.BasePageRaw != null
                ? _config.Itm.BasePage
                : (ItmPage?)null;
            return _pageTable.ResolveBase(configuredBase, defaultWirePage).Identity;
        }

        /// <summary>
        /// Inserts a copy of a rule directly below it (the ⋯ menu's Duplicate): a fresh id, a
        /// byte-faithful clone of every other field (so a degraded rule duplicates verbatim),
        /// and — only for a user-named rule — a " (copy)" name suffix. The new rule's id is
        /// returned so the caller can select it. No-op returning the current config (and a
        /// null id) when the source id is unknown.
        /// </summary>
        public DisplayCustomizationConfig DuplicateRule(string id, out string newId)
        {
            newId = null;
            var rules = CurrentRules();
            int i = IndexOf(rules, id);
            if (i < 0)
                return _config;
            var source = rules[i];
            var copy = CloneRuleWithRun(source, source.Enabled, eligibility: null);
            copy.Id = newId = Guid.NewGuid().ToString("N");
            if (!string.IsNullOrWhiteSpace(source.Name))
                copy.Name = source.Name + " (copy)";
            rules.Insert(i + 1, copy);
            return Commit(rules);
        }

        /// <summary>Inserts a new rule built from <paramref name="draft"/> at the TOP of the
        /// stack (highest priority) — the v9 add flow's draft-at-top. A GUID is assigned and
        /// returned. Contrast <see cref="AddRule"/>, which appends at the bottom.</summary>
        public DisplayCustomizationConfig InsertRuleAtTop(RuleEdit draft, out string newId)
        {
            var rules = CurrentRules();
            var rule = BuildRule(draft, forceNewId: true);
            newId = rule.Id;
            rules.Insert(0, rule);
            return Commit(rules);
        }

        // ── Property-pick shaping (the unified add + edit property picker) ──

        /// <summary>Whether a property name is a Control Mapper role property
        /// (<see cref="MappedControlPropertyName"/>) — a wheel button/control, not telemetry.</summary>
        public static bool IsMappedControlProperty(string name)
            => name != null && name.StartsWith("InputStatus.ControlMapperPlugin.", StringComparison.Ordinal);

        /// <summary>
        /// Stamps a freshly-picked property onto a draft and coerces the draft to the shape
        /// that property implies. A mapped-control property (a wheel button) adopts the
        /// <c>isTrue</c> + <c>whileActive</c> + any-time defaults so picking a control in the
        /// unified picker produces the same rule shape the old dedicated mapped-control add
        /// did; a telemetry property leaves the operator/hold/eligibility as they are.
        /// </summary>
        public static void AdoptPickedProperty(RuleEdit draft, string name, PropertyKind kind)
        {
            if (draft == null)
                return;
            draft.SourceKind = kind;
            draft.SourceName = name;
            if (IsMappedControlProperty(name))
            {
                draft.Operator = ConditionKind.IsTrue;
                draft.Value = null;
                draft.Hold = HoldKind.WhileActive;
                draft.Eligibility = RuleEligibility.Any;
            }
        }

        // ── Row model ─────────────────────────────────────────────────────

        /// <summary>
        /// The editor rows in priority order. In <see cref="TriggerTableMode.Workbench"/>
        /// every rule row is emitted (ranks are the config positions) and the base is left to
        /// the editor's footer; in <see cref="TriggerTableMode.Monitor"/> (the Overview's
        /// "what's in play" list) disabled/degraded rules and rules the current session state
        /// makes ineligible are dropped, the survivors renumber 1..n, the pinned base row is
        /// appended last, and each row carries the live "Now" value. Live state is merged in
        /// by rule id from <paramref name="snapshot"/> (so poll re-renders can patch chips
        /// without rebuilding), and the base row's page name follows the running stack's own
        /// resolution exactly as the Overview does.
        /// </summary>
        public IReadOnlyList<TriggerTableRow> Rows(DisplayRuleSnapshot snapshot, byte defaultWirePage,
            TriggerTableMode mode = TriggerTableMode.Monitor)
        {
            var rows = new List<TriggerTableRow>();
            var rules = Rules;
            bool monitor = mode == TriggerTableMode.Monitor;

            Dictionary<string, DisplayRuleRow> live = null;
            if (snapshot?.ItmRules != null)
            {
                live = new Dictionary<string, DisplayRuleRow>(StringComparer.Ordinal);
                foreach (var r in snapshot.ItmRules)
                    if (r.RuleId != null)
                        live[r.RuleId] = r;
            }

            int rank = 0;
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];

                // Resolve the live state first — the Monitor eligibility filter reads it.
                RuleStatus liveStatus = RuleStatus.Armed;
                DisplayRuleRow state = default;
                bool haveLive = live != null && rule.Id != null && live.TryGetValue(rule.Id, out state);
                if (haveLive)
                    liveStatus = state.Status;

                // Monitor is "what's in play": drop disabled/degraded rules and rows the
                // current session state (in-game vs idle, reported per frame as Ineligible)
                // excludes. The survivors' ranks renumber contiguously below.
                if (monitor && (rule.DegradedAtLoad || !rule.Enabled
                    || liveStatus == RuleStatus.Disabled
                    || liveStatus == RuleStatus.Ineligible))
                    continue;

                rank++;
                var row = new TriggerTableRow
                {
                    RuleId = rule.Id,
                    Rank = (monitor ? rank : i + 1).ToString(),
                    Label = DisplayRuleFormatter.Label(rule),
                    Enabled = rule.Enabled,
                    Degraded = rule.DegradedAtLoad,
                    Draggable = true,
                    Expandable = !rule.DegradedAtLoad,
                    Eligibility = rule.DegradedAtLoad ? "" : EligibilityLabel(rule.Eligible),
                };
                ApplyStructuredWhen(row, rule);
                if (haveLive)
                {
                    var chip = DisplayOverviewRender.StateChip(state.Status, state.RemainingMs);
                    row.Chip = chip.Chip;
                    row.Seconds = chip.Seconds;
                    row.OnScreen = chip.OnScreen;
                    row.Muted = chip.Muted;
                    row.NowText = state.LiveText;
                }
                if (rule.DegradedAtLoad)
                    row.Muted = true;      // always dimmed, regardless of live state
                else
                {
                    ApplyWorkbenchColumns(row, rule, liveStatus);
                    if (!rule.Enabled)
                        row.Muted = true;  // a disabled rule dims even with no live snapshot
                }
                rows.Add(row);
            }

            // The pinned base row: shown as the last stack row in Monitor (the Overview's
            // "what's in play" list) but pulled OUT of the Workbench stack — the editor
            // renders it as a dedicated BASE PAGE footer instead (spec 2b §5). Label keeps the
            // "Always → <page>" form for the plain consumers/tests; ShowText carries the bare
            // page name so the Monitor footer can render its "→ <page>" Show cell.
            if (monitor)
            {
                string baseName = DisplayOverviewRender.BasePageName(
                    snapshot, _config, _itmDeviceId, defaultWirePage);
                rows.Add(new TriggerTableRow
                {
                    Rank = "★",
                    Label = "Always → " + baseName,
                    ShowText = baseName,
                    Chip = "base",
                    IsBase = true,
                    Draggable = false,
                    Expandable = false,
                });
            }
            return rows;
        }

        // Fill the v9 dense-grid columns (Show / Timeout / Runs / State) for a non-degraded
        // rule from its own fields plus the live status. Pure text — the same wording the
        // TriggerTableModel projection helpers pin.
        private void ApplyWorkbenchColumns(TriggerTableRow row, DisplayRule rule, RuleStatus liveStatus)
        {
            row.ShowText = ShowTextFor(rule.Show);
            row.Timeout = TriggerTableModel.TimeoutText(
                rule.Hold?.Kind ?? HoldKind.WhileActive,
                rule.Hold?.DurationMs ?? HoldSpec.DefaultDurationMs);
            string runId = !rule.Enabled ? RunDisabled : RunIdFor(rule.Eligible);
            row.RunGlyph = RunGlyph(runId);
            row.RunLabel = RunLabel(runId);
            row.StateText = TriggerTableModel.StateText(liveStatus, rule.Enabled);
        }

        /// <summary>The Show column text for a target: "Page N · Name" (single page, wire
        /// number from this device's page table), "P2 ⇄ P5" (alternate / cycle short labels
        /// joined with " ⇄ "), "screen 'X'" (legacy), or "" when the target is
        /// missing/unresolved.</summary>
        public string ShowTextFor(RuleTarget show)
        {
            if (show == null)
                return "";
            switch (show.Kind)
            {
                case TargetKind.Page:
                    return show.Page == null ? "" : PageLabel(show.Page.Value);
                case TargetKind.Alternate:
                case TargetKind.Cycle:
                {
                    var pages = show.CyclePages;
                    if (pages == null || pages.Count == 0)
                        return "";
                    var parts = new string[pages.Count];
                    for (int i = 0; i < pages.Count; i++)
                        parts[i] = PageShort(pages[i]);
                    return string.Join(" ⇄ ", parts);
                }
                case TargetKind.LegacyScreen:
                    return "screen '" + (show.ScreenId ?? "?") + "'";
                default:
                    return "";
            }
        }

        private string PageLabel(ItmPage page)
        {
            string name = ItmTelemetry.NameOf(page);
            return _pageTable.TryGetWire(page, out byte wire)
                ? "Page " + wire + " · " + name
                : name;
        }

        private string PageShort(ItmPage? page)
        {
            if (page == null)
                return "?";
            return _pageTable.TryGetWire(page.Value, out byte wire)
                ? "P" + wire
                : ItmTelemetry.NameOf(page.Value);
        }

        // The v9 structured WHEN: shown for a non-degraded, unnamed rule that has a source
        // property. A user-named rule keeps its name (via Label) and a degraded/base row has
        // no editable condition, so those leave PropertyName null and the view uses Label.
        // ActionTriggered is excluded too: its label carries a distinct quoted framing
        // ("'Action' triggered", DescribeCondition) that the property/operator/value grammar
        // would drop and re-namespace — such rules (imported only; the editor never authors
        // them) fall back to Label to keep that framing.
        internal static void ApplyStructuredWhen(TriggerTableRow row, DisplayRule rule)
        {
            if (rule.DegradedAtLoad
                || !string.IsNullOrWhiteSpace(rule.Name)
                || rule.When?.Source?.Name == null
                || rule.When.Kind == ConditionKind.ActionTriggered)
                return;
            var w = WhenFields.From(rule.When);
            row.PropertyName = w.PropertyName;
            row.DisplayKind = w.DisplayKind;
            row.Operator = w.Operator;
            row.ValueText = w.ValueText;
            row.TargetText = DisplayRuleFormatter.DescribeTarget(rule.Show);
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

        /// <summary>The operator dropdown for a draft as a <see cref="ChoiceList"/> (the
        /// <see cref="DropDownCell"/> model): the options <see cref="OperatorOptionsFor"/> gives
        /// for the draft's current operator, each id'd by its enum name and labelled by
        /// <see cref="OperatorLabel"/>, with the draft's operator selected.</summary>
        public static ChoiceList OperatorChoices(RuleEdit draft)
        {
            var builder = ChoiceList.Build();
            ConditionKind current = draft != null ? draft.Operator : ConditionKind.Unknown;
            foreach (var op in OperatorOptionsFor(current))
                builder.Add(op.ToString(), OperatorLabel(op));
            return builder.Selected(current.ToString());
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

        // ── "Run this trigger" (the v9 enable × eligibility fold, plan B6) ────

        /// <summary>Run-scope ids for the Runs column / dropdown. "disabled" is not an
        /// eligibility — it flips the rule's own enable switch while leaving its stored
        /// eligibility untouched (re-enabling restores it).</summary>
        public const string RunInGame = "in";
        public const string RunIdle = "idle";
        public const string RunAny = "any";
        public const string RunDisabled = "disabled";

        /// <summary>The run id for an eligibility (the enabled cases).</summary>
        public static string RunIdFor(RuleEligibility eligibility)
        {
            switch (eligibility)
            {
                case RuleEligibility.Idle: return RunIdle;
                case RuleEligibility.Any: return RunAny;
                default: return RunInGame;
            }
        }

        /// <summary>The leading glyph for a run id (⚑ in game, ☾ idle, ∞ always, ⊘ disabled).</summary>
        public static string RunGlyph(string runId)
        {
            switch (runId)
            {
                case RunIdle: return "☾";
                case RunAny: return "∞";
                case RunDisabled: return "⊘";
                default: return "⚑";
            }
        }

        /// <summary>The label for a run id.</summary>
        public static string RunLabel(string runId)
        {
            switch (runId)
            {
                case RunIdle: return "Idle";
                case RunAny: return "Always";
                case RunDisabled: return "Disabled";
                default: return "In game";
            }
        }

        /// <summary>The "Run this trigger" options as a <see cref="ChoiceList"/> (glyph +
        /// label per id), with the draft's current run selected: Disabled when the draft is
        /// turned off, else the scope its eligibility maps to.</summary>
        public static ChoiceList RunsChoices(RuleEdit draft)
        {
            var builder = ChoiceList.Build();
            builder.Add(RunInGame, RunLabel(RunInGame), RunGlyph(RunInGame));
            builder.Add(RunIdle, RunLabel(RunIdle), RunGlyph(RunIdle));
            builder.Add(RunAny, RunLabel(RunAny), RunGlyph(RunAny));
            builder.Add(RunDisabled, RunLabel(RunDisabled), RunGlyph(RunDisabled));
            string selected = draft != null && !draft.Enabled
                ? RunDisabled
                : RunIdFor(draft?.Eligibility ?? RuleEligibility.InGame);
            return builder.Selected(selected);
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
            => Commit(rules, _config?.Itm?.BasePageRaw);

        private DisplayCustomizationConfig Commit(List<DisplayRule> rules, string basePageRaw)
        {
            var src = _config;
            var cfg = new DisplayCustomizationConfig
            {
                SchemaVersion = src?.SchemaVersion ?? DisplayCustomizationConfig.CurrentSchemaVersion,
                ProfileId = src?.ProfileId,
                Itm = new ItmRuleSet
                {
                    Rules = rules,
                    BasePageRaw = basePageRaw,
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
                case TargetKind.Cycle:
                    // Draft never holds Alternate — ToDraft maps it to Cycle, so BuildRule
                    // always writes kind "cycle" + camelCase pages (ItmPage? setter spelling).
                    show.PagesRaw = new List<string>();
                    if (e.CyclePages != null)
                    {
                        for (int i = 0; i < e.CyclePages.Count; i++)
                            show.PagesRaw.Add(EnumText.Write(e.CyclePages[i]));
                    }
                    show.PeriodMs = e.CyclePeriodMs;
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

        // A byte-faithful copy with Enabled flipped, eligibility preserved verbatim.
        private static DisplayRule CloneRuleWithEnabled(DisplayRule rule, bool enabled)
            => CloneRuleWithRun(rule, enabled, eligibility: null);

        // A byte-faithful copy with Enabled set and, optionally, eligibility set: copies
        // every serialized (*Raw) field verbatim so a value only a future version understands
        // survives, then overrides Enabled and — when <paramref name="eligibility"/> is given
        // — the eligibility. A null eligibility preserves the rule's stored EligibleRaw
        // exactly (the disable / duplicate path), so a mere on/off flip round-trips
        // identically and re-enabling to the prior scope restores it byte-for-byte.
        private static DisplayRule CloneRuleWithRun(DisplayRule rule, bool enabled,
            RuleEligibility? eligibility)
        {
            var clone = new DisplayRule
            {
                Id = rule.Id,
                Name = rule.Name,
                Enabled = enabled,
                EligibleRaw = rule.EligibleRaw,
            };
            if (eligibility != null)
                clone.Eligible = eligibility.Value;
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
                    // Fresh list so a later mutation of the clone cannot reach the source
                    // rule's PagesRaw (byte-faithful clone for cycle rules).
                    PagesRaw = rule.Show.PagesRaw == null
                        ? null
                        : new List<string>(rule.Show.PagesRaw),
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
