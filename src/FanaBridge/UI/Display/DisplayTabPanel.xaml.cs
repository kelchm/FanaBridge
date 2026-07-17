using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FanaBridge.Adapters;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>
    /// The per-device Display tab: a DISPLAY MODE header (ITM display / Legacy only)
    /// and hub-and-spoke views — Overview is the landing view (current-page caption,
    /// recent activity, the read-only display priority list, and the option controls
    /// the old Screen tab carried, same settings and semantics), and the editor views
    /// (Triggers, Pages &amp; fields, Legacy screens — placeholders in this piece) are
    /// reached only through Overview's contextual links, each returning via a ‹ ghost
    /// back button. There is no persistent tab strip; the mode header shows on
    /// Overview only.
    ///
    /// Threading: everything live is read through the <see cref="IDisplayPanelHost"/>
    /// members — the snapshot accessor returns the ONE immutable envelope the device
    /// instance publishes. A DispatcherTimer polls it at 100 ms while the panel is
    /// loaded and re-renders per part on change (values part → the mirror; rule part
    /// or status line → the rows). The panel never touches engine state directly.
    /// </summary>
    public partial class DisplayTabPanel : UserControl
    {
        private enum TabView { Overview, Triggers, Pages, Legacy }

        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private DisplaySettings _settings;
        private bool _suppressEvents;
        private bool _isItm;

        // The resolved caps this panel's cap-dependent layout was last built for. The
        // poll loop compares the live host values against these and rebuilds atomically
        // when they change (a profile override applied while the tab stays open).
        private DisplayType _boundDisplayType;
        private byte _boundItmDeviceId;

        private Dictionary<TabView, UIElement> _views;
        private TabView _currentView = TabView.Overview;

        // ── Polling state ────────────────────────────────────────────────
        private DispatcherTimer _timer;
        private DisplayRuleSnapshot _lastSnapshot;
        private DisplayValuesSnapshot _lastValues;
        private string _lastStatus;

        public DisplayTabPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the panel to its device host and the two on-demand editor catalogs. Call
        /// once after construction, before the panel is displayed (the old Screen panel's
        /// contract). The catalogs are pulled only when a picker/dropdown opens — the
        /// polling/rendering path uses <paramref name="host"/> alone.
        /// </summary>
        internal void Bind(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _propertyCatalog = propertyCatalog ?? throw new ArgumentNullException(nameof(propertyCatalog));
            _roleCatalog = roleCatalog ?? throw new ArgumentNullException(nameof(roleCatalog));
            _settings = host.DisplaySettings ?? new DisplaySettings();
            _isItm = host.DisplayType == DisplayType.Itm;
            _boundDisplayType = host.DisplayType;
            _boundItmDeviceId = host.ItmDeviceId;
            _suppressEvents = true;

            _views = new Dictionary<TabView, UIElement>
            {
                { TabView.Overview, viewOverview },
                { TabView.Triggers, viewTriggers },
                { TabView.Pages,    viewPages },
                { TabView.Legacy,   viewLegacy },
            };

            // The Triggers editor is its own control now — bind it to the same host, catalogs,
            // and mutable settings, and wire its two seam events: ‹ back returns to Overview,
            // and a committed edit refreshes the Overview priority list (the immediacy the old
            // in-shell RenderPriority gave).
            viewTriggers.Bind(_host, _propertyCatalog, _roleCatalog, _settings);
            viewTriggers.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewTriggers.ConfigApplied += (s, e) => RenderPriority(_lastSnapshot);

            // Option controls — identical semantics to the old Screen tab. "None"
            // (legacy page off) is an ITM-only display-mode choice.
            cmbItemNone.Visibility = _isItm ? Visibility.Visible : Visibility.Collapsed;
            SelectByTag(cmbDisplayMode, _settings.DisplayMode ?? DisplaySettings.DefaultMode);
            chkShowLapTotal.IsChecked = _settings.ItmShowLapTotal;
            chkShowPositionTotal.IsChecked = _settings.ItmShowPositionTotal;
            // _isItm and the default-page table below are read once, at bind, from the host's
            // override-resolved caps. Per-poll consumers (mirror, labels) re-read the live host
            // values each frame; this bind-time layout is NOT re-derived if the resolved caps
            // change while the tab stays open (an override applied after a reconnect) — a known
            // limitation until the Display tab is split into per-view controls.
            PopulateDefaultPages(host.ItmDeviceId);
            SelectByPageNumber(cmbDefaultPage, _settings.ItmDefaultPage);

            // ITM wheels get the mode header (via NavigateTo — it shows on Overview
            // only), the info banner, and the ITM options; basic-display wheels get
            // only the (7-segment) Display Mode section — the same information as the
            // old panel.
            var itmOnly = _isItm ? Visibility.Visible : Visibility.Collapsed;
            borderItmInfo.Visibility = itmOnly;
            sectionItmOptions.Visibility = itmOnly;
            sectionDisplayMode.Title = _isItm ? "Legacy Display Mode" : "Display Mode";

            NavigateTo(TabView.Overview);
            UpdateModeState();

            _suppressEvents = false;
            Poll(force: true);
        }

        // ── DISPLAY MODE toggle (owns DisplaySettings.ItmEnabled) ────────

        private void ModeItm_Click(object sender, RoutedEventArgs e) => SetItmEnabled(true);

        private void ModeLegacy_Click(object sender, RoutedEventArgs e) => SetItmEnabled(false);

        private void SetItmEnabled(bool enabled)
        {
            if (_suppressEvents || _settings == null)
                return;
            if (_settings.ItmEnabled == enabled)
                return;

            _settings.ItmEnabled = enabled;
            UpdateModeState();
            _host?.NotifySettingsChanged();
            Poll(force: true);
        }

        // Mode-dependent chrome: toggle visuals, hint text, which panels exist, and the
        // old panel's grey-out of the ITM sub-options while the ITM display is off.
        private void UpdateModeState()
        {
            bool on = _settings.ItmEnabled;

            btnModeItm.Background = on ? DisplayPalette.AccentBg : Brushes.Transparent;
            txtModeItm.Foreground = on ? Brushes.White : DisplayPalette.ToggleIdleText;
            btnModeLegacy.Background = on ? Brushes.Transparent : DisplayPalette.AccentBg;
            txtModeLegacy.Foreground = on ? DisplayPalette.ToggleIdleText : Brushes.White;
            txtModeHint.Text = on
                ? "Legacy-only hides the ITM pages and shows just the 3-character display."
                : "ITM pages are off. Switch to ITM display to use them.";

            // The live Overview cards — and with them every link into an editor —
            // exist only while this wheel is actually driving an ITM display;
            // Legacy-only (and basic wheels) keep the Overview-equivalent content:
            // the options below.
            bool itmUi = _isItm && on;
            panelItmLive.Visibility = itmUi ? Visibility.Visible : Visibility.Collapsed;
            if (!itmUi)
                NavigateTo(TabView.Overview);

            panelDefaultPage.IsEnabled = on;
            panelTotals.IsEnabled = on;
        }

        // ── View navigation (hub-and-spoke, wizard-style panel visibility):
        //    Overview's contextual links go out, the editors' ‹ comes back ──

        private void ManageTriggers_Click(object sender, RoutedEventArgs e) => NavigateTo(TabView.Triggers);

        private void EditPages_Click(object sender, RoutedEventArgs e) => NavigateTo(TabView.Pages);

        private void EditLegacy_Click(object sender, RoutedEventArgs e) => NavigateTo(TabView.Legacy);

        // "Units & format" lives in the Pages & fields editor (per-field unit/format).
        private void EditUnits_Click(object sender, RoutedEventArgs e) => NavigateTo(TabView.Pages);

        private void Back_Click(object sender, RoutedEventArgs e) => NavigateTo(TabView.Overview);

        // The Overview empty-state "＋ Add trigger" button: jump to the Triggers editor and
        // open its add card straight away (the editor rebuilds on Enter first).
        private void OverviewAddTrigger_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(TabView.Triggers);
            viewTriggers.BeginAdd();
        }

        private void NavigateTo(TabView view)
        {
            _currentView = view;
            foreach (var kv in _views)
                kv.Value.Visibility = kv.Key == view ? Visibility.Visible : Visibility.Collapsed;

            // Build (or rebuild) the Triggers editor from the current config each time it
            // becomes the active view — a clean slate, snapshot-driven from there.
            if (view == TabView.Triggers && _host != null)
                viewTriggers.Enter(_lastSnapshot);

            // The DISPLAY MODE header belongs to the hub — it shows on Overview only
            // (and only on ITM wheels), never inside an editor.
            RefreshModeHeader();
        }

        // The DISPLAY MODE header (segmented ITM/Legacy toggle + divider) shows on the
        // Overview of an ITM wheel only. Its visibility depends on both _isItm and the
        // current view, so it must be re-derived whenever either changes — NavigateTo
        // covers view changes; SyncResolvedCaps covers a live basic↔ITM caps rebind that
        // doesn't renavigate.
        private void RefreshModeHeader()
        {
            var headerVisibility = _isItm && _currentView == TabView.Overview
                ? Visibility.Visible
                : Visibility.Collapsed;
            panelModeHeader.Visibility = headerVisibility;
            lineModeHeader.Visibility = headerVisibility;
        }

        // ── Option controls (settings semantics identical to the old tab) ─

        private void ItmOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null)
                return;

            _settings.ItmShowLapTotal = chkShowLapTotal.IsChecked == true;
            _settings.ItmShowPositionTotal = chkShowPositionTotal.IsChecked == true;
            _host?.NotifySettingsChanged();
        }

        private void CmbDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null)
                return;

            if (cmbDisplayMode.SelectedItem is ComboBoxItem selected)
            {
                _settings.DisplayMode = (string)selected.Tag;
                _host?.NotifySettingsChanged();
            }
        }

        private void CmbDefaultPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null)
                return;

            if (cmbDefaultPage.SelectedItem is ComboBoxItem selected && selected.Tag is byte pageNumber)
            {
                _settings.ItmDefaultPage = pageNumber;
                _host?.NotifySettingsChanged();
                // The base ("Always") row: with no rule stack live the ITM driver reads
                // this setting each frame, so refresh the row to the new page now. While
                // a stack is live the snapshot carries the stack's OWN base page — the
                // engine captures this setting at build time, so the row deliberately
                // keeps showing the engine's actual base until the next rebuild rather
                // than a page the engine isn't using yet.
                RenderPriority(_lastSnapshot);
            }
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if ((string)item.Tag == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        // Populate the default-page choices for the given display device — name shown,
        // wire page number stored. Includes the legacy page (some setups start there).
        private void PopulateDefaultPages(byte itmDeviceId)
        {
            cmbDefaultPage.Items.Clear();
            foreach (var page in ItmDeviceCatalog.PagesFor(itmDeviceId))
                cmbDefaultPage.Items.Add(new ComboBoxItem { Content = page.Name, Tag = page.Number });
        }

        private void SelectByPageNumber(ComboBox combo, byte pageNumber)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag is byte n && n == pageNumber)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            // Stored page isn't offered by this device — fall back to the first page AND
            // correct the backing setting, so it can't persist a page this device doesn't
            // have. (SelectionChanged is suppressed during Bind, so sync directly.)
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
                if (_settings != null && ((ComboBoxItem)combo.Items[0]).Tag is byte first)
                    _settings.ItmDefaultPage = first;
            }
        }

        // ── Polling (the volatile-snapshot read path) ────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_timer == null)
            {
                // 100ms: the mirror should feel live (the hardware repaints values at
                // 40ms cadence). Cheap by construction — each tick is one volatile read
                // plus reference compares; parts re-render only when their snapshot
                // actually changed, so an idle tab does no layout work at any rate.
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += (s, a) => Poll();
            }
            _timer.Start();
            Poll(force: true);   // the tab may reload with stale content
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer?.Stop();
        }

        // Re-derive the cap-dependent layout when the host's resolved caps change while
        // the tab stays open (a profile override after a reconnect, or a generation
        // rebind). Steady state — caps unchanged — is a two-compare no-op, so the normal
        // polling path is untouched. The combo repopulate is wrapped in _suppressEvents
        // exactly as Bind does, so the rebuild never fires a spurious commit.
        private void SyncResolvedCaps()
        {
            if (_host == null) return;
            var dt = _host.DisplayType;
            var id = _host.ItmDeviceId;
            if (dt == _boundDisplayType && id == _boundItmDeviceId) return;   // steady state: no-op
            _boundDisplayType = dt;
            _boundItmDeviceId = id;
            _isItm = dt == DisplayType.Itm;
            _suppressEvents = true;
            cmbItemNone.Visibility = _isItm ? Visibility.Visible : Visibility.Collapsed;
            var itmOnly = _isItm ? Visibility.Visible : Visibility.Collapsed;
            borderItmInfo.Visibility = itmOnly;
            sectionItmOptions.Visibility = itmOnly;
            sectionDisplayMode.Title = _isItm ? "Legacy Display Mode" : "Display Mode";
            PopulateDefaultPages(id);
            SelectByPageNumber(cmbDefaultPage, _settings.ItmDefaultPage);
            _suppressEvents = false;
            UpdateModeState();               // recomputes panelItmLive visibility; navigates to Overview if no longer ITM
            RefreshModeHeader();             // basic→ITM live rebind reveals the DISPLAY MODE toggle without a renavigate
            RenderPriority(_lastSnapshot);   // Overview base-name/rows for the new device
            if (_currentView == TabView.Triggers)
                viewTriggers.Enter(_lastSnapshot);   // rebuild the editor for the new device; drops any open draft
        }

        private void Poll(bool force = false)
        {
            if (_host == null)
                return;

            // Re-derive the cap-dependent layout FIRST — before the ITM-live early-return
            // — so a basic→ITM (or ITM→basic) override is detected even while the ITM live
            // panel is collapsed. A no-op in steady state (two compares).
            SyncResolvedCaps();

            if (panelItmLive.Visibility != Visibility.Visible)
                return;

            // ONE volatile read — the envelope; the parts gate their own re-renders
            // below (values part → the mirror; rule part / status line → the rows).
            var envelope = _host.Snapshot;
            var snapshot = envelope?.Rules;
            var values = envelope?.Values;
            string status = envelope?.ItmStatus;
            bool snapshotChanged = !ReferenceEquals(snapshot, _lastSnapshot);
            bool valuesChanged = !ReferenceEquals(values, _lastValues);
            bool statusChanged = !string.Equals(status, _lastStatus, StringComparison.Ordinal);
            _lastSnapshot = snapshot;
            _lastValues = values;
            _lastStatus = status;

            // The LIVE card: the mirror redraws only on a values-snapshot reference
            // change; the captions also follow the status line (their fallback path).
            if (force || valuesChanged)
                displayMirror.Render(values);
            if (force || valuesChanged || statusChanged)
            {
                txtCurrentPage.Text = ItmDisplayMirrorRender.PageCaption(
                    values, status, _host.ItmDeviceId);
                txtMirrorState.Text = ItmDisplayMirrorRender.StateCaption(values) ?? "";
            }

            if (force || snapshotChanged || statusChanged)
            {
                RenderPriority(snapshot);
                RenderActivity(snapshot);
                // The Triggers editor merges the same live state into its rows — patched in
                // place while an editor is open so poll re-renders never disturb it.
                if (_currentView == TabView.Triggers)
                    viewTriggers.Poll(snapshot);
            }
        }

        // ── Overview rendering (row models from DisplayOverviewRender) ───

        private void RenderPriority(DisplayRuleSnapshot snapshot)
        {
            if (_host == null)
                return;
            var config = _host.GetDisplayConfig();
            string basePage = DisplayOverviewRender.BasePageName(
                snapshot, config, _host.ItmDeviceId, _settings.ItmDefaultPage);

            panelPriorityRows.Children.Clear();
            foreach (var row in DisplayOverviewRender.PriorityRows(snapshot, basePage))
                panelPriorityRows.Children.Add(BuildPriorityRow(row));

            panelNoTriggers.Visibility = DisplayOverviewRender.HasConfiguredTriggers(config)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void RenderActivity(DisplayRuleSnapshot snapshot)
        {
            panelActivity.Children.Clear();
            int count = 0;
            if (snapshot != null)
            {
                var rows = DisplayOverviewRender.ActivityRows(snapshot);
                foreach (var row in rows)
                    panelActivity.Children.Add(BuildActivityRow(row));
                count = rows.Count;
            }
            txtNoActivity.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // One priority row: [rank] [label ……] [chip] [seconds]. On-screen rows get the
        // green accent and left bar; the base row a dashed outline; muted rows dim whole.
        private UIElement BuildPriorityRow(PriorityRowModel model)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rank = new TextBlock
            {
                Text = model.Rank,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = model.OnScreen ? DisplayPalette.GreenRank : (model.IsBase ? DisplayPalette.BaseRank : DisplayPalette.MutedRank),
                Width = 18,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 0);
            grid.Children.Add(rank);

            var label = new TextBlock
            {
                Text = model.Label,
                FontSize = 12.5,
                Foreground = model.OnScreen ? DisplayPalette.OnScreenText : (model.IsBase ? DisplayPalette.BaseText : DisplayPalette.RowText),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            if (!string.IsNullOrEmpty(model.Chip))
            {
                var chip = new TextBlock
                {
                    Text = model.Chip,
                    FontSize = 10.5,
                    Foreground = model.OnScreen ? DisplayPalette.GreenAccent : DisplayPalette.ChipText,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(chip, 2);
                grid.Children.Add(chip);
            }

            if (model.Seconds != null)
            {
                var seconds = new TextBlock
                {
                    Text = model.Seconds,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = DisplayPalette.GreenAccent,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(seconds, 3);
                grid.Children.Add(seconds);
            }

            if (model.IsBase)
            {
                // WPF borders can't dash — a dashed Rectangle under the content gives the
                // design's "pinned base" outline.
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
                host.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = grid,
                });
                return host;
            }

            var border = new Border
            {
                Background = model.OnScreen ? DisplayPalette.OnScreenBg : DisplayPalette.RowBg,
                BorderBrush = model.OnScreen ? DisplayPalette.OnScreenBorder : DisplayPalette.RowBorder,
                BorderThickness = model.OnScreen ? new Thickness(3, 1, 1, 1) : new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = grid,
            };
            if (model.Muted)
                border.Opacity = 0.5;
            return border;
        }

        // One activity row: [age] [event text].
        private static UIElement BuildActivityRow(ActivityRowModel model)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var age = new TextBlock
            {
                Text = model.Time,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = DisplayPalette.AgeText,
                Margin = new Thickness(0, 1, 10, 0),
            };
            Grid.SetColumn(age, 0);
            grid.Children.Add(age);

            var text = new TextBlock
            {
                Text = model.Text,
                FontSize = 12,
                Foreground = DisplayPalette.ActivityText,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            return grid;
        }
    }
}
