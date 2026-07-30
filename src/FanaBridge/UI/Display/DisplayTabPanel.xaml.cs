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
        private object _lastManual;
        private object _lastAggregates;
        private bool _lastInGame;

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
            // The engine allocates a fresh composed record every tick, so a reference
            // gate would fire on all of them and the child views would rebuild (and
            // steal focus) 10×/s. Content comparison — ignoring the tick stamp —
            // passes only real arbitration changes through.
            // Manual / Aggregates / InGame are envelope facts the views render too —
            // the runtime keeps their references stable while unchanged, so reference
            // compares are exact (they were invisible behind the old always-new
            // snapshot reference; the content gate must carry them explicitly).
            bool changed = force
                || !ReferenceEquals(values, _lastValues)
                || !SameComposedContent(composed, _lastComposed)
                || !ReferenceEquals(config, _lastConfig)
                || _lastDisplayType != displayType
                || !string.Equals(status, _lastStatus, StringComparison.Ordinal)
                || !ReferenceEquals(envelope?.Manual, _lastManual)
                || !ReferenceEquals(envelope?.Aggregates, _lastAggregates)
                || (envelope?.InGame ?? false) != _lastInGame;

            _lastValues = values;
            _lastComposed = composed;
            _lastConfig = config;
            _lastDisplayType = displayType;
            _lastStatus = status;
            _lastManual = envelope?.Manual;
            _lastAggregates = envelope?.Aggregates;
            _lastInGame = envelope?.InGame ?? false;
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

        /// <summary>
        /// Content equality for the poll gate. <see cref="ComposedResolutionRecord.TickMs"/>
        /// is deliberately ignored — it advances every tick and the UI only uses it as an
        /// existence check; everything the views actually render is compared.
        /// </summary>
        internal static bool SameComposedContent(
            ComposedResolutionRecord a, ComposedResolutionRecord b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;

            return string.Equals(a.DeviceKey, b.DeviceKey, StringComparison.Ordinal)
                && a.HasDeviceBlock == b.HasDeviceBlock
                && a.PageKnowledge.Equals(b.PageKnowledge)
                && a.RevertedThisTick == b.RevertedThisTick
                && a.AdoptWarnedThisTick == b.AdoptWarnedThisTick
                && a.ItmDeviceId == b.ItmDeviceId
                && a.SurfaceHeld == b.SurfaceHeld
                && a.ReleaseEdge == b.ReleaseEdge
                && a.HasCapabilityEnvelope == b.HasCapabilityEnvelope
                && ReferenceEquals(a.CapabilityEnvelope, b.CapabilityEnvelope)
                && SameItems(a.SurfaceWinners, b.SurfaceWinners)
                && SameItems(a.CarrierStatuses, b.CarrierStatuses)
                && SameItems(a.CarrierSnapshots, b.CarrierSnapshots)
                && SameStrings(a.DismissedCarrierIds, b.DismissedCarrierIds);
        }

        private static bool SameItems<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
            where T : struct
        {
            if (ReferenceEquals(a, b))
                return true;
            int countA = a?.Count ?? 0;
            int countB = b?.Count ?? 0;
            if (countA != countB)
                return false;
            for (int i = 0; i < countA; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }

        private static bool SameStrings(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (ReferenceEquals(a, b))
                return true;
            int countA = a?.Count ?? 0;
            int countB = b?.Count ?? 0;
            if (countA != countB)
                return false;
            for (int i = 0; i < countA; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
