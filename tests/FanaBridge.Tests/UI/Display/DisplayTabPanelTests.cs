using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    public class DisplayTabPanelTests
    {
        [Fact]
        public void NoDocument_NeverBlank()
        {
            OnSta(() =>
            {
                // A registered device has a Display tab before its wheel connects.
                var host = new FakeHost();
                var panel = new DisplayTabPanel();
                panel.BindCore(
                    host,
                    new EmptyPropertyCatalog(),
                    new EmptyRoleCatalog(),
                    pickerStore: null);

                Assert.Equal(Visibility.Visible, panel.panelBakePending.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.viewOverviewV2.Visibility);
                Assert.Equal(DisplayCopy.BakePendingTitle, panel.txtBakePendingTitle.Text);
                Assert.Equal(DisplayCopy.BakePendingBody, panel.txtBakePendingBody.Text);
                Assert.Equal(
                    DisplayCopy.BakePendingDisconnected,
                    panel.txtBakePendingDisconnected.Text);
                Assert.Equal(
                    Visibility.Visible,
                    panel.txtBakePendingDisconnected.Visibility);

                // The first live frame bakes the v2 document; the next panel poll
                // replaces pending with Overview.
                host.BakeFromLiveFrame();
                panel.PollForTest();

                Assert.Equal(Visibility.Collapsed, panel.panelBakePending.Visibility);
                Assert.Equal(Visibility.Visible, panel.viewOverviewV2.Visibility);
            });
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

        private sealed class FakeHost : IDisplayPanelHost
        {
            private DisplayConfigV2 _live = null!;

            public DisplayType DisplayType => DisplayType.Itm;
            public byte ItmDeviceId => 3;
            public string WheelCode => "pbme";
            public DisplayPanelSnapshot Snapshot => null!;

            public DisplayConfigV2 GetDisplayConfigV2() => _live;

            public void ApplyDisplayConfigV2(DisplayConfigV2 config)
            {
                _live = config;
            }

            public bool TryApplyDisplayConfigV2(
                DisplayConfigV2 expected,
                DisplayConfigV2 config)
            {
                if (!ReferenceEquals(_live, expected))
                    return false;
                _live = config;
                return true;
            }

            public void BakeFromLiveFrame()
            {
                _live = DisplayConfigV2Validator.Normalize(
                    new DisplayConfigV2(),
                    _ => { });
            }
        }

        private sealed class EmptyPropertyCatalog : IDisplayPropertyCatalog
        {
            public IReadOnlyList<string> GetAllPropertyNames()
                => Array.Empty<string>();

            public bool TryReadPropertyValue(string name, out object value)
            {
                value = null!;
                return false;
            }
        }

        private sealed class EmptyRoleCatalog : IMappedRoleCatalog
        {
            public MappedRoles GetMappedRoles() => MappedRoles.None;
        }
    }
}
