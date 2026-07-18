using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// The shared trigger table: the priority-ordered rule rows (rank, structured/plain
    /// WHEN label, live-state chip + countdown, eligibility), the pinned base row, drag +
    /// keyboard/context-menu reorder, and the live-chip patch that never disturbs an open
    /// editor row or an in-flight drag. Extracted from the Triggers editor (Phase 2 commit
    /// 2a) so the same row machinery can serve both the Workbench editor and the Overview
    /// Monitor list; 2a wires only <see cref="TriggerTableMode.Workbench"/>, rendering
    /// exactly the shipped row look.
    ///
    /// The control is pure WPF over the <see cref="TriggerTableRow"/> projection — it holds
    /// no config, engine, or snapshot vocabulary. The host owns the edit model and commit
    /// paths and drives the control through <see cref="SetRows"/> (full rebuild) and
    /// <see cref="PatchLive"/> (in-place chip/accent/seconds patch), providing the expansion
    /// detail through <see cref="ExpansionContent"/>. Every user gesture surfaces back as an
    /// event: <see cref="RowActivated"/> (click / Enter / Space), <see cref="RowMoved"/>
    /// (drag drop, Alt+arrows, context-menu move — carrying the target index), and
    /// <see cref="RowAction"/> (context-menu action, e.g. "remove").
    /// </summary>
    internal class TriggerTableControl : StackPanel
    {
        // Generous character budget before the property grammar left-elides (the WPF
        // CharacterEllipsis is the visual backstop past this): the collapsed row shares its
        // width with the operator/value spans.
        private const int RowPropertyBudget = 34;

        // Per-row live-chip handles, keyed by rule id, so a poll can patch the state chip
        // and countdown in place (never rebuilding an open editor row).
        private sealed class RowChips
        {
            public Border Container;
            public TextBlock Chip;
            public CountdownRing Seconds;
            public TextBlock Rank;
            public TextBlock Label;
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

        /// <summary>The table's mode. 2a wires only <see cref="TriggerTableMode.Workbench"/>
        /// (the extracted editor); Monitor lands in 2c.</summary>
        public TriggerTableMode Mode { get; set; } = TriggerTableMode.Workbench;

        /// <summary>The one rule id whose editor drawer is open, or null. Set by the host
        /// before <see cref="SetRows"/>; the matching row renders expanded (rounded top,
        /// chevron down) and its <see cref="ExpansionContent"/> is appended.</summary>
        public string ExpandedRuleId { get; set; }

        /// <summary>Builds the expansion detail for the expanded row (the host owns the
        /// editor drawer). Returns null to render no drawer (e.g. a degraded/unknown row).</summary>
        public Func<string, UIElement> ExpansionContent { get; set; }

        /// <summary>Raised when a row is activated (header click, or Enter/Space) with its
        /// rule id. Degraded rows raise it too (keyboard only); the host decides it is a
        /// no-op there.</summary>
        public event Action<string> RowActivated;

        /// <summary>Raised when a reorder gesture (drag drop, Alt+Up/Down, context-menu
        /// Move up/down) targets a new position: the rule id and the desired index among the
        /// rule rows. The host translates it to its edit-model move.</summary>
        public event Action<string, int> RowMoved;

        /// <summary>Raised for a per-row action from the overflow/context menu: the rule id
        /// and an action id (e.g. "remove").</summary>
        public event Action<string, string> RowAction;

        /// <summary>True while a drag gesture holds the ⠿ handle capture. The host's poll
        /// consults this to defer any rebuild until the drop (a rebuild would unparent the
        /// captured handle and strand the gesture).</summary>
        public bool IsDragging => _dragHandle != null || _dragging;

        // ── Rendering ─────────────────────────────────────────────────────

        /// <summary>Full rebuild: clears the rows (and any in-flight drag) and rebuilds from
        /// <paramref name="rows"/> in order, appending the expansion drawer to the row whose
        /// id matches <see cref="ExpandedRuleId"/>.</summary>
        public void SetRows(IReadOnlyList<TriggerTableRow> rows)
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
            Children.Clear();
            _rowChips.Clear();
            _ruleRowOrder.Clear();

            if (rows == null)
                return;
            foreach (var row in rows)
            {
                if (row.IsBase)
                    Children.Add(BuildBaseRow(row));
                else if (row.Degraded)
                    Children.Add(BuildDegradedRow(row));
                else
                    Children.Add(BuildRuleRow(row));
            }
        }

        /// <summary>In-place chip patch: only the live-state chip, countdown, accent, and
        /// muted dimming for each existing rule row (matched by id), so an open editor row
        /// keeps its controls and focus and an in-flight drag is never disturbed. Never
        /// touches the children collection.</summary>
        public void PatchLive(IReadOnlyList<TriggerTableRow> rows)
        {
            var live = new Dictionary<string, TriggerTableRow>(StringComparer.Ordinal);
            if (rows != null)
                foreach (var r in rows)
                    if (r.RuleId != null)
                        live[r.RuleId] = r;

            foreach (var kv in _rowChips)
            {
                var holder = kv.Value;
                var chip = live.TryGetValue(kv.Key, out var state)
                    ? new RuleStateChip
                    {
                        Chip = state.Chip,
                        Seconds = state.Seconds,
                        OnScreen = state.OnScreen,
                        Muted = state.Muted,
                    }
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
                holder.Seconds.Update(double.NaN, chip.Seconds);
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

        // One editable rule row: a clickable collapsed header, plus the expansion drawer when
        // this is the open row.
        private UIElement BuildRuleRow(TriggerTableRow row)
        {
            bool expanded = string.Equals(row.RuleId, ExpandedRuleId, StringComparison.Ordinal);

            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };

            var header = BuildRowHeader(row, expanded, out var chips);
            container.Children.Add(header);
            _rowChips[row.RuleId] = chips;
            _ruleRowOrder.Add(new KeyValuePair<string, FrameworkElement>(row.RuleId, container));

            if (expanded)
            {
                var detail = ExpansionContent?.Invoke(row.RuleId);
                if (detail != null)
                    container.Children.Add(detail);
            }

            return container;
        }

        // The collapsed header: [⠿ handle] [rank] [label] [eligibility] [state chip]
        // [countdown] [chevron]. Clicking anywhere but the handle toggles the editor.
        private Border BuildRowHeader(TriggerTableRow row, bool expanded, out RowChips chips)
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

            // The label column: the v9 structured WHEN (dim-ns/bright-leaf property + plain
            // operator/value/target spans) when the model populated it, else the plain label
            // (base/degraded/user-named rows). `label` is the span the on-screen accent recolours.
            FrameworkElement labelColumn;
            TextBlock label;
            if (!string.IsNullOrEmpty(row.PropertyName))
            {
                var strip = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                strip.Children.Add(PropertyLabel.ForProperty(
                    row.PropertyName, row.DisplayKind, RowPropertyBudget));
                label = new TextBlock
                {
                    Text = RestText(row),
                    FontSize = 12.5,
                    Foreground = DisplayPalette.RowText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                };
                strip.Children.Add(label);
                labelColumn = strip;
            }
            else
            {
                label = new TextBlock
                {
                    Text = row.Label,
                    FontSize = 12.5,
                    Foreground = DisplayPalette.RowText,   // on-screen accent applied via ApplyRowAccent below
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                labelColumn = label;
            }
            Grid.SetColumn(labelColumn, 2);
            grid.Children.Add(labelColumn);

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

            var seconds = new CountdownRing
            {
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            seconds.Update(double.NaN, row.Seconds);
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
                RowActivated?.Invoke(ruleId);
            };
            border.KeyDown += (s, e) => RowKeyDown(ruleId, e);
            border.ContextMenu = BuildRowContextMenu(ruleId, canRemove: true);

            chips = new RowChips { Container = border, Chip = chip, Seconds = seconds, Rank = rank, Label = label };
            ApplyRowAccent(chips, row.OnScreen);
            return border;
        }

        // A degraded rule (loaded from a newer version): muted, non-expandable, still
        // reorderable and removable, with a "created by a newer version" hint.
        private UIElement BuildDegradedRow(TriggerTableRow row)
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
        private UIElement BuildBaseRow(TriggerTableRow row)
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

        // ── Reorder: context menu + keyboard ──────────────────────────────

        private ContextMenu BuildRowContextMenu(string ruleId, bool canRemove)
        {
            var menu = new ContextMenu();
            var up = new MenuItem { Header = "Move up" };
            up.Click += (s, e) => RaiseMove(ruleId, -1);
            menu.Items.Add(up);
            var down = new MenuItem { Header = "Move down" };
            down.Click += (s, e) => RaiseMove(ruleId, +1);
            menu.Items.Add(down);
            if (canRemove)
            {
                menu.Items.Add(new Separator());
                var remove = new MenuItem { Header = "Remove" };
                remove.Click += (s, e) => RowAction?.Invoke(ruleId, "remove");
                menu.Items.Add(remove);
            }
            return menu;
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

        // A relative move (Alt+arrow / context menu): resolve the row's current index among
        // the rule rows and surface the desired index. The host clamps and no-ops as needed.
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
            int at = Math.Max(0, Math.Min(slot, _ruleRowOrder.Count));
            // Rule rows occupy the first children; the base row follows. Insert the
            // indicator just above the row at `slot` (or above the base row at the end).
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

        // The plain remainder of a structured row after the property: "> 10 → Fuel / ERS / DRS".
        private static string RestText(TriggerTableRow row)
        {
            string s = row.Operator ?? "";
            if (!string.IsNullOrEmpty(row.ValueText))
                s = s.Length > 0 ? s + " " + row.ValueText : row.ValueText;
            if (!string.IsNullOrEmpty(row.TargetText))
                s = s.Length > 0 ? s + " → " + row.TargetText : "→ " + row.TargetText;
            return s;
        }
    }
}
