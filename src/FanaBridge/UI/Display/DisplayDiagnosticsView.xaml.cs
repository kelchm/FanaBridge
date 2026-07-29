using System;
using System.Windows;
using System.Windows.Controls;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Minimal diagnostics panel — read-only live table over
    /// <see cref="DisplayDiagnosticsModel"/>. Same poll cadence as Overview; visible
    /// only when a v2 document is live (shell gate). No document writes. Thin view:
    /// model carries every fact; this control only binds and refreshes.
    /// </summary>
    public partial class DisplayDiagnosticsView : UserControl
    {
        private IDisplayPanelHost _host;
        private AliasTable _aliases;
        private DisplayDiagnosticsModel _model;

        /// <summary>‹ ghost back → Overview.</summary>
        public event EventHandler BackRequested;

        public DisplayDiagnosticsView()
        {
            InitializeComponent();
            ApplyStaticCopy();
        }

        /// <summary>Bind once after construction. Alias table optional (condition copy).</summary>
        internal void Bind(IDisplayPanelHost host, AliasTable aliases = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _aliases = aliases;
            Poll(force: true);
        }

        /// <summary>Refresh from the host envelope. Safe to call every poll tick.</summary>
        internal void Poll(bool force = false)
        {
            if (_host == null)
                return;

            var envelope = _host.Snapshot;
            var config = _host.GetDisplayConfigV2();
            var resolution = ProjectResolution(envelope);

            _model = DisplayDiagnosticsModel.Project(resolution, config, _aliases);
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
            txtTitle.Text = DisplayCopy.Diagnostics;
            txtLadderSection.Text = DisplayCopy.DiagnosticsLadderSection;
            txtDeviceSection.Text = DisplayCopy.DiagnosticsDeviceSection;
            txtWheelScreenSection.Text = DisplayCopy.DiagnosticsWheelScreenSection;
            txtManualSection.Text = DisplayCopy.DiagnosticsManualSection;
            txtFloorSection.Text = DisplayCopy.DiagnosticsFloorSection;
        }

        private void ApplyModel(DisplayDiagnosticsModel model)
        {
            if (model == null) return;

            if (!model.HasResolution)
            {
                txtEmptyState.Text = model.EmptyStateLine ?? DisplayCopy.DiagnosticsEmptyState;
                txtEmptyState.Visibility = Visibility.Visible;
                panelContent.Visibility = Visibility.Collapsed;
                return;
            }

            txtEmptyState.Visibility = Visibility.Collapsed;
            panelContent.Visibility = Visibility.Visible;

            listLadderRows.ItemsSource = model.LadderRows;
            listDeviceLines.ItemsSource = model.DeviceLines;
            listWheelScreenLines.ItemsSource = model.WheelScreenLines;
            listManualLines.ItemsSource = model.ManualLines;
            listFloorLines.ItemsSource = model.FloorLines;

            // Hide section chrome when a block has nothing to show (still never blank overall).
            sectionLadder.Visibility = model.LadderRows.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            sectionDevice.Visibility = model.DeviceLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            sectionWheelScreen.Visibility = model.WheelScreenLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            sectionManual.Visibility = model.ManualLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            sectionFloor.Visibility = model.FloorLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);
    }
}
