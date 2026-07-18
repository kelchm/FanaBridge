using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    /// The Triggers editor of the Display tab (design's ITM Triggers workbench): the dense
    /// rule grid (rank · When · Show · Timeout · Runs · State · ⋯) with the expand-to-edit
    /// drawer, the draft-at-top add flow (property picker opens straight away, mapped controls
    /// included), the ⋯ overflow (Duplicate / Move to top / Delete), the BASE PAGE footer, and
    /// the live-state merge that patches State cells in place without ever disturbing an open
    /// drawer. Every committed edit flows through the SimHub-free
    /// <see cref="DisplayTriggersEditModel"/> into <c>host.ApplyDisplayConfig</c> — this view
    /// is only the WPF that builds and commits; the shared <see cref="TriggerTableControl"/>
    /// owns the row machinery (build/drag/menu/keyboard/poll).
    ///
    /// The Display tab shell owns navigation and polling: it calls <see cref="Bind"/> once,
    /// <see cref="Enter"/> when this view becomes active, <see cref="Poll"/> each tick while
    /// it is, and <see cref="BeginAdd"/> from the Overview empty-state. The view signals back
    /// through <see cref="BackRequested"/> (the ‹ ghost back button) and
    /// <see cref="ConfigApplied"/> (after a committed edit republishes the config).
    /// </summary>
    public partial class DisplayTriggersView : UserControl
    {
        // The synthetic id of the draft-at-top row while the add flow is open. Never a real
        // rule id (the model assigns GUIDs), so it can't collide.
        private const string DraftRowId = "__draft__";

        // Generous character budget before the property grammar left-elides in the drawer's
        // property field (the WPF CharacterEllipsis is the visual backstop past it).
        private const int DetailPropertyBudget = 42;

        // ── Bound members (the shell's own instances, wired in Bind) ───────
        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private IDisplayPickerStore _pickerStore;
        private DisplaySettings _settings;
        private DisplayRuleSnapshot _lastSnapshot;

        // ── Editor state ──────────────────────────────────────────────────
        private DisplayTriggersEditModel _editModel;
        private DisplayCustomizationConfig _editModelSource;   // the config the model was built from
        private TriggerRuleSet _ruleSet = TriggerRuleSet.Itm;  // which list Enter builds for
        private string _expandedRuleId;                        // the one open drawer's rule, or null
        private RuleEdit _expandedDraft;                       // the open row's working draft (survives re-renders)
        private bool _draftExpanded;                           // the pending draft row is the expanded one
        private RuleEdit _addDraft;                            // the pending, uncommitted draft (survives collapse)
        private string _baseFooterSelected;                    // the base-footer cell's built selection (rebuild gate)

        // ── Seam events (the shell subscribes once in its Bind) ────────────
        internal event EventHandler BackRequested;
        internal event EventHandler ConfigApplied;

        public DisplayTriggersView()
        {
            InitializeComponent();
            // The shared table owns the row machinery (build/drag/menu/keyboard); this view
            // owns the edit model and every commit. Wire the table's gestures to the commit
            // paths and hand it the expansion-drawer builder.
            triggerTable.ExpansionContent = BuildExpansionContent;
            triggerTable.RowActivated += OnRowActivated;
            triggerTable.RowMoved += OnRowMoved;
            triggerTable.RowAction += OnRowAction;
        }

        // ── The bind/input surface (the seam) ──────────────────────────────

        // Wires the view to the shell's device host, the two on-demand editor catalogs, the
        // plugin-wide picker store (favorites/recents), and the SAME mutable DisplaySettings
        // reference the shell holds. Call once after construction.
        internal void Bind(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog,
            DisplaySettings settings,
            IDisplayPickerStore pickerStore)
        {
            _host = host;
            _propertyCatalog = propertyCatalog;
            _roleCatalog = roleCatalog;
            _settings = settings;
            _pickerStore = pickerStore;
        }

        // Called when the Triggers view becomes active: cache the current snapshot, build a
        // fresh edit model from the host's current config, and render. Resets any prior
        // editing state (a re-entry is a clean slate). <paramref name="ruleSet"/> selects
        // ITM pages vs legacy virtual pages (basic wheels always open Legacy).
        internal void Enter(DisplayRuleSnapshot snapshot, TriggerRuleSet ruleSet)
        {
            _lastSnapshot = snapshot;
            _ruleSet = ruleSet;
            EnterTriggersEditor();
        }

        // The Overview Monitor row-click path: enter the editor (a clean slate, as
        // <see cref="Enter"/> does) and immediately expand the clicked rule so the drawer is
        // open on arrival. An unknown or degraded id simply enters with nothing expanded.
        internal void EnterAndSelect(DisplayRuleSnapshot snapshot, string ruleId,
            TriggerRuleSet ruleSet)
        {
            _lastSnapshot = snapshot;
            _ruleSet = ruleSet;
            EnterTriggersEditor();
            var rule = FindRule(ruleId);
            if (rule != null && !rule.DegradedAtLoad)
            {
                _expandedRuleId = ruleId;
                _expandedDraft = null;
                RenderTriggerRows(_lastSnapshot);
            }
        }

        // The Overview empty-state "＋ Add trigger" path: open the draft-at-top add flow. The
        // shell has already navigated here (rebuilding the model).
        internal void BeginAdd() => StartAddDraft();

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        // ── Entry / navigation ────────────────────────────────────────────

        // Builds a fresh edit model from the host's current config and renders the rows.
        // Resets any prior editing state.
        private void EnterTriggersEditor()
        {
            _expandedRuleId = null;
            _expandedDraft = null;
            _draftExpanded = false;
            _addDraft = null;
            _baseFooterSelected = null;   // force a footer rebuild for the (possibly new) device
            _editModelSource = _host?.GetDisplayConfig();
            _editModel = new DisplayTriggersEditModel(_editModelSource, _host?.ItmDeviceId ?? 0,
                _settings?.ItmDefaultPage ?? (byte)1, _ruleSet);
            RenderTriggerRows(_lastSnapshot);
        }

        private void TriggersAdd_Click(object sender, RoutedEventArgs e) => StartAddDraft();

        // The v9 add flow: a fresh telemetry draft becomes the expanded top row and the
        // property picker opens immediately (mock addTrigger). Picking a property completes
        // (mapped controls) or reveals the value field. An incomplete draft never commits,
        // but it also never silently disappears: collapsing it / clicking away keeps it as
        // a pending top row, and only its ⋯ remove (or leaving the editor) discards it.
        private void StartAddDraft()
        {
            if (_editModel == null)
                return;
            _expandedRuleId = null;
            _expandedDraft = null;
            _draftExpanded = true;
            // ＋ with a pending draft re-expands it rather than silently replacing it.
            if (_addDraft != null)
            {
                RenderTriggerRows(_lastSnapshot);
                return;
            }
            _addDraft = _editModel.NewTelemetryDraft();
            if (PickProperty(_addDraft))
                CommitField(_addDraft, DraftRowId, isDraft: true);   // may promote (mapped) or reveal the value box
            else
                RenderTriggerRows(_lastSnapshot);                    // keep the empty draft open
        }

        private void DiscardDraft()
        {
            _draftExpanded = false;
            _addDraft = null;
            RenderTriggerRows(_lastSnapshot);
        }

        // ── Poll integration (called from the shell's Poll while active) ───

        internal void Poll(DisplayRuleSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            if (_editModel == null || _host == null)
                return;
            // A drag in progress must never be interrupted by a rebuild: it holds mouse
            // capture on a ⠿ handle, and clearing the table's children unparents that handle,
            // dropping the capture. Chip patching never touches the children collection, so it
            // stays safe; defer every rebuild until the drop.
            bool dragInProgress = triggerTable.IsDragging;
            if (!ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
            {
                if (dragInProgress)
                    return;
                EnterTriggersEditor();
                return;
            }
            // Not editing: a full rebuild is harmless and keeps the row look fully live.
            // Editing (an open drawer or the add draft) or mid-drag: only patch the State
            // cells, so the open controls, focus, text-in-progress, and the drag gesture are
            // never disturbed.
            if (!dragInProgress && _expandedRuleId == null && !_draftExpanded)
                RenderTriggerRows(snapshot);
            else
                PatchTriggerChips(snapshot);
        }

        // ── Row rendering ─────────────────────────────────────────────────

        private void RenderTriggerRows(DisplayRuleSnapshot snapshot)
        {
            byte wire = _settings != null ? _settings.ItmDefaultPage : (byte)1;
            triggerTable.ExpandedRuleId = _draftExpanded && _addDraft != null
                ? DraftRowId : _expandedRuleId;

            var rows = new List<TriggerTableRow>();
            if (_addDraft != null)
                rows.Add(BuildDraftRow(_addDraft));   // pending draft persists, even collapsed
            rows.AddRange(_editModel.Rows(snapshot, wire, TriggerTableMode.Workbench));
            triggerTable.SetRows(rows);

            txtTriggersEmpty.Visibility = (_editModel.Rules.Count == 0 && _addDraft == null)
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateBaseFooter(wire);
        }

        // In-place State patch: recompute the row projection from the fresh snapshot and let
        // the table patch each rule row in place, so an open drawer keeps its controls and
        // focus and an in-flight drag is never disturbed. The draft row (not in the model) is
        // left with a cleared State cell, which is correct — a draft has no live state.
        private void PatchTriggerChips(DisplayRuleSnapshot snapshot)
        {
            byte wire = _settings != null ? _settings.ItmDefaultPage : (byte)1;
            triggerTable.PatchLive(_editModel.Rows(snapshot, wire, TriggerTableMode.Workbench));
        }

        // The synthetic top row for the pending draft: expanded, its When cell reflecting the
        // draft so far (or a "pick a property" prompt), Show/Timeout/Runs previewed from the
        // draft. Not draggable (an uncommitted rule has no priority slot yet).
        private TriggerTableRow BuildDraftRow(RuleEdit draft)
        {
            var row = new TriggerTableRow
            {
                RuleId = DraftRowId,
                Rank = "•",
                Enabled = draft.Enabled,
                Draggable = false,
                Expandable = true,
            };
            if (!string.IsNullOrEmpty(draft.SourceName))
            {
                row.PropertyName = draft.SourceName;
                row.DisplayKind = PropertyGrammar.KindFor(draft.SourceKind);
                row.Operator = DisplayRuleFormatter.OperatorText(draft.Operator);
                row.ValueText = draft.Operator.RequiresValue()
                    ? DisplayRuleFormatter.FormatValue(draft.Value) : "";
            }
            else
            {
                row.Label = "New trigger — pick a property";
            }
            row.ShowText = _editModel.ShowTextFor(DraftTarget(draft));
            HoldKind hold = draft.Hold != HoldKind.Unknown
                ? draft.Hold
                : (draft.Operator.IsLevel() ? HoldKind.WhileActive : HoldKind.ForDuration);
            row.Timeout = TriggerTableModel.TimeoutText(hold, draft.HoldDurationMs);
            string runId = !draft.Enabled
                ? DisplayTriggersEditModel.RunDisabled
                : DisplayTriggersEditModel.RunIdFor(draft.Eligibility);
            row.RunGlyph = DisplayTriggersEditModel.RunGlyph(runId);
            row.RunLabel = DisplayTriggersEditModel.RunLabel(runId);
            return row;
        }

        // A RuleTarget shape sufficient for ShowTextFor on a live draft.
        private static RuleTarget DraftTarget(RuleEdit draft)
        {
            var t = new RuleTarget
            {
                Kind = draft.TargetKind,
                Page = draft.Page,
                ScreenId = draft.ScreenId,
                PeriodMs = draft.CyclePeriodMs,
            };
            if (draft.TargetKind == TargetKind.Cycle && draft.CyclePages != null)
            {
                t.PagesRaw = new List<string>(draft.CyclePages.Count);
                for (int i = 0; i < draft.CyclePages.Count; i++)
                    t.PagesRaw.Add(EnumText.Write(draft.CyclePages[i]));
            }
            if (draft.TargetKind == TargetKind.Special)
                t.Command = draft.Command != SpecialCommand.Unknown
                    ? draft.Command
                    : SpecialCommand.LogoScreen;
            return t;
        }

        // ── Base page footer ──────────────────────────────────────────────

        private void UpdateBaseFooter(byte wire)
        {
            // Rebuild only when the resting base actually changes (a config edit / device
            // switch) — never on a plain live poll tick, so an open base-page dropdown the
            // user is interacting with is not yanked out from under them mid-selection.
            string selected = _editModel.IsLegacyMode
                ? ("L:" + (_editModel.EffectiveBaseScreenId ?? DisplayVirtualPagesEditModel.BaseBlankId))
                : _editModel.EffectiveBasePage(wire).ToString();
            if (panelBaseFooter.Children.Count > 0
                && string.Equals(selected, _baseFooterSelected, StringComparison.Ordinal))
                return;
            _baseFooterSelected = selected;

            panelBaseFooter.Children.Clear();

            bool legacy = _editModel.IsLegacyMode;
            panelBaseFooter.Children.Add(new TextBlock
            {
                Text = legacy ? "BASE SCREEN" : "BASE PAGE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.ThLabel,
                Margin = new Thickness(0, 0, 0, 7),
            });

            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            line.Children.Add(new TextBlock
            {
                Text = "★",
                Foreground = DisplayPalette.BaseRank,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            line.Children.Add(new TextBlock
            {
                Text = "When nothing's firing → rest on",
                FontSize = 12.5,
                Foreground = DisplayPalette.BaseText,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var pageCell = new DropDownCell { Width = 210, VerticalAlignment = VerticalAlignment.Center };
            if (legacy)
            {
                pageCell.SetChoices(_editModel.BaseScreenChoices());
                pageCell.SelectionCommitted += (s, id) => OnBaseScreenChosen(id);
            }
            else
            {
                pageCell.SetChoices(BasePageChoices(wire));
                pageCell.SelectionCommitted += (s, id) => OnBasePageChosen(id);
            }
            line.Children.Add(pageCell);
            panelBaseFooter.Children.Add(line);

            panelBaseFooter.Children.Add(new TextBlock
            {
                Text = legacy
                    ? "What the 3-character display rests on between triggers. Blank clears the face."
                    : "Where the display rests between triggers. Idle behavior is just a "
                        + "trigger with Run = ☾ Idle.",
                FontSize = 11,
                Foreground = DisplayPalette.KLabelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0),
            });
        }

        private ChoiceList BasePageChoices(byte wire)
        {
            var builder = ChoiceList.Build();
            foreach (var p in _editModel.PageOptions())
                builder.Add(p.ToString(), PageChoiceLabel(p));
            return builder.Selected(_editModel.EffectiveBasePage(wire).ToString());
        }

        private string PageChoiceLabel(ItmPage page)
            => _editModel.ShowTextFor(new RuleTarget { Kind = TargetKind.Page, Page = page });

        private void OnBasePageChosen(string id)
        {
            if (!Enum.TryParse(id, out ItmPage page))
                return;
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.SetBasePage(page);
            ApplyAndReload(cfg);
            RenderTriggerRows(_lastSnapshot);
        }

        private void OnBaseScreenChosen(string id)
        {
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.SetBaseScreenId(id);
            ApplyAndReload(cfg);
            RenderTriggerRows(_lastSnapshot);
        }

        // ── Table gesture handlers (each routes to a commit path) ──────────

        private void OnRowActivated(string ruleId)
        {
            if (string.Equals(ruleId, DraftRowId, StringComparison.Ordinal))
            {
                // Collapse/expand toggles only — a pending draft is never discarded by a
                // stray click (explicit ⋯ remove is the discard path).
                _draftExpanded = !_draftExpanded;
                if (_draftExpanded)
                {
                    _expandedRuleId = null;
                    _expandedDraft = null;
                }
                RenderTriggerRows(_lastSnapshot);
                return;
            }
            ToggleExpanded(ruleId);
        }

        // A reorder gesture (drag drop, Alt+arrow, context-menu Move to top) targets a new
        // index among the rule rows; translate it to the edit model's relative move. A move to
        // the same slot is a no-op (no republish), exactly as the drag path was before.
        private void OnRowMoved(string ruleId, int newIndex)
        {
            int from = IndexOfRule(ruleId);
            if (from < 0)
                return;                    // the draft row (not in the model) is not reorderable
            int delta = newIndex - from;
            if (delta == 0)
                return;
            MoveRule(ruleId, delta);
        }

        private void OnRowAction(string ruleId, string actionId)
        {
            if (string.Equals(ruleId, DraftRowId, StringComparison.Ordinal))
            {
                if (string.Equals(actionId, "remove", StringComparison.Ordinal))
                    DiscardDraft();
                return;
            }
            if (string.Equals(actionId, "remove", StringComparison.Ordinal))
                RemoveRule(ruleId);
            else if (string.Equals(actionId, "duplicate", StringComparison.Ordinal))
                DuplicateRule(ruleId);
        }

        // ── The expand-to-edit drawer ─────────────────────────────────────

        // The table asks for the drawer of the selected row. The draft row builds from the
        // pending _addDraft; a committed row builds from a per-row working draft (kept across
        // re-renders); a degraded/unknown row yields none.
        private UIElement BuildExpansionContent(string ruleId)
        {
            if (string.Equals(ruleId, DraftRowId, StringComparison.Ordinal))
                return _addDraft != null ? BuildDrawer(DraftRowId, _addDraft, isDraft: true) : null;

            var rule = FindRule(ruleId);
            if (rule == null || rule.DegradedAtLoad)
                return null;
            if (_expandedDraft == null
                || !string.Equals(_expandedDraft.Id, rule.Id, StringComparison.Ordinal))
                _expandedDraft = DisplayTriggersEditModel.ToDraft(rule);
            return BuildDrawer(ruleId, _expandedDraft, isDraft: false);
        }

        // The two-column drawer: IF (◆ When to fire) on the left, THEN (▶ What to show) on the
        // right, live-committing (no Done/Close button). NO Property/Formula segment (formula
        // UI deferred), NO Remove link (moved to ⋯), NO Enabled checkbox (Runs carries it).
        private UIElement BuildDrawer(string ruleId, RuleEdit draft, bool isDraft)
        {
            Action commit = () => CommitField(draft, ruleId, isDraft);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = BuildWhenColumn(draft, commit);
            Grid.SetColumn(left, 0);
            columns.Children.Add(left);

            var right = BuildShowColumn(draft, commit);
            Grid.SetColumn(right, 1);
            columns.Children.Add(right);

            return new Border
            {
                Background = DisplayPalette.DrawerBg,
                BorderBrush = DisplayPalette.DrawerBar,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(34, 14, 16, 16),
                Child = columns,
            };
        }

        private FrameworkElement BuildWhenColumn(RuleEdit draft, Action commit)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
            col.Children.Add(SectionTitle("◆ WHEN TO FIRE", DisplayPalette.WhenTitle));

            col.Children.Add(FieldLabel("Trigger — the event"));

            // Property (star) · operator (96) · value (84, only when the operator needs one).
            var fieldRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var prop = BuildPropertyField(draft, commit);
            Grid.SetColumn(prop, 0);
            fieldRow.Children.Add(prop);

            var op = BuildOperatorCell(draft, commit);
            op.Width = 96;
            op.Margin = new Thickness(7, 0, 0, 0);
            Grid.SetColumn(op, 1);
            fieldRow.Children.Add(op);

            if (draft.Operator.RequiresValue())
            {
                var val = BuildValueBox(draft, commit);
                val.Margin = new Thickness(7, 0, 0, 0);
                Grid.SetColumn(val, 2);
                fieldRow.Children.Add(val);
            }
            col.Children.Add(fieldRow);

            // Hysteresis — the existing advanced field, restyled to fit under the trigger row.
            if (draft.Operator.RequiresValue())
            {
                col.Children.Add(FieldLabel("Hysteresis (±)", top: 11));
                col.Children.Add(BuildHysteresisBox(draft, commit));
            }

            col.Children.Add(FieldLabel("Run this trigger", top: 13));
            col.Children.Add(BuildRunCell(draft, commit));
            return col;
        }

        private FrameworkElement BuildShowColumn(RuleEdit draft, Action commit)
        {
            var inner = new StackPanel();
            inner.Children.Add(SectionTitle("▶ WHAT TO SHOW", DisplayPalette.ShowTitle));

            inner.Children.Add(FieldLabel("Action"));
            inner.Children.Add(BuildActionCell(draft, commit));

            if (draft.TargetKind == TargetKind.Special)
            {
                var cmd = BuildSpecialCommandCell(draft, commit);
                cmd.Margin = new Thickness(0, 7, 0, 0);
                inner.Children.Add(cmd);
            }
            else if (_editModel.IsLegacyMode || draft.TargetKind == TargetKind.LegacyScreen)
            {
                // Legacy vocabulary: virtual page + screen DropDownCell (or special above).
                var screen = BuildScreenCell(draft, commit);
                screen.Margin = new Thickness(0, 7, 0, 0);
                inner.Children.Add(screen);
            }
            else if (draft.TargetKind == TargetKind.Cycle)
            {
                // Chips row (⠿ · mono page · ✕) + "＋ Add ITM page" + period seconds field.
                inner.Children.Add(BuildCyclePagesPanel(draft, commit));
                inner.Children.Add(FieldLabel("Every (seconds)", top: 11));
                inner.Children.Add(BuildCyclePeriodBox(draft, commit));
            }
            else
            {
                var page = BuildPageCell(draft.Page, id => { draft.Page = ParsePage(id); commit(); });
                page.Margin = new Thickness(0, 7, 0, 0);
                inner.Children.Add(page);
            }

            inner.Children.Add(FieldLabel("Timeout", top: 13));
            inner.Children.Add(BuildTimeoutRow(draft, commit));

            return new Border
            {
                BorderBrush = DisplayPalette.DrawerSep,
                BorderThickness = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(18, 0, 0, 0),
                Child = inner,
            };
        }

        // The property field: the mono grammar + ✎ affordance; a click opens the property
        // picker (mapped controls included). An empty source shows the "(pick property)"
        // placeholder.
        private FrameworkElement BuildPropertyField(RuleEdit draft, Action commit)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = PropertyLabel.ForProperty(draft.SourceName,
                PropertyGrammar.KindFor(draft.SourceKind), DetailPropertyBudget);
            label.Margin = new Thickness(10, 0, 6, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var pencil = new TextBlock
            {
                Text = "✎",
                Foreground = DisplayPalette.PencilBlue,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pencil, 1);
            grid.Children.Add(pencil);

            var border = new Border
            {
                Background = DisplayPalette.FieldBg,
                BorderBrush = DisplayPalette.FieldBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Height = 30,
                Cursor = Cursors.Hand,
                Focusable = true,
                Child = grid,
            };
            Action pick = () => { if (PickProperty(draft)) commit(); };
            border.MouseLeftButtonUp += (s, e) => pick();
            MakeKeyActivatable(border, pick);
            return border;
        }

        private DropDownCell BuildOperatorCell(RuleEdit draft, Action commit)
        {
            var cell = new DropDownCell();
            cell.SetChoices(DisplayTriggersEditModel.OperatorChoices(draft));
            cell.SelectionCommitted += (s, id) =>
            {
                if (Enum.TryParse(id, out ConditionKind op) && op != draft.Operator)
                {
                    draft.Operator = op;
                    commit();
                }
            };
            return cell;
        }

        private TextBox BuildValueBox(RuleEdit draft, Action commit)
        {
            var box = new TextBox
            {
                Width = 84,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = draft.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
            };
            CommitOnLeave(box, () =>
            {
                draft.Value = ParseNum(box.Text);
                commit();
            });
            return box;
        }

        private TextBox BuildHysteresisBox(RuleEdit draft, Action commit)
        {
            var box = new TextBox
            {
                Width = 120,
                Height = 30,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = draft.Hysteresis?.ToString(CultureInfo.InvariantCulture) ?? "",
                ToolTip = "Deadband that stops a value hovering at the threshold from flapping.",
            };
            CommitOnLeave(box, () =>
            {
                draft.Hysteresis = ParseNum(box.Text);
                commit();
            });
            return box;
        }

        // "Run this trigger": the enable × eligibility fold. On the draft it mutates the draft
        // (a brand-new rule has no stored eligibility to preserve); on a committed rule it goes
        // through SetRun so a Disabled choice keeps the rule's stored eligibility for re-enable.
        private DropDownCell BuildRunCell(RuleEdit draft, Action commit)
        {
            var cell = new DropDownCell { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Width = 200 };
            cell.SetChoices(DisplayTriggersEditModel.RunsChoices(draft));
            cell.SelectionCommitted += (s, runId) =>
            {
                if (draft == _addDraft)
                {
                    ApplyRunToDraft(draft, runId);
                    commit();
                }
                else
                {
                    SetRun(draft.Id, runId);
                }
            };
            return cell;
        }

        private static void ApplyRunToDraft(RuleEdit draft, string runId)
        {
            if (string.Equals(runId, DisplayTriggersEditModel.RunDisabled, StringComparison.Ordinal))
            {
                draft.Enabled = false;
                return;
            }
            draft.Enabled = true;
            draft.Eligibility =
                string.Equals(runId, DisplayTriggersEditModel.RunIdle, StringComparison.Ordinal) ? RuleEligibility.Idle :
                string.Equals(runId, DisplayTriggersEditModel.RunAny, StringComparison.Ordinal) ? RuleEligibility.Any :
                RuleEligibility.InGame;
        }

        // The Action dropdown: ITM vocabulary is "Show an ITM page" / "Cycle ITM pages" /
        // "Special command"; legacy vocabulary is "Show a virtual page" / "Special command".
        private DropDownCell BuildActionCell(RuleEdit draft, Action commit)
        {
            var cell = new DropDownCell { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            if (_editModel.IsLegacyMode)
            {
                string selected = draft.TargetKind == TargetKind.Special ? "special" : "legacy";
                cell.SetChoices(ChoiceList.Build()
                    .Add("legacy", "Show a virtual page")
                    .Add("special", "Special command")
                    .Selected(selected));
                cell.SelectionCommitted += (s, id) =>
                {
                    if (string.Equals(id, "special", StringComparison.Ordinal))
                    {
                        if (draft.TargetKind == TargetKind.Special)
                            return;
                        draft.TargetKind = TargetKind.Special;
                        if (draft.Command == SpecialCommand.Unknown)
                            draft.Command = SpecialCommand.LogoScreen;
                    }
                    else
                    {
                        if (draft.TargetKind == TargetKind.LegacyScreen)
                            return;
                        draft.TargetKind = TargetKind.LegacyScreen;
                        if (string.IsNullOrEmpty(draft.ScreenId)
                            && _editModel.ScreenOptions().Count > 0)
                            draft.ScreenId = _editModel.ScreenOptions()[0].Id;
                    }
                    commit();
                };
                return cell;
            }

            string itmSelected =
                draft.TargetKind == TargetKind.Cycle ? "cycle" :
                draft.TargetKind == TargetKind.Special ? "special" : "page";
            var choices = ChoiceList.Build()
                .Add("page", "Show an ITM page")
                .Add("cycle", "Cycle ITM pages")
                .Add("special", "Special command")
                .Selected(itmSelected);
            cell.SetChoices(choices);
            cell.SelectionCommitted += (s, id) =>
            {
                if (string.Equals(id, "cycle", StringComparison.Ordinal))
                {
                    if (draft.TargetKind == TargetKind.Cycle)
                        return;
                    draft.TargetKind = TargetKind.Cycle;
                    // Seed a two-page cycle when the draft has fewer than two entries.
                    if (draft.CyclePages == null)
                        draft.CyclePages = new List<ItmPage>();
                    if (draft.CyclePages.Count < 2)
                    {
                        ItmPage first = draft.CyclePages.Count > 0
                            ? draft.CyclePages[0]
                            : (draft.Page ?? DefaultPage());
                        draft.CyclePages = new List<ItmPage> { first, OtherPage(first) };
                    }
                }
                else if (string.Equals(id, "special", StringComparison.Ordinal))
                {
                    if (draft.TargetKind == TargetKind.Special)
                        return;
                    draft.TargetKind = TargetKind.Special;
                    if (draft.Command == SpecialCommand.Unknown)
                        draft.Command = SpecialCommand.LogoScreen;
                }
                else
                {
                    if (draft.TargetKind == TargetKind.Page)
                        return;
                    draft.TargetKind = TargetKind.Page;
                    draft.Page = (draft.CyclePages != null && draft.CyclePages.Count > 0)
                        ? draft.CyclePages[0]
                        : DefaultPage();
                }
                commit();
            };
            return cell;
        }

        private DropDownCell BuildSpecialCommandCell(RuleEdit draft, Action commit)
        {
            var cell = new DropDownCell { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            SpecialCommand current = draft.Command != SpecialCommand.Unknown
                ? draft.Command
                : SpecialCommand.LogoScreen;
            cell.SetChoices(DisplayTriggersEditModel.SpecialCommandChoices(current));
            cell.SelectionCommitted += (s, id) =>
            {
                var cmd = SpecialCommands.Parse(id);
                if (cmd == SpecialCommand.Unknown || cmd == draft.Command)
                    return;
                draft.TargetKind = TargetKind.Special;
                draft.Command = cmd;
                commit();
            };
            return cell;
        }

        private DropDownCell BuildScreenCell(RuleEdit draft, Action commit)
        {
            var cell = new DropDownCell { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            cell.SetChoices(_editModel.ScreenChoices(draft.ScreenId));
            cell.SelectionCommitted += (s, id) =>
            {
                if (string.Equals(draft.ScreenId, id, StringComparison.Ordinal))
                    return;
                draft.TargetKind = TargetKind.LegacyScreen;
                draft.ScreenId = id;
                commit();
            };
            return cell;
        }

        // Cycle chips: dark rounded wrap panel, one chip per page (⠿ decorative · mono
        // label · ✕ remove), plus a dashed "＋ Add ITM page" chip that opens a ContextMenu
        // of PageOptions (least new machinery vs. a custom DropDownCell for a non-value chip).
        // ✕ is hidden while the list has exactly two entries (a cycle can't drop below 2).
        private FrameworkElement BuildCyclePagesPanel(RuleEdit draft, Action commit)
        {
            if (draft.CyclePages == null)
                draft.CyclePages = new List<ItmPage>();

            var wrap = new WrapPanel();
            bool canRemove = draft.CyclePages.Count > 2;
            for (int i = 0; i < draft.CyclePages.Count; i++)
            {
                int index = i;
                ItmPage page = draft.CyclePages[i];
                var chip = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                chip.Children.Add(new TextBlock
                {
                    Text = "⠿",
                    FontSize = 11,
                    Foreground = DisplayPalette.PropMono,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                chip.Children.Add(new TextBlock
                {
                    Text = PageChoiceLabel(page),
                    FontSize = 11.5,
                    FontFamily = DisplayPalette.Mono,
                    Foreground = DisplayPalette.FieldText,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (canRemove)
                {
                    var remove = new TextBlock
                    {
                        Text = "✕",
                        FontSize = 11.5,
                        Foreground = DisplayPalette.SubLabel,
                        Margin = new Thickness(7, 0, 0, 0),
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Remove page from cycle",
                    };
                    Action drop = () =>
                    {
                        if (draft.CyclePages != null && draft.CyclePages.Count > 2
                            && index >= 0 && index < draft.CyclePages.Count)
                        {
                            draft.CyclePages.RemoveAt(index);
                            commit();
                        }
                    };
                    remove.MouseLeftButtonUp += (s, e) => { drop(); e.Handled = true; };
                    MakeKeyActivatable(remove, drop);
                    chip.Children.Add(remove);
                }
                wrap.Children.Add(new Border
                {
                    Background = DisplayPalette.FieldBg,
                    BorderBrush = DisplayPalette.FieldBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 8, 6),
                    Child = chip,
                });
            }
            wrap.Children.Add(BuildAddCyclePageChip(draft, commit));

            return new Border
            {
                Background = DisplayPalette.SegBarBg,
                BorderBrush = DisplayPalette.FieldBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9),
                Margin = new Thickness(0, 7, 0, 0),
                Child = wrap,
            };
        }

        // Dashed "＋ Add ITM page" chip: ContextMenu of PageOptions; pick appends + commits.
        private FrameworkElement BuildAddCyclePageChip(RuleEdit draft, Action commit)
        {
            var label = new TextBlock
            {
                Text = "＋ Add ITM page",
                FontSize = 11.5,
                Foreground = DisplayPalette.Caret,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var host = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            host.Children.Add(new Rectangle
            {
                Stroke = DisplayPalette.FieldBorder,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                RadiusX = 4,
                RadiusY = 4,
                Fill = DisplayPalette.AddCardBg,
            });
            host.Children.Add(new Border
            {
                Padding = new Thickness(10, 5, 10, 5),
                Child = label,
            });

            var menu = new ContextMenu();
            foreach (var p in _editModel.PageOptions())
            {
                ItmPage page = p;
                var item = new MenuItem { Header = PageChoiceLabel(page) };
                item.Click += (s, e) =>
                {
                    if (draft.CyclePages == null)
                        draft.CyclePages = new List<ItmPage>();
                    draft.CyclePages.Add(page);
                    commit();
                };
                menu.Items.Add(item);
            }

            var border = new Border
            {
                Cursor = Cursors.Hand,
                Child = host,
                Focusable = true,
            };
            Action open = () =>
            {
                menu.PlacementTarget = border;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            border.MouseLeftButtonUp += (s, e) => { open(); e.Handled = true; };
            MakeKeyActivatable(border, open);
            // Issue #37 companion: close the menu if the drawer is torn down mid-open.
            border.Unloaded += (s, e) => menu.IsOpen = false;
            border.IsVisibleChanged += (s, e) => { if (!border.IsVisible) menu.IsOpen = false; };
            return border;
        }

        // Cycle flip period: free seconds field (timeout-box pattern). Blank → default 3 s;
        // commit clamps to ≥ 1 s (validator floor is 1000 ms).
        private TextBox BuildCyclePeriodBox(RuleEdit draft, Action commit)
        {
            var box = new TextBox
            {
                Width = 56,
                Height = 30,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = (draft.CyclePeriodMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                ToolTip = "Seconds between page flips while this trigger is on screen.",
            };
            Func<string> currentText = () =>
                (draft.CyclePeriodMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            CommitOnLeave(box, () =>
            {
                double? secs = ParseNum(box.Text);
                if (secs == null)
                {
                    // Unparseable (or blank) text never rewrites the period — keep the
                    // draft's value and put its text back (the hold-seconds contract).
                    box.Text = currentText();
                    return;
                }
                int ms = (int)Math.Round(Math.Max(1.0, secs.Value) * 1000.0);
                if (ms == draft.CyclePeriodMs)
                {
                    // Same effective period (e.g. sub-floor text clamping to the current
                    // value): no commit, but the box must show what the engine will do.
                    box.Text = currentText();
                    return;
                }
                draft.CyclePeriodMs = ms;
                commit();
            });
            return box;
        }

        private DropDownCell BuildPageCell(ItmPage? selected, Action<string> onCommit)
        {
            var cell = new DropDownCell { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            var builder = ChoiceList.Build();
            foreach (var p in _editModel.PageOptions())
                builder.Add(p.ToString(), PageChoiceLabel(p));
            cell.SetChoices(builder.Selected(selected?.ToString()));
            cell.SelectionCommitted += (s, id) => onCommit(id);
            return cell;
        }

        // Timeout: mode dropdown (While active for level kinds only) + a seconds field shown
        // only for "For a set time".
        private FrameworkElement BuildTimeoutRow(RuleEdit draft, Action commit)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            bool level = draft.Operator.IsLevel();
            HoldKind effective = draft.Hold != HoldKind.Unknown
                ? draft.Hold
                : (level ? HoldKind.WhileActive : HoldKind.ForDuration);

            var mode = new DropDownCell { Width = 150 };
            var builder = ChoiceList.Build();
            if (level)
                builder.Add(HoldId(HoldKind.WhileActive), "While active");
            builder.Add(HoldId(HoldKind.ForDuration), "For a set time");
            builder.Add(HoldId(HoldKind.Indefinite), "Until replaced");
            mode.SetChoices(builder.Selected(HoldId(effective)));
            row.Children.Add(mode);

            var seconds = new TextBox
            {
                Width = 56,
                Height = 30,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = (draft.HoldDurationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                ToolTip = "Seconds to hold the page after each fire.",
                Visibility = effective == HoldKind.ForDuration ? Visibility.Visible : Visibility.Collapsed,
            };
            row.Children.Add(seconds);
            row.Children.Add(new TextBlock
            {
                Text = "seconds",
                FontSize = 11,
                Foreground = DisplayPalette.SubLabel,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = effective == HoldKind.ForDuration ? Visibility.Visible : Visibility.Collapsed,
            });

            mode.SelectionCommitted += (s, id) =>
            {
                HoldKind chosen = ParseHold(id);
                if (chosen != draft.Hold)
                {
                    draft.Hold = chosen;
                    commit();
                }
            };
            CommitOnLeave(seconds, () =>
            {
                var secs = ParseNum(seconds.Text);
                if (secs != null && secs.Value > 0)
                {
                    draft.HoldDurationMs = (int)Math.Round(secs.Value * 1000.0);
                    commit();
                }
            });
            return row;
        }

        private static string HoldId(HoldKind kind) => kind.ToString();
        private static HoldKind ParseHold(string id)
            => Enum.TryParse(id, out HoldKind k) ? k : HoldKind.WhileActive;

        // ── Commit paths (every one goes through ApplyDisplayConfig) ───────

        // One field commit for either the pending draft or a committed rule. For the draft: a
        // committable draft is promoted to a real top-of-stack rule (and editing continues on
        // it); an incomplete draft just re-renders so the value box appears and the pending
        // change is retained. For a committed rule: the existing update path (which itself
        // gates on committability so an empty VALUE box never degrades the rule).
        private void CommitField(RuleEdit draft, string ruleId, bool isDraft)
        {
            if (!isDraft)
            {
                CommitUpdate(draft, ruleId);
                return;
            }
            if (!DisplayTriggersEditModel.IsCommittable(_addDraft))
            {
                RenderTriggerRows(_lastSnapshot);   // keep the draft open; reveal the value box
                return;
            }
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.InsertRuleAtTop(_addDraft, out string newId);
            _draftExpanded = false;
            _addDraft = null;
            ApplyAndReload(cfg);
            _expandedRuleId = newId;
            var reloaded = FindRule(newId);
            _expandedDraft = reloaded != null ? DisplayTriggersEditModel.ToDraft(reloaded) : null;
            RenderTriggerRows(_lastSnapshot);
        }

        private void CommitUpdate(RuleEdit draft, string ruleId)
        {
            if (ReconcileIfExternallyChanged())
                return;
            // Gate exactly like the add flow: a draft that would degrade the rule (a
            // value-requiring operator with no value yet) is NOT applied. Re-render so the
            // VALUE box appears and the working draft carries the pending change; the rule on
            // disk stays intact until the edit is complete.
            if (!DisplayTriggersEditModel.IsCommittable(draft))
            {
                RenderTriggerRows(_lastSnapshot);
                return;
            }
            var cfg = _editModel.UpdateRule(draft);
            ApplyAndReload(cfg);
            // Refresh the working draft from the normalized, reloaded rule so any load-time
            // coercion is reflected and the draft cannot drift from what was applied.
            var reloaded = FindRule(ruleId);
            _expandedDraft = reloaded != null ? DisplayTriggersEditModel.ToDraft(reloaded) : null;
            RenderTriggerRows(_lastSnapshot);   // keeps _expandedRuleId open, fresh draft
        }

        private void SetRun(string ruleId, string runId)
        {
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.SetRun(ruleId, runId);
            ApplyAndReload(cfg);
            var reloaded = FindRule(ruleId);
            _expandedDraft = reloaded != null ? DisplayTriggersEditModel.ToDraft(reloaded) : null;
            RenderTriggerRows(_lastSnapshot);
        }

        private void RemoveRule(string ruleId)
        {
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.RemoveRule(ruleId);
            if (string.Equals(ruleId, _expandedRuleId, StringComparison.Ordinal))
            {
                _expandedRuleId = null;
                _expandedDraft = null;
            }
            ApplyAndReload(cfg);
            RenderTriggerRows(_lastSnapshot);
        }

        private void DuplicateRule(string ruleId)
        {
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.DuplicateRule(ruleId, out string newId);
            ApplyAndReload(cfg);
            // The copy opens, selected (spec) — the pending draft collapses but survives.
            _draftExpanded = false;
            if (newId != null)
            {
                _expandedRuleId = newId;
                _expandedDraft = null;
            }
            RenderTriggerRows(_lastSnapshot);
        }

        private void MoveRule(string ruleId, int delta)
        {
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.MoveRule(ruleId, delta);
            ApplyAndReload(cfg);
            RenderTriggerRows(_lastSnapshot);
        }

        // If the host's document changed out from under the editor (a generation rebind or
        // another surface republished it) between the last reload and this commit, adopt the
        // external document instead of overwriting it with one built from the now-stale model.
        private bool ReconcileIfExternallyChanged()
        {
            if (_host == null || ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
                return false;
            EnterTriggersEditor();
            return true;
        }

        // Publish the edit, then rebuild the model from the normalized, republished config
        // (ids survive normalization), and signal the shell so it can keep the Overview
        // consistent.
        private void ApplyAndReload(DisplayCustomizationConfig cfg)
        {
            _host.ApplyDisplayConfig(cfg);
            _editModelSource = _host.GetDisplayConfig();
            // _ruleSet MUST be carried into the rebuilt model — omitting it fell back to
            // the ctor's Itm default, so the first commit in the legacy editor silently
            // swapped the whole row list to the ITM rules.
            _editModel = new DisplayTriggersEditModel(_editModelSource, _host.ItmDeviceId,
                _settings?.ItmDefaultPage ?? (byte)1, _ruleSet);
            ConfigApplied?.Invoke(this, EventArgs.Empty);
        }

        private void ToggleExpanded(string ruleId)
        {
            // Opening a committed row collapses a pending draft but keeps it — clicking
            // away must never silently lose in-progress work (only ⋯ remove discards).
            _draftExpanded = false;
            if (string.Equals(ruleId, _expandedRuleId, StringComparison.Ordinal))
            {
                _expandedRuleId = null;
            }
            else
            {
                // Degraded rows are not editable — never open one (a keyboard Enter/Space
                // reaches every focusable row, degraded ones included).
                var rule = FindRule(ruleId);
                if (rule == null || rule.DegradedAtLoad)
                {
                    RenderTriggerRows(_lastSnapshot);
                    return;
                }
                _expandedRuleId = ruleId;
            }
            _expandedDraft = null;   // BuildDrawer builds a fresh draft for the newly open row
            RenderTriggerRows(_lastSnapshot);
        }

        // ── Small helpers ─────────────────────────────────────────────────

        // The rule's current index in the edit model (== its position among the table's rule
        // rows), used to translate a table RowMoved(newIndex) into a relative move.
        private int IndexOfRule(string ruleId)
        {
            var rules = _editModel.Rules;
            for (int i = 0; i < rules.Count; i++)
                if (string.Equals(rules[i].Id, ruleId, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private DisplayRule FindRule(string ruleId)
        {
            foreach (var rule in _editModel.Rules)
                if (string.Equals(rule.Id, ruleId, StringComparison.Ordinal))
                    return rule;
            return null;
        }

        // Opens the property picker for a draft's WHEN source; returns true when the user
        // picked one (and the draft was updated — a mapped control also adopts its shape).
        private bool PickProperty(RuleEdit draft)
        {
            var owner = Window.GetWindow(this);
            var builtIns = BuiltInProperties.All;
            var all = _propertyCatalog != null
                ? _propertyCatalog.GetAllPropertyNames()
                : Array.Empty<string>();
            var mappedRoles = _roleCatalog?.GetMappedRoles()?.Roles;
            var itmPages = CollectItmPageProperties();
            // Live-value reader: defensive — any catalog miss or throw becomes null text.
            Func<string, object> valueReader = name =>
            {
                if (_propertyCatalog != null
                    && _propertyCatalog.TryReadPropertyValue(name, out object value))
                    return value;
                return null;
            };
            if (PropertyPickerDialog.TryPick(owner, builtIns, all, mappedRoles, draft.SourceName,
                    _pickerStore, itmPages, valueReader,
                    out string picked, out PropertyKind kind))
            {
                DisplayTriggersEditModel.AdoptPickedProperty(draft, picked, kind);
                return true;
            }
            return false;
        }

        // FieldMappings sources first (Phase 6 forward-ready), then BuiltInProperties.All,
        // deduped — today's pages are fed by built-ins.
        private IReadOnlyList<string> CollectItmPageProperties()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var mappings = _host?.GetDisplayConfig()?.FieldMappings;
            if (mappings != null)
            {
                foreach (var kv in mappings)
                {
                    string name = kv.Value?.Source?.Name;
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                        result.Add(name);
                }
            }
            foreach (var name in BuiltInProperties.All)
            {
                if (seen.Add(name))
                    result.Add(name);
            }
            return result;
        }

        private ItmPage DefaultPage()
        {
            var pages = _editModel.PageOptions();
            return pages.Count > 0 ? pages[0] : ItmPage.LapInfo;
        }

        private ItmPage OtherPage(ItmPage? notThis)
        {
            foreach (var p in _editModel.PageOptions())
                if (p != notThis)
                    return p;
            return DefaultPage();
        }

        private static ItmPage? ParsePage(string id)
            => Enum.TryParse(id, out ItmPage p) ? p : (ItmPage?)null;

        private static TextBlock SectionTitle(string text, Brush color)
            => new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = color,
                Margin = new Thickness(0, 0, 0, 12),
            };

        private static TextBlock FieldLabel(string text, double top = 0)
            => new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = DisplayPalette.SubLabel,
                Margin = new Thickness(0, top, 0, 0),
            };

        // Give a Border/TextBlock "link" the same activation from the keyboard as from a
        // click: focusable, and Enter/Space run the same action.
        private static void MakeKeyActivatable(FrameworkElement el, Action activate)
        {
            el.Focusable = true;
            el.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    activate();
                    e.Handled = true;
                }
            };
        }

        // Commit a text edit on focus loss or Enter (never per keystroke).
        private static void CommitOnLeave(TextBox box, Action commit)
        {
            box.LostFocus += (s, e) => commit();
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    commit();
                    e.Handled = true;
                }
            };
        }

        private static double? ParseNum(string s)
            => double.TryParse(s?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                ? v : (double?)null;
    }
}
