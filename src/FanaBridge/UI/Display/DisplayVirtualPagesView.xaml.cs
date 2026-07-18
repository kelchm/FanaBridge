using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The Virtual pages editor of the Display tab: pills per legacy screen, a NAME /
    /// CONTENT / EFFECT editor card with LIVE 3-char preview, immediate delete via ⋯,
    /// and the base-screen row. Every committed edit flows through
    /// <see cref="DisplayVirtualPagesEditModel"/> into <c>host.ApplyDisplayConfig</c>.
    /// Hosted on <c>TabView.Legacy</c>; ITM wheels also reach it via the Page-6 card.
    /// </summary>
    public partial class DisplayVirtualPagesView : UserControl
    {
        private const int PropertyBudget = 36;

        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private IDisplayPickerStore _pickerStore;
        private DisplaySettings _settings;
        private bool _isItm;

        private DisplayVirtualPagesEditModel _editModel;
        private DisplayCustomizationConfig _editModelSource;
        private readonly SevenSegmentFace _liveFace = new SevenSegmentFace();

        internal event EventHandler BackRequested;
        internal event EventHandler ConfigApplied;

        public DisplayVirtualPagesView()
        {
            InitializeComponent();
            hostLiveFace.Content = _liveFace;
        }

        internal void Bind(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog,
            DisplaySettings settings,
            IDisplayPickerStore pickerStore)
        {
            _host = host;
            _propertyCatalog = propertyCatalog;
            _roleCatalog = roleCatalog;
            _settings = settings;
            _pickerStore = pickerStore;
            _isItm = host != null && host.DisplayType == DisplayType.Itm;
            txtSubtitle.Text = _isItm ? "shown on ITM Page 6" : "3-character display";
        }

        /// <summary>Called when Virtual pages becomes active: fresh model from host config.</summary>
        internal void Enter()
        {
            if (_host != null)
                _isItm = _host.DisplayType == DisplayType.Itm;
            txtSubtitle.Text = _isItm ? "shown on ITM Page 6" : "3-character display";
            EnterEditor();
        }

        /// <summary>Poll while active: rebuild if the host document changed under us;
        /// otherwise advance the LIVE face for scroll/blink effects.</summary>
        internal void Poll()
        {
            if (_editModel == null || _host == null)
                return;
            if (!ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
            {
                EnterEditor();
                return;
            }
            RefreshLiveFace();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private void EnterEditor()
        {
            _editModelSource = _host?.GetDisplayConfig();
            _editModel = new DisplayVirtualPagesEditModel(_editModelSource);
            RenderAll();
        }

        private void RenderAll()
        {
            if (_editModel == null)
                return;
            RenderPills();
            bool has = _editModel.Screens.Count > 0;
            panelEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            txtEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (has)
                RenderEditorCard();
            else
                _liveFace.Render(SevenSegmentFaceRender.BlankFrame());
            RenderBaseRow();
        }

        private void RenderPills()
        {
            panelPagePills.Children.Clear();
            foreach (var pill in _editModel.PagePills())
            {
                string id = pill.Id;
                var border = new Border
                {
                    Padding = new Thickness(13, 7, 13, 7),
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Background = pill.IsSelected
                        ? DisplayPalette.AccentBg
                        : DisplayPalette.PagePillIdleBg,
                    BorderBrush = pill.IsSelected
                        ? DisplayPalette.AccentBg
                        : DisplayPalette.PagePillIdleBorder,
                    BorderThickness = new Thickness(1),
                };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock
                {
                    Text = pill.Index.ToString(),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Foreground = pill.IsSelected ? Brushes.White : DisplayPalette.RowText,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                line.Children.Add(new TextBlock
                {
                    Text = pill.Name,
                    FontSize = 12,
                    Foreground = pill.IsSelected ? Brushes.White : DisplayPalette.RowText,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                // Overflow ⋯ on the selected pill — immediate delete, no confirm.
                if (pill.IsSelected)
                {
                    var menuBtn = new TextBlock
                    {
                        Text = "⋯",
                        FontSize = 14,
                        Foreground = Brushes.White,
                        Margin = new Thickness(10, 0, 0, 0),
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Page options",
                    };
                    menuBtn.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        ShowOverflowMenu(menuBtn, id);
                    };
                    line.Children.Add(menuBtn);
                }
                border.Child = line;
                border.MouseLeftButtonUp += (s, e) =>
                {
                    if (_editModel == null) return;
                    _editModel.SelectScreen(id);
                    RenderAll();
                };
                panelPagePills.Children.Add(border);
            }

            // Dashed ＋ add pill (mock :339).
            var add = new Border
            {
                Padding = new Thickness(13, 7, 13, 7),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = DisplayPalette.BaseDash,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "＋",
                    FontSize = 14,
                    Foreground = DisplayPalette.SubLabel,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            add.MouseLeftButtonUp += (s, e) => OnAddScreen();
            panelPagePills.Children.Add(add);
        }

        private void ShowOverflowMenu(FrameworkElement anchor, string screenId)
        {
            var menu = new ContextMenu();
            var del = new MenuItem { Header = "Delete" };
            del.Click += (s, e) => OnDeleteScreen(screenId);
            menu.Items.Add(del);
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void RenderEditorCard()
        {
            panelFields.Children.Clear();
            var screen = _editModel.SelectedScreen;
            if (screen == null)
                return;

            string id = screen.Id;
            var kind = screen.ContentKind == LegacyContentKind.Unknown
                ? LegacyContentKind.Text
                : screen.ContentKind;

            // Header: name · kind subtitle + (kind already on pill).
            panelFields.Children.Add(new TextBlock
            {
                Text = "Virtual page · " + DisplayVirtualPagesEditModel.DisplayName(screen),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = DisplayPalette.RowText,
                Margin = new Thickness(0, 0, 0, 13),
            });

            // NAME
            panelFields.Children.Add(SectionLabel("NAME"));
            var nameBox = new TextBox
            {
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = screen.Name ?? "",
                Margin = new Thickness(0, 0, 0, 12),
            };
            CommitOnLeave(nameBox, () =>
            {
                if (ReconcileIfExternallyChanged()) return;
                ApplyAndReload(_editModel.SetName(id, nameBox.Text), id);
            });
            panelFields.Children.Add(nameBox);

            // CONTENT
            panelFields.Children.Add(SectionLabel("CONTENT"));
            var kindCell = new DropDownCell
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 6),
            };
            kindCell.SetChoices(DisplayVirtualPagesEditModel.ContentKindChoices(kind));
            kindCell.SelectionCommitted += (s, choiceId) =>
            {
                if (!Enum.TryParse(choiceId, true, out LegacyContentKind k))
                    return;
                if (ReconcileIfExternallyChanged()) return;
                ApplyAndReload(_editModel.SetContentKind(id, k), id);
            };
            panelFields.Children.Add(kindCell);
            panelFields.Children.Add(new TextBlock
            {
                Text = "Speed · Gear · RPM · Position · Fuel · Text · Message · Property",
                FontSize = 10.5,
                Foreground = DisplayPalette.KLabelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            if (DisplayVirtualPagesEditModel.ShowsTextField(kind))
            {
                panelFields.Children.Add(SectionLabel(
                    kind == LegacyContentKind.Message ? "MESSAGE" : "TEXT"));
                var textBox = new TextBox
                {
                    Height = 30,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Text = screen.Text ?? "",
                    Margin = new Thickness(0, 0, 0, 12),
                    FontFamily = DisplayPalette.Mono,
                };
                CommitOnLeave(textBox, () =>
                {
                    if (ReconcileIfExternallyChanged()) return;
                    ApplyAndReload(_editModel.SetText(id, textBox.Text), id);
                });
                panelFields.Children.Add(textBox);
            }

            if (DisplayVirtualPagesEditModel.ShowsPropertyField(kind))
            {
                panelFields.Children.Add(SectionLabel("PROPERTY"));
                panelFields.Children.Add(BuildPropertyRow(screen));
            }

            // EFFECT
            panelFields.Children.Add(new Border
            {
                BorderBrush = DisplayPalette.RowBorder,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(0, 12, 0, 0),
                Child = BuildEffectSection(screen),
            });

            RefreshLiveFace();
            txtLiveHint.Text = kind == LegacyContentKind.Message
                ? "Message can scroll when longer than 3 characters; blink is optional."
                : kind == LegacyContentKind.Property
                    ? "Property values render as 0–999 on the face."
                    : "Preview uses sample values for dynamic content — the wire shows live telemetry.";
        }

        private UIElement BuildEffectSection(LegacyScreen screen)
        {
            string id = screen.Id;
            var stack = new StackPanel();
            stack.Children.Add(SectionLabel("EFFECT", bottom: 7));
            var cell = new DropDownCell { HorizontalAlignment = HorizontalAlignment.Stretch };
            cell.SetChoices(DisplayVirtualPagesEditModel.EffectChoices(screen.Effect));
            cell.SelectionCommitted += (s, choiceId) =>
            {
                if (!Enum.TryParse(choiceId, true, out LegacyEffect effect))
                    return;
                if (ReconcileIfExternallyChanged()) return;
                ApplyAndReload(_editModel.SetEffect(id, effect), id);
            };
            stack.Children.Add(cell);
            return stack;
        }

        private UIElement BuildPropertyRow(LegacyScreen screen)
        {
            string id = screen.Id;
            string sourceName = screen.Source?.Name;
            var kind = screen.Source?.Kind ?? PropertyKind.SimHubProperty;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new PropertyLabel();
            label.SetRuns(
                PropertyGrammar.Format(sourceName, kind, PropertyBudget),
                sourceName ?? "");
            label.Margin = new Thickness(10, 0, 6, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var pencil = new TextBlock
            {
                Text = "✎",
                Foreground = DisplayPalette.PencilBlue,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pencil, 1);
            grid.Children.Add(pencil);

            var border = new Border
            {
                Background = DisplayPalette.FieldBg,
                BorderBrush = DisplayPalette.DrawerBar,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Height = 30,
                Cursor = Cursors.Hand,
                Focusable = true,
                Child = grid,
                Margin = new Thickness(0, 0, 0, 12),
            };
            Action pick = () => OnPickProperty(id, sourceName);
            border.MouseLeftButtonUp += (s, e) => pick();
            border.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    pick();
                    e.Handled = true;
                }
            };
            return border;
        }

        private void RenderBaseRow()
        {
            panelBaseRow.Children.Clear();
            panelBaseRow.Children.Add(new TextBlock
            {
                Text = "BASE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.ThLabel,
                Margin = new Thickness(0, 0, 0, 7),
            });

            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            line.Children.Add(new TextBlock
            {
                Text = "★",
                Foreground = DisplayPalette.BaseRank,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            line.Children.Add(new TextBlock
            {
                Text = "When nothing's firing →",
                FontSize = 12.5,
                Foreground = DisplayPalette.BaseText,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var cell = new DropDownCell { Width = 210, VerticalAlignment = VerticalAlignment.Center };
            cell.SetChoices(_editModel.BaseScreenChoices());
            cell.SelectionCommitted += (s, id) =>
            {
                if (ReconcileIfExternallyChanged()) return;
                string keep = _editModel.SelectedScreenId;
                ApplyAndReload(_editModel.SetBaseScreenId(id), keep);
            };
            line.Children.Add(cell);
            panelBaseRow.Children.Add(line);
            panelBaseRow.Children.Add(new TextBlock
            {
                Text = "What the 3-character display rests on between triggers. Blank clears the face.",
                FontSize = 11,
                Foreground = DisplayPalette.KLabelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0),
            });
        }

        private void RefreshLiveFace()
        {
            if (_editModel == null)
                return;
            // Effect clock uses wall ms so scroll/blink advance while the editor is open.
            long nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalMilliseconds;
            _liveFace.Render(_editModel.PreviewSegments(nowMs));
        }

        private void OnAddScreen()
        {
            if (_editModel == null || _host == null)
                return;
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.AddScreen();
            ApplyAndReload(cfg, _editModel.SelectedScreenId);
        }

        private void OnDeleteScreen(string id)
        {
            if (_editModel == null || _host == null)
                return;
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.RemoveScreen(id);
            ApplyAndReload(cfg, _editModel.SelectedScreenId);
        }

        private void OnPickProperty(string screenId, string current)
        {
            if (_editModel == null || _host == null)
                return;
            if (ReconcileIfExternallyChanged())
                return;

            var owner = Window.GetWindow(this);
            var builtIns = BuiltInProperties.All;
            var all = _propertyCatalog != null
                ? _propertyCatalog.GetAllPropertyNames()
                : Array.Empty<string>();
            var mappedRoles = _roleCatalog?.GetMappedRoles()?.Roles;
            if (!PropertyPickerDialog.TryPick(owner, builtIns, all, mappedRoles, current,
                    _pickerStore, itmPageProperties: null, valueReader: null,
                    out string picked, out PropertyKind kind))
                return;

            ApplyAndReload(_editModel.SetSource(screenId, kind, picked), screenId);
        }

        private bool ReconcileIfExternallyChanged()
        {
            if (_host == null || ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
                return false;
            EnterEditor();
            return true;
        }

        private void ApplyAndReload(DisplayCustomizationConfig cfg, string selectId)
        {
            _host.ApplyDisplayConfig(cfg);
            _editModelSource = _host.GetDisplayConfig();
            _editModel = new DisplayVirtualPagesEditModel(_editModelSource);
            if (!string.IsNullOrEmpty(selectId))
                _editModel.SelectScreen(selectId);
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            RenderAll();
        }

        private static TextBlock SectionLabel(string text, double bottom = 5)
            => new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.KLabelBrush,
                Margin = new Thickness(0, 0, 0, bottom),
            };

        private static void CommitOnLeave(TextBox box, Action commit)
        {
            box.LostKeyboardFocus += (s, e) => commit();
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    commit();
                    e.Handled = true;
                }
            };
        }
    }
}
