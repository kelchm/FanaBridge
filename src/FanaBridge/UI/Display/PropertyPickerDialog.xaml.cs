using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The SimHub property picker modal (design's "Pick a property"): a focus-on-open
    /// filter box, a left rail of browse scopes (Favorites / Recent / On your ITM pages /
    /// All properties / auto roots), and a virtualized results list with match highlighting,
    /// hover ★ favorites, and a live-values column (capped). Up/Down move the selection
    /// (from the filter box), Enter / double-click / Select commit, Escape / Cancel abandon.
    /// All grouping and filtering lives in the SimHub-free
    /// <see cref="DisplayPropertyPickerModel"/>; this is only the view. Rail is mouse-only
    /// this phase.
    /// </summary>
    public partial class PropertyPickerDialog : Window
    {
        /// <summary>Live values resolve only when the current result list has at most this
        /// many property rows (broad search shows no values until narrowed).</summary>
        internal const int LiveValueCap = 300;

        private DisplayPropertyPickerModel _model;
        private readonly IReadOnlyList<string> _builtIns;
        private readonly IReadOnlyList<string> _allProperties;
        private readonly IReadOnlyList<string> _mappedRoles;
        private readonly IReadOnlyList<string> _itmPageProperties;
        private readonly IDisplayPickerStore _store;
        private readonly Func<string, object> _valueReader;
        private readonly string _current;

        // The current filtered rows, reassigned whole each rebuild and handed to the list as
        // ONE ItemsSource swap — one reset instead of a Clear() + thousands of per-item Add()
        // notifications on a multi-thousand-property catalog.
        private IReadOnlyList<PickerRow> _rows = Array.Empty<PickerRow>();
        private IReadOnlyList<PickerRail> _rails = Array.Empty<PickerRail>();
        private PickerScope _scope;
        // Coalesces rapid typing: a keystroke restarts this; only the pause rebuilds.
        private readonly DispatcherTimer _filterDebounce;
        // Re-resolves visible live values while the dialog is open (decision 4).
        private readonly DispatcherTimer _valueTimer;
        private bool _suppressFilter;
        private bool _suppressRail;

        private string _resultName;
        private PropertyKind _resultKind;

        private PropertyPickerDialog(
            IReadOnlyList<string> builtIns,
            IReadOnlyList<string> allProperties,
            IReadOnlyList<string> mappedRoles,
            IReadOnlyList<string> itmPageProperties,
            IDisplayPickerStore store,
            Func<string, object> valueReader,
            string current)
        {
            InitializeComponent();
            _builtIns = builtIns;
            _allProperties = allProperties;
            _mappedRoles = mappedRoles;
            _itmPageProperties = itmPageProperties;
            _store = store;
            _valueReader = valueReader;
            _current = current;

            _model = BuildModel();
            _scope = _model.DefaultScope();
            _rails = _model.Rails();
            lstRails.ItemsSource = _rails;
            lstProps.ItemsSource = _rows;

            _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _filterDebounce.Tick += (s, e) => { _filterDebounce.Stop(); Rebuild(selectCurrent: false); };

            _valueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _valueTimer.Tick += (s, e) => RefreshLiveValues();

            Loaded += OnLoaded;
        }

        /// <summary>
        /// Opens the picker over <paramref name="owner"/> and, on OK, returns the chosen
        /// property name and the kind to stamp on the spec (built-in vs SimHub property).
        /// Returns false when the user cancels. <paramref name="store"/> and
        /// <paramref name="valueReader"/> may be null (empty rails / no live values).
        /// </summary>
        internal static bool TryPick(
            Window owner,
            IReadOnlyList<string> builtIns,
            IReadOnlyList<string> allProperties,
            IReadOnlyList<string> mappedRoles,
            string current,
            IDisplayPickerStore store,
            IReadOnlyList<string> itmPageProperties,
            Func<string, object> valueReader,
            out string name,
            out PropertyKind kind)
        {
            var dialog = new PropertyPickerDialog(
                builtIns, allProperties, mappedRoles, itmPageProperties,
                store, valueReader, current)
            {
                Owner = owner,
            };
            bool ok = dialog.ShowDialog() == true;
            name = dialog._resultName;
            kind = dialog._resultKind;
            return ok;
        }

        private DisplayPropertyPickerModel BuildModel()
            => new DisplayPropertyPickerModel(
                _builtIns, _allProperties, _mappedRoles,
                _store?.Favorites, _store?.Recents, _itmPageProperties);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SelectRailForScope(_scope);
            Rebuild(selectCurrent: true);
            _valueTimer.Start();
            txtFilter.Focus();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _filterDebounce.Stop();
            _valueTimer.Stop();
        }

        // Debounced: restart the timer; the pause (or an explicit navigate/commit, which
        // flushes first) does the rebuild — so typing never pays a full re-scan per keystroke.
        private void Filter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressFilter)
                return;
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }

        // Apply a pending filter immediately, so keyboard navigation and commit act on the
        // rows the user is actually looking at (not the pre-keystroke list).
        private void FlushPendingFilter()
        {
            if (_filterDebounce.IsEnabled)
            {
                _filterDebounce.Stop();
                Rebuild(selectCurrent: false);
            }
        }

        // The list follows the filter; from the filter box Up/Down walk the selection and
        // Enter commits, so the user never has to leave the box to navigate.
        private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    FlushPendingFilter();
                    MoveSelection(+1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    FlushPendingFilter();
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    FlushPendingFilter();
                    CommitSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Cancel();
                    e.Handled = true;
                    break;
            }
        }

        // Rail is mouse-only this phase. Clicking a rail clears the search box and shows
        // that rail's content (decision 1).
        private void Rail_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRail)
                return;
            if (!(lstRails.SelectedItem is PickerRail rail) || rail.IsSection)
                return;

            _scope = rail.Scope;
            if (!string.IsNullOrEmpty(txtFilter.Text))
            {
                _suppressFilter = true;
                txtFilter.Text = "";
                _suppressFilter = false;
                _filterDebounce.Stop();
            }
            Rebuild(selectCurrent: false);
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => CommitSelected();

        private void Ok_Click(object sender, RoutedEventArgs e) => CommitSelected();

        private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

        // ☆/★ zone: toggle favorite without committing the row; selection preserved.
        private void Favorite_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!(sender is FrameworkElement el) || !(el.DataContext is PickerRow row) || !row.IsProperty)
                return;
            if (_store == null || string.IsNullOrEmpty(row.PropertyName))
                return;

            bool next = !row.IsFavorite;
            _store.SetFavorite(row.PropertyName, next);
            string keep = row.PropertyName;
            int keepIndex = lstProps.SelectedIndex;
            _model = BuildModel();
            // Rails don't change shape on favorite toggle; only favorite flags / Favorites rail content.
            Rebuild(selectCurrent: false, preferName: keep, preferIndex: keepIndex);
        }

        // ── Rebuild + selection ───────────────────────────────────────────

        private void Rebuild(bool selectCurrent, string preferName = null, int preferIndex = -1)
        {
            string filter = txtFilter.Text;
            bool searching = !string.IsNullOrWhiteSpace(filter);

            // Non-empty search is global; rail highlight clears (decision 1).
            if (searching)
            {
                _suppressRail = true;
                lstRails.SelectedIndex = -1;
                _suppressRail = false;
            }
            else
            {
                SelectRailForScope(_scope);
            }

            _rows = _model.Rows(_scope, filter);
            ApplyLiveValues(_rows);
            lstProps.ItemsSource = _rows;

            int selectable = 0;
            foreach (var row in _rows)
                if (row.IsProperty)
                    selectable++;

            txtCount.Text = HeaderCountText(selectable, filter, searching);
            btnOk.IsEnabled = selectable > 0;

            int target = -1;
            if (selectCurrent && !string.IsNullOrEmpty(_current))
                target = IndexOfName(_current);
            if (target < 0 && !string.IsNullOrEmpty(preferName))
                target = IndexOfName(preferName);
            if (target < 0 && preferIndex >= 0 && preferIndex < _rows.Count
                && _rows[preferIndex].IsProperty)
                target = preferIndex;
            if (target < 0)
                target = FirstProperty();
            Select(target, scroll: true);
        }

        private void SelectRailForScope(PickerScope scope)
        {
            _suppressRail = true;
            int index = -1;
            for (int i = 0; i < _rails.Count; i++)
            {
                var r = _rails[i];
                if (!r.IsSection && r.Scope.Equals(scope))
                {
                    index = i;
                    break;
                }
            }
            lstRails.SelectedIndex = index;
            if (index >= 0)
                lstRails.ScrollIntoView(_rails[index]);
            _suppressRail = false;
        }

        private string HeaderCountText(int selectable, string filter, bool searching)
        {
            if (searching)
            {
                string q = filter.Trim();
                if (selectable == 0)
                    return "no match \u201c" + q + "\u201d";
                // Mock voice: "312 match “brake”" (always "match", not "matches").
                return selectable + " match \u201c" + q + "\u201d";
            }

            // Empty search: scope label (rail name) rather than a count.
            return ScopeLabel(_scope);
        }

        private string ScopeLabel(PickerScope scope)
        {
            switch (scope.Kind)
            {
                case PickerScopeKind.Favorites: return "Favorites";
                case PickerScopeKind.Recents: return "Recent";
                case PickerScopeKind.ItmPages: return "On your ITM pages";
                case PickerScopeKind.AllProperties: return "All properties";
                case PickerScopeKind.Root:
                    return string.IsNullOrEmpty(scope.RootName) ? "" : scope.RootName;
                default: return "";
            }
        }

        private void ApplyLiveValues(IReadOnlyList<PickerRow> rows)
        {
            int propertyCount = 0;
            foreach (var row in rows)
                if (row.IsProperty)
                    propertyCount++;

            bool resolve = _valueReader != null && propertyCount > 0 && propertyCount <= LiveValueCap;
            foreach (var row in rows)
            {
                if (!row.IsProperty)
                {
                    row.LiveValue = null;
                    continue;
                }
                if (!resolve)
                {
                    row.LiveValue = "";
                    continue;
                }
                row.LiveValue = FormatLiveValue(SafeRead(row.PropertyName));
            }
        }

        private void RefreshLiveValues()
        {
            if (_rows == null || _rows.Count == 0)
                return;

            int propertyCount = 0;
            foreach (var row in _rows)
                if (row.IsProperty)
                    propertyCount++;
            if (_valueReader == null || propertyCount == 0 || propertyCount > LiveValueCap)
                return;

            // LiveValue is change-notifying, so bound rows update in place — no
            // ItemsSource reset (which would throw away scroll position and selection).
            foreach (var row in _rows)
            {
                if (!row.IsProperty)
                    continue;
                row.LiveValue = FormatLiveValue(SafeRead(row.PropertyName));
            }
        }

        private object SafeRead(string name)
        {
            if (_valueReader == null || string.IsNullOrEmpty(name))
                return null;
            try
            {
                return _valueReader(name);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Compact invariant display text for a live property value: "0.###" for
        /// doubles/floats, raw invariant ToString otherwise, empty on null/error.
        /// </summary>
        internal static string FormatLiveValue(object value)
        {
            if (value == null)
                return "";
            try
            {
                if (value is double d)
                    return d.ToString("0.###", CultureInfo.InvariantCulture);
                if (value is float f)
                    return f.ToString("0.###", CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
            catch
            {
                return "";
            }
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
            if (index >= 0 && index < _rows.Count && scroll)
                lstProps.ScrollIntoView(_rows[index]);
        }

        private void CommitSelected()
        {
            if (!(lstProps.SelectedItem is PickerRow row) || !row.IsProperty)
                return;
            _resultName = row.PropertyName;
            _resultKind = row.PropertyKind;
            _store?.NoteRecent(row.PropertyName);
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
