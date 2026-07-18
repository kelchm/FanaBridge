using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The per-device Display tab: a DISPLAY MODE header (ITM display / Legacy only / Off)
    /// and hub-and-spoke views — Overview is the landing view (current-page caption,
    /// recent activity, the read-only display priority list, and the option controls
    /// the old Screen tab carried, same settings and semantics), and the editor views
    /// (Triggers, Pages &amp; fields, Legacy screens — placeholders in this piece) are
    /// reached only through Overview's contextual links, each returning via a ‹ ghost
    /// back button. There is no persistent tab strip; the mode header shows on
    /// Overview (ITM wheels) and whenever control is Off (so the user can leave Off).
    /// Off collapses normal content in favour of a dedicated Off card (mode state, not
    /// a navigation view).
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
        private IDisplayPickerStore _pickerStore;
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
        private readonly SevenSegmentFace _legacyFace = new SevenSegmentFace();

        // ITM-wheel chrome for the legacy face: the same 4:1 panel + Min/MaxWidth as
        // ItmDisplayMirror, with the 3-char face scaled up and centered — the legacy
        // page lives on the same physical display, so the card must not shrink when
        // control flips Itm ↔ Legacy. Basic wheels host the face bare (small).
        private readonly Viewbox _wideFaceSlot;
        private readonly Viewbox _wideFacePanel;

        // ── Polling state ────────────────────────────────────────────────
        private DispatcherTimer _timer;
        private DisplayRuleSnapshot _lastSnapshot;
        private DisplayValuesSnapshot _lastValues;
        private string _lastStatus;

        public DisplayTabPanel()
        {
            InitializeComponent();

            _wideFaceSlot = new Viewbox
            {
                Height = 210,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _wideFacePanel = new Viewbox
            {
                Stretch = Stretch.Uniform,
                MinWidth = 320,   // ItmDisplayMirror's bounds — the two cards must match
                MaxWidth = 720,
                Child = new Grid
                {
                    Width = 1000,   // ItmDisplayMirror's 4:1 virtual panel
                    Height = 250,
                    Background = DisplayPalette.LegacyFaceBg,
                    ClipToBounds = true,
                    Children = { _wideFaceSlot },
                },
            };

            hostLegacyFace.Content = _legacyFace;
        }

        // (Re)parents the shared face into the host that matches the bound caps: bare
        // (small) on basic wheels, inside the wide ITM-sized panel on ITM wheels. The
        // face must be detached from both possible parents before re-attaching.
        private void ApplyLegacyFaceHost()
        {
            hostLegacyFace.Content = null;
            _wideFaceSlot.Child = null;
            if (DisplayShellRouting.UseWideLegacyFace(_boundDisplayType))
            {
                _wideFaceSlot.Child = _legacyFace;
                hostLegacyFace.Content = _wideFacePanel;
            }
            else
            {
                hostLegacyFace.Content = _legacyFace;
            }
        }

        /// <summary>
        /// Binds the panel to its device host and the two on-demand editor catalogs. Call
        /// once after construction, before the panel is displayed (the old Screen panel's
        /// contract). The catalogs are pulled only when a picker/dropdown opens — the
        /// polling/rendering path uses <paramref name="host"/> alone. The plugin-wide
        /// <paramref name="pickerStore"/> (favorites/recents) is shared across wheels.
        /// </summary>
        internal void Bind(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog,
            IDisplayPickerStore pickerStore)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _propertyCatalog = propertyCatalog ?? throw new ArgumentNullException(nameof(propertyCatalog));
            _roleCatalog = roleCatalog ?? throw new ArgumentNullException(nameof(roleCatalog));
            _pickerStore = pickerStore;
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
            // picker store, and mutable settings, and wire its two seam events: ‹ back returns
            // to Overview, and a committed edit refreshes the Overview Monitor list so it
            // stays consistent.
            viewTriggers.Bind(_host, _propertyCatalog, _roleCatalog, _settings, _pickerStore);
            viewTriggers.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewTriggers.ConfigApplied += (s, e) => RenderMonitor(_lastSnapshot);

            // Pages & fields editor — same Bind/Enter/Poll/BackRequested seam. ConfigApplied
            // is a no-op for Overview chrome today (field mappings don't change the priority
            // list); wired for symmetry / future live-mirror refresh. LegacyRequested lands
            // on the Virtual pages editor (Page 6 delegation card).
            viewPages.Bind(_host, _propertyCatalog, _roleCatalog, _settings, _pickerStore);
            viewPages.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewPages.ConfigApplied += (s, e) => { /* field mappings — Overview rows unchanged */ };
            viewPages.LegacyRequested += (s, e) => NavigateTo(TabView.Legacy);

            // Virtual pages editor — Bind/Enter/Poll/BackRequested; ConfigApplied refreshes
            // the legacy Overview monitor (screens/base change the Show cells).
            viewLegacy.Bind(_host, _propertyCatalog, _roleCatalog, _settings, _pickerStore);
            viewLegacy.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewLegacy.ConfigApplied += (s, e) =>
            {
                RenderMonitor(_lastSnapshot);
                RenderLegacyOverview(_lastSnapshot);
            };

            // The Overview priority list IS the shared trigger table in Monitor mode: a
            // read-only "what's in play" list. A row click lands in the Triggers editor with
            // that rule expanded (the EnterAndSelect seam).
            monitorTable.Mode = TriggerTableMode.Monitor;
            monitorTable.RowActivated += id => NavigateTo(TabView.Triggers, id);
            legacyMonitorTable.Mode = TriggerTableMode.Monitor;
            legacyMonitorTable.RowActivated += id => NavigateTo(TabView.Triggers, id);

            // DISPLAY MODE segments — tri-state ITM / Legacy / Off, driven by
            // DisplaySettings.DisplayControl. Off's selected fill is amber; the others
            // keep the default accent. SelectionChanged fires on user activation only;
            // UpdateModeState mirrors the setting back into SelectedId without re-entering.
            segMode.SetItems(new (string, string, Brush)[]
            {
                (DisplayModeHeaderModel.SegmentItm, "ITM display", null),
                (DisplayModeHeaderModel.SegmentLegacy, "Legacy only", null),
                (DisplayModeHeaderModel.SegmentOff, "Off", DisplayPalette.OffAccentBg),
            });
            segMode.SelectionChanged += (s, id) =>
                SetDisplayControl(DisplayModeHeaderModel.ControlForSegment(id));

            // Option controls — identical semantics to the old Screen tab. "None"
            // (legacy page off) is an ITM-only display-mode choice.
            cmbItemNone.Visibility = _isItm ? Visibility.Visible : Visibility.Collapsed;
            SelectByTag(cmbDisplayMode, _settings.DisplayMode ?? DisplaySettings.DefaultMode);
            // chkShowLapTotal / chkShowPositionTotal retired (Phase 6b) — format lives in
            // the Pages editor; settings booleans remain for one-release migration.
            // _isItm and the default-page table below are read once, at bind, from the host's
            // override-resolved caps. Per-poll consumers (mirror, labels) re-read the live host
            // values each frame; this bind-time layout is NOT re-derived if the resolved caps
            // change while the tab stays open (an override applied after a reconnect) — a known
            // limitation until the Display tab is split into per-view controls.
            PopulateDefaultPages(host.ItmDeviceId);
            SelectByPageNumber(cmbDefaultPage, _settings.ItmDefaultPage);

            // ITM wheels get the mode header (via NavigateTo — it shows on Overview
            // only, plus whenever control is Off); the info banner and ITM options are
            // mode-dependent chrome owned by UpdateModeState below. Basic-display wheels
            // get only the (7-segment) Display Mode section — the same information as
            // the old panel — unless they are trapped in Off.
            sectionDisplayMode.Title = _isItm ? "Legacy Display Mode" : "Display Mode";
            ApplyLegacyFaceHost();

            NavigateTo(TabView.Overview);
            UpdateModeState();

            _suppressEvents = false;
            Poll(force: true);
        }

        // ── DISPLAY MODE toggle (owns DisplaySettings.DisplayControl) ────

        private void SetDisplayControl(string control)
        {
            if (_suppressEvents || _settings == null || string.IsNullOrEmpty(control))
                return;

            // Canonical casing (segment ids map through DisplayModeHeaderModel).
            if (string.Equals(control, DisplaySettings.ControlLegacy, StringComparison.OrdinalIgnoreCase))
                control = DisplaySettings.ControlLegacy;
            else if (string.Equals(control, DisplaySettings.ControlOff, StringComparison.OrdinalIgnoreCase))
                control = DisplaySettings.ControlOff;
            else
                control = DisplaySettings.ControlItm;

            // Control-only no-op per spec: do not rewrite / Notify when the control is
            // already selected, even if the downgrade ItmEnabled mirror disagrees.
            if (DisplayModeHeaderModel.IsSameControl(_settings.DisplayControl, control))
                return;

            _settings.DisplayControl = control;
            // Downgrade-safety mirror: every write keeps ItmEnabled == (control == Itm).
            _settings.ItmEnabled = control == DisplaySettings.ControlItm;
            UpdateModeState();
            _host?.NotifySettingsChanged();
            Poll(force: true);
        }

        // Off card: hand control back to FanaBridge (Itm on ITM wheels, Legacy on basic).
        private void TurnDisplayOn_Click(object sender, RoutedEventArgs e)
            => SetDisplayControl(DisplayModeHeaderModel.TurnBackOnControl(_isItm));

        // Mode-dependent chrome: toggle visuals, hint text, which panels exist, and the
        // old panel's grey-out of the ITM sub-options while the ITM display is off. Off
        // collapses all normal content and shows the Off card (mode state, not a view).
        private void UpdateModeState()
        {
            string control = _settings?.DisplayControl ?? DisplaySettings.ControlItm;
            bool isOff = DisplayModeHeaderModel.IsOff(control);

            segMode.SelectedId = DisplayModeHeaderModel.SegmentIdFor(control);
            txtModeHint.Text = DisplayModeHeaderModel.ModeHint(control);

            panelOffCard.Visibility = isOff ? Visibility.Visible : Visibility.Collapsed;

            if (isOff)
            {
                // Off is mode state: force Overview, hide every normal Overview section
                // (and with it every link into an editor), show only the Off card + header.
                NavigateTo(TabView.Overview);
                panelItmLive.Visibility = Visibility.Collapsed;
                panelLegacyLive.Visibility = Visibility.Collapsed;
                borderItmInfo.Visibility = Visibility.Collapsed;
                sectionItmOptions.Visibility = Visibility.Collapsed;
                sectionDisplayMode.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Itm/Legacy: Off card already collapsed; live cards per shell routing —
                // ITM Overview while ITM is the active world, legacy Overview on basic
                // wheels and on ITM wheels in Legacy-only control (the legacy world is
                // the whole display then, rendered at the ITM panel size).
                bool itmUi = DisplayShellRouting.ShowItmOverview(
                    _isItm ? DisplayType.Itm : DisplayType.Basic, control);
                bool legacyUi = DisplayShellRouting.ShowLegacyOverview(
                    _isItm ? DisplayType.Itm : DisplayType.Basic, control);
                panelItmLive.Visibility = itmUi ? Visibility.Visible : Visibility.Collapsed;
                panelLegacyLive.Visibility = legacyUi ? Visibility.Visible : Visibility.Collapsed;
                // When an ITM wheel leaves ITM-active, drop out of the Pages editor (its
                // twin is ITM-only). Virtual pages / Triggers stay reachable.
                if (_isItm && !itmUi && _currentView == TabView.Pages)
                    NavigateTo(TabView.Overview);

                // The info banner and ITM options are ITM-world chrome: shown only while
                // the ITM Overview is up, not alongside the legacy Overview in Legacy-only
                // control. The (Legacy) Display Mode section stays — it picks the fallback
                // face when the legacy rule world is empty.
                var itmChrome = itmUi ? Visibility.Visible : Visibility.Collapsed;
                borderItmInfo.Visibility = itmChrome;
                sectionItmOptions.Visibility = itmChrome;
                sectionDisplayMode.Visibility = Visibility.Visible;

                panelDefaultPage.IsEnabled = _settings.ItmActive;
            }

            // Header visibility depends on control as well as view/_isItm.
            RefreshModeHeader();
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

        private void NavigateTo(TabView view, string expandRuleId = null)
        {
            _currentView = view;
            foreach (var kv in _views)
                kv.Value.Visibility = kv.Key == view ? Visibility.Visible : Visibility.Collapsed;

            var ruleSet = TriggersRuleSet();

            // Build (or rebuild) the Triggers editor from the current config each time it
            // becomes the active view — a clean slate, snapshot-driven from there. A row-click
            // from the Overview Monitor list carries the rule to expand on arrival. Basic
            // wheels open the legacy rule set (virtual-page targets).
            if (view == TabView.Triggers && _host != null)
            {
                if (expandRuleId != null)
                    viewTriggers.EnterAndSelect(_lastSnapshot, expandRuleId, ruleSet);
                else
                    viewTriggers.Enter(_lastSnapshot, ruleSet);
            }

            // Pages editor: same clean-slate Enter on activation (uses the values snapshot
            // the Overview mirror already holds).
            if (view == TabView.Pages && _host != null)
                viewPages.Enter(_lastValues, _lastSnapshot);

            // Virtual pages editor.
            if (view == TabView.Legacy && _host != null)
                viewLegacy.Enter();

            // The DISPLAY MODE header belongs to the hub — it shows on Overview (ITM) and
            // whenever control is Off, never inside an editor unless Off keeps it up.
            RefreshModeHeader();
        }

        private TriggerRuleSet TriggersRuleSet()
            => DisplayShellRouting.TriggersRuleSetFor(
                _isItm ? DisplayType.Itm : DisplayType.Basic,
                _settings?.DisplayControl);

        // The DISPLAY MODE header (segmented ITM/Legacy/Off toggle + divider) shows on the
        // Overview of an ITM wheel, and on any wheel while control is Off (Off-trap guard
        // after a live ITM→basic caps rebind). Re-derived on view changes and caps rebinds.
        private void RefreshModeHeader()
        {
            string control = _settings?.DisplayControl ?? DisplaySettings.ControlItm;
            var headerVisibility = DisplayModeHeaderModel.ShowModeHeader(
                    _isItm, _currentView == TabView.Overview, control)
                ? Visibility.Visible
                : Visibility.Collapsed;
            panelModeHeader.Visibility = headerVisibility;
            lineModeHeader.Visibility = headerVisibility;
        }

        // ── Option controls (settings semantics identical to the old tab) ─

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
                RenderMonitor(_lastSnapshot);
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
            // Mode-dependent chrome (info banner, ITM options, live panels) is re-derived
            // by the UpdateModeState call below for the new caps.
            sectionDisplayMode.Title = _isItm ? "Legacy Display Mode" : "Display Mode";
            ApplyLegacyFaceHost();
            PopulateDefaultPages(id);
            SelectByPageNumber(cmbDefaultPage, _settings.ItmDefaultPage);
            _suppressEvents = false;
            UpdateModeState();               // recomputes panelItmLive / Off card; navigates to Overview if no longer ITM-active
            RefreshModeHeader();             // basic↔ITM live rebind + Off-trap header visibility
            RenderMonitor(_lastSnapshot);    // Overview base-name/rows for the new device
            if (_currentView == TabView.Triggers)
                viewTriggers.Enter(_lastSnapshot, TriggersRuleSet());   // rebuild for new device
            if (_currentView == TabView.Pages)
                viewPages.Enter(_lastValues, _lastSnapshot);
            if (_currentView == TabView.Legacy)
                viewLegacy.Enter();
        }

        private void Poll(bool force = false)
        {
            if (_host == null)
                return;

            // Re-derive the cap-dependent layout FIRST — before the live early-return
            // — so a basic→ITM (or ITM→basic) override is detected even while the ITM live
            // panel is collapsed. A no-op in steady state (two compares).
            SyncResolvedCaps();

            bool itmLive = panelItmLive.Visibility == Visibility.Visible;
            bool legacyLive = panelLegacyLive.Visibility == Visibility.Visible;
            bool editorActive = _currentView == TabView.Triggers
                || _currentView == TabView.Pages
                || _currentView == TabView.Legacy;
            if (!itmLive && !legacyLive && !editorActive)
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
            // Overview mirror stays read-only (IsInteractive defaults false).
            if (itmLive)
            {
                if (force || valuesChanged)
                    displayMirror.Render(values);
                if (force || valuesChanged || statusChanged)
                {
                    txtCurrentPage.Text = ItmDisplayMirrorRender.PageCaption(
                        values, status, _host.ItmDeviceId);
                    txtMirrorState.Text = ItmDisplayMirrorRender.StateCaption(values) ?? "";
                }
            }

            if (legacyLive && (force || snapshotChanged || statusChanged))
                RenderLegacyOverview(snapshot);

            // Pages editor twin: same values snapshot, selection chrome on its own mirror.
            if (_currentView == TabView.Pages && (force || valuesChanged || snapshotChanged))
                viewPages.Poll(values, snapshot);

            if (_currentView == TabView.Legacy && (force || snapshotChanged))
                viewLegacy.Poll();

            if (force || snapshotChanged || statusChanged)
            {
                if (itmLive)
                {
                    RenderMonitor(snapshot);
                    RenderActivity(snapshot);
                }
                // The Triggers editor merges the same live state into its rows — patched in
                // place while an editor is open so poll re-renders never disturb it.
                if (_currentView == TabView.Triggers)
                    viewTriggers.Poll(snapshot);
            }
        }

        // ── Overview rendering (the shared trigger table, Monitor mode) ───

        // The Overview "Display priority" list. The shared table is read-only here (no drag,
        // no ⋯, no drawer) and its filtered row-set is itself live-derived (session-ineligible
        // rules drop), so a full SetRows on each gated snapshot change — the reference-compare
        // the poll loop already applies — is the right rebuild: nothing open to protect, and it
        // handles rows appearing/leaving the filter that an in-place patch could not.
        private void RenderMonitor(DisplayRuleSnapshot snapshot)
        {
            if (_host == null || panelItmLive.Visibility != Visibility.Visible)
                return;
            var config = _host.GetDisplayConfig();
            monitorTable.SetRows(DisplayOverviewRender.MonitorRows(
                snapshot, config, _host.ItmDeviceId, _settings.ItmDefaultPage));

            txtSituation.Text = "live · " + SituationLabel(_lastStatus);
            panelNoTriggers.Visibility = DisplayOverviewRender.HasConfiguredTriggers(config)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // Legacy Overview (basic wheels; ITM wheels in Legacy-only control): face from
        // last-sent segments (rule path) or caption fallback (mode-based face), plus the
        // legacy Monitor priority list. LegacyPageActive gates the face — with the page
        // off (ITM wheel, DisplayMode "None") the stack resolves segments for the
        // snapshot only and the mirror must not paint what the wire never wrote.
        private void RenderLegacyOverview(DisplayRuleSnapshot snapshot)
        {
            if (_host == null || panelLegacyLive.Visibility != Visibility.Visible)
                return;
            var config = _host.GetDisplayConfig();
            bool pageActive = _settings?.LegacyPageActive == true;
            if (DisplayShellRouting.UseRuleDrivenSegments(snapshot?.LegacySegments, pageActive))
                _legacyFace.Render(snapshot.LegacySegments);
            else
                _legacyFace.Render(SevenSegmentFaceRender.BlankFrame());

            txtLegacyCaption.Text = DisplayShellRouting.LegacyMirrorCaption(
                snapshot?.LegacyScreenName, _settings?.DisplayMode, pageActive);

            legacyMonitorTable.SetRows(DisplayOverviewRender.MonitorRows(
                snapshot, config, _host.ItmDeviceId, _settings?.ItmDefaultPage ?? 1,
                legacyMode: true));
            panelLegacyNoTriggers.Visibility =
                DisplayOverviewRender.HasConfiguredTriggers(config, legacyMode: true)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            // Activity reuses the same snapshot stream (legacy + ITM events merged).
            panelLegacyActivity.Children.Clear();
            int count = 0;
            if (snapshot != null)
            {
                var rows = DisplayOverviewRender.ActivityRows(snapshot);
                foreach (var row in rows)
                    panelLegacyActivity.Children.Add(BuildActivityRow(row));
                count = rows.Count;
            }
            txtLegacyNoActivity.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // The section header's situation caption ("in game" / "idle"), derived from the ITM
        // lifecycle status line the envelope already carries — "Idle" means no game is feeding
        // the display; a synced/bring-up/recovery line means a game is running.
        private static string SituationLabel(string itmStatus)
            => string.Equals(itmStatus, "Idle", StringComparison.Ordinal) ? "idle" : "in game";

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
