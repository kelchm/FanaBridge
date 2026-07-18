using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// An anchored dropdown cell (the design's .dd): a dark flat cell showing the selected
    /// value + a ▾ affordance that opens a <see cref="Popup"/> menu below it. Items come from a
    /// <see cref="ChoiceList"/>; a commit raises <see cref="SelectionCommitted"/> with the
    /// chosen id. Mouse-away dismiss rides <c>StaysOpen=false</c>; the keyboard closes on Esc,
    /// walks with Up/Down, and commits on Enter, always returning focus to the cell. House
    /// style — code-built, no template; the option model is the pure <see cref="ChoiceList"/>.
    ///
    /// Issue #37: the popup closes on Unloaded, so a cell that outlives its panel can never
    /// leave an orphaned popup on screen.
    /// </summary>
    public class DropDownCell : Border
    {
        private readonly TextBlock _value;
        private readonly Popup _popup;
        private readonly StackPanel _menu;
        private readonly List<Row> _rows = new List<Row>();
        private ChoiceList _choices = new ChoiceList(new Choice[0], null);
        private int _highlight = -1;

        private sealed class Row
        {
            public string Id;
            public bool Enabled;
            public Border Host;
        }

        /// <summary>Raised when the user commits an option (mouse or keyboard).</summary>
        public event EventHandler<string> SelectionCommitted;

        public DropDownCell()
        {
            Background = DisplayPalette.SegBarBg;
            BorderBrush = DisplayPalette.SegBorder;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(3);
            Cursor = Cursors.Hand;
            Focusable = true;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _value = new TextBlock
            {
                FontSize = 12,
                Foreground = DisplayPalette.RowText,
                Margin = new Thickness(10, 5, 6, 5),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(_value, 0);
            grid.Children.Add(_value);
            var arrow = new TextBlock
            {
                Text = "▾",
                FontSize = 11,
                Foreground = DisplayPalette.ChevronBrush,
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
            Child = grid;

            _menu = new StackPanel();
            _popup = new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = this,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = DisplayPalette.SegBarBg,
                    BorderBrush = DisplayPalette.SegBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(2),
                    Child = _menu,
                },
            };
            _popup.KeyDown += Popup_KeyDown;

            MouseLeftButtonUp += (s, e) => Toggle();
            KeyDown += Cell_KeyDown;
            Unloaded += (s, e) => _popup.IsOpen = false;
        }

        /// <summary>Populate the cell from a choice list and reflect its selection.</summary>
        public void SetChoices(ChoiceList choices)
        {
            _choices = choices ?? new ChoiceList(new Choice[0], null);
            _value.Text = _choices.SelectedLabelWithGlyph();
            RebuildMenu();
        }

        private void RebuildMenu()
        {
            _menu.Children.Clear();
            _rows.Clear();
            _highlight = -1;
            var items = _choices.Items;
            for (int i = 0; i < items.Count; i++)
            {
                var choice = items[i];
                bool selected = choice.Id == _choices.SelectedId;
                if (selected)
                    _highlight = i;
                var text = new TextBlock
                {
                    Text = string.IsNullOrEmpty(choice.Glyph) ? choice.Label : choice.Glyph + "  " + choice.Label,
                    FontSize = 12,
                    Foreground = choice.Enabled ? DisplayPalette.RowText : DisplayPalette.MutedRank,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var host = new Border
                {
                    Background = selected ? DisplayPalette.AccentBg : Brushes.Transparent,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(10, 5, 14, 5),
                    Cursor = choice.Enabled ? Cursors.Hand : Cursors.Arrow,
                    Child = text,
                };
                string id = choice.Id;
                bool enabled = choice.Enabled;
                if (enabled)
                {
                    host.MouseLeftButtonUp += (s, e) => { e.Handled = true; Commit(id); };
                    host.MouseEnter += (s, e) => Highlight(IndexOf(id));
                }
                _rows.Add(new Row { Id = id, Enabled = enabled, Host = host });
                _menu.Children.Add(host);
            }
        }

        private int IndexOf(string id)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Id == id)
                    return i;
            return -1;
        }

        private void Toggle()
        {
            if (_popup.IsOpen)
                _popup.IsOpen = false;
            else
                Open();
        }

        private void Open()
        {
            if (_rows.Count == 0)
                return;
            ApplyHighlight();
            _popup.IsOpen = true;
            _popup.Child.Focus();
        }

        private void Cell_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Down)
            {
                Open();
                e.Handled = true;
            }
        }

        private void Popup_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Up:
                    Step(-1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    Step(+1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_highlight >= 0 && _highlight < _rows.Count && _rows[_highlight].Enabled)
                        Commit(_rows[_highlight].Id);
                    e.Handled = true;
                    break;
            }
        }

        private void Step(int direction)
        {
            if (_rows.Count == 0)
                return;
            int i = _highlight;
            for (int n = 0; n < _rows.Count; n++)
            {
                i += direction;
                if (i < 0) i = 0;
                if (i > _rows.Count - 1) i = _rows.Count - 1;
                if (_rows[i].Enabled)
                {
                    Highlight(i);
                    return;
                }
                if (i == 0 || i == _rows.Count - 1)
                    return;   // clamped at an edge on a disabled row — stop
            }
        }

        private void Highlight(int index)
        {
            if (index < 0)
                return;
            _highlight = index;
            ApplyHighlight();
        }

        private void ApplyHighlight()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                bool selected = _rows[i].Id == _choices.SelectedId;
                bool hot = i == _highlight;
                _rows[i].Host.Background = selected
                    ? DisplayPalette.AccentBg
                    : (hot ? DisplayPalette.RowBg : Brushes.Transparent);
            }
        }

        private void Commit(string id)
        {
            _popup.IsOpen = false;
            Focus();
            var handler = SelectionCommitted;
            if (handler != null)
                handler(this, id);
        }

        private void Close()
        {
            _popup.IsOpen = false;
            Focus();
        }
    }
}
