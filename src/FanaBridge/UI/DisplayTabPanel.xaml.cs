using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FanaBridge.Adapters;
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
    /// Threading: everything live is read through the <see cref="DisplayPanelContext"/>
    /// delegates, which return volatile snapshots — a DispatcherTimer polls them at
    /// 500 ms while the panel is loaded and re-renders only on change (snapshot
    /// reference or status string), plus a 1 s floor so the relative-age labels keep
    /// ticking between snapshots. The panel never touches engine state directly.
    /// </summary>
    public partial class DisplayTabPanel : UserControl
    {
        private enum TabView { Overview, Triggers, Pages, Legacy }

        private DisplayPanelContext _context;
        private DisplaySettings _settings;
        private bool _suppressEvents;
        private bool _isItm;

        private Dictionary<TabView, UIElement> _views;

        // ── Polling state ────────────────────────────────────────────────
        private DispatcherTimer _timer;
        private DisplayRuleSnapshot _lastSnapshot;
        private string _lastStatus;
        private DateTime _lastAgeRenderUtc;

        // ── Palette (the design mock's SimHub-dark values) ───────────────
        private static readonly SolidColorBrush AccentBg = Frozen("#1E8FD5");
        private static readonly SolidColorBrush ToggleIdleText = Frozen("#B6B6B6");
        private static readonly SolidColorBrush RowBg = Frozen("#303032");
        private static readonly SolidColorBrush RowBorder = Frozen("#3D3D3F");
        private static readonly SolidColorBrush RowText = Frozen("#EAEAEA");
        private static readonly SolidColorBrush OnScreenBg = Frozen("#22321F");
        private static readonly SolidColorBrush OnScreenBorder = Frozen("#3F7A4A");
        private static readonly SolidColorBrush OnScreenText = Frozen("#FFFFFF");
        private static readonly SolidColorBrush GreenAccent = Frozen("#8FE0A8");
        private static readonly SolidColorBrush GreenRank = Frozen("#7FCE9A");
        private static readonly SolidColorBrush MutedRank = Frozen("#7A7A7A");
        private static readonly SolidColorBrush ChipText = Frozen("#8F8F8F");
        private static readonly SolidColorBrush BaseRank = Frozen("#C9A24A");
        private static readonly SolidColorBrush BaseText = Frozen("#C8C8C8");
        private static readonly SolidColorBrush BaseBg = Frozen("#2A2A2B");
        private static readonly SolidColorBrush BaseDash = Frozen("#4A4A4A");
        private static readonly SolidColorBrush AgeText = Frozen("#7A7A7A");
        private static readonly SolidColorBrush ActivityText = Frozen("#E6E6E6");

        private static SolidColorBrush Frozen(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex);
            brush.Freeze();
            return brush;
        }

        public DisplayTabPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the panel to its device context. Call once after construction, before
        /// the panel is displayed (the old Screen panel's contract).
        /// </summary>
        internal void Bind(DisplayPanelContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _settings = context.DisplaySettings ?? new DisplaySettings();
            _isItm = context.DisplayType == DisplayType.Itm;
            _suppressEvents = true;

            _views = new Dictionary<TabView, UIElement>
            {
                { TabView.Overview, viewOverview },
                { TabView.Triggers, viewTriggers },
                { TabView.Pages,    viewPages },
                { TabView.Legacy,   viewLegacy },
            };

            // Option controls — identical semantics to the old Screen tab. "None"
            // (legacy page off) is an ITM-only display-mode choice.
            cmbItemNone.Visibility = _isItm ? Visibility.Visible : Visibility.Collapsed;
            SelectByTag(cmbDisplayMode, _settings.DisplayMode ?? DisplaySettings.DefaultMode);
            chkShowLapTotal.IsChecked = _settings.ItmShowLapTotal;
            chkShowPositionTotal.IsChecked = _settings.ItmShowPositionTotal;
            PopulateDefaultPages(context.ItmDeviceId);
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
            _context?.SettingsChanged?.Invoke();
            Poll(force: true);
        }

        // Mode-dependent chrome: toggle visuals, hint text, which panels exist, and the
        // old panel's grey-out of the ITM sub-options while the ITM display is off.
        private void UpdateModeState()
        {
            bool on = _settings.ItmEnabled;

            btnModeItm.Background = on ? AccentBg : Brushes.Transparent;
            txtModeItm.Foreground = on ? Brushes.White : ToggleIdleText;
            btnModeLegacy.Background = on ? Brushes.Transparent : AccentBg;
            txtModeLegacy.Foreground = on ? ToggleIdleText : Brushes.White;
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

        private void NavigateTo(TabView view)
        {
            foreach (var kv in _views)
                kv.Value.Visibility = kv.Key == view ? Visibility.Visible : Visibility.Collapsed;

            // The DISPLAY MODE header belongs to the hub — it shows on Overview only
            // (and only on ITM wheels), never inside an editor.
            var headerVisibility = _isItm && view == TabView.Overview
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
            _context?.SettingsChanged?.Invoke();
        }

        private void CmbDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null)
                return;

            if (cmbDisplayMode.SelectedItem is ComboBoxItem selected)
            {
                _settings.DisplayMode = (string)selected.Tag;
                _context?.SettingsChanged?.Invoke();
            }
        }

        private void CmbDefaultPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null)
                return;

            if (cmbDefaultPage.SelectedItem is ComboBoxItem selected && selected.Tag is byte pageNumber)
            {
                _settings.ItmDefaultPage = pageNumber;
                _context?.SettingsChanged?.Invoke();
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
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _timer.Tick += (s, a) => Poll();
            }
            _timer.Start();
            Poll(force: true);   // the tab may reload with stale content
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer?.Stop();
        }

        private void Poll(bool force = false)
        {
            if (_context == null || panelItmLive.Visibility != Visibility.Visible)
                return;

            var snapshot = _context.GetSnapshot?.Invoke();
            string status = _context.GetItmStatus?.Invoke();
            bool snapshotChanged = !ReferenceEquals(snapshot, _lastSnapshot);
            bool statusChanged = !string.Equals(status, _lastStatus, StringComparison.Ordinal);
            _lastSnapshot = snapshot;
            _lastStatus = status;

            if (force || snapshotChanged || statusChanged)
            {
                txtCurrentPage.Text = DisplayOverviewRender.CurrentPageCaption(
                    status, _context.ItmDeviceId);
                RenderPriority(snapshot);
                RenderActivity(snapshot);
            }
            else if (snapshot != null && snapshot.Activity.Count > 0
                && (DateTime.UtcNow - _lastAgeRenderUtc).TotalMilliseconds >= 1000)
            {
                // Nothing changed, but the age labels still tick — 1 s floor.
                RenderActivity(snapshot);
            }
        }

        // ── Overview rendering (row models from DisplayOverviewRender) ───

        private void RenderPriority(DisplayRuleSnapshot snapshot)
        {
            if (_context == null)
                return;
            var config = _context.GetConfig?.Invoke();
            string basePage = DisplayOverviewRender.BasePageName(
                snapshot, config, _context.ItmDeviceId, _settings.ItmDefaultPage);

            panelPriorityRows.Children.Clear();
            foreach (var row in DisplayOverviewRender.PriorityRows(snapshot, basePage))
                panelPriorityRows.Children.Add(BuildPriorityRow(row));

            panelNoTriggers.Visibility = DisplayOverviewRender.HasConfiguredTriggers(config)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void RenderActivity(DisplayRuleSnapshot snapshot)
        {
            _lastAgeRenderUtc = DateTime.UtcNow;
            panelActivity.Children.Clear();
            int count = 0;
            if (snapshot != null)
            {
                var rows = DisplayOverviewRender.ActivityRows(snapshot,
                    DisplayOverviewRender.EstimatedNowMs(snapshot, DateTime.UtcNow));
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
                Foreground = model.OnScreen ? GreenRank : (model.IsBase ? BaseRank : MutedRank),
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
                Foreground = model.OnScreen ? OnScreenText : (model.IsBase ? BaseText : RowText),
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
                    Foreground = model.OnScreen ? GreenAccent : ChipText,
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
                    Foreground = GreenAccent,
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
                    Stroke = BaseDash,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = BaseBg,
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
                Background = model.OnScreen ? OnScreenBg : RowBg,
                BorderBrush = model.OnScreen ? OnScreenBorder : RowBorder,
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
                Text = model.Age,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = AgeText,
                Margin = new Thickness(0, 1, 10, 0),
            };
            Grid.SetColumn(age, 0);
            grid.Children.Add(age);

            var text = new TextBlock
            {
                Text = model.Text,
                FontSize = 12,
                Foreground = ActivityText,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            return grid;
        }
    }
}
