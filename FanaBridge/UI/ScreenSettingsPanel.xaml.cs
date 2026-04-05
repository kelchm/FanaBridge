using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FanaBridge.Adapters;
using FanaBridge.Profiles;
using SimHub.Plugins.UI;
using SHPropertiesPicker = SimHub.Plugins.OutputPlugins.Dash.WPFUI.PropertiesPicker;

namespace FanaBridge.UI
{
    public partial class ScreenSettingsPanel : UserControl
    {
        private DisplaySettings _settings;
        private FanatecDisplayManager _displayManager;
        private bool _suppressEvents;
        private DispatcherTimer _previewTimer;
        private DispatcherTimer _scrollTimer;
        private DisplayLayerCard _selectedCard;
        private Point _dragStartPoint;
        private double _dragCardOriginX;
        private bool _isDragging;
        private int _dragCurrentIndex;
        private DisplayLayerCard _dragCard;

        private static readonly SolidColorBrush SelectedBorder = Frozen(Color.FromRgb(0x44, 0x88, 0xCC));
        private static readonly SolidColorBrush NormalBorder = Frozen(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush SelectedBackground = Frozen(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush NormalBackground = Frozen(Color.FromRgb(0x25, 0x25, 0x25));

        public event Action SettingsChanged;

        public ScreenSettingsPanel()
        {
            InitializeComponent();

            // "Add layer" combo
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Add layer...", IsEnabled = false });
            // Separator: Base
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "── Base Displays ──", IsEnabled = false, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
            foreach (var entry in LayerCatalog.All.Where(l => l.Mode == DisplayLayerMode.Constant))
                cmbAddLayer.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.CatalogKey });
            // Separator: Overlays
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "── Overlays ──", IsEnabled = false, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
            foreach (var entry in LayerCatalog.All.Where(l => l.Mode != DisplayLayerMode.Constant))
                cmbAddLayer.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.CatalogKey });
            // Separator: Custom
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "── Custom ──", IsEnabled = false, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Custom constant", Tag = "custom:Constant" });
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Custom on change", Tag = "custom:OnChange" });
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Custom while true", Tag = "custom:WhileTrue" });
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Custom expression", Tag = "custom:Expression" });
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Static text", Tag = "custom:StaticText" });
            cmbAddLayer.SelectedIndex = 0;

            // Template dropdown (for catalog layers — rebuilt dynamically based on layer mode)
            RebuildTemplateDropdown(DisplayLayerMode.Constant);

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _previewTimer.Tick += (s, e) => UpdateLivePreview();

            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _scrollTimer.Tick += (s, e) => previewDisplay.ScrollTick();

