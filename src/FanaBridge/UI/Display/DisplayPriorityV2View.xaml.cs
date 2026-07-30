using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    /// Priority ladder view (5b / 5j / 5n / 5f). Pure projection via
    /// <see cref="DisplayPriorityV2Model"/>; every write opens a
    /// <see cref="DisplayConfigV2EditSession"/>, mutates, and
    /// <c>TryApply</c>s — conflict surfaces <see cref="DisplayCopy.ConfigEditConflict"/>
    /// and re-opens against the fresh document.
    /// </summary>
    public partial class DisplayPriorityV2View : UserControl
    {
        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private IDisplayPickerStore _pickerStore;
        private WheelCatalog _catalog;
        private AliasTable _aliases;
        private DisplayPriorityV2Model _model;
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
        private readonly SevenSegmentFace _previewFace = new SevenSegmentFace();
        private string _pickerMode; // "idle" | "base"
        private PriorityPickerModel _activePicker;
        private PriorityPickerItemModel _expandedPlaylistItem;
        private string _epRowId;
        private string _epSummonId;
        private bool _epIsNew;
        private bool _epEnabled = true;
        private ValueSourceKind _epSourceKind = ValueSourceKind.SimHubProperty;
        private LifetimeKind _epLifetimeKind = LifetimeKind.WhileTrue;
        private int _epDurationMs = Lifetime.DefaultDurationMs;
        /// <summary>Q10: last Manual timer seconds shown — survives uncheck across Poll rebuilds.</summary>
        private int _rememberedManualSeconds = 30;
        private PriorityRowModel _dragRow;
        private Point _dragStart;
        private bool _dragging;
        /// <summary>Programmatic control updates must not clobber authored state.</summary>
        private bool _suppressEvents;
        /// <summary>
        /// Q6 destination liveness (host-owned). False → N1: drawn links disabled with
        /// SpokeArrivingLater; never cursor-only fakes or dead handlers.
        /// </summary>
        private bool _pagesAndFieldsDestinationLive;

        /// <summary>‹ Overview breadcrumb.</summary>
        public event EventHandler BackRequested;

        /// <summary>Raised after a successful session apply.</summary>
        public event EventHandler ConfigApplied;

        /// <summary>
        /// Navigation to Pages &amp; Fields (layer form / field form). Raised only when
        /// the host has declared the destination live via
        /// <see cref="SetPagesAndFieldsDestinationLive"/>.
        /// </summary>
        public event EventHandler PagesAndFieldsRequested;

        /// <summary>Surface B: + Add a page → 5h flow.</summary>
        public event EventHandler AddPageRequested;

        /// <summary>
        /// Host declares whether Pages &amp; Fields navigation is live. When false
        /// (N1 later phase), drawn links render disabled with
        /// <see cref="DisplayCopy.SpokeArrivingLater"/>. When true, clicks raise
        /// <see cref="PagesAndFieldsRequested"/>.
        /// </summary>
        internal void SetPagesAndFieldsDestinationLive(bool live)
            => _pagesAndFieldsDestinationLive = live;

        /// <summary>Test/host seam: resolved wheel catalog currently bound.</summary>
        internal WheelCatalog BoundCatalog => _catalog;

        /// <summary>
        /// Remove-all confirm seam. Defaults to the production MessageBox-backed
        /// dialog. Tests inject a recording handler (true = proceed, false = cancel).
        /// </summary>
        internal Func<DisplayConfigV2EditSession.PageContentRemovalPlan, bool> ConfirmRemoveAll;

        /// <summary>Page display name for the default remove-all confirm header.</summary>
        private string _removeAllPageName;

        public DisplayPriorityV2View()
        {
            InitializeComponent();
            hostPreviewFace.Content = _previewFace;
            ApplyStaticCopy();
            ConfirmRemoveAll = DefaultConfirmRemoveAll;
        }

        internal void Bind(
            IDisplayPanelHost host,
            WheelCatalog catalog = null,
            AliasTable aliases = null,
            IDisplayPropertyCatalog propertyCatalog = null,
            IMappedRoleCatalog roleCatalog = null,
            IDisplayPickerStore pickerStore = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _catalog = catalog;
            _aliases = aliases;
            _propertyCatalog = propertyCatalog;
            _roleCatalog = roleCatalog;
            _pickerStore = pickerStore;
            Poll(force: true);
        }

        internal void Poll(bool force = false)
        {
            if (_host == null)
                return;

            // A grip drag captures the mouse on a per-rebuild element; rebuilding
            // mid-drag would tear it out of the tree and kill the drop. Nothing the
            // poll paints can change under a held button anyway.
            if (_dragRow != null)
                return;

            // Same law for typing: a non-forced repaint must never yank the manual
            // seconds box / an open form control / an open overflow menu's anchor
            // out from under the user.
            if (!force
                && (popupEntrypoint.IsOpen
                    || popupPicker.IsOpen
                    || _overflowMenuOpen
                    || InlineEditGuard.IsEditingWithin(this)))
            {
                return;
            }

            var envelope = _host.Snapshot;
            var config = _host.GetDisplayConfigV2();
            var resolution = ProjectResolution(envelope);
            var values = envelope?.Values;

            // Digest §5: real next/prev mapping from SimHub's plugin-action bindings.
            bool nextMapped = false;
            bool prevMapped = false;
            if (_roleCatalog != null)
            {
                DisplayPriorityV2Model.ResolvePageControlMapping(
                    _roleCatalog.GetInputActionTargets(),
                    out nextMapped,
                    out prevMapped);
            }

            // Keep remembered Manual seconds in sync when the document has a live timer.
            if (config?.Priority?.Rows != null)
            {
                for (int i = 0; i < config.Priority.Rows.Count; i++)
                {
                    var r = config.Priority.Rows[i];
                    if (r != null
                        && r.Kind == PriorityRowKind.Manual
                        && r.ReturnToRestAfterMs != null
                        && r.ReturnToRestAfterMs.Value > 0)
                    {
                        _rememberedManualSeconds = Math.Max(
                            1, (r.ReturnToRestAfterMs.Value + 500) / 1000);
                        break;
                    }
                }
            }

            _model = DisplayPriorityV2Model.Project(
                config,
                resolution,
                values,
                _host.DisplayType,
                _catalog,
                _aliases,
                nextPageMapped: nextMapped,
                prevPageMapped: prevMapped,
                expandedRowIds: _expanded,
                rememberedManualSeconds: _rememberedManualSeconds);

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
            txtTitle.Text = DisplayCopy.Priority;
            txtDivider.Text = DisplayCopy.ModeProfileDivider;
            txtColRank.Text = DisplayCopy.ColRank;
            txtColPage.Text = DisplayCopy.ColPage;
            txtColEntrypoint.Text = DisplayCopy.ColEntrypoint;
            txtColRightNow.Text = DisplayCopy.ColRightNow;
            btnAddPage.Content = DisplayCopy.AddAPage;
            txtPreviewLabel.Text = DisplayCopy.TheSegmentsNow;

            txtEpWhen.Text = DisplayCopy.When;
            txtEpForHowLong.Text = DisplayCopy.ForHowLong;
            txtEpReads.Text = DisplayCopy.ThisEntrypointReads;
            txtEpAssembled.Text = DisplayCopy.AssembledFromBinding;
            txtEpWhere.Text = DisplayCopy.WhereItRanks;
            txtEpRankHint.Text = DisplayCopy.WhileRowAboveLiveWaits;
            txtEpPropertyHint.Text = DisplayCopy.PropertyRowHint;
            txtEpLiveBadge.Text = DisplayCopy.Live;
            txtEpUntilDismissedNote.Text = DisplayCopy.UntilDismissedConsequence
                + " " + DisplayCopy.MapControlOrTimedHold;
            btnEpDelete.Content = DisplayCopy.Delete;
            btnEpCancel.Content = DisplayCopy.Cancel;
            btnEpSave.Content = DisplayCopy.Save;

            // 5f: three-segment source control (SegmentedControl idiom, not ComboBox).
            if (segEpSourceKind != null)
            {
                segEpSourceKind.SegmentPadding = new Thickness(10, 5, 10, 5);
                segEpSourceKind.SegmentFontSize = 11.5;
                segEpSourceKind.OuterCornerRadius = 4;
                segEpSourceKind.SetItems(new (string, string)[]
                {
                    ("itm", DisplayCopy.SourceItmField),
                    ("simhub", DisplayCopy.SourceSimHubProperty),
                    ("script", DisplayCopy.SourceScript),
                });
                segEpSourceKind.SelectedId = "simhub";
                segEpSourceKind.SelectionChanged -= EpSourceKind_SegChanged;
                segEpSourceKind.SelectionChanged += EpSourceKind_SegChanged;
            }

            cmbEpOperator.Items.Clear();
            cmbEpOperator.Items.Add(DisplayCopy.OpBelow);
            cmbEpOperator.Items.Add(DisplayCopy.OpAtOrBelow);
            cmbEpOperator.Items.Add(DisplayCopy.OpAbove);
            cmbEpOperator.Items.Add(DisplayCopy.OpAtOrAbove);
            cmbEpOperator.Items.Add(DisplayCopy.OpEquals);
            cmbEpOperator.Items.Add(DisplayCopy.OpNotEquals);
            cmbEpOperator.Items.Add(DisplayCopy.OpIsOn);
            cmbEpOperator.Items.Add(DisplayCopy.OpIsOff);
            cmbEpOperator.SelectedIndex = 0;

            if (txtEpUnit != null)
                txtEpUnit.ToolTip = DisplayCopy.ConditionUnitTooltip;
        }

        private void ApplyModel(DisplayPriorityV2Model model, DisplayValuesSnapshot values)
        {
            if (model == null) return;

            txtSurfaceWord.Text = model.SurfaceWord;
            txtSituation.Text = model.SituationCopy;
            dotSituation.Fill = model.InGame
                ? new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5C));

            txtLadderHeader.Text = model.LadderHeader;
            txtLadderSubtitle.Text = model.LadderSubtitle;

            btnAddPage.IsEnabled = model.AddPageEnabled;
            btnAddPage.ToolTip = model.AddPageTooltip;
            ToolTipService.SetShowOnDisabled(btnAddPage, true);

            colPageHdr.Width = new GridLength(model.PageColWidth);
            colStatusHdr.Width = new GridLength(model.StatusColWidth);

            bool showLadder = model.ShowLadder;
            panelLadder.Visibility = showLadder ? Visibility.Visible : Visibility.Collapsed;
            txtModeOffEmpty.Text = model.ModeOffEmptyState ?? string.Empty;
            txtModeOffEmpty.Visibility = showLadder ? Visibility.Collapsed : Visibility.Visible;

            if (model.ShowSegmentPreview)
            {
                colPreview.Width = new GridLength(250);
                panelPreview.Visibility = Visibility.Visible;
                txtPreviewCaption.Text = model.PreviewCaption ?? string.Empty;
                // Live face bytes are not on DisplayValuesSnapshot; blank frame keeps the
                // column structure (5j). Full segment paint lands with a later values path.
                _previewFace.Render(null);
            }
            else
            {
                colPreview.Width = new GridLength(0);
                panelPreview.Visibility = Visibility.Collapsed;
            }

            listExplainers.ItemsSource = model.Explainers;
            RebuildRows(model);
        }

        private void RebuildRows(DisplayPriorityV2Model model)
        {
            listRows.Items.Clear();
            for (int i = 0; i < model.Rows.Count; i++)
                listRows.Items.Add(BuildRowVisual(model.Rows[i], model));
        }

        private FrameworkElement BuildRowVisual(PriorityRowModel row, DisplayPriorityV2Model model)
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 0, 4),
                Tag = row,
            };

            // Row palette per digest §2 (Normal #313133 — differs from Overview).
            switch (row.State)
            {
                case PriorityRowState.Winner:
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x2F, 0x3D));
                    outer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0xA0, 0xDD));
                    outer.BorderThickness = new Thickness(3, 0, 0, 0);
                    break;
                case PriorityRowState.Off:
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2C));
                    break;
                case PriorityRowState.Pinned:
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x25));
                    outer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47));
                    outer.BorderThickness = new Thickness(1);
                    outer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47));
                    // Dashed: approximate with solid muted border (WPF Border has no DashArray).
                    outer.Margin = new Thickness(0, row.IsIdleRow ? 3 : 4, 0, 4);
                    break;
                default:
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x31, 0x31, 0x33));
                    break;
            }

            if (row.IsExpanded && !row.IsPinned)
            {
                // Expanded wrapper palette.
                if (row.State == PriorityRowState.Winner)
                {
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x2F, 0x3D));
                    outer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0xA0, 0xDD));
                    outer.BorderThickness = new Thickness(1);
                }
                else if (row.IsManual)
                {
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2D));
                    outer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47));
                    outer.BorderThickness = new Thickness(1);
                }
                else
                {
                    outer.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x38));
                    outer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x60));
                    outer.BorderThickness = new Thickness(1);
                }
            }

            var stack = new StackPanel();
            var header = BuildHeaderGrid(row, model);
            stack.Children.Add(header);

            if (row.HasExpansionBody || (row.IsExpanded && row.IsManual))
                stack.Children.Add(BuildExpansionBody(row, model));

            outer.Child = stack;

            // Q4: header click toggles expansion (seats + manual). Header only —
            // clicks inside the expansion body must never collapse the row.
            if (!row.IsPinned || row.IsManual)
            {
                header.Background = Brushes.Transparent;
                header.MouseLeftButtonUp += (s, e) =>
                {
                    if (_dragging) return;
                    if (e.OriginalSource is Button || e.OriginalSource is CheckBox
                        || e.OriginalSource is TextBox || e.OriginalSource is ComboBox)
                        return;
                    ToggleExpand(row);
                    e.Handled = true;
                };
            }

            return outer;
        }

        private Grid BuildHeaderGrid(PriorityRowModel row, DisplayPriorityV2Model model)
        {
            var grid = new Grid { Margin = new Thickness(12, 9, 12, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(model.PageColWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(model.StatusColWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

            // Grip
            var grip = new TextBlock
            {
                Text = row.ShowGrip ? DisplayCopy.GripGlyph : string.Empty,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = row.State == PriorityRowState.Off
                    ? new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4C))
                    : new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = row.ShowGrip ? Cursors.SizeAll : Cursors.Arrow,
            };
            if (row.ShowGrip)
            {
                grip.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    _dragRow = row;
                    _dragStart = e.GetPosition(this);
                    _dragging = false;
                    ((UIElement)s).CaptureMouse();
                    e.Handled = true;
                };
                grip.PreviewMouseMove += (s, e) =>
                {
                    if (_dragRow == null || e.LeftButton != MouseButtonState.Pressed)
                        return;
                    var pos = e.GetPosition(this);
                    if (!_dragging
                        && (Math.Abs(pos.Y - _dragStart.Y) > 4 || Math.Abs(pos.X - _dragStart.X) > 4))
                        _dragging = true;
                };
                grip.PreviewMouseLeftButtonUp += (s, e) =>
                {
                    // Snapshot BEFORE releasing capture: ReleaseMouseCapture raises
                    // LostMouseCapture synchronously and that handler clears the
                    // drag state (it must, for torn-away captures).
                    var dropRow = _dragRow;
                    bool wasDragging = _dragging;
                    _dragRow = null;
                    _dragging = false;
                    ((UIElement)s).ReleaseMouseCapture();
                    if (wasDragging && dropRow != null)
                    {
                        // Drop: find nearest ranked row under cursor and reorder.
                        TryDropReorder(dropRow, e.GetPosition(listRows));
                    }
                    e.Handled = true;
                };
                // Capture can be torn away (window switch, tree change) without a
                // mouse-up — never leave a drag latched, it would swallow row clicks
                // and freeze the poll.
                grip.LostMouseCapture += (s, e) =>
                {
                    _dragRow = null;
                    _dragging = false;
                };
            }
            Grid.SetColumn(grip, 0);
            grid.Children.Add(grip);

            // Rank
            var rank = new TextBlock
            {
                Text = row.RankText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = row.State == PriorityRowState.Off
                    ? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5C))
                    : new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 1);
            grid.Children.Add(rank);

            // Page cell
            var pagePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = row.State == PriorityRowState.Off ? 0.55 : 1.0,
            };
            // Q4: 11px disclosure slot — ▼ only when expanded.
            pagePanel.Children.Add(new TextBlock
            {
                Text = row.ShowDisclosure ? DisplayCopy.ExpandedGlyph : string.Empty,
                Width = 11,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            for (int b = 0; b < row.Destination.Badges.Count; b++)
            {
                pagePanel.Children.Add(new TextBlock
                {
                    Text = row.Destination.Badges[b],
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = row.Destination.IsLegacy
                        ? new SolidColorBrush(Color.FromRgb(0xC9, 0xC4, 0xBA))
                        : new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            pagePanel.Children.Add(new TextBlock
            {
                Text = row.Destination.Name,
                FontSize = 12.5,
                FontWeight = row.IsPinned ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            // OWNER-WAIVED FIDELITY (Surface C / D19): › reference marker + child name.
            if (row.IsSatellite && !string.IsNullOrEmpty(row.SplitReferenceName))
            {
                pagePanel.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.SplitRowFromMarker,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                pagePanel.Children.Add(new TextBlock
                {
                    Text = row.SplitReferenceName,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            Grid.SetColumn(pagePanel, 2);
            grid.Children.Add(pagePanel);

            // Detail / idle editor
            FrameworkElement detailEl;
            if (row.IsIdleRow)
            {
                var idlePanel = new StackPanel { Orientation = Orientation.Horizontal };
                idlePanel.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.IdleTargetPrefix,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var combo = new Button
                {
                    Content = row.IdleTargetLabel ?? string.Empty,
                    Padding = new Thickness(8, 3, 8, 3),
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x56)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    Tag = row,
                };
                combo.Click += (s, e) =>
                {
                    OpenPicker("idle", model.IdlePicker);
                    e.Handled = true;
                };
                idlePanel.Children.Add(combo);
                if (row.ShowPlaylistBadge)
                {
                    idlePanel.Children.Add(new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x56)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(8, 0, 0, 0),
                        Child = new TextBlock
                        {
                            Text = DisplayCopy.PlaylistBadge,
                            FontSize = 10,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                        },
                    });
                }
                else if (!string.IsNullOrEmpty(row.IdleTrailingNote))
                {
                    idlePanel.Children.Add(new TextBlock
                    {
                        Text = row.IdleTrailingNote,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                detailEl = idlePanel;
            }
            else
            {
                detailEl = new TextBlock
                {
                    Text = row.Detail,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            Grid.SetColumn(detailEl, 3);
            grid.Children.Add(detailEl);

            // Status
            FrameworkElement statusEl;
            if (row.IsOutlinedStatusChip)
            {
                statusEl = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6C)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock
                    {
                        Text = row.StatusCopy,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                    },
                };
            }
            else
            {
                statusEl = new TextBlock
                {
                    Text = row.StatusCopy,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            Grid.SetColumn(statusEl, 4);
            grid.Children.Add(statusEl);

            // Overflow ⋯
            var overflow = new Button
            {
                Content = DisplayCopy.OverflowGlyph,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = row.State == PriorityRowState.Winner
                    ? new SolidColorBrush(Color.FromRgb(0x7F, 0x9A, 0xB0))
                    : new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Visible,
                Tag = row,
            };
            if (row.ShowOverflowMenu)
            {
                overflow.Click += (s, e) =>
                {
                    OpenOverflowMenu(row, (Button)s);
                    e.Handled = true;
                };
            }
            else
            {
                // Q3: glyph drawn but opens nothing (Manual / idle).
                overflow.IsEnabled = false;
                overflow.Opacity = 0.55;
            }
            Grid.SetColumn(overflow, 5);
            grid.Children.Add(overflow);

            return grid;
        }

        private FrameworkElement BuildExpansionBody(PriorityRowModel row, DisplayPriorityV2Model model)
        {
            var body = new StackPanel { Margin = new Thickness(64, 2, 12, 12) };

            if (row.IsSeat)
            {
                // ENTRYPOINTS
                body.Children.Add(SectionHeader(
                    DisplayCopy.EntrypointsSection,
                    DisplayCopy.EntrypointsSectionHint(row.RankNumber)));
                for (int i = 0; i < row.Entrypoints.Count; i++)
                {
                    body.Children.Add(ChildRowVisual(
                        row.Entrypoints[i], wideStatus: true, entrypointOwner: row));
                }

                var addEp = new Button
                {
                    Content = DisplayCopy.AddAnEntrypoint,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0, 6, 0, 6),
                    Cursor = Cursors.Hand,
                    Tag = row,
                };
                addEp.Click += (s, e) =>
                {
                    OpenEntrypointForm(row, summonId: null, isNew: true);
                    e.Handled = true;
                };
                body.Children.Add(addEp);

                // OVERRIDES (ITM seats) — Q6: link navigates only when destination is live.
                if (row.Overrides.Count > 0 || (row.Target != null && row.Target.Kind == PageRefKind.ItmPage))
                {
                    bool fieldNavLive = _pagesAndFieldsDestinationLive;
                    body.Children.Add(SectionHeader(
                        DisplayCopy.OverridesSection,
                        DisplayCopy.OverridesReadOnlyHint,
                        linkText: DisplayCopy.EditThemOnTheField,
                        linkAction: fieldNavLive ? NavigateToPagesAndFields : null,
                        linkEnabled: fieldNavLive,
                        linkDisabledTooltip: DisplayCopy.SpokeArrivingLater(DisplayCopy.PagesAndFields)));
                    for (int i = 0; i < row.Overrides.Count; i++)
                        body.Children.Add(ChildRowVisual(row.Overrides[i], overrideLayout: true));
                }

                // LAYERS (hosted seats — 5j) — Q6: whole-row click only when destination live.
                if (row.Layers.Count > 0)
                {
                    bool pageNavLive = _pagesAndFieldsDestinationLive;
                    body.Children.Add(SectionHeader(
                        DisplayCopy.LayersSection,
                        DisplayCopy.LayersReadOnlyHint,
                        linkText: DisplayCopy.EditThemOnThePage,
                        linkAction: pageNavLive ? NavigateToPagesAndFields : null,
                        linkEnabled: pageNavLive,
                        linkDisabledTooltip: DisplayCopy.SpokeArrivingLater(DisplayCopy.PagesAndFields)));
                    for (int i = 0; i < row.Layers.Count; i++)
                        body.Children.Add(ChildRowVisual(row.Layers[i], layerLayout: true));
                }

                if (row.ShowBaseBlock)
                {
                    var baseBlock = new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 6, 0, 0),
                        Child = new StackPanel
                        {
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = DisplayCopy.BaseBlockLabel,
                                    FontSize = 9.5,
                                    FontFamily = new FontFamily("Consolas"),
                                    Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                                },
                                new TextBlock
                                {
                                    Text = row.BaseBlockBody ?? DisplayCopy.BaseBlockBlank,
                                    FontSize = 12,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                                    Margin = new Thickness(0, 4, 0, 0),
                                    TextWrapping = TextWrapping.Wrap,
                                },
                            },
                        },
                    };
                    body.Children.Add(baseBlock);
                }
            }

            if (row.IsManual && row.ManualOptions != null)
            {
                var opts = row.ManualOptions;
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
                var chk = new CheckBox
                {
                    IsChecked = opts.ReturnEnabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                var secondsBox = new TextBox
                {
                    Text = opts.ShownSeconds.ToString(CultureInfo.InvariantCulture),
                    Width = 40,
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(6, 0, 6, 0),
                    IsEnabled = opts.ReturnEnabled,
                    Opacity = opts.ReturnEnabled ? 1.0 : 0.55,
                    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x56)),
                };
                // Q10: uncheck keeps last value shown greyed across Poll rebuilds
                // (field-level _rememberedManualSeconds); check writes shown value.
                chk.Checked += (s, e) =>
                {
                    int sec = _rememberedManualSeconds;
                    if (int.TryParse(secondsBox.Text, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                        sec = parsed;
                    _rememberedManualSeconds = sec;
                    ApplyEdit(session => session.SetReturnToRestAfterMs(sec * 1000));
                };
                chk.Unchecked += (s, e) =>
                {
                    if (int.TryParse(secondsBox.Text, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                        _rememberedManualSeconds = parsed;
                    ApplyEdit(session => session.SetReturnToRestAfterMs(null));
                };
                secondsBox.LostFocus += (s, e) =>
                {
                    if (!int.TryParse(secondsBox.Text, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int sec) || sec <= 0)
                        return;
                    _rememberedManualSeconds = sec;
                    if (chk.IsChecked != true) return;
                    ApplyEdit(session => session.SetReturnToRestAfterMs(sec * 1000));
                };
                rowPanel.Children.Add(chk);
                rowPanel.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.ReturnToBaseAfter,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                rowPanel.Children.Add(secondsBox);
                rowPanel.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.SecondsOfNoInput,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                rowPanel.Children.Add(new TextBlock
                {
                    Text = " " + DisplayCopy.CountedFromLastPress,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                body.Children.Add(rowPanel);
                body.Children.Add(new TextBlock
                {
                    Text = opts.Consequence,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                });
                if (opts.ShowUnmappedAmber)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = DisplayCopy.ManualUnmappedAmber,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x5F)),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }

            return body;
        }

        private FrameworkElement SectionHeader(
            string label,
            string hint,
            string linkText = null,
            Action linkAction = null,
            bool linkEnabled = true,
            string linkDisabledTooltip = null)
        {
            var tb = new TextBlock { Margin = new Thickness(0, 8, 0, 4) };
            tb.Inlines.Add(new System.Windows.Documents.Run(label)
            {
                FontSize = 9.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
            });
            if (!string.IsNullOrEmpty(hint))
            {
                // When a link fragment is drawn inside the hint, render the non-link
                // remainder as plain text and the link as a Hyperlink.
                if (!string.IsNullOrEmpty(linkText)
                    && hint.IndexOf(linkText, StringComparison.Ordinal) >= 0)
                {
                    int idx = hint.IndexOf(linkText, StringComparison.Ordinal);
                    string before = hint.Substring(0, idx);
                    string after = hint.Substring(idx + linkText.Length);
                    if (before.Length > 0)
                    {
                        tb.Inlines.Add(new System.Windows.Documents.Run("  " + before)
                        {
                            FontSize = 11.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                        });
                    }
                    else
                    {
                        tb.Inlines.Add(new System.Windows.Documents.Run("  ")
                        {
                            FontSize = 11.5,
                        });
                    }

                    if (linkEnabled && linkAction != null)
                    {
                        var link = new System.Windows.Documents.Hyperlink(
                            new System.Windows.Documents.Run(linkText))
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0xB0, 0xE0)),
                            TextDecorations = null,
                        };
                        link.Click += (s, e) =>
                        {
                            linkAction();
                            e.Handled = true;
                        };
                        tb.Inlines.Add(link);
                    }
                    else
                    {
                        var disabled = new System.Windows.Documents.Run(linkText)
                        {
                            FontSize = 11.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
                            ToolTip = linkDisabledTooltip
                                ?? DisplayCopy.SpokeArrivingLater(DisplayCopy.PagesAndFields),
                        };
                        tb.Inlines.Add(disabled);
                    }

                    if (after.Length > 0)
                    {
                        tb.Inlines.Add(new System.Windows.Documents.Run(after)
                        {
                            FontSize = 11.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                        });
                    }
                }
                else
                {
                    tb.Inlines.Add(new System.Windows.Documents.Run("  " + hint)
                    {
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                    });
                }
            }
            return tb;
        }

        private void NavigateToPagesAndFields()
        {
            // Only raise when the host declared the destination live — never a dead invoke.
            if (!_pagesAndFieldsDestinationLive)
                return;
            PagesAndFieldsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Test seam for Q6 destination gating.</summary>
        internal void NavigateToPagesAndFieldsForTest() => NavigateToPagesAndFields();

        private FrameworkElement ChildRowVisual(
            PriorityChildRowModel child,
            bool wideStatus = false,
            bool overrideLayout = false,
            bool layerLayout = false,
            PriorityRowModel entrypointOwner = null)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x31)),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                Tag = child,
            };
            var grid = new Grid();
            if (overrideLayout)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                grid.Children.Add(Cell(child.ChipLabel ?? string.Empty, 0, mono: true));
                var writes = new StackPanel { Orientation = Orientation.Horizontal };
                if (!string.IsNullOrEmpty(child.WritesLabel))
                {
                    writes.Children.Add(new TextBlock
                    {
                        Text = child.WritesLabel,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                        Margin = new Thickness(0, 0, 6, 0),
                    });
                }
                // P7: glyph only
                if (child.ActsAsEntrypoint)
                {
                    writes.Children.Add(new TextBlock
                    {
                        Text = DisplayCopy.EntrypointGlyph,
                        ToolTip = DisplayCopy.EntrypointTooltip,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    });
                }
                Grid.SetColumn(writes, 1);
                grid.Children.Add(writes);
                grid.Children.Add(Cell(child.Label, 2));
                grid.Children.Add(Cell(child.StatusCopy, 3, right: true));
            }
            else if (layerLayout)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                chip.Children.Add(new TextBlock
                {
                    Text = child.ChipLabel ?? DisplayCopy.LayerChip,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    Margin = new Thickness(0, 0, 6, 0),
                });
                if (!string.IsNullOrEmpty(child.WritesLabel))
                {
                    chip.Children.Add(new Border
                    {
                        Background = Brushes.Black,
                        Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock
                        {
                            Text = child.WritesLabel,
                            FontSize = 10,
                            Foreground = Brushes.White,
                        },
                    });
                }
                Grid.SetColumn(chip, 0);
                grid.Children.Add(chip);
                grid.Children.Add(Cell(child.Label, 1));
                var glyph = new TextBlock
                {
                    Text = child.EntrypointGlyph,
                    ToolTip = child.ActsAsEntrypoint ? DisplayCopy.EntrypointTooltip : null,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                };
                Grid.SetColumn(glyph, 2);
                grid.Children.Add(glyph);
                // Q6: whole-row click only when destination is live (8b item 9).
                // Drawn-but-undestined → N1 disabled w/ SpokeArrivingLater (not cursor fake).
                if (child.IsClickable && _pagesAndFieldsDestinationLive)
                {
                    border.Cursor = Cursors.Hand;
                    border.ToolTip = DisplayCopy.OpenThisLayersForm;
                    border.MouseLeftButtonUp += (s, e) =>
                    {
                        NavigateToPagesAndFields();
                        e.Handled = true;
                    };
                }
                else if (child.IsClickable)
                {
                    border.Opacity = 0.7;
                    border.ToolTip = DisplayCopy.SpokeArrivingLater(DisplayCopy.PagesAndFields);
                    ToolTipService.SetShowOnDisabled(border, true);
                }
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                grid.Children.Add(Cell(child.Label, 0));
                grid.Children.Add(Cell(child.StatusCopy, 1, right: true));
            }

            // Entrypoint rows open their own 5f form — the row is the edit
            // affordance (Add and the overflow menu only create new ones).
            if (entrypointOwner != null && !string.IsNullOrEmpty(child.Id))
            {
                border.Cursor = Cursors.Hand;
                border.ToolTip = DisplayCopy.OpenThisEntrypointsForm;
                border.MouseLeftButtonUp += (s, e) =>
                {
                    OpenEntrypointForm(entrypointOwner, child.Id, isNew: false);
                    e.Handled = true;
                };
            }

            border.Child = grid;
            return border;
        }

        private static TextBlock Cell(string text, int col, bool mono = false, bool right = false)
        {
            var tb = new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = mono ? 11 : 12,
                FontFamily = mono ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(tb, col);
            return tb;
        }

        private void ToggleExpand(PriorityRowModel row)
        {
            if (row == null || string.IsNullOrEmpty(row.RowId))
                return;
            if (!_expanded.Add(row.RowId))
                _expanded.Remove(row.RowId);
            Poll(force: true);
        }

        /// <summary>An overflow ContextMenu is open — its popup root is outside the
        /// view's trees, so the typing guard can't see it; polls must not rebuild
        /// the anchor button out from under it.</summary>
        private bool _overflowMenuOpen;

        private void OpenOverflowMenu(PriorityRowModel row, Button anchor)
        {
            var menu = new ContextMenu();
            menu.Opened += (s, e) => _overflowMenuOpen = true;
            menu.Closed += (s, e) => _overflowMenuOpen = false;

            if (row.IsBaseRow)
            {
                // UNBOARDED (owner ruling #2): Choose the Base page…
                var choose = new MenuItem { Header = DisplayCopy.ChooseTheBasePage };
                choose.Click += (s, e) => OpenPicker("base", _model?.BasePagePicker);
                menu.Items.Add(choose);
                menu.PlacementTarget = anchor;
                menu.IsOpen = true;
                return;
            }

            if (!row.IsSeat)
                return;

            // OWNER-WAIVED FIDELITY (Surface C / D19): satellite menu is rejoin-only.
            if (row.IsSatellite)
            {
                var rejoin = new MenuItem
                {
                    Header = DisplayCopy.RejoinTheHomeRow,
                    IsEnabled = row.CanRejoinHome,
                };
                string satId = row.RowId;
                rejoin.Click += (s, e) =>
                    ApplyEdit(session => session.MergeSatellite(satId));
                menu.Items.Add(rejoin);
                menu.PlacementTarget = anchor;
                menu.IsOpen = true;
                return;
            }

            // Reorder fallback that survives any rebuild — each click is a complete
            // gesture through the same tested core the drag path uses.
            int rankedIdx = -1;
            int rankedCount = 0;
            if (_model != null)
            {
                for (int i = 0; i < _model.Rows.Count; i++)
                {
                    var r = _model.Rows[i];
                    if (r == null || r.IsPinned)
                        continue;
                    if (string.Equals(r.RowId, row.RowId, StringComparison.Ordinal))
                        rankedIdx = rankedCount;
                    rankedCount++;
                }
            }
            string moveRowId = row.RowId;
            var moveUp = new MenuItem
            {
                Header = DisplayCopy.MoveUpTheLadder,
                IsEnabled = rankedIdx > 0,
            };
            int upTarget = rankedIdx - 1;
            moveUp.Click += (s, e) => ReorderCore(moveRowId, upTarget);
            menu.Items.Add(moveUp);
            var moveDown = new MenuItem
            {
                Header = DisplayCopy.MoveDownTheLadder,
                IsEnabled = rankedIdx >= 0 && rankedIdx < rankedCount - 1,
            };
            int downTarget = rankedIdx + 1;
            moveDown.Click += (s, e) => ReorderCore(moveRowId, downTarget);
            menu.Items.Add(moveDown);
            menu.Items.Add(new Separator());

            // Q6 / N1: overflow fields item follows the same destination-live rule as
            // section links and layer rows — never a dead handler while undestined.
            var editFields = new MenuItem { Header = DisplayCopy.EditThisPagesFields };
            if (_pagesAndFieldsDestinationLive)
            {
                editFields.Click += (s, e) => NavigateToPagesAndFields();
            }
            else
            {
                editFields.IsEnabled = false;
                editFields.ToolTip = DisplayCopy.SpokeArrivingLater(DisplayCopy.PagesAndFields);
                ToolTipService.SetShowOnDisabled(editFields, true);
            }
            menu.Items.Add(editFields);

            var addEp = new MenuItem { Header = DisplayCopy.AddAnEntrypointMenu };
            addEp.Click += (s, e) => OpenEntrypointForm(row, null, isNew: true);
            menu.Items.Add(addEp);

            // OWNER-WAIVED FIDELITY (Surface C / D19 / C-O2): split when 2+ summons.
            if (row.CanSplitEntrypoint && row.SplitSummons.Count >= 2)
            {
                var split = new MenuItem
                {
                    Header = DisplayCopy.GiveThisEntrypointItsOwnPriority,
                };
                string splitRowId = row.RowId;
                for (int i = 0; i < row.SplitSummons.Count; i++)
                {
                    var choice = row.SplitSummons[i];
                    var splitChoice = new MenuItem
                    {
                        Header = DisplayCopy.SplitSummonChoice(
                            choice.Label, choice.IsEnabled),
                    };
                    string splitSummonId = choice.SummonId;
                    splitChoice.Click += (s, e) =>
                        SplitSummonCore(splitRowId, splitSummonId);
                    split.Items.Add(splitChoice);
                }
                menu.Items.Add(split);
            }

            if (!string.IsNullOrEmpty(row.PrimarySummonId))
            {
                var toggle = new MenuItem
                {
                    Header = row.PrimarySummonEnabled
                        ? DisplayCopy.TurnThisEntrypointOff
                        : DisplayCopy.TurnThisEntrypointOn,
                };
                bool enable = !row.PrimarySummonEnabled;
                string summonId = row.PrimarySummonId;
                string rowId = row.RowId;
                toggle.Click += (s, e) =>
                    ApplyEdit(session => session.SetSummonEnabled(rowId, summonId, enable));
                menu.Items.Add(toggle);
            }

            menu.Items.Add(new Separator());

            // Owner ruling: TWO distinct removal options. Counts are recomputed from
            // the session's fresh document at confirm time (stale-count impossible).
            var target = row.Target;
            string pageName = row.PageName;

            var removeRows = new MenuItem
            {
                Header = DisplayCopy.RemovePageRowsOnly(pageName),
            };
            removeRows.Click += (s, e) => ConfirmAndRemoveRows(target, pageName);
            menu.Items.Add(removeRows);

            // Fail-closed: destructive option disabled without a resolvable catalog.
            bool canDestroy = DisplayConfigV2EditSession.CanRemovePageContent(target, _catalog);
            var removeAll = new MenuItem
            {
                Header = DisplayCopy.RemovePageAndOverrides(pageName),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0x8B, 0x8B)),
                IsEnabled = canDestroy,
                ToolTip = canDestroy
                    ? null
                    : DisplayCopy.RemovePageAndOverridesUnavailable,
            };
            ToolTipService.SetShowOnDisabled(removeAll, true);
            if (canDestroy)
                removeAll.Click += (s, e) => ConfirmAndRemoveAll(target, pageName);
            menu.Items.Add(removeAll);

            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
        }

        /// <summary>Split the specifically chosen authored summon through the edit session.</summary>
        internal void SplitSummonCore(string rowId, string summonId)
        {
            ApplyEdit(session => session.SplitSatellite(rowId, summonId));
        }

        private void OpenPicker(string mode, PriorityPickerModel picker)
        {
            if (picker == null) return;
            _pickerMode = mode;
            _activePicker = picker;
            txtPickerSearch.Text = string.Empty;
            txtPickerSearch.Tag = picker.SearchPlaceholder;
            if (txtPickerSearch.Template == null)
            {
                // Placeholder via empty text is fine; set tooltip as fallback.
                txtPickerSearch.ToolTip = picker.SearchPlaceholder;
            }
            txtPickerFooter.Text = picker.Footer ?? string.Empty;
            txtPickerFooter.Visibility = string.IsNullOrEmpty(picker.Footer)
                ? Visibility.Collapsed : Visibility.Visible;
            _expandedPlaylistItem = null;
            panelPlaylistCard.Visibility = Visibility.Collapsed;
            panelPlaylistCardBody.Children.Clear();
            ApplyPickerFilter(string.Empty);
            popupPicker.IsOpen = true;
        }

        /// <summary>STA test/host seam for the production idle picker bring-up.</summary>
        internal bool OpenIdlePickerCore()
        {
            if (_model?.IdlePicker == null)
                return false;
            OpenPicker("idle", _model.IdlePicker);
            return true;
        }

        internal PriorityPickerModel ActivePickerForTest => _activePicker;
        internal bool PlaylistInspectionExpandedForTest => _expandedPlaylistItem != null;

        private void RenderPlaylistCard(string playlistId)
        {
            if (panelPlaylistCard == null || panelPlaylistCardBody == null)
                return;

            var card = DisplayPriorityV2Model.ProjectPlaylistCard(
                _host?.GetDisplayConfigV2(), playlistId, _catalog);
            if (card == null)
            {
                panelPlaylistCard.Visibility = Visibility.Collapsed;
                panelPlaylistCardBody.Children.Clear();
                return;
            }

            panelPlaylistCardBody.Children.Clear();

            // Header: PLAYLIST badge + name + READ-ONLY chip
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var chip = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6C)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = card.ReadOnlyChip,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                },
            };
            DockPanel.SetDock(chip, Dock.Right);
            header.Children.Add(chip);
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = card.Badge,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = card.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(titleRow);
            panelPlaylistCardBody.Children.Add(header);

            // STEPS
            panelPlaylistCardBody.Children.Add(new TextBlock
            {
                Text = card.StepsLabel,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)),
                Margin = new Thickness(0, 0, 0, 2),
            });
            panelPlaylistCardBody.Children.Add(new TextBlock
            {
                Text = card.StepsCaption,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                Margin = new Thickness(0, 0, 0, 6),
            });

            for (int i = 0; i < card.Steps.Count; i++)
            {
                var step = card.Steps[i];
                var stepGrid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                stepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                stepGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                stepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                stepGrid.Children.Add(Cell(step.Numeral, 0, mono: true));
                stepGrid.Children.Add(Cell(step.DestinationName, 1));
                stepGrid.Children.Add(Cell(step.DurationLabel, 2, mono: true, right: true));
                panelPlaylistCardBody.Children.Add(stepGrid);
            }

            // Amber provenance
            if (!string.IsNullOrEmpty(card.Provenance))
            {
                panelPlaylistCardBody.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x32)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 4, 0, 4),
                    Margin = new Thickness(0, 8, 0, 4),
                    Child = new TextBlock
                    {
                        Text = card.Provenance,
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x5F)),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }

            if (!string.IsNullOrEmpty(card.UsedByLine))
            {
                panelPlaylistCardBody.Children.Add(new TextBlock
                {
                    Text = card.UsedByLine,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 6),
                });
            }

            var reRun = new Button
            {
                Content = card.ReRunLabel,
                IsEnabled = card.ReRunEnabled,
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x56)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                FontSize = 12,
                ToolTip = card.ReRunTooltip,
            };
            ToolTipService.SetShowOnDisabled(reRun, true);
            panelPlaylistCardBody.Children.Add(reRun);

            var confirm = new Button
            {
                Content = DisplayCopy.UseThisPlaylist,
                Tag = _expandedPlaylistItem,
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x7F, 0xAD)),
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            };
            confirm.Click += PlaylistConfirm_Click;
            panelPlaylistCardBody.Children.Add(confirm);

            panelPlaylistCard.Visibility = Visibility.Visible;
        }

        private void ApplyPickerFilter(string query)
        {
            if (_activePicker == null) return;
            query = (query ?? string.Empty).Trim();
            var filtered = new List<PriorityPickerGroupModel>();
            for (int g = 0; g < _activePicker.Groups.Count; g++)
            {
                var group = _activePicker.Groups[g];
                var items = new List<PriorityPickerItemModel>();
                for (int i = 0; i < group.Items.Count; i++)
                {
                    var it = group.Items[i];
                    if (!it.IsEnabled && it.CapabilityNote == null && string.IsNullOrEmpty(query))
                    {
                        // still show greyed unsupported
                    }
                    if (query.Length > 0
                        && (it.Name == null
                            || it.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                        && (it.Badge == null
                            || it.Badge.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    items.Add(it);
                }
                if (items.Count > 0 || query.Length == 0)
                {
                    filtered.Add(new PriorityPickerGroupModel(
                        group.Header,
                        new System.Collections.ObjectModel.ReadOnlyCollection<PriorityPickerItemModel>(items),
                        emptyState: query.Length == 0 ? group.EmptyState : null));
                }
            }
            listPickerGroups.ItemsSource = filtered;
        }

        private void PickerSearch_Changed(object sender, TextChangedEventArgs e)
            => ApplyPickerFilter(txtPickerSearch.Text);

        private void PickerItem_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as FrameworkElement;
            var item = border?.Tag as PriorityPickerItemModel
                ?? (border as Border)?.Tag as PriorityPickerItemModel;
            // Walk up for Tag
            if (item == null && sender is FrameworkElement fe)
            {
                var cur = fe;
                while (cur != null && item == null)
                {
                    item = cur.Tag as PriorityPickerItemModel;
                    cur = VisualTreeHelper.GetParent(cur) as FrameworkElement;
                }
            }
            // DataContext path
            if (item == null && sender is FrameworkElement fe2)
                item = fe2.DataContext as PriorityPickerItemModel;
            if (item == null || !item.IsEnabled)
                return;

            // Surface D: the row click expands read-only detail only. The card's distinct
            // confirmation action is the sole write path for a playlist target.
            if (item.IdleKind == IdleKind.Playlist && !string.IsNullOrEmpty(item.PlaylistId))
            {
                if (!ExpandPlaylistPickerItemCore(item))
                    return;
                AttachPlaylistCardToPickerRow(border);
                RenderPlaylistCard(item.PlaylistId);
                popupPicker.IsOpen = true;
                e.Handled = true;
                return;
            }

            if (string.Equals(_pickerMode, "base", StringComparison.Ordinal))
            {
                if (item.PageRef == null) return;
                ApplyEdit(session => session.SetInSessionPage(item.PageRef));
            }
            else
            {
                var idle = DisplayPriorityV2Model.IdleFromPickerItem(item);
                ApplyEdit(session => session.SetIdle(idle));
            }
            popupPicker.IsOpen = false;
            e.Handled = true;
        }

        /// <summary>First playlist click only expands its read-only card.</summary>
        internal bool ExpandPlaylistPickerItemCore(PriorityPickerItemModel item)
        {
            if (item == null
                || !item.IsEnabled
                || item.IdleKind != IdleKind.Playlist
                || string.IsNullOrEmpty(item.PlaylistId)
                || string.Equals(_pickerMode, "base", StringComparison.Ordinal))
                return false;
            _expandedPlaylistItem = item;
            return true;
        }

        /// <summary>Distinct playlist confirmation writes idle and keeps inspection open.</summary>
        internal bool ConfirmPlaylistPickerItemCore(PriorityPickerItemModel item)
        {
            if (item == null
                || !item.IsEnabled
                || item.IdleKind != IdleKind.Playlist
                || string.IsNullOrEmpty(item.PlaylistId)
                || !ReferenceEquals(item, _expandedPlaylistItem))
                return false;
            var idle = DisplayPriorityV2Model.IdleFromPickerItem(item);
            ApplyEdit(session => session.SetIdle(idle));
            popupPicker.IsOpen = true;
            return true;
        }

        private void PlaylistConfirm_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.Tag as PriorityPickerItemModel;
            ConfirmPlaylistPickerItemCore(item);
            e.Handled = true;
        }

        private void AttachPlaylistCardToPickerRow(FrameworkElement row)
        {
            var target = row == null
                ? null
                : VisualTreeHelper.GetParent(row) as StackPanel;
            if (target == null)
            {
                RestorePlaylistCardBottomDock();
                return;
            }
            var current = VisualTreeHelper.GetParent(panelPlaylistCard) as Panel
                ?? LogicalTreeHelper.GetParent(panelPlaylistCard) as Panel;
            current?.Children.Remove(panelPlaylistCard);
            panelPlaylistCard.Margin = new Thickness(12, 0, 12, 8);
            target.Children.Add(panelPlaylistCard);
        }

        private void RestorePlaylistCardBottomDock()
        {
            if (panelPlaylistCard == null || panelPickerLayout == null)
                return;

            var current = VisualTreeHelper.GetParent(panelPlaylistCard) as Panel
                ?? LogicalTreeHelper.GetParent(panelPlaylistCard) as Panel;
            if (!ReferenceEquals(current, panelPickerLayout))
            {
                current?.Children.Remove(panelPlaylistCard);
                int insertAt = scrollPickerGroups == null
                    ? panelPickerLayout.Children.Count
                    : panelPickerLayout.Children.IndexOf(scrollPickerGroups);
                if (insertAt < 0)
                    panelPickerLayout.Children.Add(panelPlaylistCard);
                else
                    panelPickerLayout.Children.Insert(insertAt, panelPlaylistCard);
            }

            DockPanel.SetDock(panelPlaylistCard, Dock.Bottom);
            panelPlaylistCard.Margin = new Thickness(10, 4, 10, 4);
        }

        private void ConfirmAndRemoveRows(PageRef target, string pageName)
        {
            // One session at confirm-entry: count from it, Yes mutates it, then TryApply.
            if (_host == null) return;
            var session = DisplayConfigV2EditSession.Open(_host.GetDisplayConfigV2());
            int rankCount = DisplayPriorityV2Model.CountRowsForTarget(session.Document, target);
            int ovCount = DisplayPriorityV2Model.CountOverridesForTarget(
                session.Document, target, _catalog);
            string body = DisplayCopy.RemovePageRowsOnlyConfirm(rankCount, ovCount);
            string header = DisplayCopy.RemovePageRowsOnly(pageName);
            if (!ConfirmDestructive(header, body))
                return;
            session.RemoveRowsForTarget(target);
            FinishSessionApply(session);
        }

        private void ConfirmAndRemoveAll(PageRef target, string pageName)
        {
            // Dispatch layer covered by the runtime UI verification pass (E9 exit).
            _removeAllPageName = pageName;
            RemoveAllRequestedCore(target);
        }

        /// <summary>
        /// Production remove-all path: open session → plan (exclusivity) → confirm
        /// (<see cref="ConfirmRemoveAll"/>) → apply THAT set via THAT session → TryApply.
        /// Conflict surfaces the banner and re-polls, then re-confirms against fresh live.
        /// </summary>
        /// <param name="target">Resolved page target from the overflow menu.</param>
        internal bool RemoveAllRequestedCore(PageRef target)
        {
            if (_host == null || target == null)
                return false;
            if (!DisplayConfigV2EditSession.CanRemovePageContent(target, _catalog))
                return false;

            var confirm = ConfirmRemoveAll ?? DefaultConfirmRemoveAll;

            for (;;)
            {
                var session = DisplayConfigV2EditSession.Open(_host.GetDisplayConfigV2());
                if (!session.TryPlanRemovePageContent(target, _catalog, out var plan))
                    return false;

                if (!confirm(plan))
                    return false;

                session.ApplyPageContentRemoval(plan);
                var result = session.TryApply(_host);
                if (result.IsConflict)
                {
                    bannerConflict.Visibility = Visibility.Visible;
                    txtConflict.Text = result.Message ?? DisplayCopy.ConfigEditConflict;
                    Poll(force: true);
                    // Re-confirm against the fresh document (new plan / new session).
                    continue;
                }

                bannerConflict.Visibility = Visibility.Collapsed;
                ConfigApplied?.Invoke(this, EventArgs.Empty);
                Poll(force: true);
                return true;
            }
        }

        private bool DefaultConfirmRemoveAll(
            DisplayConfigV2EditSession.PageContentRemovalPlan plan)
        {
            if (plan == null)
                return false;
            string body = DisplayCopy.RemovePageAndOverridesConfirm(
                plan.RankCount, plan.ContentCount);
            string header = DisplayCopy.RemovePageAndOverrides(_removeAllPageName);
            return ConfirmDestructive(header, body);
        }

        private void FinishSessionApply(DisplayConfigV2EditSession session)
        {
            if (_host == null || session == null) return;
            var result = session.TryApply(_host);
            if (result.IsConflict)
            {
                bannerConflict.Visibility = Visibility.Visible;
                txtConflict.Text = result.Message ?? DisplayCopy.ConfigEditConflict;
                Poll(force: true);
                return;
            }
            bannerConflict.Visibility = Visibility.Collapsed;
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            Poll(force: true);
        }

        private bool ConfirmDestructive(string title, string body)
        {
            // Digest 5b confirm shape: title + ruled body + Yes/No.
            var result = MessageBox.Show(
                body,
                title ?? string.Empty,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }

        private void OpenEntrypointForm(PriorityRowModel row, string summonId, bool isNew)
        {
            _epRowId = row.RowId;
            _epSummonId = summonId;
            _epIsNew = isNew;
            _epEnabled = true;
            _epSourceKind = ValueSourceKind.SimHubProperty;
            _epLifetimeKind = LifetimeKind.WhileTrue;
            _epDurationMs = Lifetime.DefaultDurationMs;

            string pageLabel = row.PageName;
            if (row.Destination.Badges.Count > 0)
                pageLabel = DisplayCopy.PageCaption(row.Destination.Badges[0], row.PageName);
            txtEpTitle.Text = DisplayCopy.AnEntrypointTo + " " + pageLabel;
            txtEpRank.Text = DisplayCopy.PrioritySharedRank(row.RankNumber);
            txtEpSourcePath.Text = string.Empty;
            txtEpSourceFriendly.Text = string.Empty;
            txtEpLiveValue.Text = string.Empty;
            txtEpValue.Text = string.Empty;
            txtEpUnit.Text = string.Empty;
            cmbEpOperator.SelectedIndex = 0;
            SelectSourceKind(ValueSourceKind.SimHubProperty);

            // Seed from existing summon when editing (preserve Enabled for UpdateSummon).
            var config = _host?.GetDisplayConfigV2();
            if (!isNew && config?.Priority?.Rows != null && !string.IsNullOrEmpty(summonId))
            {
                for (int i = 0; i < config.Priority.Rows.Count; i++)
                {
                    var r = config.Priority.Rows[i];
                    if (r == null || !string.Equals(r.Id, row.RowId, StringComparison.Ordinal))
                        continue;
                    if (r.Summons == null) break;
                    for (int s = 0; s < r.Summons.Count; s++)
                    {
                        var sum = r.Summons[s];
                        if (sum == null || !string.Equals(sum.Id, summonId, StringComparison.Ordinal))
                            continue;
                        _epEnabled = sum.Enabled;
                        var src = sum.Condition?.Source;
                        if (src != null)
                        {
                            _epSourceKind = src.Kind == ValueSourceKind.Unknown
                                ? ValueSourceKind.SimHubProperty
                                : src.Kind;
                            txtEpSourcePath.Text = src.Name ?? string.Empty;
                            txtEpSourceFriendly.Text = FriendlySourceLabel(src.Name);
                            SelectSourceKind(_epSourceKind);
                        }
                        if (sum.Condition?.Value != null)
                            txtEpValue.Text = sum.Condition.Value.Value.ToString(
                                CultureInfo.InvariantCulture);
                        SelectOperator(sum.Condition?.Operator ?? ConditionOperator.LessThan);
                        if (sum.Lifetime != null)
                        {
                            _epLifetimeKind = sum.Lifetime.Kind;
                            if (sum.Lifetime.DurationMsPresent)
                                _epDurationMs = sum.Lifetime.DurationMs;
                        }
                        break;
                    }
                    break;
                }
            }

            BuildLifetimeRadios();
            RefreshLiveValue();
            RefreshEntrypointSentence();
            UpdateUntilDismissedCard();
            ConstrainEntrypointModal();
            popupEntrypoint.IsOpen = true;
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (popupEntrypoint != null && popupEntrypoint.IsOpen)
                ConstrainEntrypointModal();
        }

        private void ConstrainEntrypointModal()
        {
            DisplayModalLayout.Constrain(
                this,
                popupEntrypoint,
                chromeEntrypointModal,
                fallbackHeight: 640);
        }

        private void SelectSourceKind(ValueSourceKind kind)
        {
            // SegmentedControl.SelectedId is programmatic — does not raise SelectionChanged.
            // Still guard with _suppressEvents for any other chrome that may re-enter.
            _suppressEvents = true;
            try
            {
                if (segEpSourceKind == null)
                    return;
                if (kind == ValueSourceKind.ItmField)
                    segEpSourceKind.SelectedId = "itm";
                else if (kind == ValueSourceKind.Script)
                    segEpSourceKind.SelectedId = "script";
                else
                    segEpSourceKind.SelectedId = "simhub"; // BuiltIn maps to simhub chrome
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void UpdateUntilDismissedCard()
        {
            if (cardEpUntilDismissed == null)
                return;
            cardEpUntilDismissed.Visibility =
                _epLifetimeKind == LifetimeKind.UntilDismissed
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void SelectOperator(ConditionOperator op)
        {
            string phrase = DisplayCopy.OperatorPhrase(op);
            for (int i = 0; i < cmbEpOperator.Items.Count; i++)
            {
                if (string.Equals(cmbEpOperator.Items[i] as string, phrase, StringComparison.Ordinal))
                {
                    cmbEpOperator.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string FriendlySourceLabel(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            int last = path.LastIndexOf('.');
            return last >= 0 && last < path.Length - 1
                ? path.Substring(last + 1)
                : path;
        }

        private void RefreshLiveValue()
        {
            string path = txtEpSourcePath?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path) || _propertyCatalog == null)
            {
                if (txtEpLiveValue != null)
                    txtEpLiveValue.Text = string.Empty;
                return;
            }
            if (_propertyCatalog.TryReadPropertyValue(path, out object value) && value != null)
                txtEpLiveValue.Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            else
                txtEpLiveValue.Text = string.Empty;
        }

        private void EpSourceKind_SegChanged(object sender, string id)
        {
            if (_suppressEvents)
                return;
            if (string.Equals(id, "itm", StringComparison.Ordinal))
                _epSourceKind = ValueSourceKind.ItmField;
            else if (string.Equals(id, "script", StringComparison.Ordinal))
                _epSourceKind = ValueSourceKind.Script;
            else
                _epSourceKind = ValueSourceKind.SimHubProperty;
            RefreshEntrypointSentence();
        }

        private void EpPropertyRow_Click(object sender, MouseButtonEventArgs e)
        {
            var owner = Window.GetWindow(this);
            var builtIns = BuiltInProperties.All;
            var all = _propertyCatalog != null
                ? _propertyCatalog.GetAllPropertyNames()
                : Array.Empty<string>();
            var mappedRoles = _roleCatalog?.GetMappedRoles()?.Roles;
            Func<string, object> valueReader = name =>
            {
                if (_propertyCatalog != null
                    && _propertyCatalog.TryReadPropertyValue(name, out object value))
                    return value;
                return null;
            };
            if (PropertyPickerDialog.TryPick(
                    owner, builtIns, all, mappedRoles,
                    txtEpSourcePath.Text,
                    _pickerStore,
                    builtIns,
                    valueReader,
                    out string picked,
                    out PropertyKind kind))
            {
                // Dispatch layer covered by the runtime UI verification pass (E9 exit).
                PickerResultCore(picked, kind);
            }
            e.Handled = true;
        }

        /// <summary>
        /// Production 5f picker-result path (path + kind after dialog resolves).
        /// Built-in pick persists as builtIn — suppress chrome selection so it cannot
        /// overwrite <c>_epSourceKind</c> back to simHubProperty.
        /// </summary>
        internal void PickerResultCore(string path, PropertyKind kind)
        {
            txtEpSourcePath.Text = path ?? string.Empty;
            txtEpSourceFriendly.Text = FriendlySourceLabel(path);
            _epSourceKind = kind == PropertyKind.BuiltIn
                ? ValueSourceKind.BuiltIn
                : ValueSourceKind.SimHubProperty;
            _suppressEvents = true;
            try
            {
                // Chrome shows SimHub-property segment for both builtIn and simHub
                // (segment control has no builtIn tile); kind stays on the field.
                SelectSourceKind(ValueSourceKind.SimHubProperty);
            }
            finally
            {
                _suppressEvents = false;
            }
            // Re-assert after any residual selection path.
            if (kind == PropertyKind.BuiltIn)
                _epSourceKind = ValueSourceKind.BuiltIn;
            RefreshLiveValue();
            RefreshEntrypointSentence();
        }

        private void BuildLifetimeRadios()
        {
            panelEpLifetime.Children.Clear();
            AddLifetimeRadio(LifetimeKind.WhileTrue, 0);
            AddLifetimeRadioForDuration(
                _epDurationMs > 0 ? _epDurationMs : Lifetime.DefaultDurationMs);
            AddLifetimeRadio(LifetimeKind.UntilDismissed, 0);
            AddLifetimeRadio(LifetimeKind.OnChange, 0);
            UpdateUntilDismissedCard();
        }

        private void AddLifetimeRadio(LifetimeKind kind, int durationMs)
        {
            var rb = new RadioButton
            {
                Content = DisplayCopy.LifetimeFormLabel(kind, durationMs),
                GroupName = "epLifetime",
                IsChecked = kind == _epLifetimeKind,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                FontSize = 12.5,
                Tag = kind,
            };
            // Until-dismissed selected look: amber-bordered card container below owns
            // the consequence; the radio itself stays in the list.
            rb.Checked += (s, e) =>
            {
                _epLifetimeKind = kind;
                UpdateUntilDismissedCard();
                RefreshEntrypointSentence();
            };
            panelEpLifetime.Children.Add(rb);
        }

        /// <summary>
        /// For-duration row with an editable inline seconds field (5f drawn form).
        /// </summary>
        private void AddLifetimeRadioForDuration(int durationMs)
        {
            int seconds = Math.Max(1, (durationMs + 500) / 1000);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var secondsBox = new TextBox
            {
                Text = seconds.ToString(CultureInfo.InvariantCulture),
                Width = 40,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x56)),
            };
            var rb = new RadioButton
            {
                GroupName = "epLifetime",
                IsChecked = _epLifetimeKind == LifetimeKind.ForDuration,
                Margin = new Thickness(0, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                FontSize = 12.5,
                Tag = LifetimeKind.ForDuration,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.Children.Add(new TextBlock
            {
                Text = DisplayCopy.LifetimeForDurationPrefix,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            label.Children.Add(secondsBox);
            label.Children.Add(new TextBlock
            {
                Text = DisplayCopy.LifetimeForDurationSuffix,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            rb.Content = label;
            rb.Checked += (s, e) =>
            {
                _epLifetimeKind = LifetimeKind.ForDuration;
                if (int.TryParse(secondsBox.Text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int sec) && sec > 0)
                    _epDurationMs = sec * 1000;
                UpdateUntilDismissedCard();
                RefreshEntrypointSentence();
            };
            secondsBox.LostFocus += (s, e) =>
            {
                if (!int.TryParse(secondsBox.Text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int sec) || sec <= 0)
                    return;
                _epDurationMs = sec * 1000;
                if (rb.IsChecked == true)
                    RefreshEntrypointSentence();
            };
            secondsBox.TextChanged += (s, e) =>
            {
                if (int.TryParse(secondsBox.Text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int sec) && sec > 0)
                    _epDurationMs = sec * 1000;
            };
            row.Children.Add(rb);
            panelEpLifetime.Children.Add(row);
        }

        private void RefreshEntrypointSentence()
        {
            string source = string.IsNullOrWhiteSpace(txtEpSourceFriendly?.Text)
                ? (string.IsNullOrWhiteSpace(txtEpSourcePath?.Text)
                    ? "value"
                    : FriendlySourceLabel(txtEpSourcePath.Text.Trim()))
                : txtEpSourceFriendly.Text.Trim();
            if (string.IsNullOrEmpty(source)
                && !string.IsNullOrWhiteSpace(txtEpSourcePath?.Text))
                source = txtEpSourcePath.Text.Trim();
            string op = cmbEpOperator.SelectedItem as string ?? DisplayCopy.OpBelow;
            string val = txtEpValue.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(txtEpUnit?.Text))
                val = (val + " " + txtEpUnit.Text.Trim()).Trim();
            string core;
            if (op == DisplayCopy.OpIsOn || op == DisplayCopy.OpIsOff)
                core = DisplayCopy.ConditionBoolSentence(source, op);
            else
                core = DisplayCopy.ConditionLevelSentence(source, op, val);
            core += DisplayCopy.LifetimeLadderSuffix(
                _epLifetimeKind,
                _epLifetimeKind == LifetimeKind.ForDuration ? _epDurationMs : 0);
            txtEpSentence.Text = core;
        }

        private void EntrypointSave_Click(object sender, RoutedEventArgs e)
        {
            EntrypointSaveCore();
            popupEntrypoint.IsOpen = false;
        }

        /// <summary>
        /// Production 5f save path: BuildSummonFromForm → AddSummon/UpdateSummon → TryApply.
        /// Form must already be open (row/summon context + picker/chrome fields).
        /// </summary>
        internal void EntrypointSaveCore()
        {
            var summon = BuildSummonFromForm();
            if (_epIsNew)
            {
                ApplyEdit(session => session.AddSummon(_epRowId, summon));
            }
            else
            {
                // Form writes ONLY its edited fields; UpdateSummon clones existing
                // and merges — Name/Runs/hysteresis/extension data survive.
                string id = _epSummonId;
                ApplyEdit(session => session.UpdateSummon(_epRowId, id, summon));
            }
        }

        /// <summary>
        /// Production form bring-up by row id (same body as Add/Edit entrypoint handlers).
        /// </summary>
        internal bool OpenEntrypointFormCore(string rowId, string summonId, bool isNew)
        {
            if (_model == null || string.IsNullOrEmpty(rowId))
                return false;
            for (int i = 0; i < _model.Rows.Count; i++)
            {
                var row = _model.Rows[i];
                if (row != null && string.Equals(row.RowId, rowId, StringComparison.Ordinal))
                {
                    OpenEntrypointForm(row, summonId, isNew);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Surface B plain door: choose the first authored seat and open 5f.</summary>
        internal bool OpenFirstEntrypointFormCore()
        {
            if (_model == null)
                return false;
            for (int i = 0; i < _model.Rows.Count; i++)
            {
                var row = _model.Rows[i];
                if (row != null
                    && row.Kind == PriorityRowKind.Seat
                    && !string.IsNullOrEmpty(row.RowId))
                    return OpenEntrypointFormCore(row.RowId, null, isNew: true);
            }
            return false;
        }

        private void EntrypointDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_epIsNew || string.IsNullOrEmpty(_epSummonId))
            {
                popupEntrypoint.IsOpen = false;
                return;
            }
            string id = _epSummonId;
            ApplyEdit(session => session.RemoveSummon(_epRowId, id));
            popupEntrypoint.IsOpen = false;
        }

        private void EntrypointCancel_Click(object sender, RoutedEventArgs e)
            => popupEntrypoint.IsOpen = false;

        private Summon BuildSummonFromForm()
        {
            string opText = cmbEpOperator.SelectedItem as string ?? DisplayCopy.OpBelow;
            ConditionOperator op = ConditionOperator.LessThan;
            if (opText == DisplayCopy.OpAtOrBelow) op = ConditionOperator.LessOrEqual;
            else if (opText == DisplayCopy.OpAbove) op = ConditionOperator.GreaterThan;
            else if (opText == DisplayCopy.OpAtOrAbove) op = ConditionOperator.GreaterOrEqual;
            else if (opText == DisplayCopy.OpEquals) op = ConditionOperator.Equals;
            else if (opText == DisplayCopy.OpNotEquals) op = ConditionOperator.NotEquals;
            else if (opText == DisplayCopy.OpIsOn) op = ConditionOperator.IsTrue;
            else if (opText == DisplayCopy.OpIsOff) op = ConditionOperator.IsFalse;

            double? value = null;
            if (op != ConditionOperator.IsTrue && op != ConditionOperator.IsFalse
                && double.TryParse(txtEpValue.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double v))
                value = v;

            var lifetime = new Lifetime { Kind = _epLifetimeKind };
            if (_epLifetimeKind == LifetimeKind.ForDuration)
                lifetime.DurationMs = _epDurationMs;

            // Only the form-edited fields: Condition (source/op/value) + Lifetime.
            // Enabled is the prior value on edit so UpdateSummon does not re-enable.
            // Name / Runs / hysteresis / extension data are not authored here.
            return new Summon
            {
                Id = _epIsNew ? null : _epSummonId,
                Condition = new FanaBridge.Display.Schema2.Condition
                {
                    Source = new FanaBridge.Display.Schema2.ValueSource
                    {
                        Kind = _epSourceKind == ValueSourceKind.Unknown
                            ? ValueSourceKind.SimHubProperty
                            : _epSourceKind,
                        Name = txtEpSourcePath?.Text?.Trim() ?? string.Empty,
                    },
                    Operator = op,
                    Value = value,
                },
                Lifetime = lifetime,
                Enabled = _epEnabled,
            };
        }

        private void TryDropReorder(PriorityRowModel source, Point posInList)
        {
            if (source == null) return;

            // Nearest ranked row by vertical midpoint of the actual visuals —
            // expanded rows and pinned rows make any fixed-height mapping wrong.
            int best = -1;
            double bestDist = double.MaxValue;
            int rankedIndex = 0;
            for (int i = 0; i < listRows.Items.Count; i++)
            {
                var fe = listRows.Items[i] as FrameworkElement;
                if (!(fe?.Tag is PriorityRowModel row) || row.IsPinned)
                    continue;
                double mid = fe.TranslatePoint(new Point(0, 0), listRows).Y
                    + fe.ActualHeight / 2;
                double dist = Math.Abs(posInList.Y - mid);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = rankedIndex;
                }
                rankedIndex++;
            }
            if (best >= 0)
                ReorderCore(source.RowId, best);
        }

        /// <summary>
        /// Production reorder path after drag resolution: ensure authored seeds
        /// (full-clone BringUpLifetime/ChildRef/extension data) → MoveRow → TryApply.
        /// <paramref name="targetIndex"/> is the destination among ranked (non-pinned) rows.
        /// </summary>
        internal bool ReorderCore(string sourceRowId, int targetIndex)
        {
            if (_model == null || string.IsNullOrEmpty(sourceRowId))
                return false;

            var ranked = new List<PriorityRowModel>();
            PriorityRowModel source = null;
            for (int i = 0; i < _model.Rows.Count; i++)
            {
                var r = _model.Rows[i];
                if (r == null || r.IsPinned)
                    continue;
                ranked.Add(r);
                if (string.Equals(r.RowId, sourceRowId, StringComparison.Ordinal))
                    source = r;
            }
            if (source == null || source.IsPinned || ranked.Count < 2)
                return false;

            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex >= ranked.Count) targetIndex = ranked.Count - 1;

            int fromDisplay = ranked.FindIndex(r =>
                string.Equals(r.RowId, source.RowId, StringComparison.Ordinal));
            if (fromDisplay < 0 || fromDisplay == targetIndex)
                return false;

            var targetRow = ranked[targetIndex];
            ApplyEdit(session =>
            {
                // Q2: materialize if needed, then MoveRow on authored indices.
                // Seed is a FULL clone of the existing authored/compiled row — never a
                // sparse kind/id/target literal (BringUpLifetime, ChildRef, extension data).
                if (source.IsMaterialized)
                    session.EnsureAuthoredRow(SeedForReorder(session, source));

                var doc = session.Document;
                if (targetRow.IsMaterialized
                    && !string.Equals(targetRow.RowId, source.RowId, StringComparison.Ordinal))
                {
                    session.EnsureAuthoredRow(SeedForReorder(session, targetRow));
                }
                doc = session.Document;
                int fromAuth = DisplayPriorityV2Model.AuthoredIndexOf(doc, source.RowId);
                int toAuth = DisplayPriorityV2Model.AuthoredIndexOf(doc, targetRow.RowId);
                if (fromAuth < 0 || toAuth < 0 || fromAuth == toAuth)
                    return doc;
                return session.MoveRow(fromAuth, toAuth);
            });
            return true;
        }

        /// <summary>
        /// Clone the existing authored or compiled (EffectiveRows) row for materialization.
        /// Falls back to a minimal seat only when no source row can be found.
        /// </summary>
        private PriorityRow SeedForReorder(
            DisplayConfigV2EditSession session, PriorityRowModel model)
        {
            if (model == null)
                return null;

            // Prefer live compiled row (RuntimeRows after Normalize), then session working,
            // then opened-against — full clone so BringUpLifetime/ChildRef/extension survive.
            var live = _host?.GetDisplayConfigV2();
            var found = FindRowById(live?.Priority?.EffectiveRows, model.RowId)
                ?? FindRowById(session?.OpenedAgainst?.Priority?.EffectiveRows, model.RowId)
                ?? FindRowById(session?.Document?.Priority?.Rows, model.RowId)
                ?? FindRowById(live?.Priority?.Rows, model.RowId);

            if (found != null)
                return DisplayConfigV2Serializer.CloneNode(found);

            return new PriorityRow
            {
                Kind = model.Kind == PriorityRowKind.Unknown
                    ? PriorityRowKind.Seat : model.Kind,
                Id = model.RowId,
                Target = model.Target == null
                    ? null
                    : DisplayConfigV2Serializer.CloneNode(model.Target),
            };
        }

        private static PriorityRow FindRowById(
            System.Collections.Generic.IReadOnlyList<PriorityRow> rows, string id)
        {
            if (rows == null || string.IsNullOrEmpty(id))
                return null;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r != null && string.Equals(r.Id, id, StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        /// <summary>
        /// Open → mutate → TryApply. On conflict: surface ruled message, re-open
        /// against the fresh live document, and re-project.
        /// </summary>
        private void ApplyEdit(Func<DisplayConfigV2EditSession, DisplayConfigV2> mutate)
        {
            if (_host == null) return;

            var live = _host.GetDisplayConfigV2();
            var session = DisplayConfigV2EditSession.Open(live);
            mutate(session);
            var result = session.TryApply(_host);

            if (result.IsConflict)
            {
                bannerConflict.Visibility = Visibility.Visible;
                txtConflict.Text = result.Message ?? DisplayCopy.ConfigEditConflict;
                // Re-open against fresh document (no stale session).
                Poll(force: true);
                return;
            }

            bannerConflict.Visibility = Visibility.Collapsed;
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            Poll(force: true);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private void AddPage_Click(object sender, RoutedEventArgs e)
        {
            // Surface B: plain door live.
            if (_model != null && _model.AddPageEnabled)
                AddPageRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
