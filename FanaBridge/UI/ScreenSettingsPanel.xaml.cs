using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FanaBridge.Profiles;
using FanaBridge.SegmentDisplay;
using FanaBridge.Shared.Conditions;

namespace FanaBridge.UI
{
    public partial class ScreenSettingsPanel : UserControl
    {
        private SegmentDisplaySettings _settings;
        private SegmentDisplayController _displayController;
        private LayerStackEvaluator _previewEvaluator = new LayerStackEvaluator();
        private bool _suppressEvents;
        private DispatcherTimer _previewTimer;
        private DisplayLayerCard _selectedCard;

        private static readonly SolidColorBrush SelectedBorder = Frozen(Color.FromRgb(0x44, 0x88, 0xCC));
        private static readonly SolidColorBrush NormalBorder = Frozen(Color.FromRgb(0x33, 0x33, 0x33));

        /// <summary>Fired when settings change. The parent should persist.</summary>
        public event Action SettingsChanged;

        public ScreenSettingsPanel()
        {
            InitializeComponent();

            // "Add layer" dropdown
            cmbAddLayer.Items.Add(new ComboBoxItem { Content = "Add layer...", IsEnabled = false });

            // Screens
            cmbAddLayer.Items.Add(new ComboBoxItem
            {
                Content = "\u2500\u2500 Screens \u2500\u2500",
                IsEnabled = false, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray,
            });
            foreach (var entry in SegmentLayerCatalog.All.Where(l => l.Role == LayerRole.Screen))
                cmbAddLayer.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.CatalogKey });