            Loaded += (s, e) => { _previewTimer.Start(); _scrollTimer.Start(); };
            Unloaded += (s, e) =>
            {
                _previewTimer.Stop();
                _scrollTimer.Stop();
                if (_settings != null)
                    _settings.Layers.CollectionChanged -= Layers_CollectionChanged;
            };
        }

        public void Bind(DisplaySettings settings, DisplayType displayType = DisplayType.Basic,
                         FanatecDisplayManager displayManager = null)
        {
            // Unsubscribe from previous settings to avoid leaking handlers
            if (_settings != null)
                _settings.Layers.CollectionChanged -= Layers_CollectionChanged;

            _settings = settings ?? DisplaySettings.CreateDefault();
            _displayManager = displayManager;
            _suppressEvents = true;

            RebuildCardList();
            _settings.Layers.CollectionChanged += Layers_CollectionChanged;

            // Scroll timer uses a fixed interval for the live preview

            borderItmInfo.Visibility = displayType == DisplayType.Itm
                ? Visibility.Visible : Visibility.Collapsed;

            _suppressEvents = false;

            // Select first card
            if (layerStack.Children.Count > 0)
                SelectCard(layerStack.Children[0] as DisplayLayerCard);

            UpdateLivePreview();
        }

        // ── Card list management ─────────────────────────────────────

        private void RebuildCardList()
        {
            layerStack.Children.Clear();
            for (int i = 0; i < _settings.Layers.Count; i++)
                AddCard(_settings.Layers[i], i);
        }

        private DisplayLayerCard AddCard(DisplayLayer layer, int index)
        {
            var card = new DisplayLayerCard { Layer = layer };
            card.SetPriority(index + 1);
            card.MouseLeftButtonDown += Card_MouseDown;
            card.MouseMove += Card_MouseMove;
            card.MouseLeftButtonUp += Card_MouseUp;
            layerStack.Children.Add(card);
            UpdateCardPreview(card);
            return card;
        }

        private void Layers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var selectedLayer = _selectedCard?.Layer;
            RebuildCardList();

            // Re-select the card for the previously selected layer
            DisplayLayerCard match = null;
            if (selectedLayer != null)
            {
                foreach (DisplayLayerCard card in layerStack.Children)
                {
                    if (ReferenceEquals(card.Layer, selectedLayer))
                    { match = card; break; }
                }
            }
            SelectCard(match);
        }

        private void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var card = sender as DisplayLayerCard;
            SelectCard(card);
            _dragStartPoint = e.GetPosition(layerStack);
            _dragCard = card;
            _isDragging = false;
            _dragCurrentIndex = layerStack.Children.IndexOf(card);

            // Record the card's layout origin for offset calculation
            var cardTransform = card.TransformToAncestor(layerStack);
            _dragCardOriginX = cardTransform.Transform(new Point(0, 0)).X;

            card.CaptureMouse();
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCard == null || e.LeftButton != MouseButtonState.Pressed) return;

            var pos = e.GetPosition(layerStack);
            double dx = pos.X - _dragStartPoint.X;

            if (!_isDragging && Math.Abs(dx) > 8)
            {
                _isDragging = true;
                Panel.SetZIndex(_dragCard, 10);
                _dragCard.Opacity = 0.85;
                _dragCard.Cursor = Cursors.SizeWE;
            }

            if (!_isDragging) return;

            // Move the dragged card with the mouse via RenderTransform
            _dragCard.RenderTransform = new TranslateTransform(dx, 0);

            // Determine where the card would drop based on its visual center
            double cardCenterX = _dragCardOriginX + dx + _dragCard.ActualWidth / 2;
            int targetIdx = GetDropIndex(cardCenterX);

            if (targetIdx != _dragCurrentIndex)
                _dragCurrentIndex = targetIdx;

            // Animate other cards: cards between source and target shift to fill/make room
            int sourceIdx = layerStack.Children.IndexOf(_dragCard);
            double cardWidth = _dragCard.ActualWidth + _dragCard.Margin.Left + _dragCard.Margin.Right;

            for (int i = 0; i < layerStack.Children.Count; i++)
            {
                var child = layerStack.Children[i] as DisplayLayerCard;
                if (child == null || child == _dragCard) continue;

                double offset = 0;

                if (_dragCurrentIndex <= sourceIdx)
                {
                    // Dragging left: cards between target..source shift right
                    if (i >= _dragCurrentIndex && i < sourceIdx)
                        offset = cardWidth;
                }
                else
                {
                    // Dragging right: cards between source..target shift left
                    if (i > sourceIdx && i <= _dragCurrentIndex)
                        offset = -cardWidth;
                }

                var tt = child.RenderTransform as TranslateTransform;
                if (tt == null)
                {
                    tt = new TranslateTransform();
                    child.RenderTransform = tt;
                }
                tt.BeginAnimation(TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(
                        offset, new Duration(TimeSpan.FromMilliseconds(150)))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
                    });
            }
        }

        private void Card_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var card = sender as DisplayLayerCard;
            card.ReleaseMouseCapture();

            if (_isDragging)
            {
                // Commit the reorder
                int sourceIdx = _settings.Layers.IndexOf(_dragCard.Layer);
                if (_dragCurrentIndex != sourceIdx)
                {
                    _suppressEvents = true;
                    _settings.Layers.Move(sourceIdx, _dragCurrentIndex);
                    _suppressEvents = false;
                    RebuildCardList();
                    // Re-select the moved card
                    if (_dragCurrentIndex < layerStack.Children.Count)
                        SelectCard(layerStack.Children[_dragCurrentIndex] as DisplayLayerCard);
                    NotifyChanged();
                }
                else
                {
                    // Reset transforms — no move happened
                    ResetDragTransforms();
                }

                _isDragging = false;
            }

            _dragCard = null;
        }

        private void ResetDragTransforms()
        {
            foreach (DisplayLayerCard child in layerStack.Children)
            {
                child.RenderTransform = null;
                child.Opacity = child.Layer.IsEnabled ? 1.0 : 0.5;
                child.Cursor = Cursors.Hand;
                Panel.SetZIndex(child, 0);
            }
        }

        private int GetDropIndex(double cardCenterX)
        {
            // Use layout slot positions (ignoring RenderTransform) for stable hit testing
            double slotX = 0;
            for (int i = 0; i < layerStack.Children.Count; i++)
            {
                var child = layerStack.Children[i] as FrameworkElement;
                if (child == null) continue;

                double slotWidth = child.ActualWidth + child.Margin.Left + child.Margin.Right;
                double slotCenter = slotX + slotWidth / 2;

                if (cardCenterX < slotCenter)
                    return i;

                slotX += slotWidth;
            }
            return layerStack.Children.Count - 1;
        }

        private void SelectCard(DisplayLayerCard card)
        {
            // Deselect previous
            if (_selectedCard != null)
            {
                var border = _selectedCard.FindName("cardBorder") as Border;
                if (border != null)
                {
                    border.BorderBrush = NormalBorder;
                    border.Background = NormalBackground;
                }
            }

            _selectedCard = card;

            if (card != null)
            {
                var border = card.FindName("cardBorder") as Border;
                if (border != null)
                {
                    border.BorderBrush = SelectedBorder;
                    border.Background = SelectedBackground;
                }
            }

            UpdateEditPanel();
        }

        private DisplayLayer SelectedLayer => _selectedCard?.Layer;

        private void UpdateCardPreviews()
        {
            foreach (DisplayLayerCard card in layerStack.Children)
                UpdateCardPreview(card);
        }

        private void UpdateCardPreview(DisplayLayerCard card)
        {
            if (card.Layer == null) return;

            string text = EvaluateLayerForPreview(card.Layer);
            text = FanatecDisplayManager.AlignText(text, card.Layer.DisplayFormat);
            card.SetPreviewText(text);

            // Status dot
            if (_displayManager != null)
            {
                bool isWinning = _displayManager.WinningLayer == card.Layer;
                bool isActive = _displayManager.IsLayerActive(card.Layer);
                card.SetStatus(card.Layer.IsEnabled, isWinning, isActive);
            }
            else
            {
                card.SetStatus(card.Layer.IsEnabled, false, false);
            }
        }

        // TODO: This method duplicates evaluation logic from FanatecDisplayManager.
        // The display manager should be the single source of truth for all layer
        // evaluation, formatting, and alignment — both runtime and UI preview.
        // See: FanatecDisplayManager.EvaluateLayerPreview, GetDisplayText, FormatValue
        private string EvaluateLayerForPreview(DisplayLayer layer)
        {
            // Static text doesn't need the plugin manager at all
            if (layer.Source == DisplaySource.FixedText)
                return layer.FixedText ?? "";

            // Try via display manager first (has formatting logic)
            var pm = FanatecPlugin.Instance?.PluginManager;
            if (_displayManager != null && pm != null)
            {
                try { return _displayManager.EvaluateLayerPreview(pm, layer); }
                catch { }
            }

            // Fallback: try reading the property directly
            if (pm != null && !string.IsNullOrEmpty(layer.PropertyName))
            {
                try
                {
                    var val = pm.GetPropertyValue(layer.PropertyName);
                    if (val != null) return val.ToString();
                }
                catch { }
            }

            return "";
        }

        // ── Live combined preview ────────────────────────────────────

        private void UpdateLivePreview()
        {
            if (_settings == null) return;

            UpdateCardPreviews();

            if (_displayManager != null && !string.IsNullOrEmpty(_displayManager.CurrentText))
            {
                // Hardware connected — show what the wheel is actually displaying
                previewDisplay.SetText(_displayManager.CurrentText);
                txtActiveLayer.Text = string.IsNullOrEmpty(_displayManager.ActiveScreenName)
                    ? "" : "Active: " + _displayManager.ActiveScreenName;
            }
            else
            {
                // No hardware — simulate the layer stack using actual game state.
                bool gameRunning = false;
                var pm = FanatecPlugin.Instance?.PluginManager;
                if (pm != null)
                {
                    try { gameRunning = pm.LastData != null && pm.LastData.GameRunning; }
                    catch { }
                }

                // Find the first enabled constant layer matching current state.
                string text = "";
                string name = "";

                foreach (var layer in _settings.Layers)
                {
                    if (!layer.IsEnabled || layer.Mode != DisplayLayerMode.Constant) continue;
                    bool visible = (gameRunning && layer.ShowWhenRunning)
                                || (!gameRunning && layer.ShowWhenIdle);
                    if (visible)
                    {
                        text = FanatecDisplayManager.AlignText(
                            EvaluateLayerForPreview(layer), layer.DisplayFormat);
                        name = layer.Name;
                        break;
                    }
                }

                // Fallback: if nothing matched, show the first enabled constant layer
                if (name == "")
                {
                    foreach (var layer in _settings.Layers)
                    {
                        if (!layer.IsEnabled || layer.Mode != DisplayLayerMode.Constant) continue;
                        text = FanatecDisplayManager.AlignText(
                            EvaluateLayerForPreview(layer), layer.DisplayFormat);
                        name = layer.Name;
                        break;
                    }
                }

                previewDisplay.SetText(text);
                txtActiveLayer.Text = string.IsNullOrEmpty(name) ? "" : "Preview: " + name;
            }
        }

        // ── Edit panel ───────────────────────────────────────────────

        private void UpdateEditPanel()
        {
            var layer = SelectedLayer;
            if (layer == null)
            {
                borderEditPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _suppressEvents = true;
            borderEditPanel.Visibility = Visibility.Visible;
            txtEditHeader.Text = "Layer Settings \u2014 " + (layer.Name ?? "Untitled");
            chkEnabled.IsChecked = layer.IsEnabled;

            bool isCustom = layer.IsCustom;
            bool isConstant = layer.Mode == DisplayLayerMode.Constant;
            bool isOnChange = layer.Mode == DisplayLayerMode.OnChange;
            bool isWhileTrue = layer.Mode == DisplayLayerMode.WhileTrue;
            bool isExpressionMode = layer.Mode == DisplayLayerMode.Expression;
            bool isFixedText = layer.Source == DisplaySource.FixedText;
            bool isProperty = layer.Source == DisplaySource.Property;

            // Template dropdown (any catalog layer)
            panelTemplate.Visibility = !isCustom ? Visibility.Visible : Visibility.Collapsed;
            if (!isCustom)
            {
                RebuildTemplateDropdown(layer.Mode);
                SelectComboByTag(cmbTemplate, layer.CatalogKey ?? "Custom");
            }

            // Editing fields — Expression mode shows only expression + display format
            panelName.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            panelTrigger.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            panelWatch.Visibility = (isCustom && !isExpressionMode && (isOnChange || isWhileTrue)) ? Visibility.Visible : Visibility.Collapsed;
            panelDuration.Visibility = (isOnChange && !isExpressionMode) ? Visibility.Visible : Visibility.Collapsed;
            panelDisplaySource.Visibility = (isCustom && !isExpressionMode) ? Visibility.Visible : Visibility.Collapsed;
            panelProperty.Visibility = (isCustom && !isExpressionMode && isProperty) ? Visibility.Visible : Visibility.Collapsed;
            panelFixedText.Visibility = (isCustom && !isExpressionMode && isFixedText) ? Visibility.Visible : Visibility.Collapsed;
            panelExpression.Visibility = (isCustom && isExpressionMode) || (!isCustom && isExpressionMode) ? Visibility.Visible : Visibility.Collapsed;
            panelDisplayFormat.Visibility = (isCustom && !isFixedText) || isExpressionMode ? Visibility.Visible : Visibility.Collapsed;
            bool isText = layer.DisplayFormat == DisplayFormat.Text;
            panelScrollSpeed.Visibility = (isCustom && isText) ? Visibility.Visible : Visibility.Collapsed;
            panelShowWhen.Visibility = (isConstant && !isExpressionMode) ? Visibility.Visible : Visibility.Collapsed;

            // Populate fields
            txtName.Text = layer.Name ?? "";
            SelectComboByTag(cmbTrigger, layer.Mode.ToString());
            txtWatch.Text = layer.WatchProperty ?? "";
            SelectComboByTag(cmbDuration, layer.DurationMs.ToString());
            SelectComboByTag(cmbDisplaySource, layer.Source.ToString());
            txtProperty.Text = layer.PropertyName ?? "";
            txtFixedText.Text = layer.FixedText ?? "";
            txtExpression.Text = layer.Expression ?? "";
            SelectComboByTag(cmbDisplayFormat, layer.DisplayFormat.ToString());
            SelectComboByTag(cmbLayerScrollSpeed, layer.ScrollSpeedMs.ToString());
            chkRunning.IsChecked = layer.ShowWhenRunning;
            chkIdle.IsChecked = layer.ShowWhenIdle;

            _suppressEvents = false;
        }

        private void NotifyChanged()
        {
            if (_suppressEvents) return;
            // Refresh the selected card's visual state
            _selectedCard?.Refresh();
            SettingsChanged?.Invoke();
            UpdateLivePreview();
        }

        // ── Add / Remove / Reorder ───────────────────────────────────

        private void CmbAddLayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null || cmbAddLayer.SelectedIndex <= 0) return;
            var tag = (cmbAddLayer.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;

            DisplayLayer layer;
            if (tag.StartsWith("custom:"))
            {
                string subtype = tag.Substring(7);
                if (subtype == "StaticText")
                {
                    layer = new DisplayLayer
                    {
                        Name = "Static Text", Mode = DisplayLayerMode.Constant,
                        Source = DisplaySource.FixedText, FixedText = "---",
                        ShowWhenIdle = true, IsEnabled = true,
                    };
                }
                else
                {
                    DisplayLayerMode mode;
                    Enum.TryParse(subtype, out mode);
                    DisplaySource source = mode == DisplayLayerMode.Expression ? DisplaySource.Expression
                        : mode == DisplayLayerMode.WhileTrue ? DisplaySource.FixedText
                        : DisplaySource.Property;
                    string defaultName = mode == DisplayLayerMode.Expression ? "Custom expression"
                        : mode == DisplayLayerMode.WhileTrue ? "Custom overlay"
                        : mode == DisplayLayerMode.OnChange ? "Custom overlay"
                        : "Custom display";
                    layer = new DisplayLayer
                    {
                        Name = defaultName, Mode = mode,
                        Source = source,
                        DisplayFormat = mode == DisplayLayerMode.Expression ? DisplayFormat.Text : DisplayFormat.Number,
                        DurationMs = 2000, IsEnabled = true,
                        ShowWhenRunning = mode == DisplayLayerMode.Constant,
                    };
                }
            }
            else
            {
                layer = LayerCatalog.CreateFromCatalog(tag);
            }

            if (layer != null)
            {
                _settings.Layers.Add(layer);
                // RebuildCardList fires via CollectionChanged; select the new card
                if (layerStack.Children.Count > 0)
                    SelectCard(layerStack.Children[layerStack.Children.Count - 1] as DisplayLayerCard);
                NotifyChanged();
            }

            _suppressEvents = true;
            cmbAddLayer.SelectedIndex = 0;
            _suppressEvents = false;
        }

        private void BtnRemove_Click(object s, RoutedEventArgs e)
        {
            if (SelectedLayer == null) return;
            int idx = _settings.Layers.IndexOf(SelectedLayer);
            _settings.Layers.Remove(SelectedLayer);
            if (layerStack.Children.Count > 0)
                SelectCard(layerStack.Children[Math.Min(idx, layerStack.Children.Count - 1)] as DisplayLayerCard);
            else
                SelectCard(null);
            NotifyChanged();
        }

        private void BtnLeft_Click(object s, RoutedEventArgs e)
        {
            if (SelectedLayer == null) return;
            int idx = _settings.Layers.IndexOf(SelectedLayer);
            if (idx > 0)
            {
                _settings.Layers.Move(idx, idx - 1);
                SelectCard(layerStack.Children[idx - 1] as DisplayLayerCard);
                NotifyChanged();
            }
        }

        private void BtnRight_Click(object s, RoutedEventArgs e)
        {
            if (SelectedLayer == null) return;
            int idx = _settings.Layers.IndexOf(SelectedLayer);
            if (idx < _settings.Layers.Count - 1)
            {
                _settings.Layers.Move(idx, idx + 1);
                SelectCard(layerStack.Children[idx + 1] as DisplayLayerCard);
                NotifyChanged();
            }
        }

        // ── Edit event handlers ──────────────────────────────────────

        private void CmbTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            var tag = (cmbTemplate.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag == null) return;

            if (tag == "Custom")
            {
                SelectedLayer.CatalogKey = null;
            }
            else
            {
                var template = LayerCatalog.FindByKey(tag);
                if (template != null)
                {
                    SelectedLayer.CatalogKey = tag;
                    SelectedLayer.Name = template.Name;
                    SelectedLayer.Mode = template.Mode;
                    SelectedLayer.Source = template.Source;
                    SelectedLayer.PropertyName = template.PropertyName;
                    SelectedLayer.DisplayFormat = template.DisplayFormat;
                    SelectedLayer.FixedText = template.FixedText;
                    SelectedLayer.Expression = template.Expression;
                    SelectedLayer.ScrollSpeedMs = template.ScrollSpeedMs;
                    SelectedLayer.WatchProperty = template.WatchProperty;
                    SelectedLayer.DurationMs = template.DurationMs;
                    SelectedLayer.ShowWhenRunning = template.ShowWhenRunning;
                    SelectedLayer.ShowWhenIdle = template.ShowWhenIdle;
                }
            }
            UpdateEditPanel();
            NotifyChanged();
        }

        private void RebuildTemplateDropdown(DisplayLayerMode mode)
        {
            cmbTemplate.Items.Clear();
            bool isBase = mode == DisplayLayerMode.Constant;

            foreach (var entry in LayerCatalog.All.Where(l =>
                isBase ? l.Mode == DisplayLayerMode.Constant : l.Mode != DisplayLayerMode.Constant))
            {
                cmbTemplate.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.CatalogKey });
            }
            cmbTemplate.Items.Add(new ComboBoxItem { Content = "Custom", Tag = "Custom" });
        }

        private void TxtName_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.Name = txtName.Text;
            NotifyChanged();
        }

        private void CmbTrigger_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            var tag = (cmbTrigger.SelectedItem as ComboBoxItem)?.Tag as string;
            DisplayLayerMode mode;
            if (tag != null && Enum.TryParse(tag, out mode))
            {
                SelectedLayer.Mode = mode;
                UpdateEditPanel();
                NotifyChanged();
            }
        }

        private void TxtWatch_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.WatchProperty = txtWatch.Text;
            NotifyChanged();
        }

        private void BtnBrowseWatch_Click(object s, RoutedEventArgs e) => BrowseProperty(
            r => { if (SelectedLayer != null) { SelectedLayer.WatchProperty = r; txtWatch.Text = r; NotifyChanged(); } });

        private void CmbDuration_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            var tag = (cmbDuration.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag != null) { SelectedLayer.DurationMs = int.Parse(tag); NotifyChanged(); }
        }

        private void CmbDisplaySource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            var tag = (cmbDisplaySource.SelectedItem as ComboBoxItem)?.Tag as string;
            DisplaySource src;
            if (tag != null && Enum.TryParse(tag, out src))
            {
                SelectedLayer.Source = src;
                UpdateEditPanel();
                NotifyChanged();
            }
        }

        private void TxtProperty_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.PropertyName = txtProperty.Text;
            NotifyChanged();
        }

        private void BtnBrowseProperty_Click(object s, RoutedEventArgs e) => BrowseProperty(
            r => { if (SelectedLayer != null) { SelectedLayer.PropertyName = r; txtProperty.Text = r; NotifyChanged(); } });

        private void CmbDisplayFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            var tag = (cmbDisplayFormat.SelectedItem as ComboBoxItem)?.Tag as string;
            DisplayFormat fmt;
            if (tag != null && Enum.TryParse(tag, out fmt))
            {
                SelectedLayer.DisplayFormat = fmt;
                UpdateEditPanel();
                NotifyChanged();
            }
        }

        private void CmbLayerScrollSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            var tag = (cmbLayerScrollSpeed.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag != null) { SelectedLayer.ScrollSpeedMs = int.Parse(tag); NotifyChanged(); }
        }

        private void TxtFixedText_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.FixedText = txtFixedText.Text;
            NotifyChanged();
        }

        private void TxtExpression_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.Expression = txtExpression.Text;
            NotifyChanged();
        }

        private void ChkEnabled_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.IsEnabled = chkEnabled.IsChecked == true;
            _selectedCard?.Refresh();
            SettingsChanged?.Invoke();
            UpdateLivePreview();
        }

        private void ChkRunning_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.ShowWhenRunning = chkRunning.IsChecked == true;
            NotifyChanged();
        }

        private void ChkIdle_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressEvents || SelectedLayer == null) return;
            SelectedLayer.ShowWhenIdle = chkIdle.IsChecked == true;
            NotifyChanged();
        }

        // ── Helpers ──────────────────────────────────────────────────

        private void BrowseProperty(Action<string> onResult)
        {
            try
            {
                var picker = new SHPropertiesPicker();
                picker.ShowDialogWindow(this, () =>
                {
                    if (picker.Result != null)
                        onResult(picker.Result.GetPropertyName());
                });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("ScreenSettingsPanel: PropertiesPicker failed: " + ex.Message);
            }
        }

        private static void SelectComboByTag(ComboBox combo, string tagValue)
        {
            foreach (ComboBoxItem item in combo.Items)
                if ((string)item.Tag == tagValue) { combo.SelectedItem = item; return; }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
