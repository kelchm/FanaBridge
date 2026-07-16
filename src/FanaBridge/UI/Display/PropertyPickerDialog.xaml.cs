using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FanaBridge.Customization;

namespace FanaBridge.UI
{
    /// <summary>
    /// The SimHub property picker modal (design's picker): a focus-on-open filter box over a
    /// grouped, virtualized list — the curated FanaBridge built-ins first, then every SimHub
    /// property name grouped by its first dotted segment. Up/Down move the selection (from
    /// the filter box, so typing and navigating never fight), Enter / double-click / OK
    /// commit, Escape / Cancel abandon. All grouping and filtering lives in the SimHub-free
    /// <see cref="DisplayPropertyPickerModel"/>; this is only the view.
    /// </summary>
    public partial class PropertyPickerDialog : Window
    {
        private readonly DisplayPropertyPickerModel _model;
        private readonly ObservableCollection<PickerRow> _rows = new ObservableCollection<PickerRow>();
        private readonly string _current;

        private string _resultName;
        private PropertyKind _resultKind;

        private PropertyPickerDialog(DisplayPropertyPickerModel model, string current)
        {
            InitializeComponent();
            _model = model;
            _current = current;
            lstProps.ItemsSource = _rows;
            Loaded += OnLoaded;
        }

        /// <summary>
        /// Opens the picker over <paramref name="owner"/> and, on OK, returns the chosen
        /// property name and the kind to stamp on the spec (built-in vs SimHub property).
        /// Returns false when the user cancels.
        /// </summary>
        internal static bool TryPick(Window owner, IReadOnlyList<string> builtIns,
            IReadOnlyList<string> allProperties, string current,
            out string name, out PropertyKind kind)
        {
            var model = new DisplayPropertyPickerModel(builtIns, allProperties);
            var dialog = new PropertyPickerDialog(model, current) { Owner = owner };
            bool ok = dialog.ShowDialog() == true;
            name = dialog._resultName;
            kind = dialog._resultKind;
            return ok;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Rebuild(SelectCurrent: true);
            txtFilter.Focus();
        }

        private void Filter_TextChanged(object sender, TextChangedEventArgs e)
            => Rebuild(SelectCurrent: false);

        // The list follows the filter; from the filter box Up/Down walk the selection and
        // Enter commits, so the user never has to leave the box to navigate.
        private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(+1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    CommitSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Cancel();
                    e.Handled = true;
                    break;
            }
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => CommitSelected();

        private void Ok_Click(object sender, RoutedEventArgs e) => CommitSelected();

        private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

        // ── Rebuild + selection ───────────────────────────────────────────

        private void Rebuild(bool SelectCurrent)
        {
            var rows = _model.Rows(txtFilter.Text);
            _rows.Clear();
            foreach (var row in rows)
                _rows.Add(row);

            int selectable = 0;
            foreach (var row in _rows)
                if (row.IsProperty)
                    selectable++;
            txtCount.Text = selectable == 0
                ? "no matches"
                : selectable + (selectable == 1 ? " property" : " properties");
            btnOk.IsEnabled = selectable > 0;

            int target = -1;
            if (SelectCurrent && !string.IsNullOrEmpty(_current))
                target = IndexOfName(_current);
            if (target < 0)
                target = FirstProperty();
            Select(target, scroll: true);
        }

        private int IndexOfName(string name)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].IsProperty &&
                    string.Equals(_rows[i].PropertyName, name, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private int FirstProperty()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].IsProperty)
                    return i;
            return -1;
        }

        // Step the selection to the next/previous selectable (property) row, skipping the
        // non-focusable group headers.
        private void MoveSelection(int direction)
        {
            int start = lstProps.SelectedIndex;
            int i = start;
            while (true)
            {
                i += direction;
                if (i < 0 || i >= _rows.Count)
                    return;                    // clamp at the ends
                if (_rows[i].IsProperty)
                {
                    Select(i, scroll: true);
                    return;
                }
            }
        }

        private void Select(int index, bool scroll)
        {
            lstProps.SelectedIndex = index;
            if (index >= 0 && scroll)
                lstProps.ScrollIntoView(_rows[index]);
        }

        private void CommitSelected()
        {
            if (!(lstProps.SelectedItem is PickerRow row) || !row.IsProperty)
                return;
            _resultName = row.PropertyName;
            _resultKind = row.PropertyKind;
            DialogResult = true;
            Close();
        }

        private void Cancel()
        {
            DialogResult = false;
            Close();
        }
    }
}
