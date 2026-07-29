using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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

        [Fact]
        public void PhubPbme_CompositeBindsCatalog_AddPicker_OverrideDoor_AndOrigin()
        {
            OnSta(() =>
            {
                var host = new FakeHost
                {
                    WheelCode = "PHUB",
                    ModuleCode = "PBME",
                };
                host.SetLive(new DisplayConfigV2
                {
                    Settings = new SettingsBlock { Mode = SettingsMode.On },
                    Pages = new List<PageEntry>
                    {
                        new PageEntry
                        {
                            Kind = PageEntryKind.ItmPage,
                            CatalogPageId = "tyreTemps",
                            Removed = true,
                        },
                    },
                    Priority = new PriorityLadder
                    {
                        Rest = new RestBlock
                        {
                            InSessionPage = new PageRef
                            {
                                Kind = PageRefKind.ItmPage,
                                CatalogPageId = "lapInfo",
                            },
                            Idle = new IdleSpec { Kind = IdleKind.Blank },
                        },
                    },
                });

                var panel = new DisplayTabPanel();
                panel.BindCore(
                    host,
                    new EmptyPropertyCatalog(),
                    new EmptyRoleCatalog(),
                    pickerStore: null);

                Assert.Equal("pbme", panel.viewPriorityV2.BoundCatalog.WheelId);
                Assert.Contains(
                    panel.viewAddPageV2.ModelForTest.ItmChoices,
                    p => p.CatalogPageId == "tyreTemps");
                panel.viewAddPageV2.PopulateItmPickerForTest();
                Assert.Equal(Visibility.Collapsed, panel.viewAddPageV2.txtItmPickerEmpty.Visibility);
                Assert.Single(panel.viewAddPageV2.listItmChoices.Items);
                Assert.True(panel.viewPagesFieldsV2.OpenFirstOverrideFormCore());

                var allPresent = DisplayConfigV2Serializer.Clone(host.GetDisplayConfigV2());
                allPresent.Pages[0].Removed = false;
                host.SetLive(allPresent);
                panel.viewAddPageV2.Poll(force: true);
                panel.viewAddPageV2.PopulateItmPickerForTest();
                Assert.Equal(Visibility.Visible, panel.viewAddPageV2.txtItmPickerEmpty.Visibility);
                Assert.Equal(
                    DisplayCopy.EveryCatalogPageAlreadyOnWheel,
                    panel.viewAddPageV2.txtItmPickerEmpty.Text);
                Assert.Empty(panel.viewAddPageV2.listItmChoices.Items);

                var legacyOnly = DisplayConfigV2Serializer.Clone(allPresent);
                legacyOnly.Settings.Mode = SettingsMode.LegacyOnly;
                host.SetLive(legacyOnly);
                panel.PollForTest(force: false);
                Assert.Equal(
                    DisplayCopy.SegmentDisplay,
                    panel.viewOverviewV2.txtSurfaceWord.Text);

                panel.NavigateToAddPage(AddPageOrigin.PagesAndFields);
                Assert.Equal(DisplayCopy.PagesAndFields, panel.viewAddPageV2.txtPriorityCrumb.Text);
                panel.viewAddPageV2.ReturnToOriginAfterCreateForTest();
                Assert.Equal(Visibility.Visible, panel.viewPagesFieldsV2.Visibility);
                panel.NavigateToAddPage(AddPageOrigin.Priority);
                Assert.Equal(DisplayCopy.Priority, panel.viewAddPageV2.txtPriorityCrumb.Text);

                Assert.Equal(
                    3,
                    ((StackPanel)panel.viewOverviewV2.segMode.Child).Children.Count);
                panel.NavigateToOverviewForTest();
                host.DisplayType = DisplayType.Basic;
                panel.PollForTest(force: false);
                Assert.Equal(
                    2,
                    ((StackPanel)panel.viewOverviewV2.segMode.Child).Children.Count);
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

            public DisplayType DisplayType { get; set; } = DisplayType.Itm;
            public byte ItmDeviceId => 3;
            public string WheelCode { get; set; } = "pbme";
            public string ModuleCode { get; set; } = null!;
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

            public void SetLive(DisplayConfigV2 config)
            {
                _live = config;
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
            public IReadOnlyList<string> GetInputActionTargets() => Array.Empty<string>();
        }
    }
}
