using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// The shared trigger table: the v9 dense grid of priority-ordered rule rows
    /// (rank · When · Show · Timeout · Runs · State · ⋯), the expand-to-edit drawer beneath
    /// the selected row, drag + keyboard/context-menu reorder, and the live-state patch that
    /// never disturbs an open editor row or an in-flight drag. Extracted from the Triggers
    /// editor (Phase 2 commit 2a) so the same row machinery can serve both the Workbench
    /// editor (2b) and the Overview Monitor list (2c).
    ///
    /// The control is pure WPF over the <see cref="TriggerTableRow"/> projection — it holds
    /// no config, engine, or snapshot vocabulary. The host owns the edit model and commit
    /// paths and drives the control through <see cref="SetRows"/> (full rebuild) and
    /// <see cref="PatchLive"/> (in-place State/accent/countdown patch), providing the
    /// expansion drawer through <see cref="ExpansionContent"/>. Every user gesture surfaces
    /// back as an event: <see cref="RowActivated"/> (click / Enter / Space),
    /// <see cref="RowMoved"/> (drag drop, Alt+arrows, context-menu Move to top — carrying the
    /// target index), and <see cref="RowAction"/> (context-menu action, e.g. "duplicate" /
    /// "remove").
    /// </summary>
    internal class TriggerTableControl : StackPanel
    {
        // Generous character budget before the property grammar left-elides (the WPF
        // CharacterEllipsis is the visual backstop past this): the When cell shares its
        // width with the operator/value span.
        private const int RowPropertyBudget = 30;

        // Per-row live handles, keyed by rule id, so a poll can patch the State cell,
        // countdown, and accent in place (never rebuilding an open editor row).
        private sealed class RowChips
        {
            public Border Container;
            public TextBlock Chip;     // the State-cell text ("on screen" / "waiting" / "off")
            public UIElement Dot;      // the on-screen green dot (Workbench State cell)
            public CountdownRing Seconds;
            public TextBlock Rank;
            public TextBlock Label;    // the When-cell operator/value span (accent-recoloured)
            public TextBlock Now;      // the Monitor Now-cell live value ("62%", "on", "—")
            public Border NowDot;      // the Monitor Now-cell dot (green on screen, grey else)
            public bool Degraded;
        }
        private readonly Dictionary<string, RowChips> _rowChips =
            new Dictionary<string, RowChips>(StringComparer.Ordinal);

        // Rule-row containers in priority order (base row excluded) — the drop-target
        // geometry for drag reordering, and the index source for keyboard/menu moves.
        private readonly List<KeyValuePair<string, FrameworkElement>> _ruleRowOrder =
            new List<KeyValuePair<string, FrameworkElement>>();

        // ── Drag state ────────────────────────────────────────────────────
        private string _dragRuleId;
        private Point _dragStart;
        private bool _dragging;
        private UIElement _dragHandle;
        private Rectangle _dropIndicator;

        /// <summary>The table's mode. 2b wires only <see cref="TriggerTableMode.Workbench"/>
        /// (the expand-to-edit editor); Monitor (the Overview list with the loud winning
        /// emphasis) lands in 2c.</summary>
        public TriggerTableMode Mode { get; set; } = TriggerTableMode.Workbench;

        /// <summary>The one rule id whose editor drawer is open, or null. Set by the host
        /// before <see cref="SetRows"/>; the matching row renders selected (blue left bar,
        /// caret down) and its <see cref="ExpansionContent"/> is appended beneath it.</summary>
        public string ExpandedRuleId { get; set; }

        /// <summary>Builds the expansion drawer for the selected row (the host owns the
        /// editor). Returns null to render no drawer (e.g. a degraded/unknown row).</summary>
        public Func<string, UIElement> ExpansionContent { get; set; }

        /// <summary>Raised when a row is activated (header click, or Enter/Space) with its
        /// rule id. Degraded rows raise it too (keyboard only); the host decides it is a
        /// no-op there.</summary>
        public event Action<string> RowActivated;

        /// <summary>Raised when a reorder gesture (drag drop, Alt+Up/Down, context-menu
        /// Move to top) targets a new position: the rule id and the desired index among the
        /// rule rows. The host translates it to its edit-model move.</summary>
        public event Action<string, int> RowMoved;

        /// <summary>Raised for a per-row action from the overflow/context menu: the rule id
        /// and an action id ("duplicate" / "remove").</summary>
        public event Action<string, string> RowAction;

        /// <summary>True while a drag gesture holds the ⠿ handle capture. The host's poll
        /// consults this to defer any rebuild until the drop (a rebuild would unparent the
        /// captured handle and strand the gesture).</summary>
        public bool IsDragging => _dragHandle != null || _dragging;

        // ── Rendering ─────────────────────────────────────────────────────

        /// <summary>Full rebuild: clears the rows (and any in-flight drag) and rebuilds from
        /// <paramref name="rows"/> in order, appending the expansion drawer to the row whose
        /// id matches <see cref="ExpandedRuleId"/>. In Workbench a dense-grid header strip
        /// leads the stack.</summary>
        public void SetRows(IReadOnlyList<TriggerTableRow> rows)
        {
            // A rebuild invalidates any in-progress drag (its captured handle is about to be
            // unparented). Release the capture and clear the drag state here so a rebuild can
            // never strand _dragging=true and deaden row clicks, whatever path reached us.
            AbortDrag();

            Children.Clear();
            _rowChips.Clear();
            _ruleRowOrder.Clear();

            bool monitor = Mode == TriggerTableMode.Monitor;
            Children.Add(monitor ? BuildMonitorHeaderStrip() : BuildHeaderStrip());

            if (rows == null)
                return;
            foreach (var row in rows)
            {
                if (monitor)
                    Children.Add(row.IsBase ? BuildMonitorBaseRow(row) : BuildMonitorRow(row));
                else if (row.IsBase)
                    Children.Add(BuildBaseRow(row));
                else if (row.Degraded)
                    Children.Add(BuildDegradedRow(row));
                else
                    Children.Add(BuildRuleRow(row));
            }
        }

        /// <summary>In-place live patch: only the State text, on-screen dot, countdown, accent,
        /// and muted dimming for each existing rule row (matched by id), so an open editor row
        /// keeps its controls and focus and an in-flight drag is never disturbed. Never touches
        /// the children collection.</summary>
        public void PatchLive(IReadOnlyList<TriggerTableRow> rows)
        {
            var live = new Dictionary<string, TriggerTableRow>(StringComparer.Ordinal);
            if (rows != null)
                foreach (var r in rows)
                    if (r.RuleId != null)
                        live[r.RuleId] = r;

            foreach (var kv in _rowChips)
            {
                live.TryGetValue(kv.Key, out var state);
                ApplyRowLive(kv.Value, state);
            }
        }

        // The single definition of a row's live presentation — the State cell text + colour,
        // the on-screen dot, the countdown ring, and (Monitor only) the loud winning accent —
        // shared by first build and every in-place patch so the two can't drift. In Workbench
        // the on-screen state is a quiet dot + green text (colour = status); the loud green
        // bar/bg is Monitor-only (kelchm call).
        private void ApplyRowLive(RowChips holder, TriggerTableRow row)
        {
            string stateText = row?.StateText ?? "";
            bool onScreen = row?.OnScreen ?? false;
            string seconds = row?.Seconds;
            bool muted = row?.Muted ?? false;

            if (holder.Chip != null)
            {
                holder.Chip.Text = stateText;
                holder.Chip.Visibility = string.IsNullOrEmpty(stateText)
                    ? Visibility.Collapsed : Visibility.Visible;
                holder.Chip.Foreground = onScreen ? DisplayPalette.GreenAccent : DisplayPalette.ChipText;
            }
            if (holder.Dot != null)
                holder.Dot.Visibility = onScreen ? Visibility.Visible : Visibility.Collapsed;
            if (holder.Seconds != null)
                holder.Seconds.Update(double.NaN, seconds);

            // Monitor "Now" cell: the live value + its dot (green on screen, grey otherwise),
            // always visible — an unreadable value reads "—".
            if (holder.Now != null)
            {
                string now = string.IsNullOrEmpty(row?.NowText) ? "—" : row.NowText;
                holder.Now.Text = now;
                holder.Now.Foreground = onScreen ? DisplayPalette.GreenAccent : DisplayPalette.ChipText;
            }
            if (holder.NowDot != null)
                holder.NowDot.Background = onScreen ? DisplayPalette.GreenDot : DisplayPalette.NowDotIdle;

            if (Mode == TriggerTableMode.Monitor && !holder.Degraded)
                ApplyRowAccent(holder, onScreen);
            else if (holder.Container != null && !holder.Degraded)
                // Workbench: the selection (blue bar/bg) owns the row chrome; only the muted
                // dimming tracks live state here.
                holder.Container.Opacity = muted ? 0.5 : 1.0;
        }

        // Monitor-only loud winning accent — green row bg, green left bar, green rank and
        // label. Reserved for 2c; not used in the Workbench.
        private static void ApplyRowAccent(RowChips holder, bool onScreen)
        {
            if (holder.Rank != null)
                holder.Rank.Foreground = onScreen ? DisplayPalette.GreenRank : DisplayPalette.MutedRank;
            if (holder.Label != null)
                holder.Label.Foreground = onScreen ? DisplayPalette.OnScreenText : DisplayPalette.RowText;
            if (holder.Container != null)
            {
                holder.Container.Background = onScreen ? DisplayPalette.OnScreenBg : DisplayPalette.RowBg;
                holder.Container.BorderBrush = onScreen ? DisplayPalette.OnScreenBorder : DisplayPalette.TableDivider;
                holder.Container.BorderThickness = onScreen ? new Thickness(3, 1, 0, 0) : new Thickness(0, 1, 0, 0);
            }
        }

        // ── The dense grid ────────────────────────────────────────────────

        // The 7 columns of the v9 dense grid: [handle+rank] When · Show · Timeout · Runs ·
        // State · ⋯. Star weights mirror the mock (2.4 / 1.5 / .9 / 1.05 / .95); the two
        // fixed columns hold the handle/rank cluster and the overflow trigger.
        private static void AddDenseColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        }

        private UIElement BuildHeaderStrip()
        {
            var grid = new Grid { Background = DisplayPalette.ThHeaderBg };
            AddDenseColumns(grid);
            AddHeaderLabel(grid, 1, "When");
            AddHeaderLabel(grid, 2, "Show");
            AddHeaderLabel(grid, 3, "Timeout");
            AddHeaderLabel(grid, 4, "Runs");
            AddHeaderLabel(grid, 5, "State");
            return grid;
        }

        private static void AddHeaderLabel(Grid grid, int col, string text)
        {
            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.ThLabel,
                Margin = new Thickness(11, 9, 11, 9),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        // One editable rule row: the clickable dense header, plus the expansion drawer when
        // this is the selected row.
        private UIElement BuildRuleRow(TriggerTableRow row)
        {
            bool expanded = string.Equals(row.RuleId, ExpandedRuleId, StringComparison.Ordinal);

            var container = new StackPanel();

            var header = BuildRowHeader(row, expanded, out var chips);
            container.Children.Add(header);
            _rowChips[row.RuleId] = chips;
            // Only reorderable rows join the drag/keyboard geometry — the uncommitted draft
            // row (Draggable=false) is excluded, so its transient presence never shifts the
            // committed rules' index space.
            if (row.Draggable)
                _ruleRowOrder.Add(new KeyValuePair<string, FrameworkElement>(row.RuleId, container));

            if (expanded)
            {
                var detail = ExpansionContent?.Invoke(row.RuleId);
                if (detail != null)
                    container.Children.Add(detail);
            }

            return container;
        }

        private Border BuildRowHeader(TriggerTableRow row, bool expanded, out RowChips chips)
        {
            var grid = new Grid();
            AddDenseColumns(grid);

            // col0 — handle + rank.
            var lead = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 0, 0, 0),
            };
            if (row.Draggable)
            {
                var handle = new TextBlock
                {
                    Text = "⠿",
                    FontSize = 12,
                    Foreground = DisplayPalette.HandColor,
                    Cursor = Cursors.SizeAll,
                    ToolTip = "Drag to reorder priority",
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AttachDragHandle(handle, row.RuleId);
                lead.Children.Add(handle);
            }
            var rank = new TextBlock
            {
                Text = row.Rank,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.MutedRank,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lead.Children.Add(rank);
            Grid.SetColumn(lead, 0);
            grid.Children.Add(lead);

            // col1 — When: caret + structured property (or plain label) + operator/value span.
            var when = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 8, 6, 8),
            };
            when.Children.Add(new TextBlock
            {
                Text = expanded ? "▾" : "▸",
                FontSize = 10,
                Foreground = DisplayPalette.Caret,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            TextBlock label;
            if (!string.IsNullOrEmpty(row.PropertyName))
            {
                when.Children.Add(PropertyLabel.ForProperty(
                    row.PropertyName, row.DisplayKind, RowPropertyBudget));
                label = new TextBlock
                {
                    Text = WhenRest(row),
                    FontSize = 12,
                    Foreground = DisplayPalette.BaseText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                };
            }
            else
            {
                label = new TextBlock
                {
                    Text = row.Label,
                    FontSize = 12,
                    Foreground = DisplayPalette.RowText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            when.Children.Add(label);
            Grid.SetColumn(when, 1);
            grid.Children.Add(when);

            // col2 — Show.
            var show = new TextBlock
            {
                Text = row.ShowText,
                FontSize = 12,
                Foreground = DisplayPalette.TargetText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 8, 6, 8),
            };
            Grid.SetColumn(show, 2);
            grid.Children.Add(show);

            // col3 — Timeout.
            var timeout = new TextBlock
            {
                Text = row.Timeout,
                FontSize = 11.5,
                Foreground = DisplayPalette.TargetText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 8, 6, 8),
            };
            Grid.SetColumn(timeout, 3);
            grid.Children.Add(timeout);

            // col4 — Runs.
            var runs = new TextBlock
            {
                Text = (row.RunGlyph + " " + row.RunLabel).Trim(),
                FontSize = 11.5,
                Foreground = DisplayPalette.TargetText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 8, 6, 8),
            };
            Grid.SetColumn(runs, 4);
            grid.Children.Add(runs);

            // col5 — State: green dot (on screen) + text + countdown ring.
            var statePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 8, 6, 8),
            };
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = DisplayPalette.GreenDot,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            statePanel.Children.Add(dot);
            var stateText = new TextBlock
            {
                FontSize = 10.5,
                Foreground = DisplayPalette.ChipText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            statePanel.Children.Add(stateText);
            var seconds = new CountdownRing
            {
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            statePanel.Children.Add(seconds);
            Grid.SetColumn(statePanel, 5);
            grid.Children.Add(statePanel);

            // col6 — overflow ⋯.
            var menu = BuildRowContextMenu(row.RuleId);
            var overflow = new TextBlock
            {
                Text = "⋯",
                FontSize = 16,
                Foreground = DisplayPalette.ChipText,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = true,
                ToolTip = "More…",
            };
            overflow.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenMenu(overflow, menu); };
            overflow.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    OpenMenu(overflow, menu);
                    e.Handled = true;
                }
            };
            Grid.SetColumn(overflow, 6);
            grid.Children.Add(overflow);

            var border = new Border
            {
                Background = expanded ? DisplayPalette.DrawerBg : DisplayPalette.RowBg,
                BorderBrush = expanded ? DisplayPalette.DrawerBar : DisplayPalette.TableDivider,
                BorderThickness = expanded ? new Thickness(3, 1, 0, 0) : new Thickness(0, 1, 0, 0),
                Cursor = Cursors.Hand,
                Focusable = true,
                Child = grid,
            };
            string ruleId = row.RuleId;
            border.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragging) return;   // a drag ended on the handle, not a click
                RowActivated?.Invoke(ruleId);
            };
            border.KeyDown += (s, e) => RowKeyDown(ruleId, e);
            border.ContextMenu = menu;
            CloseMenuWhenAnchorLeaves(border, menu);

            chips = new RowChips
            {
                Container = border,
                Chip = stateText,
                Dot = dot,
                Seconds = seconds,
                Rank = rank,
                Label = label,
            };
            ApplyRowLive(chips, row);
            return border;
        }

        // A degraded rule (loaded from a newer version): muted, non-expandable, still
        // reorderable and removable, with a "created by a newer version" hint.
        private UIElement BuildDegradedRow(TriggerTableRow row)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            var lead = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 0, 0, 0),
            };
            var handle = new TextBlock
            {
                Text = "⠿",
                FontSize = 12,
                Foreground = DisplayPalette.HandColor,
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to reorder priority",
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AttachDragHandle(handle, row.RuleId);
            lead.Children.Add(handle);
            lead.Children.Add(new TextBlock
            {
                Text = row.Rank,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.MutedRank,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(lead, 0);
            grid.Children.Add(lead);

            var text = new StackPanel { Margin = new Thickness(11, 8, 6, 8) };
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
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var menu = BuildRowContextMenu(row.RuleId);
            var border = new Border
            {
                Background = DisplayPalette.RowBg,
                BorderBrush = DisplayPalette.TableDivider,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Opacity = 0.5,
                Focusable = true,
                Child = grid,
            };
            string ruleId = row.RuleId;
            border.KeyDown += (s, e) => RowKeyDown(ruleId, e);
            border.ContextMenu = menu;
            CloseMenuWhenAnchorLeaves(border, menu);

            _rowChips[row.RuleId] = new RowChips { Container = border, Degraded = true };
            _ruleRowOrder.Add(new KeyValuePair<string, FrameworkElement>(row.RuleId, border));
            return border;
        }

        // The pinned "★ Always → <base>" row (Monitor only in 2c — the Workbench renders the
        // base as a footer instead): dashed, last, not draggable, not expandable.
        private UIElement BuildBaseRow(TriggerTableRow row)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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

            var host = new Grid { Margin = new Thickness(0, 6, 0, 0) };
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

        // ── Monitor mode (the Overview's read-only "what's in play" list) ─────
        //    Columns: rank · When · Now · Show · State. No drag handle, no ⋯, no
        //    expansion; the whole row activates (→ Triggers). Winning rows carry the
        //    loud green emphasis (bg + 3px bar + green rank); grammar colours stay fixed.

        private const double MonRankWidth = 44;

        private static void AddMonitorColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MonRankWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.05, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.92, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        }

        private UIElement BuildMonitorHeaderStrip()
        {
            var grid = new Grid { Background = DisplayPalette.ThHeaderBg };
            AddMonitorColumns(grid);
            AddHeaderLabel(grid, 1, "When");
            AddHeaderLabel(grid, 2, "Now");
            AddHeaderLabel(grid, 3, "Show");
            AddHeaderLabel(grid, 4, "State");
            return grid;
        }

        // One Monitor rule row: rank · When (grammar or label) · Now (dot + live value) ·
        // Show (→ target) · State (state text + countdown ring). Clicking anywhere activates it.
        private UIElement BuildMonitorRow(TriggerTableRow row)
        {
            var grid = new Grid();
            AddMonitorColumns(grid);

            // col0 — rank.
            var rank = new TextBlock
            {
                Text = row.Rank,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.MutedRank,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 0);
            grid.Children.Add(rank);

            // col1 — When: structured grammar (dim ns / bright leaf, fixed) + operator/value.
            var when = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            TextBlock label;
            if (!string.IsNullOrEmpty(row.PropertyName))
            {
                when.Children.Add(PropertyLabel.ForProperty(
                    row.PropertyName, row.DisplayKind, RowPropertyBudget));
                label = new TextBlock
                {
                    Text = WhenRest(row),
                    FontSize = 12,
                    Foreground = DisplayPalette.BaseText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                };
            }
            else
            {
                label = new TextBlock
                {
                    Text = row.Label,
                    FontSize = 12,
                    Foreground = DisplayPalette.RowText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            when.Children.Add(label);
            Grid.SetColumn(when, 1);
            grid.Children.Add(when);

            // col2 — Now: dot + live value.
            var nowCell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            var nowDot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            nowCell.Children.Add(nowDot);
            var nowText = new TextBlock
            {
                FontFamily = DisplayPalette.Mono,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nowCell.Children.Add(nowText);
            Grid.SetColumn(nowCell, 2);
            grid.Children.Add(nowCell);

            // col3 — Show ("→ Page N · Name").
            var show = new TextBlock
            {
                Text = string.IsNullOrEmpty(row.ShowText) ? "" : "→ " + row.ShowText,
                FontSize = 12,
                Foreground = DisplayPalette.TargetText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            Grid.SetColumn(show, 3);
            grid.Children.Add(show);

            // col4 — State: state text + countdown ring.
            var statePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            var stateText = new TextBlock
            {
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = DisplayPalette.ChipText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            statePanel.Children.Add(stateText);
            var seconds = new CountdownRing
            {
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            statePanel.Children.Add(seconds);
            Grid.SetColumn(statePanel, 4);
            grid.Children.Add(statePanel);

            var border = new Border
            {
                BorderBrush = DisplayPalette.TableDivider,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Cursor = Cursors.Hand,
                Focusable = true,
                Child = grid,
            };
            string ruleId = row.RuleId;
            border.MouseLeftButtonUp += (s, e) => RowActivated?.Invoke(ruleId);
            border.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    RowActivated?.Invoke(ruleId);
                    e.Handled = true;
                }
            };

            var chips = new RowChips
            {
                Container = border,
                Chip = stateText,
                Seconds = seconds,
                Rank = rank,
                Label = label,
                Now = nowText,
                NowDot = nowDot,
            };
            if (row.RuleId != null)
                _rowChips[row.RuleId] = chips;
            ApplyRowLive(chips, row);
            return border;
        }

        // The Monitor base footer row (mock): ★ · "When nothing's firing" · (blank Now) ·
        // "→ <base page>" · "resting". Inside the table, a heavier top divider sets it apart.
        private UIElement BuildMonitorBaseRow(TriggerTableRow row)
        {
            var grid = new Grid { Background = DisplayPalette.ThHeaderBg };
            AddMonitorColumns(grid);

            var star = new TextBlock
            {
                Text = "★",
                FontSize = 13,
                Foreground = DisplayPalette.BaseRank,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(star, 0);
            grid.Children.Add(star);

            var when = new TextBlock
            {
                Text = "When nothing's firing",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = DisplayPalette.BaseText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            Grid.SetColumn(when, 1);
            grid.Children.Add(when);

            var show = new TextBlock
            {
                Text = string.IsNullOrEmpty(row.ShowText) ? "" : "→ " + row.ShowText,
                FontSize = 12,
                Foreground = DisplayPalette.GreenAccent,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            Grid.SetColumn(show, 3);
            grid.Children.Add(show);

            var state = new TextBlock
            {
                Text = "resting",
                FontSize = 10.5,
                Foreground = DisplayPalette.ChipText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 9, 6, 9),
            };
            Grid.SetColumn(state, 4);
            grid.Children.Add(state);

            return new Border
            {
                BorderBrush = DisplayPalette.MutedRank,
                BorderThickness = new Thickness(0, 2, 0, 0),
                Child = grid,
            };
        }

        // ── Reorder: overflow / context menu ──────────────────────────────

        // The v9 overflow menu (⋯ and right-click): Duplicate · Move to top · (divider) ·
        // Delete. Delete is immediate — no confirm (kelchm call).
        private ContextMenu BuildRowContextMenu(string ruleId)
        {
            var menu = new ContextMenu();
            var dup = new MenuItem { Header = "⧉  Duplicate" };
            dup.Click += (s, e) => RowAction?.Invoke(ruleId, "duplicate");
            menu.Items.Add(dup);
            var top = new MenuItem { Header = "↑  Move to top" };
            top.Click += (s, e) => RowMoved?.Invoke(ruleId, 0);
            menu.Items.Add(top);
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "Delete", Foreground = DisplayPalette.DeleteText };
            del.Click += (s, e) => RowAction?.Invoke(ruleId, "remove");
            menu.Items.Add(del);
            return menu;
        }

        private static void OpenMenu(UIElement anchor, ContextMenu menu)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Issue-#37 popup lifetime: the row ContextMenu (opened by ⋯ or right-click) lives in
        // its own popup HWND with the row border as PlacementTarget. A poll rebuild
        // (SetRows → Children.Clear) can unparent that border while the menu is open, orphaning
        // a detached popup. Close it when the anchor row leaves the visual tree — the same
        // Unloaded/IsVisibleChanged guard the Phase 1 DropDownCell uses.
        private static void CloseMenuWhenAnchorLeaves(FrameworkElement anchor, ContextMenu menu)
        {
            anchor.Unloaded += (s, e) => menu.IsOpen = false;
            anchor.IsVisibleChanged += (s, e) =>
            {
                if (!anchor.IsVisible)
                    menu.IsOpen = false;
            };
        }

        // Enter / Space activate (open/close) a focused row's editor — the keyboard
        // equivalent of clicking the header; Alt+Up / Alt+Down reorder it (accessibility +
        // drag fallback).
        private void RowKeyDown(string ruleId, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                RowActivated?.Invoke(ruleId);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                return;
            if (e.Key == Key.Up || e.SystemKey == Key.Up)
            {
                RaiseMove(ruleId, -1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.SystemKey == Key.Down)
            {
                RaiseMove(ruleId, +1);
                e.Handled = true;
            }
        }

        // A relative move (Alt+arrow): resolve the row's current index among the rule rows
        // and surface the desired index. The host clamps and no-ops as needed.
        private void RaiseMove(string ruleId, int delta)
        {
            int from = IndexInOrder(ruleId);
            if (from < 0)
                return;
            RowMoved?.Invoke(ruleId, from + delta);
        }

        private int IndexInOrder(string ruleId)
        {
            for (int i = 0; i < _ruleRowOrder.Count; i++)
                if (string.Equals(_ruleRowOrder[i].Key, ruleId, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        // ── Reorder: mouse drag on the ⠿ handle ───────────────────────────

        private void AttachDragHandle(UIElement handle, string ruleId)
        {
            handle.MouseLeftButtonDown += (s, e) =>
            {
                _dragRuleId = ruleId;
                _dragStart = e.GetPosition(this);
                _dragging = false;
                _dragHandle = handle;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseMove += HandleDragMove;
            handle.MouseLeftButtonUp += HandleDragUp;
            // The OS can revoke mouse capture mid-drag with NO MouseLeftButtonUp — Alt+Tab,
            // window deactivation, or a system popup/tooltip all fire LostMouseCapture instead.
            // Without this, HandleDragUp never runs and the drag state (hence IsDragging) stays
            // stuck true forever: row clicks deaden ("if (_dragging) return;") and the host's
            // Poll freezes in the chip-patch-only branch, never rebuilding. Treat a lost capture
            // as a drag abort so the table always recovers.
            handle.LostMouseCapture += HandleDragLostCapture;
        }

        // Abort any in-flight drag: release the handle capture, clear the drag fields, and drop
        // the drop indicator. Shared by SetRows (a rebuild would unparent the captured handle)
        // and the LostMouseCapture abort path. Nulls _dragHandle BEFORE releasing capture so the
        // re-entrant LostMouseCapture this raises sees cleared state and no-ops.
        private void AbortDrag()
        {
            var handle = _dragHandle;
            _dragHandle = null;
            _dragging = false;
            _dragRuleId = null;
            handle?.ReleaseMouseCapture();
            RemoveDropIndicator();
        }

        private void HandleDragLostCapture(object sender, MouseEventArgs e)
        {
            // A normal drop (HandleDragUp) and SetRows both null _dragHandle before releasing
            // capture, so the capture loss they cause no-ops here; only an OS-revoked capture
            // (handle still held) reaches AbortDrag.
            if (_dragHandle == null)
                return;
            AbortDrag();
        }

        private void HandleDragMove(object sender, MouseEventArgs e)
        {
            if (_dragHandle == null || e.LeftButton != MouseButtonState.Pressed)
                return;
            var pos = e.GetPosition(this);
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
            double y = e.GetPosition(this).Y;
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
                var t = el.TransformToAncestor(this);
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
            // Map the rule slot to a real child index by locating the target rule's own
            // container — robust to whatever non-rule children (header strip, draft row) lead
            // or interleave the stack. Insert just above the row at `slot`, or after the last
            // rule row at the end.
            int at;
            if (_ruleRowOrder.Count == 0)
                at = Children.Count;
            else if (slot < _ruleRowOrder.Count)
                at = Children.IndexOf((UIElement)_ruleRowOrder[slot].Value);
            else
                at = Children.IndexOf((UIElement)_ruleRowOrder[_ruleRowOrder.Count - 1].Value) + 1;
            if (at < 0)
                at = Children.Count;
            at = Math.Min(at, Children.Count);
            Children.Insert(at, _dropIndicator);
        }

        private void RemoveDropIndicator()
        {
            if (_dropIndicator != null)
            {
                Children.Remove(_dropIndicator);
                _dropIndicator = null;
            }
        }

        private void CommitDrag(string ruleId, int slot)
        {
            int from = IndexInOrder(ruleId);
            if (from < 0)
                return;
            int desired = slot <= from ? slot : slot - 1;
            RowMoved?.Invoke(ruleId, desired);
        }

        // ── Small helpers ─────────────────────────────────────────────────

        // The When cell's plain remainder after the property: "> 10" (operator + value). The
        // target lives in its own Show column now, so it is not appended here.
        private static string WhenRest(TriggerTableRow row)
        {
            string s = row.Operator ?? "";
            if (!string.IsNullOrEmpty(row.ValueText))
                s = s.Length > 0 ? s + " " + row.ValueText : row.ValueText;
            return s;
        }
    }
}
