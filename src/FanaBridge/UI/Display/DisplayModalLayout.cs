using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Shared v2 modal geometry. Popups are measured against the visible host pane,
    /// not the potentially taller view inside its outer ScrollViewer.
    /// </summary>
    internal static class DisplayModalLayout
    {
        internal const double HostEdgeClearance = 24;
        internal const double MinimumModalHeight = 120;
        private static readonly ConditionalWeakTable<Popup, HostHeightSubscription>
            HostHeightSubscriptions =
                new ConditionalWeakTable<Popup, HostHeightSubscription>();

        internal static double BoundToHost(double hostHeight, double fallbackHeight)
        {
            if (double.IsNaN(hostHeight)
                || double.IsInfinity(hostHeight)
                || hostHeight <= 0)
                return Math.Max(MinimumModalHeight, fallbackHeight);

            return Math.Max(MinimumModalHeight, hostHeight - HostEdgeClearance);
        }

        internal static void Constrain(
            FrameworkElement origin,
            Popup popup,
            FrameworkElement modalChrome,
            double fallbackHeight)
        {
            if (origin == null || popup == null || modalChrome == null)
                return;

            var hostPane = FindHostPane(origin);
            popup.PlacementTarget = hostPane;
            modalChrome.MaxHeight = BoundToHost(VisibleHeight(hostPane), fallbackHeight);
            HostHeightSubscriptions.GetOrCreateValue(popup).Attach(
                hostPane, popup, modalChrome, fallbackHeight);
        }

        private static FrameworkElement FindHostPane(FrameworkElement origin)
        {
            DependencyObject current = origin;
            while (current != null)
            {
                if (current is ScrollViewer scroll)
                    return scroll;

                current = ParentOf(current);
            }

            return Window.GetWindow(origin) ?? origin;
        }

        private static double VisibleHeight(FrameworkElement hostPane)
        {
            if (hostPane is ScrollViewer scroll && scroll.ViewportHeight > 0)
                return scroll.ViewportHeight;
            return hostPane?.ActualHeight ?? 0;
        }

        private static DependencyObject ParentOf(DependencyObject child)
        {
            if (child == null)
                return null;

            try
            {
                var visualParent = VisualTreeHelper.GetParent(child);
                if (visualParent != null)
                    return visualParent;
            }
            catch (InvalidOperationException)
            {
                // Non-visual content can still have a logical parent.
            }

            return LogicalTreeHelper.GetParent(child);
        }

        /// <summary>
        /// Keeps an open popup constrained when only its ancestor pane height changes.
        /// The content view can remain the same height inside a ScrollViewer, so its own
        /// SizeChanged event is not a sufficient signal.
        /// </summary>
        private sealed class HostHeightSubscription
        {
            private FrameworkElement _hostPane;
            private Popup _popup;
            private FrameworkElement _modalChrome;
            private double _fallbackHeight;
            private bool _attached;

            internal void Attach(
                FrameworkElement hostPane,
                Popup popup,
                FrameworkElement modalChrome,
                double fallbackHeight)
            {
                if (_attached && !ReferenceEquals(_hostPane, hostPane))
                    Detach();

                _hostPane = hostPane;
                _popup = popup;
                _modalChrome = modalChrome;
                _fallbackHeight = fallbackHeight;

                if (_attached || _hostPane == null || _popup == null)
                    return;

                _hostPane.SizeChanged += HostPane_SizeChanged;
                _popup.Closed += Popup_Closed;
                _attached = true;
            }

            private void HostPane_SizeChanged(object sender, SizeChangedEventArgs e)
            {
                if (!e.HeightChanged || _popup?.IsOpen != true || _modalChrome == null)
                    return;

                _modalChrome.MaxHeight =
                    BoundToHost(e.NewSize.Height, _fallbackHeight);
            }

            private void Popup_Closed(object sender, EventArgs e)
                => Detach();

            private void Detach()
            {
                if (!_attached)
                    return;

                _hostPane.SizeChanged -= HostPane_SizeChanged;
                _popup.Closed -= Popup_Closed;
                _attached = false;
            }
        }
    }
}
