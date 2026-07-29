using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Add-a-page flow (5h). Plain door live; setup porch inert (D20 /
    /// <see cref="DisplayCopy.SpokeArrivingLater"/>). Session writes for the page door;
    /// entrypoint/override doors route to Priority / Pages &amp; Fields.
    /// </summary>
    public partial class DisplayAddPageV2View : UserControl
    {
        private IDisplayPanelHost _host;
        private WheelCatalog _catalog;
        private DisplayAddPageV2Model _model;
        private string _selectedCatalogPageId;

        /// <summary>‹ Overview breadcrumb (first leaf).</summary>
        public event EventHandler BackRequested;

        /// <summary>Priority middle-leaf (B-N2) or post-create return.</summary>
        public event EventHandler PriorityRequested;

        /// <summary>Plain-door "An entrypoint" → Priority create form (host wires).</summary>
        public event EventHandler EntrypointDoorRequested;

        /// <summary>Plain-door "An override" → Pages &amp; Fields (host wires).</summary>
        public event EventHandler OverrideDoorRequested;

        /// <summary>Raised after a successful session apply.</summary>
        public event EventHandler ConfigApplied;

        public DisplayAddPageV2View()
        {
            InitializeComponent();
            ApplyStaticCopy();
        }

        internal void Bind(IDisplayPanelHost host, WheelCatalog catalog = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _catalog = catalog;
            Poll(force: true);
        }

        internal void Poll(bool force = false)
        {
            if (_host == null)
                return;

            var envelope = _host.Snapshot;
            var config = _host.GetDisplayConfigV2();
            var resolution = ProjectResolution(envelope);

            _model = DisplayAddPageV2Model.Project(
                config, resolution, _host.DisplayType, _catalog);
            ApplyModel(_model);
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
            txtTitle.Text = DisplayCopy.Add;
            txtPriorityCrumb.Text = DisplayCopy.Priority;
            txtDivider.Text = DisplayCopy.ModeProfileDivider;
            radItm.Content = DisplayCopy.PageKindItm;
            radHosted.Content = DisplayCopy.PageKindHosted;
            txtNameLabel.Text = DisplayCopy.PageNameLabel;
            chkAddRotation.Content = DisplayCopy.AddToTheRotation;
            btnFormCancel.Content = DisplayCopy.Cancel;
            btnFormCreate.Content = DisplayCopy.CreatePage;
            btnPickItm.Content = DisplayCopy.ChooseItmPage;
            txtPageFormTitle.Text = DisplayCopy.DoorAPage;
            txtProfileName.Text = DisplayCopy.CurrentProfile;
            txtProfileChevron.Text = DisplayCopy.PropertyRowChevron;
            btnProfilesManager.Content = DisplayCopy.ProfilesManager;
            btnEditProfile.Content = DisplayCopy.EditProfile;
            btnCloneProfile.Content = DisplayCopy.CloneProfile;
            btnNewProfile.Content = DisplayCopy.NewProfile;
            string profileNote = DisplayCopy.SpokeArrivingLater(DisplayCopy.ProfilesManager);
            btnProfilesManager.ToolTip = profileNote;
            btnEditProfile.ToolTip = profileNote;
            btnCloneProfile.ToolTip = profileNote;
            btnNewProfile.ToolTip = profileNote;
            ToolTipService.SetShowOnDisabled(btnProfilesManager, true);
            ToolTipService.SetShowOnDisabled(btnEditProfile, true);
            ToolTipService.SetShowOnDisabled(btnCloneProfile, true);
            ToolTipService.SetShowOnDisabled(btnNewProfile, true);
        }

        private void ApplyModel(DisplayAddPageV2Model model)
        {
            if (model == null) return;

            txtSurfaceWord.Text = model.SurfaceWord;
            txtSituation.Text = model.SituationCopy;
            dotSituation.Fill = model.InGame
                ? new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5C));

            txtSetupLabel.Text = model.SetupColumnLabel;
            txtSetupSearch.Text = string.Empty;
            txtSetupSearch.ToolTip = model.SetupSearchPlaceholder;
            ToolTipService.SetShowOnDisabled(txtSetupSearch, true);
            txtSetupSearch.IsEnabled = model.SetupPorchEnabled;
            txtSetupNote.Text = model.SetupPorchNote ?? string.Empty;

            txtDoorLabel.Text = model.PlainDoorLabel;
            txtDoorNote.Text = model.PlainDoorNote;
            txtPageAddedNote.Text = model.PageAddedNote;
            txtConfigureHint.Text = model.PlainDoorNote;

            panelDoors.Children.Clear();
            for (int i = 0; i < model.Doors.Count; i++)
            {
                var door = model.Doors[i];
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2D)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Cursor = door.Enabled ? Cursors.Hand : Cursors.Arrow,
                    Opacity = door.Enabled ? 1.0 : 0.55,
                    Tag = door,
                };
                if (!door.Enabled && !string.IsNullOrEmpty(door.DisabledTooltip))
                {
                    card.ToolTip = door.DisabledTooltip;
                    ToolTipService.SetShowOnDisabled(card, true);
                }
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = door.Title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                });
                stack.Children.Add(new TextBlock
                {
                    Text = door.Subtitle,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                card.Child = stack;
                if (door.Enabled)
                    card.MouseLeftButtonUp += DoorCard_Click;
                panelDoors.Children.Add(card);
            }
        }

        private void DoorCard_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var door = border?.Tag as AddPageDoorCardModel;
            if (door == null || !door.Enabled)
                return;

            switch (door.Kind)
            {
                case AddPageDoorKind.Page:
                    ShowPageForm();
                    break;
                case AddPageDoorKind.Entrypoint:
                    EntrypointDoorRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case AddPageDoorKind.Override:
                    OverrideDoorRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
            e.Handled = true;
        }

        private void ShowPageForm()
        {
            panelPageForm.Visibility = Visibility.Visible;
            txtConfigureHint.Visibility = Visibility.Collapsed;
            radItm.IsChecked = true;
            radHosted.IsChecked = false;
            _selectedCatalogPageId = null;
            btnPickItm.Content = DisplayCopy.ChooseItmPage;
            txtPageName.Text = string.Empty;
            chkAddRotation.IsChecked = false;
            UpdatePageKindPanels();
        }

        private void HidePageForm()
        {
            panelPageForm.Visibility = Visibility.Collapsed;
            txtConfigureHint.Visibility = Visibility.Visible;
            popupItmPicker.IsOpen = false;
        }

        private void PageKind_Changed(object sender, RoutedEventArgs e)
            => UpdatePageKindPanels();

        private void UpdatePageKindPanels()
        {
            bool itm = radItm.IsChecked == true;
            panelItmPick.Visibility = itm ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PickItm_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;
            listItmChoices.Items.Clear();
            for (int i = 0; i < _model.ItmChoices.Count; i++)
            {
                var choice = _model.ItmChoices[i];
                var row = new Border
                {
                    Padding = new Thickness(12, 7, 12, 7),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    Tag = choice,
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var badge = new TextBlock
                {
                    Text = choice.Badge,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xC4)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(badge, 0);
                grid.Children.Add(badge);
                var name = new TextBlock
                {
                    Text = choice.Name,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                if (choice.IsRemoved)
                {
                    var note = new TextBlock
                    {
                        Text = DisplayCopy.Off,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(note, 2);
                    grid.Children.Add(note);
                }
                row.Child = grid;
                row.MouseLeftButtonUp += ItmChoice_Click;
                listItmChoices.Items.Add(row);
            }
            popupItmPicker.IsOpen = true;
        }

        private void ItmChoice_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var choice = border?.Tag as AddPageItmChoiceModel;
            if (choice == null) return;
            _selectedCatalogPageId = choice.CatalogPageId;
            btnPickItm.Content = choice.Badge + "  " + choice.Name;
            if (string.IsNullOrWhiteSpace(txtPageName.Text))
                txtPageName.Text = choice.Name;
            popupItmPicker.IsOpen = false;
            e.Handled = true;
        }

        private void FormCancel_Click(object sender, RoutedEventArgs e)
            => HidePageForm();

        private void FormCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;

            bool isItm = radItm.IsChecked == true;
            bool addRotation = chkAddRotation.IsChecked == true;
            string name = (txtPageName.Text ?? string.Empty).Trim();

            PageEntry entry;
            if (isItm)
            {
                if (string.IsNullOrEmpty(_selectedCatalogPageId))
                    return;
                entry = new PageEntry
                {
                    Kind = PageEntryKind.ItmPage,
                    CatalogPageId = _selectedCatalogPageId,
                    NameOverride = string.IsNullOrEmpty(name) ? null : name,
                    Removed = false,
                };
            }
            else
            {
                if (string.IsNullOrEmpty(name))
                    return;
                entry = new PageEntry
                {
                    Kind = PageEntryKind.HostedPage,
                    Name = name,
                };
            }

            var session = DisplayConfigV2EditSession.Open(_host.GetDisplayConfigV2());
            session.AddPage(entry, addToRotation: addRotation, ensurePrioritySeat: true);
            var result = session.TryApply(_host);
            if (!result.Succeeded)
            {
                bannerConflict.Visibility = Visibility.Visible;
                txtConflict.Text = result.Message ?? DisplayCopy.ConfigEditConflict;
                Poll(force: true);
                return;
            }

            bannerConflict.Visibility = Visibility.Collapsed;
            HidePageForm();
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            // B-O3: return to Priority after create.
            PriorityRequested?.Invoke(this, EventArgs.Empty);
        }

        private void BackOverview_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private void BackPriority_Click(object sender, RoutedEventArgs e)
            => PriorityRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Test seam: run the page create path without WPF clicks.</summary>
        internal bool CreatePageForTest(
            PageEntry entry, bool addToRotation = false, bool ensurePrioritySeat = true)
        {
            if (_host == null || entry == null)
                return false;
            var session = DisplayConfigV2EditSession.Open(_host.GetDisplayConfigV2());
            session.AddPage(entry, addToRotation, ensurePrioritySeat);
            var result = session.TryApply(_host);
            if (!result.Succeeded)
            {
                bannerConflict.Visibility = Visibility.Visible;
                txtConflict.Text = result.Message ?? DisplayCopy.ConfigEditConflict;
                return false;
            }
            bannerConflict.Visibility = Visibility.Collapsed;
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>Test seam: current model after last Poll.</summary>
        internal DisplayAddPageV2Model BoundModel => _model;
    }
}
