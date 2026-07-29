using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Overview (v2) hub view — digest §2 structure, §4 navigation pins only.
    /// Pure projection via <see cref="DisplayOverviewV2Model"/>; reuses
    /// <see cref="ItmDisplayMirror"/> for the optimistic preview (§5.2).
    /// Rows are inert (O5); spokes navigate (N3 live; N2 live this phase; N1 later-phase disabled).
    /// </summary>
    public partial class DisplayOverviewV2View : UserControl
    {
        private IDisplayPanelHost _host;
        private WheelCatalog _catalog;
        private AliasTable _aliases;
        private DisplayConfigV2 _config;
        private bool _suppressEvents;
        private DisplayOverviewV2Model _model;

        // E9 later-phase: N1 destination (v2 Pages & Fields) is not built yet — spoke
        // stays disabled with a DisplayCopy tooltip. Do NOT route to the v1 editors
        // (wrong document). N2 Priority is LIVE (phase 3a).
        /// <summary>N1: Pages &amp; Fields › (later phase — disabled).</summary>
        public event EventHandler PagesAndFieldsRequested;

        /// <summary>N2: Priority › (phase 3a — live).</summary>
        public event EventHandler PriorityRequested;

        /// <summary>N3: Open Control mapper › (out of FanaBridge into SimHub).</summary>
        public event EventHandler ControlMapperRequested;

        /// <summary>
        /// NEW affordance (RE-SEQUENCE ruling): Diagnostics › — product feature, not
        /// a board spoke. Replaces the cancelled bench-kit trace file.
        /// </summary>
        public event EventHandler DiagnosticsRequested;

        /// <summary>Raised after a committed settings edit (mode / reject).</summary>
        public event EventHandler ConfigApplied;

        public DisplayOverviewV2View()
        {
            InitializeComponent();
            ApplyStaticCopy();
            segMode.SelectionChanged += OnModeSelectionChanged;
        }

        /// <summary>
        /// Bind once after construction. Catalog/alias may be null (badges fall back).
        /// </summary>
        internal void Bind(
            IDisplayPanelHost host,
            WheelCatalog catalog = null,
            AliasTable aliases = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _catalog = catalog;
            _aliases = aliases;
            _config = host.GetDisplayConfigV2();
            ConfigureModeSegments(host.DisplayType);
            Poll(force: true);
        }

        /// <summary>Refresh from the host envelope. Safe to call every poll tick.</summary>
        internal void Poll(bool force = false)
        {
            if (_host == null)
                return;

            var envelope = _host.Snapshot;
            var config = _host.GetDisplayConfigV2();
            _config = config;

            var resolution = ProjectResolution(envelope);
            var values = envelope?.Values;

            _model = DisplayOverviewV2Model.Project(
                config,
                resolution,
                values,
                _host.DisplayType,
                _catalog,
                _aliases,
                nextPageMapped: false,
                prevPageMapped: false);

            ApplyModel(_model, values);
        }

        private static DisplayResolutionSnapshotModel ProjectResolution(DisplayPanelSnapshot envelope)
        {
            if (envelope == null)
            {
                return DisplayResolutionSnapshotModel.From(
                    null, inGame: false, isConnected: false, aggregates: null, manual: null);
            }

            return DisplayResolutionSnapshotModel.From(
                envelope.ComposedResolution,
                inGame: envelope.InGame,
                isConnected: true,
                aggregates: envelope.Aggregates,
                manual: envelope.Manual);
        }

        private void ApplyStaticCopy()
        {
            txtOverviewTitle.Text = DisplayCopy.Overview;
            txtOnTheWheelNow.Text = DisplayCopy.OnTheWheelNow;
            txtModeProfileDivider.Text = DisplayCopy.ModeProfileDivider;
            SetHyperlinkText(linkPagesAndFields, DisplayCopy.PagesAndFieldsSpoke);
            // N1 later phase: disabled spoke + tooltip; wiring point tagged above.
            linkPagesAndFields.IsEnabled = false;
            linkPagesAndFields.ToolTip = DisplayCopy.SpokeArrivingLater(DisplayCopy.PagesAndFields);
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(linkPagesAndFields, true);
            txtPriorityHeader.Text = DisplayCopy.PrioritySection;
            txtLadderSubtitle.Text = DisplayCopy.LadderSubtitle;
            SetHyperlinkText(linkPriority, DisplayCopy.PrioritySpoke);
            // N2 phase 3a: Priority view is LIVE — enable the spoke.
            linkPriority.IsEnabled = true;
            linkPriority.ToolTip = null;
            txtReadingIt.Text = DisplayCopy.ReadingIt;
            txtLadderLegend.Text = DisplayCopy.LadderLegend;
            txtMirrorWatermark.Text = DisplayCopy.MirrorWatermark;
            txtThisDevice.Text = DisplayCopy.ThisDevice;
            txtDisplayMode.Text = DisplayCopy.DisplayMode;
            txtRejectLabel.Text = DisplayCopy.RejectUncommandedChanges;
            txtRejectExplainer.Text = DisplayCopy.RejectUncommandedChangesExplainer;
            txtControls.Text = DisplayCopy.Controls;
            txtNextPage.Text = DisplayCopy.NextPage;
            txtPrevPage.Text = DisplayCopy.PreviousPage;
            txtNextReadOnly.Text = DisplayCopy.ReadOnly;
            txtPrevReadOnly.Text = DisplayCopy.ReadOnly;
            SetHyperlinkText(linkControlMapper, DisplayCopy.OpenControlMapperSpoke);
            // NEW affordance (RE-SEQUENCE): Diagnostics link — not a board spoke.
            SetHyperlinkText(linkDiagnostics, DisplayCopy.DiagnosticsSpoke);
        }

        private static void SetHyperlinkText(Hyperlink link, string text)
        {
            link.Inlines.Clear();
            link.Inlines.Add(new Run(text));
        }

        private void ConfigureModeSegments(DisplayType displayType)
        {
            _suppressEvents = true;
            if (displayType == DisplayType.Itm)
            {
                segMode.SetItems(new (string, string, Brush)[]
                {
                    ("on", DisplayCopy.ModeItm, null),
                    ("legacyOnly", DisplayCopy.ModeLegacyOnly, null),
                    ("off", DisplayCopy.ModeOff, DisplayPalette.OffAccentBg),
                });
            }
            else
            {
                segMode.SetItems(new (string, string, Brush)[]
                {
                    ("on", DisplayCopy.ModeOn, null),
                    ("off", DisplayCopy.ModeOff, DisplayPalette.OffAccentBg),
                });
            }
            _suppressEvents = false;
        }

        private void ApplyModel(DisplayOverviewV2Model model, DisplayValuesSnapshot values)
        {
            if (model == null) return;

            txtSurfaceWord.Text = model.SurfaceWord;
            txtSituation.Text = model.SituationCopy;
            dotSituation.Fill = model.InGame
                ? new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5C));

            // Mirror (§5.2 existing preview path).
            displayMirror.Render(values);
            txtMirrorCaption.Text = model.MirrorCaption ?? string.Empty;

            // Ladder / Off empty-state (O1 provisional). Legend hides with the ladder.
            bool showLadder = model.ShowLadder;
            var ladderVis = showLadder ? Visibility.Visible : Visibility.Collapsed;
            listPriorityRows.Visibility = ladderVis;
            txtLadderSubtitle.Visibility = ladderVis;
            borderLadderLegend.Visibility = ladderVis;
            txtModeOffEmpty.Text = model.ModeOffEmptyState ?? string.Empty;
            txtModeOffEmpty.Visibility = showLadder ? Visibility.Collapsed : Visibility.Visible;
            listPriorityRows.ItemsSource = model.PriorityRows;

            // Mode segmented control (O9: Settings.Mode authoritative).
            _suppressEvents = true;
            segMode.SelectedId = SegmentIdFor(model.Mode);
            txtModeHint.Text = model.ModeHint;
            chkReject.IsChecked = model.RejectUncommandedChanges;
            _suppressEvents = false;

            // Controls mappings (read-only).
            txtNextMapped.Text = model.NextPageValue;
            txtPrevMapped.Text = model.PrevPageValue;

            panelConsequenceLines.Children.Clear();
            for (int i = 0; i < model.ConsequenceLines.Count; i++)
            {
                bool amber = model.ShowNothingMappedAmber
                    && i == model.ConsequenceLines.Count - 1
                    && model.ConsequenceLines[i] == DisplayCopy.ControlsConsequenceNothingMapped;
                panelConsequenceLines.Children.Add(new TextBlock
                {
                    Text = model.ConsequenceLines[i],
                    FontSize = 11.5,
                    Foreground = amber
                        ? new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x5F))
                        : new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 17,
                    Margin = new Thickness(0, 0, 0, 6),
                });
            }
        }

        private static string SegmentIdFor(SettingsMode mode)
        {
            switch (mode)
            {
                case SettingsMode.LegacyOnly: return "legacyOnly";
                case SettingsMode.Off: return "off";
                default: return "on";
            }
        }

        private static SettingsMode ModeForSegment(string segmentId)
        {
            if (string.Equals(segmentId, "legacyOnly", StringComparison.OrdinalIgnoreCase))
                return SettingsMode.LegacyOnly;
            if (string.Equals(segmentId, "off", StringComparison.OrdinalIgnoreCase))
                return SettingsMode.Off;
            return SettingsMode.On;
        }

        private void OnModeSelectionChanged(object sender, string segmentId)
        {
            if (_suppressEvents || _host == null)
                return;

            // CAS against the document this view projected from — same contract as
            // Priority / DisplayConfigV2EditSession. Concurrent writers conflict; no overwrite.
            var expected = _config;
            var mode = ModeForSegment(segmentId);
            var next = DisplayOverviewV2Model.WithMode(expected, mode);
            if (!_host.TryApplyDisplayConfigV2(expected, next))
            {
                SurfaceConflict();
                Poll(force: true);
                return;
            }

            _config = _host.GetDisplayConfigV2() ?? next;
            ClearConflict();

            // E9-exit: write-through to DisplayControl while the v1 tab lives.
            // Dies at E9-exit with the codec trim.
            var settings = _host.DisplaySettings;
            if (settings != null)
            {
                string control = DisplayOverviewV2Model.DisplayControlForMode(mode);
                if (!DisplayModeHeaderModel.IsSameControl(settings.DisplayControl, control))
                {
                    settings.DisplayControl = control;
                    settings.ItmEnabled = control == DisplaySettings.ControlItm;
                    _host.NotifySettingsChanged();
                }
            }

            ConfigApplied?.Invoke(this, EventArgs.Empty);
            Poll(force: true);
        }

        private void Reject_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _host == null)
                return;

            // CAS against the document this view projected from (expected identity).
            var expected = _config;
            bool reject = chkReject.IsChecked == true;
            var next = DisplayOverviewV2Model.WithRejectUncommanded(expected, reject);
            if (!_host.TryApplyDisplayConfigV2(expected, next))
            {
                SurfaceConflict();
                Poll(force: true);
                return;
            }

            _config = _host.GetDisplayConfigV2() ?? next;
            ClearConflict();
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            Poll(force: true);
        }

        private void SurfaceConflict()
        {
            if (bannerConflict == null || txtConflict == null)
                return;
            bannerConflict.Visibility = Visibility.Visible;
            txtConflict.Text = DisplayCopy.ConfigEditConflict;
        }

        private void ClearConflict()
        {
            if (bannerConflict == null)
                return;
            bannerConflict.Visibility = Visibility.Collapsed;
        }

        private void PagesAndFields_Click(object sender, RoutedEventArgs e)
            => PagesAndFieldsRequested?.Invoke(this, EventArgs.Empty);

        private void Priority_Click(object sender, RoutedEventArgs e)
            => PriorityRequested?.Invoke(this, EventArgs.Empty);

        private void ControlMapper_Click(object sender, RoutedEventArgs e)
            => ControlMapperRequested?.Invoke(this, EventArgs.Empty);

        private void Diagnostics_Click(object sender, RoutedEventArgs e)
            => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
    }
}
