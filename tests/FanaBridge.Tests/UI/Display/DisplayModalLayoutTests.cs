using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    public class DisplayModalLayoutTests
    {
        [Theory]
        [InlineData(800, 776)]
        [InlineData(420, 396)]
        [InlineData(100, 120)]
        [InlineData(1, 120)]
        public void BoundToHost_LeavesEdgeClearance(double hostHeight, double expected)
        {
            Assert.Equal(expected, DisplayModalLayout.BoundToHost(hostHeight, 640));
        }

        [Fact]
        public void BoundToHost_UsesFallbackBeforeAttachment()
        {
            Assert.Equal(640, DisplayModalLayout.BoundToHost(0, 640));
            Assert.Equal(640, DisplayModalLayout.BoundToHost(double.NaN, 640));
        }

        [Fact]
        public void AllV2FormModals_KeepFooterOutsideScrollableBody_AndMeasureWithinBound()
        {
            OnSta(() =>
            {
                const double bound = 396;
                var pages = new DisplayPagesFieldsV2View();
                var priority = new DisplayPriorityV2View();

                AssertModalLayout(
                    pages.chromeOverrideModal,
                    pages.scrollOverrideBody,
                    bound);
                AssertModalLayout(
                    pages.chromeRotationModal,
                    pages.scrollRotationBody,
                    bound);
                AssertModalLayout(
                    priority.chromeEntrypointModal,
                    priority.scrollEntrypointBody,
                    bound);
            });
        }

        private static void AssertModalLayout(
            FrameworkElement chrome,
            ScrollViewer body,
            double maxHeight)
        {
            var dock = Assert.IsType<DockPanel>(((Border)chrome).Child);
            Assert.Contains(
                dock.Children.Cast<UIElement>(),
                child => DockPanel.GetDock(child) == Dock.Bottom);
            Assert.Same(dock, body.Parent);

            chrome.MaxHeight = maxHeight;
            chrome.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(chrome.DesiredSize.Height <= maxHeight);
            Assert.Equal(ScrollBarVisibility.Auto, body.VerticalScrollBarVisibility);
        }

        private static void OnSta(Action body)
        {
            Exception error = null!;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
                ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