            // Overlays
            cmbAddLayer.Items.Add(new ComboBoxItem
            {
                Content = "\u2500\u2500 Overlays \u2500\u2500",
                IsEnabled = false, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray,
            });
            foreach (var entry in SegmentLayerCatalog.All.Where(l => l.Role == LayerRole.Overlay))
                cmbAddLayer.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.CatalogKey });

            cmbAddLayer.SelectedIndex = 0;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _previewTimer.Tick += (s, e) => UpdateLivePreview();

            Loaded += (s, e) => _previewTimer.Start();
            Unloaded += (s, e) =>
            {
                _previewTimer.Stop();
                if (_settings != null)
                    _settings.Layers.CollectionChanged -= Layers_CollectionChanged;
            };
        }

        /// <summary>
        /// Legacy bind method for backward compatibility during transition.
        /// Accepts the old DisplaySettings and shows the ITM banner based on display type.
        /// Will be removed in Phase 9 when integration is complete.
        /// </summary>
        public void Bind(Adapters.DisplaySettings settings, DisplayType displayType = DisplayType.Basic)
        {
            string mode = settings?.DisplayMode ?? Adapters.DisplaySettings.DefaultMode;
            Bind(SegmentDisplaySettings.MigrateFromLegacy(mode), displayType);
        }

        /// <summary>
        /// Binds the panel to settings. Call once after construction.
        /// </summary>
        public void Bind(SegmentDisplaySettings settings, DisplayType displayType = DisplayType.Basic,
                         SegmentDisplayController displayController = null)
        {
            _suppressEvents = true;

            if (_settings != null)
                _settings.Layers.CollectionChanged -= Layers_CollectionChanged;

            _settings = settings ?? SegmentDisplaySettings.CreateDefault();
            _displayController = displayController;

            _settings.Layers.CollectionChanged += Layers_CollectionChanged;

            borderItmInfo.Visibility = displayType == DisplayType.Itm
                ? Visibility.Visible
                : Visibility.Collapsed;

            RebuildLayerCards();
            _suppressEvents = false;
        }

        // ── Layer cards ─────────────────────────────────────────────

        private void RebuildLayerCards()
        {
            layerStack.Children.Clear();
            _selectedCard = null;

            for (int i = 0; i < _settings.Layers.Count; i++)
            {
                var card = new DisplayLayerCard { Layer = _settings.Layers[i] };
                card.SetPriority(i + 1);
                card.MouseLeftButtonDown += Card_Click;
                layerStack.Children.Add(card);
            }

            UpdateEditPanel();
        }

        private void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var card = sender as DisplayLayerCard;
            if (card == null) return;

            SelectCard(card);
        }

        private void SelectCard(DisplayLayerCard card)
        {
            // Deselect previous
            if (_selectedCard != null)
            {
                var prevBorder = FindChildBorder(_selectedCard, "cardBorder");
                if (prevBorder != null) prevBorder.BorderBrush = NormalBorder;
            }

            _selectedCard = card;

            // Select new
            if (_selectedCard != null)
            {
                var newBorder = FindChildBorder(_selectedCard, "cardBorder");
                if (newBorder != null) newBorder.BorderBrush = SelectedBorder;
            }

            UpdateEditPanel();
        }

        // ── Edit panel ──────────────────────────────────────────────

        private void UpdateEditPanel()
        {
            if (_selectedCard?.Layer == null)
            {
                borderEditPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _suppressEvents = true;
            borderEditPanel.Visibility = Visibility.Visible;
            var layer = _selectedCard.Layer;

            txtEditHeader.Text = layer.Name ?? "Layer";
            chkRunning.IsChecked = layer.ShowWhenRunning;
            chkIdle.IsChecked = layer.ShowWhenIdle;

            // Template panel for catalog layers
            bool isCatalog = !layer.IsCustom;
            panelTemplate.Visibility = isCatalog ? Visibility.Visible : Visibility.Collapsed;
            panelName.Visibility = !isCatalog ? Visibility.Visible : Visibility.Collapsed;

            if (isCatalog)
            {
                RebuildTemplateDropdown(layer.Role);
                SelectTemplate(layer.CatalogKey);
            }
            else
            {
                txtLayerName.Text = layer.Name ?? "";
            }

            // Effect dropdown
            if (layer.Effects != null && layer.Effects.Length > 0)
            {
                var effect = layer.Effects[0];
                if (effect is BlinkEffect)
                    SelectComboByTag(cmbEffect, "Blink");
                else if (effect is FlashEffect)
                    SelectComboByTag(cmbEffect, "Flash");
                else
                    SelectComboByTag(cmbEffect, "None");
            }
            else
            {
                SelectComboByTag(cmbEffect, "None");
            }

            _suppressEvents = false;
        }

        private void RebuildTemplateDropdown(LayerRole role)
        {
            cmbTemplate.Items.Clear();
            foreach (var entry in SegmentLayerCatalog.All.Where(l => l.Role == role))
                cmbTemplate.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.CatalogKey });
        }

        private void SelectTemplate(string key)
        {
            foreach (ComboBoxItem item in cmbTemplate.Items)
            {
                if ((string)item.Tag == key) { cmbTemplate.SelectedItem = item; return; }
            }
        }

        // ── Event handlers ──────────────────────────────────────────

        private void CmbAddLayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            var selected = cmbAddLayer.SelectedItem as ComboBoxItem;
            if (selected?.Tag == null) return;

            string key = (string)selected.Tag;
            var layer = SegmentLayerCatalog.CreateFromCatalog(key);
            if (layer != null)
            {
                _settings.Layers.Add(layer);
                SettingsChanged?.Invoke();
            }

            cmbAddLayer.SelectedIndex = 0;
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCard?.Layer == null || _settings == null) return;
            int idx = _settings.Layers.IndexOf(_selectedCard.Layer);
            if (idx >= 0)
            {
                _settings.Layers.RemoveAt(idx);
                SettingsChanged?.Invoke();
            }
        }

        private void BtnLeft_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void BtnRight_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private void MoveSelected(int direction)
        {
            if (_selectedCard?.Layer == null || _settings == null) return;
            int idx = _settings.Layers.IndexOf(_selectedCard.Layer);
            int newIdx = idx + direction;
            if (idx < 0 || newIdx < 0 || newIdx >= _settings.Layers.Count) return;

            _settings.Layers.Move(idx, newIdx);
            SettingsChanged?.Invoke();
        }

        private void ChkRunning_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _selectedCard?.Layer == null) return;
            _selectedCard.Layer.ShowWhenRunning = chkRunning.IsChecked == true;
            _selectedCard.Refresh();
            SettingsChanged?.Invoke();
        }

        private void ChkIdle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _selectedCard?.Layer == null) return;
            _selectedCard.Layer.ShowWhenIdle = chkIdle.IsChecked == true;
            _selectedCard.Refresh();
            SettingsChanged?.Invoke();
        }

        private void CmbTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _selectedCard?.Layer == null) return;
            var selected = cmbTemplate.SelectedItem as ComboBoxItem;
            if (selected?.Tag == null) return;

            string key = (string)selected.Tag;
            var template = SegmentLayerCatalog.CreateFromCatalog(key);
            if (template == null) return;

            // Replace layer in-place
            int idx = _settings.Layers.IndexOf(_selectedCard.Layer);
            if (idx >= 0)
            {
                // Preserve user's visibility flags
                template.ShowWhenRunning = _selectedCard.Layer.ShowWhenRunning;
                template.ShowWhenIdle = _selectedCard.Layer.ShowWhenIdle;
                _settings.Layers[idx] = template;
                SettingsChanged?.Invoke();
            }
        }

        private void TxtLayerName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _selectedCard?.Layer == null) return;
            _selectedCard.Layer.Name = txtLayerName.Text;
            _selectedCard.Refresh();
            SettingsChanged?.Invoke();
        }

        private void CmbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _selectedCard?.Layer == null) return;
            var selected = cmbEffect.SelectedItem as ComboBoxItem;
            if (selected?.Tag == null) return;

            string tag = (string)selected.Tag;
            switch (tag)
            {
                case "Blink":
                    _selectedCard.Layer.Effects = new SegmentEffect[] { new BlinkEffect() };
                    break;
                case "Flash":
                    _selectedCard.Layer.Effects = new SegmentEffect[] { new FlashEffect() };
                    break;
                default:
                    _selectedCard.Layer.Effects = null;
                    break;
            }

            _selectedCard.Refresh();
            SettingsChanged?.Invoke();
        }

        private void Layers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.Invoke(RebuildLayerCards);
        }

        // ── Live preview ────────────────────────────────────────────

        private void UpdateLivePreview()
        {
            if (_settings == null) return;

            // Use the shared controller if available, otherwise use local evaluator
            if (_displayController != null)
            {
                var winner = _displayController.WinningLayer;
                txtActiveLayer.Text = winner != null ? winner.Name : "";
                string text = _displayController.CurrentText;
                if (!string.IsNullOrEmpty(text))
                    previewDisplay.SetText(text);
                else
                    previewDisplay.Clear();

                // Update card statuses
                foreach (DisplayLayerCard card in layerStack.Children)
                {
                    if (card.Layer == null) continue;
                    bool isWinning = card.Layer == winner;
                    bool isActive = _displayController.ActiveLayers.Contains(card.Layer);
                    card.SetStatus(card.Layer.IsEnabled, isWinning, isActive);
                }
            }
            else
            {
                // Local preview (no hardware)
                var result = _previewEvaluator.Evaluate(null, null, false, _settings);
                txtActiveLayer.Text = result.Winner?.Name ?? "";
                if (!string.IsNullOrEmpty(result.Text))
                    previewDisplay.SetText(result.Text);
                else
                    previewDisplay.Clear();
            }

            // Scroll animation
            previewDisplay.ScrollTick();
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if ((string)item.Tag == tag) { combo.SelectedItem = item; return; }
            }
        }

        private static Border FindChildBorder(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border b && b.Name == name) return b;
                var found = FindChildBorder(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
