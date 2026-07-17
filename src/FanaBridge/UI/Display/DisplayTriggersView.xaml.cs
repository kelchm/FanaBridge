using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FanaBridge.Adapters;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>
    /// The Triggers editor of the Display tab (design's ITM Triggers editor): the
    /// add-trigger card, the priority-ordered expandable rule rows with the full
    /// WHEN/IS/VALUE/SHOW/HOLD/ELIGIBLE detail, drag + keyboard/context-menu reorder, the
    /// pinned base row, and the live-state merge that patches chips in place without ever
    /// disturbing an open editor row. Every committed edit flows through the SimHub-free
    /// <see cref="DisplayTriggersEditModel"/> into <c>host.ApplyDisplayConfig</c> — this
    /// view is only the WPF that builds and commits, exactly the pattern the collapsed
    /// Overview rows already use.
    ///
    /// The Display tab shell owns navigation and polling: it calls <see cref="Bind"/> once,
    /// <see cref="Enter"/> when this view becomes active, <see cref="Poll"/> each tick while
    /// it is, and <see cref="BeginAdd"/> from the Overview empty-state. The view signals back
    /// through <see cref="BackRequested"/> (the ‹ ghost back button) and
    /// <see cref="ConfigApplied"/> (after a committed edit republishes the config, so the
    /// shell can refresh its Overview priority list).
    /// </summary>
    public partial class DisplayTriggersView : UserControl
    {
        private enum AddTriggerType { Telemetry, MappedControl }

        // ── Bound members (the shell's own instances, wired in Bind) ───────
        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private DisplaySettings _settings;
        private DisplayRuleSnapshot _lastSnapshot;

        // ── Editor state ──────────────────────────────────────────────────
        private DisplayTriggersEditModel _editModel;
        private DisplayCustomizationConfig _editModelSource;   // the config the model was built from
        private string _expandedRuleId;                        // the one open editor row, or null
        private RuleEdit _expandedDraft;                       // the open row's working draft (survives re-renders)
        private bool _addOpen;
        private AddTriggerType _addType = AddTriggerType.Telemetry;
        private RuleEdit _addDraft;

        // Per-row live-chip handles, keyed by rule id, so a poll can patch the state chip
        // and countdown in place (never rebuilding an open editor row).
        private sealed class RowChips
        {
            public Border Container;
            public TextBlock Chip;
            public TextBlock Seconds;
            public TextBlock Rank;
            public TextBlock Label;
            public bool Degraded;
        }
        private readonly Dictionary<string, RowChips> _rowChips =
            new Dictionary<string, RowChips>(StringComparer.Ordinal);

        // Rule-row containers in priority order (base row excluded) — the drop-target
        // geometry for drag reordering.
        private readonly List<KeyValuePair<string, FrameworkElement>> _ruleRowOrder =
            new List<KeyValuePair<string, FrameworkElement>>();

        // ── Drag state ────────────────────────────────────────────────────
        private string _dragRuleId;
        private Point _dragStart;
        private bool _dragging;
        private UIElement _dragHandle;
        private Rectangle _dropIndicator;

        // ── Seam events (the shell subscribes once in its Bind) ────────────
        internal event EventHandler BackRequested;
        internal event EventHandler ConfigApplied;

        public DisplayTriggersView()
        {
            InitializeComponent();
        }

        // ── The bind/input surface (the seam) ──────────────────────────────

        // Wires the view to the shell's device host, the two on-demand editor catalogs, and
        // the SAME mutable DisplaySettings reference the shell holds (the view reads
        // ItmDefaultPage live at model-build/render time). Call once after construction.
        internal void Bind(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog,
            DisplaySettings settings)
        {
            _host = host;
            _propertyCatalog = propertyCatalog;
            _roleCatalog = roleCatalog;
            _settings = settings;
        }

        // Called when the Triggers view becomes active: cache the current snapshot, build a
        // fresh edit model from the host's current config, and render. Resets any prior
        // editing state (a re-entry is a clean slate).
        internal void Enter(DisplayRuleSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            EnterTriggersEditor();
        }

        // The Overview empty-state "＋ Add trigger" path: open the add card straight away with
        // a fresh telemetry draft. The shell has already navigated here (rebuilding the model).
        internal void BeginAdd()
        {
            _addOpen = true;
            _addType = AddTriggerType.Telemetry;
            _addDraft = _editModel.NewTelemetryDraft();
            RenderAddCard();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        // ── Entry / navigation ────────────────────────────────────────────

        // Builds a fresh edit model from the host's current config and renders the rows.
        // Resets any prior editing state.
        private void EnterTriggersEditor()
        {
            _expandedRuleId = null;
            _expandedDraft = null;
            _addOpen = false;
            _addDraft = null;
            _editModelSource = _host?.GetDisplayConfig();
            _editModel = new DisplayTriggersEditModel(_editModelSource, _host?.ItmDeviceId ?? 0,
                _settings?.ItmDefaultPage ?? (byte)1);
            RenderAddCard();
            RenderTriggerRows(_lastSnapshot);
        }

        private void TriggersAdd_Click(object sender, RoutedEventArgs e)
        {
            _addOpen = !_addOpen;
            if (_addOpen)
            {
                _addType = AddTriggerType.Telemetry;
                _addDraft = _editModel.NewTelemetryDraft();
            }
            else
            {
                _addDraft = null;
            }
            RenderAddCard();
        }

        // ── Poll integration (called from the shell's Poll while active) ───

        internal void Poll(DisplayRuleSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            if (_editModel == null || _host == null)
                return;
            // A drag in progress must never be interrupted by a rebuild: it holds mouse
            // capture on a ⠿ handle, and clearing the panel children unparents that handle,
            // dropping the capture — the gesture strands and _dragging sticks true, killing
            // subsequent row clicks. Chip patching never touches the children collection, so
            // it stays safe; defer every rebuild (and any external reconcile) until the drop.
            bool dragInProgress = _dragHandle != null || _dragging;
            // An external config change (generation rebind, another surface) → rebuild.
            if (!ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
            {
                if (dragInProgress)
                    return;
                EnterTriggersEditor();
                return;
            }
            // Not editing: a full rebuild is harmless and keeps the row look fully live.
            // Editing (an open editor or add card) or mid-drag: only patch the chips, so the
            // open controls, focus, text-in-progress, and the drag gesture are never disturbed.
            if (!dragInProgress && _expandedRuleId == null && !_addOpen)
                RenderTriggerRows(snapshot);
            else
                PatchTriggerChips(snapshot);
        }

        // ── Row rendering ─────────────────────────────────────────────────

        private void RenderTriggerRows(DisplayRuleSnapshot snapshot)
        {
            // A rebuild invalidates any in-progress drag (its captured handle is about to be
            // unparented). Release the capture and clear the drag state here so a rebuild can
            // never strand _dragging=true and deaden row clicks, whatever path reached us.
            if (_dragHandle != null)
                _dragHandle.ReleaseMouseCapture();
            _dragHandle = null;
            _dragging = false;
            _dragRuleId = null;

            RemoveDropIndicator();
            panelTriggerRows.Children.Clear();
            _rowChips.Clear();
            _ruleRowOrder.Clear();

            byte wire = _settings != null ? _settings.ItmDefaultPage : (byte)1;
            var rows = _editModel.Rows(snapshot, wire);
            foreach (var row in rows)
            {
                if (row.IsBase)
                    panelTriggerRows.Children.Add(BuildBaseRow(row));
                else if (row.Degraded)
                    panelTriggerRows.Children.Add(BuildDegradedRow(row));
                else
                    panelTriggerRows.Children.Add(BuildRuleRow(row));
            }

            txtTriggersEmpty.Visibility = _editModel.Rules.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // In-place chip patch: only the live-state chip, countdown, and muted dimming, so an
        // open editor row keeps its controls and focus.
        private void PatchTriggerChips(DisplayRuleSnapshot snapshot)
        {
            var live = new Dictionary<string, DisplayRuleRow>(StringComparer.Ordinal);
            if (snapshot?.ItmRules != null)
                foreach (var r in snapshot.ItmRules)
                    if (r.RuleId != null)
                        live[r.RuleId] = r;

            foreach (var kv in _rowChips)
            {
                var holder = kv.Value;
                var chip = live.TryGetValue(kv.Key, out var state)
                    ? DisplayOverviewRender.StateChip(state.Status, state.RemainingMs)
                    : default(RuleStateChip);
                ApplyChip(holder, chip);
            }
        }

        private static void ApplyChip(RowChips holder, RuleStateChip chip)
        {
            if (holder.Chip != null)
            {
                holder.Chip.Text = chip.Chip ?? "";
                holder.Chip.Visibility = string.IsNullOrEmpty(chip.Chip)
                    ? Visibility.Collapsed : Visibility.Visible;
                holder.Chip.Foreground = chip.OnScreen ? DisplayPalette.GreenAccent : DisplayPalette.ChipText;
            }
            if (holder.Seconds != null)
            {
                holder.Seconds.Text = chip.Seconds ?? "";
                holder.Seconds.Visibility = chip.Seconds != null
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            // The on-screen accent spans the WHOLE row, not just the chip — patch the rank,
            // label, and border together so an off→on transition during an in-place poll
            // (which runs while a different row's editor is open) can't leave a green
            // "on screen" chip on an otherwise-inactive row body. Degraded rows never go
            // on-screen, so they keep their fixed styling.
            if (!holder.Degraded)
                ApplyRowAccent(holder, chip.OnScreen);
            if (holder.Container != null)
                holder.Container.Opacity = (chip.Muted || holder.Degraded) ? 0.5 : 1.0;
        }

        // The single definition of a rule row's on-screen accent — rank + label colour and
        // the border's background / brush / left accent bar — shared by first build and every
        // in-place patch so the two can't drift.
        private static void ApplyRowAccent(RowChips holder, bool onScreen)
        {
            if (holder.Rank != null)
                holder.Rank.Foreground = onScreen ? DisplayPalette.GreenRank : DisplayPalette.MutedRank;
            if (holder.Label != null)
                holder.Label.Foreground = onScreen ? DisplayPalette.OnScreenText : DisplayPalette.RowText;
            if (holder.Container != null)
            {
                holder.Container.Background = onScreen ? DisplayPalette.OnScreenBg : DisplayPalette.RowBg;
                holder.Container.BorderBrush = onScreen ? DisplayPalette.OnScreenBorder : DisplayPalette.RowBorder;
                holder.Container.BorderThickness = onScreen ? new Thickness(3, 1, 1, 1) : new Thickness(1);
            }
        }

        // One editable rule row: a clickable collapsed header, plus the detail panel when
        // this is the open row.
        private UIElement BuildRuleRow(TriggerRowModel row)
        {
            var rule = FindRule(row.RuleId);
            bool expanded = string.Equals(row.RuleId, _expandedRuleId, StringComparison.Ordinal);

            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };

            var header = BuildRowHeader(row, expanded, out var chips);
            container.Children.Add(header);
            _rowChips[row.RuleId] = chips;
            _ruleRowOrder.Add(new KeyValuePair<string, FrameworkElement>(row.RuleId, container));

            if (expanded && rule != null)
                container.Children.Add(BuildDetail(rule));

            return container;
        }

        // The collapsed header: [⠿ handle] [rank] [label] [eligibility] [state chip]
        // [countdown] [chevron]. Clicking anywhere but the handle toggles the editor.
        private Border BuildRowHeader(TriggerRowModel row, bool expanded, out RowChips chips)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // handle
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // rank
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // eligibility
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // chip
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // seconds
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // chevron

            var handle = new TextBlock
            {
                Text = "⠿",
                FontSize = 13,
                Foreground = DisplayPalette.HandColor,
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to reorder priority",
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AttachDragHandle(handle, row.RuleId);
            Grid.SetColumn(handle, 0);
            grid.Children.Add(handle);

            var rank = new TextBlock
            {
                Text = row.Rank,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.MutedRank,   // on-screen accent applied via ApplyRowAccent below
                Width = 16,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 1);
            grid.Children.Add(rank);

            var label = new TextBlock
            {
                Text = row.Label,
                FontSize = 12.5,
                Foreground = DisplayPalette.RowText,   // on-screen accent applied via ApplyRowAccent below
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);

            if (!string.IsNullOrEmpty(row.Eligibility))
            {
                var elig = new TextBlock
                {
                    Text = row.Eligibility,
                    FontSize = 10.5,
                    Foreground = DisplayPalette.EligChip,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(elig, 3);
                grid.Children.Add(elig);
            }

            var chip = new TextBlock
            {
                Text = row.Chip ?? "",
                FontSize = 10.5,
                Foreground = row.OnScreen ? DisplayPalette.GreenAccent : DisplayPalette.ChipText,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = string.IsNullOrEmpty(row.Chip) ? Visibility.Collapsed : Visibility.Visible,
            };
            Grid.SetColumn(chip, 4);
            grid.Children.Add(chip);

            var seconds = new TextBlock
            {
                Text = row.Seconds ?? "",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.GreenAccent,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = row.Seconds != null ? Visibility.Visible : Visibility.Collapsed,
            };
            Grid.SetColumn(seconds, 5);
            grid.Children.Add(seconds);

            var chevron = new TextBlock
            {
                Text = expanded ? "▾" : "▸",
                FontSize = 12,
                Foreground = DisplayPalette.ChevronBrush,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(chevron, 6);
            grid.Children.Add(chevron);

            var border = new Border
            {
                Background = DisplayPalette.RowBg,           // on-screen accent applied via ApplyRowAccent below
                BorderBrush = DisplayPalette.RowBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = expanded ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = Cursors.Hand,
                Focusable = true,
                Child = grid,
                Opacity = row.Muted ? 0.5 : 1.0,
            };
            string ruleId = row.RuleId;
            border.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragging) return;   // a drag ended on the handle, not a click
                ToggleExpanded(ruleId);
            };
            border.KeyDown += (s, e) => RowKeyDown(ruleId, e);
            border.ContextMenu = BuildRowContextMenu(ruleId, canRemove: true);

            chips = new RowChips { Container = border, Chip = chip, Seconds = seconds, Rank = rank, Label = label };
            ApplyRowAccent(chips, row.OnScreen);
            return border;
        }

        // A degraded rule (loaded from a newer version): muted, non-expandable, still
        // reorderable and removable, with a "created by a newer version" hint.
        private UIElement BuildDegradedRow(TriggerRowModel row)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var handle = new TextBlock
            {
                Text = "⠿",
                FontSize = 13,
                Foreground = DisplayPalette.HandColor,
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to reorder priority",
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AttachDragHandle(handle, row.RuleId);
            Grid.SetColumn(handle, 0);
            grid.Children.Add(handle);

            var rank = new TextBlock
            {
                Text = row.Rank,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.MutedRank,
                Width = 16,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 1);
            grid.Children.Add(rank);

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = row.Label,
                FontSize = 12.5,
                Foreground = DisplayPalette.RowText,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = "created by a newer version — not editable here",
                FontSize = 10.5,
                Foreground = DisplayPalette.KLabelBrush,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            var border = new Border
            {
                Background = DisplayPalette.RowBg,
                BorderBrush = DisplayPalette.RowBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Opacity = 0.5,
                Focusable = true,
                Margin = new Thickness(0, 0, 0, 7),
                Child = grid,
            };
            string ruleId = row.RuleId;
            border.KeyDown += (s, e) => RowKeyDown(ruleId, e);
            border.ContextMenu = BuildRowContextMenu(ruleId, canRemove: true);

            _rowChips[row.RuleId] = new RowChips { Container = border, Degraded = true };
            _ruleRowOrder.Add(new KeyValuePair<string, FrameworkElement>(row.RuleId, border));
            return border;
        }

        // The pinned "★ Always → <base>" row: dashed, last, not draggable, not expandable —
        // its page is edited on the Overview (Starting page).
        private UIElement BuildBaseRow(TriggerRowModel row)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rank = new TextBlock
            {
                Text = row.Rank,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.BaseRank,
                Width = 16,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 0);
            grid.Children.Add(rank);

            var label = new TextBlock
            {
                Text = row.Label,
                FontSize = 12.5,
                Foreground = DisplayPalette.BaseText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var hint = new TextBlock
            {
                Text = "edit on Overview",
                FontSize = 10.5,
                Foreground = DisplayPalette.KLabelBrush,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);

            var host = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            host.Children.Add(new Rectangle
            {
                Stroke = DisplayPalette.BaseDash,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                RadiusX = 4,
                RadiusY = 4,
                Fill = DisplayPalette.BaseBg,
            });
            host.Children.Add(new Border { Padding = new Thickness(10, 8, 10, 8), Child = grid });
            return host;
        }

        // ── The expanded detail editor ────────────────────────────────────

        private UIElement BuildDetail(DisplayRule rule)
        {
            // One working draft per open row, kept across re-renders (an operator switch that
            // reveals the VALUE box re-renders without committing — see CommitUpdate). Rebuilt
            // only when a different row opens or a commit reloads the normalized rule.
            if (_expandedDraft == null
                || !string.Equals(_expandedDraft.Id, rule.Id, StringComparison.Ordinal))
                _expandedDraft = DisplayTriggersEditModel.ToDraft(rule);
            var draft = _expandedDraft;
            string ruleId = rule.Id;

            var body = new StackPanel();

            // WHEN row: property picker + operator + value + (hysteresis for thresholds).
            var when = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            when.Children.Add(FieldColumn("WHEN — SIMHUB PROPERTY",
                BuildPropertyButton(draft, () => CommitUpdate(draft, ruleId)), 240));
            when.Children.Add(FieldColumn("IS",
                BuildOperatorCombo(draft, ruleId), 130));
            if (draft.Operator.RequiresValue())
                when.Children.Add(FieldColumn("VALUE",
                    BuildValueBox(draft, ruleId), 90));
            if (draft.Operator.RequiresValue())
                when.Children.Add(FieldColumn("HYSTERESIS (±)",
                    BuildHysteresisBox(draft, ruleId), 90));
            body.Children.Add(when);

            // SHOW + HOLD row.
            var show = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            show.Children.Add(FieldColumn("SHOW", BuildShowCombo(draft, ruleId), 250));
            show.Children.Add(FieldColumn("HOLD", BuildHoldEditor(draft, ruleId), 200));
            body.Children.Add(show);

            // ELIGIBLE segmented control.
            body.Children.Add(FieldColumn("ELIGIBLE", BuildEligibleSegments(draft, ruleId), double.NaN));

            // Footer: Remove … Enabled … Close.
            body.Children.Add(BuildDetailFooter(rule, ruleId));

            return new Border
            {
                Background = DisplayPalette.DetailBg,
                BorderBrush = DisplayPalette.RowBorder,
                BorderThickness = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(14, 13, 15, 15),
                Margin = new Thickness(0, 0, 0, 0),
                Child = body,
            };
        }

        private FrameworkElement BuildPropertyButton(RuleEdit draft, Action commit)
        {
            var btn = new Button
            {
                Content = string.IsNullOrEmpty(draft.SourceName) ? "(pick property)" : draft.SourceName,
                FontFamily = DisplayPalette.Mono,
                FontSize = 11,
                Foreground = DisplayPalette.PropMono,
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
            };
            btn.Click += (s, e) =>
            {
                if (PickProperty(draft))
                    commit();
            };
            return btn;
        }

        private ComboBox BuildOperatorCombo(RuleEdit draft, string ruleId)
        {
            var combo = new ComboBox { Width = 130 };
            // A loaded rule using an unlisted-but-valid kind (ActionTriggered) is shown by
            // its own operator, not silently mislabeled as the first item.
            foreach (var op in DisplayTriggersEditModel.OperatorOptionsFor(draft.Operator))
                combo.Items.Add(new ComboBoxItem
                {
                    Content = DisplayTriggersEditModel.OperatorLabel(op),
                    Tag = op,
                });
            SelectByTagValue(combo, draft.Operator);
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is ConditionKind op)
                {
                    draft.Operator = op;
                    CommitUpdate(draft, ruleId);
                }
            };
            return combo;
        }

        private TextBox BuildValueBox(RuleEdit draft, string ruleId)
        {
            var box = new TextBox
            {
                Width = 90,
                Text = draft.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
            };
            CommitOnLeave(box, () =>
            {
                draft.Value = ParseNum(box.Text);
                CommitUpdate(draft, ruleId);
            });
            return box;
        }

        private TextBox BuildHysteresisBox(RuleEdit draft, string ruleId)
        {
            var box = new TextBox
            {
                Width = 90,
                Text = draft.Hysteresis?.ToString(CultureInfo.InvariantCulture) ?? "",
                ToolTip = "Deadband that stops a value hovering at the threshold from flapping.",
            };
            CommitOnLeave(box, () =>
            {
                draft.Hysteresis = ParseNum(box.Text);
                CommitUpdate(draft, ruleId);
            });
            return box;
        }

        // One SHOW dropdown: every single page this device offers, then the alternating
        // pairs. Legacy-screen targets are a later phase and not offered here.
        private ComboBox BuildShowCombo(RuleEdit draft, string ruleId)
        {
            var combo = new ComboBox { Width = 250 };
            var pages = _editModel.PageOptions();
            // A rule already targeting a legacy screen (v1 does not author these — P3 owns
            // that) is shown honestly by its current target rather than falling back to the
            // first page, and keeps its id unless the user deliberately picks a page here.
            if (draft.TargetKind == TargetKind.LegacyScreen && !string.IsNullOrEmpty(draft.ScreenId))
                combo.Items.Add(new ComboBoxItem
                {
                    Content = "Legacy screen: " + draft.ScreenId,
                    Tag = new ShowOption { Kind = TargetKind.LegacyScreen, ScreenId = draft.ScreenId },
                });
            foreach (var p in pages)
                combo.Items.Add(new ComboBoxItem
                {
                    Content = ItmTelemetry.NameOf(p),
                    Tag = new ShowOption { Kind = TargetKind.Page, Page = p },
                });
            for (int i = 0; i < pages.Count; i++)
                for (int j = i + 1; j < pages.Count; j++)
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = "Alternate: " + ItmTelemetry.NameOf(pages[i]) + " ⇄ "
                                  + ItmTelemetry.NameOf(pages[j]),
                        Tag = new ShowOption { Kind = TargetKind.Alternate, PageA = pages[i], PageB = pages[j] },
                    });
            SelectShowOption(combo, draft);
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is ShowOption opt)
                {
                    draft.TargetKind = opt.Kind;
                    if (opt.Kind == TargetKind.Page)
                    {
                        draft.Page = opt.Page;
                    }
                    else if (opt.Kind == TargetKind.LegacyScreen)
                    {
                        draft.ScreenId = opt.ScreenId;   // re-selecting the current target is a no-op
                    }
                    else
                    {
                        draft.PageA = opt.PageA;
                        draft.PageB = opt.PageB;
                    }
                    CommitUpdate(draft, ruleId);
                }
            };
            return combo;
        }

        // HOLD: kind dropdown (While active offered for level kinds only), with a seconds
        // box beside it when For duration is chosen.
        private FrameworkElement BuildHoldEditor(RuleEdit draft, string ruleId)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var combo = new ComboBox { Width = 130 };
            bool level = draft.Operator.IsLevel();
            foreach (var hold in DisplayTriggersEditModel.Holds)
            {
                if (hold == HoldKind.WhileActive && !level)
                    continue;   // an edge/event condition has no "still active" to hold on
                combo.Items.Add(new ComboBoxItem
                {
                    Content = DisplayTriggersEditModel.HoldLabel(hold),
                    Tag = hold,
                });
            }
            HoldKind effective = draft.Hold;
            if (effective == HoldKind.Unknown)
                effective = level ? HoldKind.WhileActive : HoldKind.ForDuration;
            SelectByTagValue(combo, effective);
            row.Children.Add(combo);

            var seconds = new TextBox
            {
                Width = 56,
                Margin = new Thickness(8, 0, 0, 0),
                Text = (draft.HoldDurationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                ToolTip = "Seconds to hold the page after each fire.",
                Visibility = effective == HoldKind.ForDuration ? Visibility.Visible : Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(seconds);
            var secondsSuffix = new TextBlock
            {
                Text = "s",
                Foreground = DisplayPalette.KLabelBrush,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = effective == HoldKind.ForDuration ? Visibility.Visible : Visibility.Collapsed,
            };
            row.Children.Add(secondsSuffix);

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is HoldKind hold)
                {
                    draft.Hold = hold;
                    CommitUpdate(draft, ruleId);
                }
            };
            CommitOnLeave(seconds, () =>
            {
                var secs = ParseNum(seconds.Text);
                if (secs != null && secs.Value > 0)
                {
                    draft.HoldDurationMs = (int)Math.Round(secs.Value * 1000.0);
                    CommitUpdate(draft, ruleId);
                }
            });
            return row;
        }

        private FrameworkElement BuildEligibleSegments(RuleEdit draft, string ruleId)
        {
            var strip = new Border
            {
                Background = DisplayPalette.SegBarBg,
                BorderBrush = DisplayPalette.SegBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var elig in DisplayTriggersEditModel.Eligibilities)
            {
                bool active = draft.Eligibility == elig;
                var seg = new Border
                {
                    Background = active ? DisplayPalette.AccentBg : Brushes.Transparent,
                    Padding = new Thickness(13, 5, 13, 5),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = DisplayTriggersEditModel.EligibilityLabel(elig),
                        FontSize = 11.5,
                        Foreground = active ? Brushes.White : DisplayPalette.ToggleIdleText,
                    },
                };
                var chosen = elig;
                Action pick = () =>
                {
                    draft.Eligibility = chosen;
                    CommitUpdate(draft, ruleId);
                };
                seg.MouseLeftButtonUp += (s, e) => pick();
                MakeKeyActivatable(seg, pick);
                row.Children.Add(seg);
            }
            strip.Child = row;
            return strip;
        }

        private FrameworkElement BuildDetailFooter(DisplayRule rule, string ruleId)
        {
            var grid = new Grid { Margin = new Thickness(0, 13, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var top = new Border
            {
                BorderBrush = DisplayPalette.RowBorder,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 11, 0, 0),
            };
            Grid.SetColumnSpan(top, 4);
            grid.Children.Add(top);

            var remove = new TextBlock
            {
                Text = "Remove",
                FontSize = 12,
                Foreground = DisplayPalette.RemoveText,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 11, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.MouseLeftButtonUp += (s, e) => RemoveRule(ruleId);
            MakeKeyActivatable(remove, () => RemoveRule(ruleId));
            Grid.SetColumn(remove, 0);
            grid.Children.Add(remove);

            var enabled = new CheckBox
            {
                Content = "Enabled",
                IsChecked = rule.Enabled,
                Margin = new Thickness(0, 11, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.Checked += (s, e) => SetEnabled(ruleId, true);
            enabled.Unchecked += (s, e) => SetEnabled(ruleId, false);
            Grid.SetColumn(enabled, 2);
            grid.Children.Add(enabled);

            var close = new TextBlock
            {
                Text = "Close",
                FontSize = 12,
                Foreground = DisplayPalette.ToggleIdleText,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 11, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            close.MouseLeftButtonUp += (s, e) => ToggleExpanded(ruleId);
            MakeKeyActivatable(close, () => ToggleExpanded(ruleId));
            Grid.SetColumn(close, 3);
            grid.Children.Add(close);

            return grid;
        }

        // ── The add-trigger card ──────────────────────────────────────────

        private void RenderAddCard()
        {
            panelAddCard.Children.Clear();
            panelAddCard.Visibility = _addOpen ? Visibility.Visible : Visibility.Collapsed;
            if (!_addOpen)
                return;

            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = "New trigger",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = DisplayPalette.AddTitle,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // Type chips: Telemetry | Mapped control (the design's "Idle event" is covered
            // by the ELIGIBLE control on every rule, so no third chip).
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            chips.Children.Add(TypeChip("Telemetry", AddTriggerType.Telemetry));
            chips.Children.Add(TypeChip("Mapped control", AddTriggerType.MappedControl));
            body.Children.Add(chips);

            if (_addType == AddTriggerType.Telemetry)
                body.Children.Add(BuildAddTelemetryFields());
            else
                body.Children.Add(BuildAddMappedFields());

            body.Children.Add(BuildAddFooter());

            panelAddCard.Children.Add(new Border
            {
                Background = DisplayPalette.AddCardBg,
                BorderBrush = DisplayPalette.AddCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(14, 13, 15, 13),
                Child = body,
            });
        }

        private Border TypeChip(string text, AddTriggerType type)
        {
            bool active = _addType == type;
            var chip = new Border
            {
                Background = active ? DisplayPalette.AccentBg : Brushes.Transparent,
                BorderBrush = active ? DisplayPalette.AccentBg : DisplayPalette.SegBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = active ? Brushes.White : DisplayPalette.ToggleIdleText,
                },
            };
            Action choose = () =>
            {
                if (_addType == type)
                    return;
                _addType = type;
                _addDraft = type == AddTriggerType.Telemetry
                    ? _editModel.NewTelemetryDraft()
                    : null;   // mapped: the draft is built when a role is chosen
                RenderAddCard();
            };
            chip.MouseLeftButtonUp += (s, e) => choose();
            MakeKeyActivatable(chip, choose);
            return chip;
        }

        private FrameworkElement BuildAddTelemetryFields()
        {
            var when = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            when.Children.Add(FieldColumn("WHEN — PROPERTY",
                BuildPropertyButton(_addDraft, () => { UpdateAddFooterState(); }), 230));
            when.Children.Add(FieldColumn("IS", BuildAddOperatorCombo(), 130));
            if (_addDraft.Operator.RequiresValue())
                when.Children.Add(FieldColumn("VALUE", BuildAddValueBox(), 90));
            return when;
        }

        private ComboBox BuildAddOperatorCombo()
        {
            var combo = new ComboBox { Width = 130 };
            foreach (var op in DisplayTriggersEditModel.Operators)
                combo.Items.Add(new ComboBoxItem
                {
                    Content = DisplayTriggersEditModel.OperatorLabel(op),
                    Tag = op,
                });
            SelectByTagValue(combo, _addDraft.Operator);
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is ConditionKind op)
                {
                    _addDraft.Operator = op;
                    RenderAddCard();   // VALUE column appears/disappears with the kind
                }
            };
            return combo;
        }

        private TextBox BuildAddValueBox()
        {
            var box = new TextBox
            {
                Width = 90,
                Text = _addDraft.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
            };
            CommitOnLeave(box, () =>
            {
                _addDraft.Value = ParseNum(box.Text);
                UpdateAddFooterState();
            });
            return box;
        }

        private FrameworkElement BuildAddMappedFields()
        {
            var roles = _roleCatalog.GetMappedRoles();
            var col = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            col.Children.Add(KLabel("ROLE"));

            if (roles.Roles.Count == 0)
            {
                col.Children.Add(new TextBlock
                {
                    Text = "No Control Mapper roles available for this wheel.",
                    FontSize = 12,
                    Foreground = DisplayPalette.KLabelBrush,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                return col;
            }

            var combo = new ComboBox { Width = 230, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var role in roles.Roles)
                combo.Items.Add(new ComboBoxItem { Content = role, Tag = role });
            // Preselect a role already chosen (re-render after a pick keeps it).
            if (_addDraft != null && !string.IsNullOrEmpty(_addDraft.SourceName))
                for (int i = 0; i < combo.Items.Count; i++)
                    if (((ComboBoxItem)combo.Items[i]).Tag is string r &&
                        DisplayTriggersEditModel.MappedControlPropertyName(r) == _addDraft.SourceName)
                        combo.SelectedIndex = i;
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is string role)
                {
                    _addDraft = _editModel.NewMappedControlDraft(role);
                    UpdateAddFooterState();
                }
            };
            col.Children.Add(combo);

            col.Children.Add(new TextBlock
            {
                Text = roles.Source == MappedRolesSource.MappedOnThisWheel
                    ? "Roles mapped on this wheel."
                    : roles.Source == MappedRolesSource.AggregatedAcrossBases
                        ? "Roles mapped across your Fanatec bases (turn on “Recognize "
                            + "Individual Wheels” in Control Mapper to tell them apart)."
                        : "All assignable roles (none mapped on this wheel yet).",
                FontSize = 11,
                Foreground = DisplayPalette.KLabelBrush,
                Margin = new Thickness(0, 5, 0, 0),
            });
            return col;
        }

        private FrameworkElement BuildAddFooter()
        {
            var grid = new Grid { Margin = new Thickness(0, 11, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var top = new Border
            {
                BorderBrush = DisplayPalette.SegBorder,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 11, 0, 0),
            };
            Grid.SetColumnSpan(top, 3);
            grid.Children.Add(top);

            var cancel = new TextBlock
            {
                Text = "Cancel",
                FontSize = 12,
                Foreground = DisplayPalette.ToggleIdleText,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 11, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Action doCancel = () =>
            {
                _addOpen = false;
                _addDraft = null;
                RenderAddCard();
            };
            cancel.MouseLeftButtonUp += (s, e) => doCancel();
            MakeKeyActivatable(cancel, doCancel);
            Grid.SetColumn(cancel, 1);
            grid.Children.Add(cancel);

            _btnAddCommit = new Button
            {
                Content = "Add",
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 11, 0, 0),
                IsEnabled = CanCommitAdd(),
            };
            _btnAddCommit.Click += (s, e) => CommitAdd();
            Grid.SetColumn(_btnAddCommit, 2);
            grid.Children.Add(_btnAddCommit);

            return grid;
        }

        private Button _btnAddCommit;

        private bool CanCommitAdd()
            => DisplayTriggersEditModel.IsCommittable(_addDraft);

        private void UpdateAddFooterState()
        {
            if (_btnAddCommit != null)
                _btnAddCommit.IsEnabled = CanCommitAdd();
            // The property button caption also has to refresh after a pick.
            RenderAddCard();
        }

        private void CommitAdd()
        {
            if (!CanCommitAdd())
                return;
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.AddRule(_addDraft);
            _addOpen = false;
            _addDraft = null;
            ApplyAndReload(cfg);
            RenderAddCard();
            RenderTriggerRows(_lastSnapshot);
        }

        // ── Commit paths (every one goes through ApplyDisplayConfig) ───────

        private void CommitUpdate(RuleEdit draft, string ruleId)
        {
            if (ReconcileIfExternallyChanged())
                return;
            // Gate exactly like the add flow: a draft that would degrade the rule (a
            // value-requiring operator with no value yet — an empty VALUE box, or an operator
            // just switched to a comparison) is NOT applied. Re-render so the VALUE box
            // appears and the working draft carries the pending change; the rule on disk
            // stays intact until the edit is complete.
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

        private void SetEnabled(string ruleId, bool enabled)
        {
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.SetRuleEnabled(ruleId, enabled);
            ApplyAndReload(cfg);
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
        // The pending edit is dropped rather than silently clobbering the external write.
        private bool ReconcileIfExternallyChanged()
        {
            if (_host == null || ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
                return false;
            EnterTriggersEditor();
            return true;
        }

        // Publish the edit, then rebuild the model from the normalized, republished config
        // (ids survive normalization, so the expanded row and the snapshot agree), and signal
        // the shell so it can keep the Overview's empty-state/priority list consistent.
        private void ApplyAndReload(DisplayCustomizationConfig cfg)
        {
            _host.ApplyDisplayConfig(cfg);
            _editModelSource = _host.GetDisplayConfig();
            _editModel = new DisplayTriggersEditModel(_editModelSource, _host.ItmDeviceId,
                _settings?.ItmDefaultPage ?? (byte)1);
            ConfigApplied?.Invoke(this, EventArgs.Empty);
        }

        private void ToggleExpanded(string ruleId)
        {
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
                    return;
                _expandedRuleId = ruleId;
            }
            _expandedDraft = null;   // BuildDetail builds a fresh draft for the newly open row
            RenderTriggerRows(_lastSnapshot);
        }

        // ── Reorder: context menu + keyboard ──────────────────────────────

        private ContextMenu BuildRowContextMenu(string ruleId, bool canRemove)
        {
            var menu = new ContextMenu();
            var up = new MenuItem { Header = "Move up" };
            up.Click += (s, e) => MoveRule(ruleId, -1);
            menu.Items.Add(up);
            var down = new MenuItem { Header = "Move down" };
            down.Click += (s, e) => MoveRule(ruleId, +1);
            menu.Items.Add(down);
            if (canRemove)
            {
                menu.Items.Add(new Separator());
                var remove = new MenuItem { Header = "Remove" };
                remove.Click += (s, e) => RemoveRule(ruleId);
                menu.Items.Add(remove);
            }
            return menu;
        }

        // Enter / Space open (or close) a focused row's editor — the keyboard equivalent of
        // clicking the header; Alt+Up / Alt+Down reorder it (accessibility + drag fallback).
        private void RowKeyDown(string ruleId, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                ToggleExpanded(ruleId);   // no-ops on a degraded row (not editable)
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                return;
            if (e.Key == Key.Up || e.SystemKey == Key.Up)
            {
                MoveRule(ruleId, -1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.SystemKey == Key.Down)
            {
                MoveRule(ruleId, +1);
                e.Handled = true;
            }
        }

        // ── Reorder: mouse drag on the ⠿ handle ───────────────────────────

        private void AttachDragHandle(UIElement handle, string ruleId)
        {
            handle.MouseLeftButtonDown += (s, e) =>
            {
                _dragRuleId = ruleId;
                _dragStart = e.GetPosition(panelTriggerRows);
                _dragging = false;
                _dragHandle = handle;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseMove += HandleDragMove;
            handle.MouseLeftButtonUp += HandleDragUp;
        }

        private void HandleDragMove(object sender, MouseEventArgs e)
        {
            if (_dragHandle == null || e.LeftButton != MouseButtonState.Pressed)
                return;
            var pos = e.GetPosition(panelTriggerRows);
            if (!_dragging)
            {
                if (Math.Abs(pos.Y - _dragStart.Y) < 5)
                    return;
                _dragging = true;
            }
            ShowDropIndicator(ComputeDropIndex(pos.Y));
        }

        private void HandleDragUp(object sender, MouseButtonEventArgs e)
        {
            var handle = _dragHandle;
            _dragHandle = null;
            handle?.ReleaseMouseCapture();

            bool wasDragging = _dragging;
            string id = _dragRuleId;
            double y = e.GetPosition(panelTriggerRows).Y;
            _dragging = false;
            _dragRuleId = null;
            RemoveDropIndicator();
            e.Handled = true;

            if (wasDragging && id != null)
                CommitDrag(id, ComputeDropIndex(y));
        }

        // The insertion slot (0..ruleCount) the cursor Y falls at, using each rule row's
        // vertical midpoint.
        private int ComputeDropIndex(double y)
        {
            for (int i = 0; i < _ruleRowOrder.Count; i++)
            {
                var el = _ruleRowOrder[i].Value;
                double top, height;
                if (!TryRowBounds(el, out top, out height))
                    continue;
                if (y < top + height / 2.0)
                    return i;
            }
            return _ruleRowOrder.Count;
        }

        private bool TryRowBounds(FrameworkElement el, out double top, out double height)
        {
            top = 0;
            height = 0;
            try
            {
                if (!el.IsVisible)
                    return false;
                var t = el.TransformToAncestor(panelTriggerRows);
                top = t.Transform(new Point(0, 0)).Y;
                height = el.ActualHeight;
                return height > 0;
            }
            catch
            {
                return false;
            }
        }

        private void ShowDropIndicator(int slot)
        {
            RemoveDropIndicator();
            _dropIndicator = new Rectangle
            {
                Height = 2,
                Fill = DisplayPalette.AccentBg,
                Margin = new Thickness(2, 0, 2, 0),
            };
            int at = Math.Max(0, Math.Min(slot, _ruleRowOrder.Count));
            // Rule rows occupy the first children; the base row follows. Insert the
            // indicator just above the row at `slot` (or above the base row at the end).
            at = Math.Min(at, panelTriggerRows.Children.Count);
            panelTriggerRows.Children.Insert(at, _dropIndicator);
        }

        private void RemoveDropIndicator()
        {
            if (_dropIndicator != null)
            {
                panelTriggerRows.Children.Remove(_dropIndicator);
                _dropIndicator = null;
            }
        }

        private void CommitDrag(string ruleId, int slot)
        {
            int from = IndexOfRule(ruleId);
            if (from < 0)
                return;
            int desired = slot <= from ? slot : slot - 1;
            int delta = desired - from;
            if (delta == 0)
                return;
            MoveRule(ruleId, delta);
        }

        // ── Small helpers ─────────────────────────────────────────────────

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
        // picked one (and the draft was updated).
        private bool PickProperty(RuleEdit draft)
        {
            var owner = Window.GetWindow(this);
            var builtIns = BuiltInProperties.All;
            var all = _propertyCatalog.GetAllPropertyNames();
            if (PropertyPickerDialog.TryPick(owner, builtIns, all, draft.SourceName,
                    out string name, out PropertyKind kind))
            {
                draft.SourceKind = kind;
                draft.SourceName = name;
                return true;
            }
            return false;
        }

        private StackPanel FieldColumn(string label, FrameworkElement control, double controlWidth)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            col.Children.Add(KLabel(label));
            control.Margin = new Thickness(0, 4, 0, 0);
            if (!double.IsNaN(controlWidth))
                control.Width = controlWidth;
            col.Children.Add(control);
            return col;
        }

        // Give a Border/TextBlock "link" the same activation from the keyboard as from a
        // click: focusable, and Enter/Space run the same action, so none of these controls is
        // mouse-only.
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

        private static TextBlock KLabel(string text)
            => new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.KLabelBrush,
            };

        private static void SelectByTagValue<T>(ComboBox combo, T value)
        {
            foreach (ComboBoxItem item in combo.Items)
                if (item.Tag is T t && EqualityComparer<T>.Default.Equals(t, value))
                {
                    combo.SelectedItem = item;
                    return;
                }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static void SelectShowOption(ComboBox combo, RuleEdit draft)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (!(item.Tag is ShowOption opt))
                    continue;
                bool match;
                switch (draft.TargetKind)
                {
                    case TargetKind.Alternate:
                        match = opt.Kind == TargetKind.Alternate && SamePair(opt, draft);
                        break;
                    case TargetKind.LegacyScreen:
                        match = opt.Kind == TargetKind.LegacyScreen
                            && string.Equals(opt.ScreenId, draft.ScreenId, StringComparison.Ordinal);
                        break;
                    default:
                        match = opt.Kind == TargetKind.Page && opt.Page == draft.Page;
                        break;
                }
                if (match)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static bool SamePair(ShowOption opt, RuleEdit draft)
            => (opt.PageA == draft.PageA && opt.PageB == draft.PageB)
            || (opt.PageA == draft.PageB && opt.PageB == draft.PageA);

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

        // A single SHOW dropdown choice (a page or an alternating pair).
        private sealed class ShowOption
        {
            public TargetKind Kind;
            public ItmPage? Page;
            public ItmPage? PageA;
            public ItmPage? PageB;
            public string ScreenId;
        }
    }
}
