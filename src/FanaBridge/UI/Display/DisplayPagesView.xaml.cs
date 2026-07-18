using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The Pages &amp; fields editor of the Display tab: page pills from the device's
    /// <see cref="ItmPageTable"/>, an interactive digital twin (selection chrome + hit
    /// regions), and a field inspector (source pick / format / firmware slot lock).
    /// Every committed edit flows through the SimHub-free
    /// <see cref="DisplayPagesEditModel"/> into <c>host.ApplyDisplayConfig</c>.
    ///
    /// The Display tab shell owns navigation and polling: it calls <see cref="Bind"/>
    /// once, <see cref="Enter"/> when this view becomes active, <see cref="Poll"/> each
    /// tick while it is, and the view signals <see cref="BackRequested"/> /
    /// <see cref="ConfigApplied"/> / <see cref="LegacyRequested"/> back.
    /// </summary>
    public partial class DisplayPagesView : UserControl
    {
        private const int PropertyBudget = 36;

        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private IDisplayPickerStore _pickerStore;
        private DisplaySettings _settings;

        private DisplayPagesEditModel _editModel;
        private DisplayCustomizationConfig _editModelSource;
        private DisplayValuesSnapshot _lastValues;
        private ushort? _lastRenderedParam;
        private ItmPage? _lastRenderedPage;
        private bool _lastWasLiveMatch;

        internal event EventHandler BackRequested;
        internal event EventHandler ConfigApplied;
        /// <summary>Raised when the Page-6 card asks to open Virtual pages (Legacy placeholder).</summary>
        internal event EventHandler LegacyRequested;

        public DisplayPagesView()
        {
            InitializeComponent();
            pagesMirror.IsInteractive = true;
            pagesMirror.SlotClicked += OnSlotClicked;
        }

        // ── The bind/input surface (the seam) ──────────────────────────────

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
        }

        /// <summary>Called when the Pages view becomes active: build a fresh edit model
        /// from the host's config and render. Clean-slate on re-entry.</summary>
        internal void Enter(DisplayValuesSnapshot values)
        {
            _lastValues = values;
            EnterPagesEditor();
        }

        /// <summary>Poll while active: refresh the twin from the latest values snapshot
        /// and rebuild the editor when the host's document changed out from under us.</summary>
        internal void Poll(DisplayValuesSnapshot values)
        {
            _lastValues = values;
            if (_editModel == null || _host == null)
                return;
            if (!ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
            {
                EnterPagesEditor();
                return;
            }
            RenderTwin();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private void OpenLegacy_Click(object sender, RoutedEventArgs e)
            => LegacyRequested?.Invoke(this, EventArgs.Empty);

        // ── Entry / render ────────────────────────────────────────────────

        private void EnterPagesEditor()
        {
            _editModelSource = _host?.GetDisplayConfig();
            _editModel = NewEditModel(_editModelSource);
            _lastRenderedParam = null;
            _lastRenderedPage = null;
            _lastWasLiveMatch = false;
            RenderAll();
        }

        private DisplayPagesEditModel NewEditModel(DisplayCustomizationConfig config)
            => new DisplayPagesEditModel(
                config,
                _host?.ItmDeviceId ?? 0,
                _settings?.ItmShowLapTotal ?? true,
                _settings?.ItmShowPositionTotal ?? true);

        private void RenderAll()
        {
            if (_editModel == null)
                return;
            RenderPills();
            bool legacy = _editModel.IsLegacyPage;
            panelLegacy.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
            panelTelemetry.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
            if (!legacy)
            {
                RenderTwin(force: true);
                RenderInspector();
            }
        }

        private void RenderPills()
        {
            panelPagePills.Children.Clear();
            foreach (var pill in _editModel.PagePills())
            {
                var page = pill.Page;
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
                    Tag = page,
                };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock
                {
                    Text = pill.Wire.ToString(),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Foreground = pill.IsSelected
                        ? Brushes.White
                        : DisplayPalette.RowText,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                line.Children.Add(new TextBlock
                {
                    Text = pill.Name,
                    FontSize = 12,
                    Foreground = pill.IsSelected
                        ? Brushes.White
                        : DisplayPalette.RowText,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                border.Child = line;
                border.MouseLeftButtonUp += (s, e) =>
                {
                    if (_editModel == null) return;
                    _editModel.SelectPage(page);
                    RenderAll();
                };
                panelPagePills.Children.Add(border);
            }
        }

        private void RenderTwin(bool force = false)
        {
            if (_editModel == null || _editModel.IsLegacyPage)
                return;

            var page = _editModel.SelectedPage;
            ushort? selected = _editModel.SelectedParamId;
            bool liveMatch = _lastValues != null
                && _lastValues.State == ItmLifecycleState.Synced
                && _lastValues.Page == page;

            // Skip a full rebuild when nothing the twin depends on changed.
            if (!force
                && liveMatch == _lastWasLiveMatch
                && page == _lastRenderedPage
                && selected == _lastRenderedParam
                && liveMatch
                && ReferenceEquals(_lastValues, pagesMirror.Tag as DisplayValuesSnapshot))
            {
                return;
            }

            MirrorModel model;
            if (liveMatch)
                model = ItmDisplayMirrorRender.Build(_lastValues, selected, interactive: true);
            else
                model = ItmDisplayMirrorRender.BuildLayout(page, selected, interactive: true);

            pagesMirror.Render(model);
            pagesMirror.Tag = liveMatch ? _lastValues : null;
            _lastRenderedPage = page;
            _lastRenderedParam = selected;
            _lastWasLiveMatch = liveMatch;
        }

        private void RenderInspector()
        {
            panelInspector.Children.Clear();
            var insp = _editModel?.Inspector();
            if (insp == null)
            {
                panelInspector.Children.Add(new TextBlock
                {
                    Text = "Select a field on the display to remap it.",
                    FontSize = 12.5,
                    Foreground = DisplayPalette.SubLabel,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            // Header: field name + provenance badge.
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 13),
            };
            header.Children.Add(new TextBlock
            {
                Text = insp.FieldName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = DisplayPalette.RowText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            header.Children.Add(ProvenanceBadge(insp.Provenance));
            panelInspector.Children.Add(header);

            if (insp.IsLocked)
            {
                panelInspector.Children.Add(new TextBlock
                {
                    Text = "This field keeps a special wire form and cannot be remapped.",
                    FontSize = 12,
                    Foreground = DisplayPalette.SubLabel,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12),
                });
            }
            else
            {
                // SIMHUB PROPERTY row.
                panelInspector.Children.Add(SectionLabel("SIMHUB PROPERTY"));
                panelInspector.Children.Add(BuildPropertyRow(insp));
                if (insp.ShowResetToDefault)
                {
                    var reset = new TextBlock
                    {
                        Text = "Reset to default",
                        FontSize = 11.5,
                        Foreground = DisplayPalette.PencilBlue,
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 5, 0, 0),
                    };
                    reset.MouseLeftButtonUp += (s, e) => OnResetToDefault(insp.ParamId);
                    panelInspector.Children.Add(reset);
                }

                // UNIT & FORMAT.
                panelInspector.Children.Add(new Border
                {
                    BorderBrush = DisplayPalette.RowBorder,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 12, 0, 0),
                    Padding = new Thickness(0, 12, 0, 0),
                    Child = BuildFormatSection(insp),
                });
            }

            // FIRMWARE SLOT — always shown, always locked.
            panelInspector.Children.Add(new Border
            {
                BorderBrush = DisplayPalette.RowBorder,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(0, 12, 0, 0),
                Child = BuildFirmwareSlot(insp),
            });
        }

        private static Border ProvenanceBadge(FieldProvenance provenance)
        {
            bool wheel = provenance == FieldProvenance.ThisWheel;
            return new Border
            {
                Background = wheel
                    ? DisplayPalette.ProvenanceWheelBg
                    : DisplayPalette.ProvenanceDefaultBg,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = wheel ? "THIS WHEEL" : "DEFAULT",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = wheel
                        ? DisplayPalette.ProvenanceWheelFg
                        : DisplayPalette.ProvenanceDefaultFg,
                },
            };
        }

        private UIElement BuildPropertyRow(FieldInspectorModel insp)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new PropertyLabel();
            label.SetRuns(
                PropertyGrammar.Format(insp.SourceName, insp.SourceKind, PropertyBudget),
                insp.SourceName ?? "");
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
            };
            Action pick = () => OnPickProperty(insp.ParamId, insp.SourceName);
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

        private UIElement BuildFormatSection(FieldInspectorModel insp)
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionLabel("UNIT & FORMAT", bottom: 7));

            var cell = new DropDownCell
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = insp.HasFormatOptions,
                Opacity = insp.HasFormatOptions ? 1.0 : 0.55,
            };
            if (insp.HasFormatOptions)
            {
                cell.SetChoices(new ChoiceList(insp.FormatChoices, insp.FormatId));
                ushort paramId = insp.ParamId;
                cell.SelectionCommitted += (s, id) => OnFormatChosen(paramId, id);
            }
            else
            {
                cell.SetChoices(new ChoiceList(
                    new[] { new Choice("_none", "—") }, "_none"));
            }
            stack.Children.Add(cell);

            if (!string.IsNullOrEmpty(insp.FormatHint))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = insp.FormatHint,
                    FontSize = 11,
                    Foreground = DisplayPalette.KLabelBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            }
            return stack;
        }

        private static UIElement BuildFirmwareSlot(FieldInspectorModel insp)
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionLabel("FIRMWARE SLOT — fixed", bottom: 6));
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock
            {
                Text = "🔒 Param",
                FontSize = 11.5,
                Foreground = DisplayPalette.SubLabel,
            });
            var idText = new TextBlock
            {
                Text = insp.FirmwareSlotText,
                FontSize = 11.5,
                FontFamily = DisplayPalette.Mono,
                Foreground = DisplayPalette.BaseText,
            };
            Grid.SetColumn(idText, 1);
            line.Children.Add(idText);
            stack.Children.Add(line);
            return stack;
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

        // ── Gestures / commits ────────────────────────────────────────────

        private void OnSlotClicked(ushort paramId)
        {
            if (_editModel == null)
                return;
            _editModel.SelectParam(paramId);
            RenderTwin(force: true);
            RenderInspector();
        }

        private void OnPickProperty(ushort paramId, string current)
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
            var itmPages = CollectItmPageProperties();
            Func<string, object> valueReader = name =>
            {
                if (_propertyCatalog != null
                    && _propertyCatalog.TryReadPropertyValue(name, out object value))
                    return value;
                return null;
            };
            if (!PropertyPickerDialog.TryPick(owner, builtIns, all, mappedRoles, current,
                    _pickerStore, itmPages, valueReader,
                    out string picked, out PropertyKind kind))
                return;

            var cfg = _editModel.SetSource(paramId, kind, picked);
            ApplyAndReload(cfg);
            _editModel.SelectParam(paramId);
            RenderAll();
        }

        private void OnFormatChosen(ushort paramId, string formatId)
        {
            if (_editModel == null || _host == null)
                return;
            if (ReconcileIfExternallyChanged())
                return;

            // One-release downgrade mirror: Lap/Position format also writes the retired
            // Show*Total toggles so a pre-6b build still sees the chosen total state.
            if (DisplayPagesEditModel.FormatMirrorsShowTotal(paramId) && _settings != null)
            {
                bool withTotal = DisplayPagesEditModel.ShowTotalFromFormat(formatId);
                if (paramId == ItmParam.Lap)
                    _settings.ItmShowLapTotal = withTotal;
                else
                    _settings.ItmShowPositionTotal = withTotal;
                _host.NotifySettingsChanged();
            }

            var cfg = _editModel.SetFormat(paramId, formatId);
            ApplyAndReload(cfg);
            _editModel.SelectParam(paramId);
            RenderAll();
        }

        private void OnResetToDefault(ushort paramId)
        {
            if (_editModel == null || _host == null)
                return;
            if (ReconcileIfExternallyChanged())
                return;
            var cfg = _editModel.ResetToDefault(paramId);
            ApplyAndReload(cfg);
            _editModel.SelectParam(paramId);
            RenderAll();
        }

        private bool ReconcileIfExternallyChanged()
        {
            if (_host == null || ReferenceEquals(_host.GetDisplayConfig(), _editModelSource))
                return false;
            EnterPagesEditor();
            return true;
        }

        private void ApplyAndReload(DisplayCustomizationConfig cfg)
        {
            _host.ApplyDisplayConfig(cfg);
            _editModelSource = _host.GetDisplayConfig();
            // Rebuild the model from the normalized, republished config so the UI
            // sees whatever the validator kept. Preserve the current page/param
            // selection across the reload.
            var page = _editModel.SelectedPage;
            var param = _editModel.SelectedParamId;
            _editModel = NewEditModel(_editModelSource);
            _editModel.SelectPage(page);
            if (param.HasValue)
                _editModel.SelectParam(param.Value);
            ConfigApplied?.Invoke(this, EventArgs.Empty);
        }

        // FieldMappings sources first, then BuiltInProperties.All — same catalog the
        // Triggers picker's "On your ITM pages" rail uses.
        private IReadOnlyList<string> CollectItmPageProperties()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var mappings = _host?.GetDisplayConfig()?.FieldMappings;
            if (mappings != null)
            {
                foreach (var kv in mappings)
                {
                    string name = kv.Value?.Source?.Name;
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                        result.Add(name);
                }
            }
            foreach (var name in BuiltInProperties.All)
            {
                if (seen.Add(name))
                    result.Add(name);
            }
            return result;
        }
    }
}
