using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FanaBridge.Adapters;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The v2-only per-device Display tab. Overview is the hub for the Priority,
    /// Pages &amp; Fields, Add Page, and Diagnostics spokes. A missing v2 document
    /// renders the honest bake-pending state until the host's first-live-frame bake
    /// creates the document.
    /// </summary>
    public partial class DisplayTabPanel : UserControl
    {
        private enum TabView
        {
            Overview,
            Diagnostics,
            Priority,
            PagesFields,
            AddPage,
        }

        private IDisplayPanelHost _host;
        private Dictionary<TabView, UIElement> _views;
        private TabView _currentView = TabView.Overview;
        private AddPageOrigin _addPageOrigin = AddPageOrigin.Priority;
        private DispatcherTimer _timer;
        private DisplayValuesSnapshot _lastValues;
        private ComposedResolutionRecord _lastComposed;
        private DisplayConfigV2 _lastConfig;
        private DisplayType? _lastDisplayType;
        private string _lastStatus;

        public DisplayTabPanel()
        {
            InitializeComponent();
            txtBakePendingTitle.Text = DisplayCopy.BakePendingTitle;
            txtBakePendingBody.Text = DisplayCopy.BakePendingBody;
            txtBakePendingDisconnected.Text = DisplayCopy.BakePendingDisconnected;
        }

        internal void Bind(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog,
            IDisplayPickerStore pickerStore)
        {
            BindCore(host, propertyCatalog, roleCatalog, pickerStore);
        }

        internal void BindCore(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog,
            IDisplayPickerStore pickerStore)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            if (propertyCatalog == null)
                throw new ArgumentNullException(nameof(propertyCatalog));
            if (roleCatalog == null)
                throw new ArgumentNullException(nameof(roleCatalog));

            _views = new Dictionary<TabView, UIElement>
            {
                { TabView.Overview, viewOverviewV2 },
                { TabView.Diagnostics, viewDiagnostics },
                { TabView.Priority, viewPriorityV2 },
                { TabView.PagesFields, viewPagesFieldsV2 },
                { TabView.AddPage, viewAddPageV2 },
            };

            WheelCatalog wheelCatalog = null;
            CatalogLoader.TryResolve(
                _host.WheelCode,
                out wheelCatalog,
                _ => { },
                itmDeviceId: _host.ItmDeviceId,
                moduleCode: _host.ModuleCode);

            viewOverviewV2.Bind(_host, catalog: wheelCatalog);
            viewOverviewV2.ControlMapperRequested += (s, e) => OpenControlMapper();
            viewOverviewV2.DiagnosticsRequested += (s, e) => NavigateTo(TabView.Diagnostics);
            viewOverviewV2.PriorityRequested += (s, e) => NavigateTo(TabView.Priority);
            viewOverviewV2.PagesAndFieldsRequested += (s, e) => NavigateTo(TabView.PagesFields);

            viewDiagnostics.Bind(_host);
            viewDiagnostics.BackRequested += (s, e) => NavigateTo(TabView.Overview);

            viewPriorityV2.Bind(
                _host,
                catalog: wheelCatalog,
                propertyCatalog: propertyCatalog,
                roleCatalog: roleCatalog,
                pickerStore: pickerStore);
            viewPriorityV2.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewPriorityV2.SetPagesAndFieldsDestinationLive(true);
            viewPriorityV2.PagesAndFieldsRequested += (s, e) => NavigateTo(TabView.PagesFields);
            viewPriorityV2.AddPageRequested += (s, e) =>
                NavigateToAddPage(AddPageOrigin.Priority);

            viewPagesFieldsV2.Bind(
                _host,
                catalog: wheelCatalog,
                propertyCatalog: propertyCatalog,
                roleCatalog: roleCatalog,
                pickerStore: pickerStore);
            viewPagesFieldsV2.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewPagesFieldsV2.PriorityRequested += (s, e) => NavigateTo(TabView.Priority);
            viewPagesFieldsV2.AddPageRequested += (s, e) =>
                NavigateToAddPage(AddPageOrigin.PagesAndFields);

            viewAddPageV2.Bind(_host, catalog: wheelCatalog);
            viewAddPageV2.BackRequested += (s, e) => NavigateTo(TabView.Overview);
            viewAddPageV2.OriginRequested += (s, e) => NavigateTo(
                _addPageOrigin == AddPageOrigin.PagesAndFields
                    ? TabView.PagesFields
                    : TabView.Priority);
            viewAddPageV2.EntrypointDoorRequested += (s, e) =>
            {
                NavigateTo(TabView.Priority);
                viewPriorityV2.OpenFirstEntrypointFormCore();
            };
            viewAddPageV2.OverrideDoorRequested += (s, e) =>
            {
                NavigateTo(TabView.PagesFields);
                viewPagesFieldsV2.OpenFirstOverrideFormCore();
            };

            NavigateTo(TabView.Overview);
            ApplyDocumentSurface();
            Poll(force: true);
        }

        private static void OpenControlMapper()
        {
            var pluginManager = FanatecPlugin.Instance?.PluginManager;
            ControlMapperReflection.ShowControlMapperUi(pluginManager);
        }

        private void NavigateTo(TabView view)
        {
            _currentView = view;
            ApplyDocumentSurface();
            if (_host?.GetDisplayConfigV2() == null)
                return;

            switch (view)
            {
                case TabView.Diagnostics:
                    viewDiagnostics.Poll(force: true);
                    break;
                case TabView.Priority:
                    viewPriorityV2.Poll(force: true);
                    break;
                case TabView.PagesFields:
                    viewPagesFieldsV2.Poll(force: true);
                    break;
                case TabView.AddPage:
                    viewAddPageV2.Poll(force: true);
                    break;
                default:
                    viewOverviewV2.Poll(force: true);
                    break;
            }
        }

        internal void NavigateToAddPage(AddPageOrigin origin)
        {
            _addPageOrigin = origin;
            viewAddPageV2.SetOrigin(origin);
            NavigateTo(TabView.AddPage);
        }

        internal void NavigateToOverviewForTest()
            => NavigateTo(TabView.Overview);

        /// <summary>
        /// Routes to the selected v2 surface when a v2 document exists; otherwise
        /// shows the bake-pending state. There is no fallback document.
        /// </summary>
        private void ApplyDocumentSurface()
        {
            if (_views == null)
                return;

            bool hasDocument = _host?.GetDisplayConfigV2() != null;
            panelBakePending.Visibility = hasDocument
                ? Visibility.Collapsed
                : Visibility.Visible;
            txtBakePendingDisconnected.Visibility = !hasDocument && _host?.Snapshot == null
                ? Visibility.Visible
                : Visibility.Collapsed;
            foreach (var pair in _views)
            {
                pair.Value.Visibility = hasDocument && pair.Key == _currentView
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };
                _timer.Tick += (s, a) => Poll();
            }

            _timer.Start();
            Poll(force: true);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer?.Stop();
        }

        private void Poll(bool force = false)
        {
            if (_host == null)
                return;

            var config = _host.GetDisplayConfigV2();
            bool hasDocument = config != null;
            if (!hasDocument && _currentView != TabView.Overview)
                _currentView = TabView.Overview;
            ApplyDocumentSurface();
            if (!hasDocument)
                return;

            var envelope = _host.Snapshot;
            var values = envelope?.Values;
            var composed = envelope?.ComposedResolution;
            string status = envelope?.ItmStatus;
            var displayType = _host.DisplayType;
            bool changed = force
                || !ReferenceEquals(values, _lastValues)
                || !ReferenceEquals(composed, _lastComposed)
                || !ReferenceEquals(config, _lastConfig)
                || _lastDisplayType != displayType
                || !string.Equals(status, _lastStatus, StringComparison.Ordinal);

            _lastValues = values;
            _lastComposed = composed;
            _lastConfig = config;
            _lastDisplayType = displayType;
            _lastStatus = status;
            if (!changed)
                return;

            switch (_currentView)
            {
                case TabView.Diagnostics:
                    viewDiagnostics.Poll(force: force);
                    break;
                case TabView.Priority:
                    viewPriorityV2.Poll(force: force);
                    break;
                case TabView.PagesFields:
                    viewPagesFieldsV2.Poll(force: force);
                    break;
                case TabView.AddPage:
                    viewAddPageV2.Poll(force: force);
                    break;
                default:
                    viewOverviewV2.Poll(force: force);
                    break;
            }
        }

        internal void PollForTest(bool force = true)
        {
            Poll(force);
        }
    }
}
