using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// End-to-end pins for Priority view paths: view CORE handlers → session → TryApply
    /// (production code path minus WPF dispatch).
    /// </summary>
    public class DisplayPriorityV2ViewTests
    {
        /// <summary>
        /// Real view/panel construction needs STA; xunit's default suite thread is MTA.
        /// </summary>
        private static void OnSta(Action body)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
                ExceptionDispatchInfo.Capture(error).Throw();
        }

        private sealed class FakeHost : IDisplayPanelHost
        {
            public DisplayConfigV2 Live = null!;
            public string WheelCode { get; set; } = "pbme";
            public string ModuleCode { get; set; } = null!;

            public DisplaySettings DisplaySettings { get; } = new DisplaySettings();
            public DisplayType DisplayType => DisplayType.Itm;
            public byte ItmDeviceId => 3;
            public DisplayConfigV2 GetDisplayConfigV2() => Live;
            public void ApplyDisplayConfigV2(DisplayConfigV2 config)
            {
                Live = config == null
                    ? null!
                    : DisplayConfigV2Validator.Normalize(
                        DisplayConfigV2Serializer.Clone(config), _ => { });
            }

            public bool TryApplyDisplayConfigV2(DisplayConfigV2 expected, DisplayConfigV2 config)
            {
                if (!ReferenceEquals(Live, expected))
                    return false;
                ApplyDisplayConfigV2(config);
                return true;
            }

            public DisplayPanelSnapshot Snapshot => null!;
        }

        private sealed class EmptyPropertyCatalog : IDisplayPropertyCatalog
        {
            public IReadOnlyList<string> GetAllPropertyNames() => Array.Empty<string>();
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

        private static DisplayConfigV2 SeedDoc()
        {
            return DisplayConfigV2Validator.Normalize(new DisplayConfigV2
            {
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat,
                            Id = "seat-full",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.ItmPage,
                                CatalogPageId = "lapInfo",
                            },
                            // bringUpLifetime domain is whileTrue|forDuration only.
                            BringUpLifetime = new Lifetime
                            {
                                Kind = LifetimeKind.ForDuration,
                                DurationMs = 2500,
                            },
                            ChildRef = new ChildRef
                            {
                                Field = "5",
                                OverrideId = "ov-1",
                            },
                            ExtensionData = new Dictionary<string, JToken>
                            {
                                ["v3RowExtra"] = JToken.FromObject("keep-row"),
                            },
                            Summons = new List<Summon>
                            {
                                new Summon
                                {
                                    Id = "sum-1",
                                    Enabled = true,
                                    Condition = new Condition
                                    {
                                        Source = new ValueSource
                                        {
                                            Kind = ValueSourceKind.BuiltIn,
                                            Name = "Fuel",
                                        },
                                        Operator = ConditionOperator.LessThan,
                                        Value = 4,
                                    },
                                    Lifetime = new Lifetime
                                    {
                                        Kind = LifetimeKind.WhileTrue,
                                    },
                                },
                            },
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat,
                            Id = "seat-2",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.ItmPage,
                                CatalogPageId = "lapTimes",
                            },
                            Summons = new List<Summon>(),
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                },
            }, _ => { });
        }

        [Fact]
        public void Reorder_EndToEnd_PreservesBringUpLifetime_ChildRef_ExtensionData()
        {
            OnSta(() =>
            {
                // Real path: ReorderCore → MoveRow → TryApply (authored row, full fields).
                // Document keeps BringUpLifetime + ChildRef + extension data.
                var host = new FakeHost { Live = SeedDoc() };
                Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));

                var before = host.Live.Priority.Rows.First(r => r.Id == "seat-full");
                Assert.Equal(LifetimeKind.ForDuration, before.BringUpLifetime?.Kind);
                Assert.Equal(2500, before.BringUpLifetime?.DurationMs);
                Assert.Equal("5", before.ChildRef?.Field);

                var view = new DisplayPriorityV2View();
                view.Bind(host, catalog: catalog);
                // Ranked: seat-full=0, seat-2=1, Manual=2 — drop onto seat-2.
                Assert.True(view.ReorderCore("seat-full", targetIndex: 1));

                var row = host.Live.Priority.Rows.First(r => r.Id == "seat-full");
                Assert.Equal(LifetimeKind.ForDuration, row.BringUpLifetime?.Kind);
                Assert.Equal(2500, row.BringUpLifetime?.DurationMs);
                Assert.Equal("5", row.ChildRef?.Field);
                Assert.Equal("ov-1", row.ChildRef?.OverrideId);
                Assert.NotNull(row.ExtensionData);
                Assert.Equal("keep-row", (string)row.ExtensionData["v3RowExtra"]);
            });
        }

        [Fact]
        public void RemovePageContent_EndToEnd_ViewSessionTryApply()
        {
            OnSta(() =>
            {
                Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
                var live = SeedDoc();
                live.Fields[505] = new FieldEntry
                {
                    Overrides = new List<FieldOverride>
                    {
                        new FieldOverride { Id = "ov-ex" },
                    },
                };
                live = DisplayConfigV2Validator.Normalize(
                    DisplayConfigV2Serializer.Clone(live), _ => { }, catalog);

                var host = new FakeHost { Live = live };
                var view = new DisplayPriorityV2View();
                view.Bind(host, catalog: catalog);
                var confirms = new List<DisplayConfigV2EditSession.PageContentRemovalPlan>();
                view.ConfirmRemoveAll = plan =>
                {
                    confirms.Add(plan);
                    return true;
                };

                var target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" };
                Assert.True(DisplayConfigV2EditSession.CanRemovePageContent(target, view.BoundCatalog));
                Assert.True(view.RemoveAllRequestedCore(target));

                Assert.Single(confirms);
                Assert.DoesNotContain(host.Live.Priority.Rows, r => r.Id == "seat-full");
                Assert.Empty(host.Live.Fields[505].Overrides);
            });
        }

        [Fact]
        public void RemovePageContent_ConfirmFalse_NoMutation()
        {
            OnSta(() =>
            {
                Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
                var host = new FakeHost { Live = SeedDoc() };
                var view = new DisplayPriorityV2View();
                view.Bind(host, catalog: catalog);
                int calls = 0;
                view.ConfirmRemoveAll = _ =>
                {
                    calls++;
                    return false;
                };

                var target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" };
                Assert.False(view.RemoveAllRequestedCore(target));

                Assert.Equal(1, calls);
                Assert.Contains(host.Live.Priority.Rows, r => r.Id == "seat-full");
            });
        }

        [Fact]
        public void Bind_WithResolvedCatalog_RemoveAllEnabledOnItmTarget()
        {
            OnSta(() =>
            {
                // Bind receives the resolved wheel catalog (same TryResolve host apply uses);
                // CanRemovePageContent succeeds for a real ITM page.
                Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
                var host = new FakeHost { Live = SeedDoc(), WheelCode = "pbme" };
                var view = new DisplayPriorityV2View();
                view.Bind(host, catalog: catalog);

                Assert.NotNull(view.BoundCatalog);
                Assert.True(DisplayConfigV2EditSession.CanRemovePageContent(
                    new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                    view.BoundCatalog));
            });
        }

        [Fact]
        public void HostApplyPath_TryResolve_WiresCatalogIntoPriorityBind()
        {
            OnSta(() =>
            {
                // Real path: DisplayTabPanel.BindCore → CatalogLoader.TryResolve(host.WheelCode,
                // host.ItmDeviceId) → viewPriorityV2.Bind(catalog) — then remove-all core.
                var host = new FakeHost { Live = SeedDoc(), WheelCode = "pbme" };
                var panel = new DisplayTabPanel();
                panel.BindCore(host, new EmptyPropertyCatalog(), new EmptyRoleCatalog(), null);

                Assert.NotNull(panel.viewPriorityV2.BoundCatalog);
                panel.viewPriorityV2.ConfirmRemoveAll = _ => true;
                Assert.True(panel.viewPriorityV2.RemoveAllRequestedCore(
                    new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" }));
                Assert.DoesNotContain(host.Live.Priority.Rows, r => r.Id == "seat-full");
            });
        }

        [Fact]
        public void BuiltInPickerResult_PersistsAsBuiltIn_NotSimHubProperty()
        {
            OnSta(() =>
            {
                // Real path: PickerResultCore → EntrypointSaveCore → TryApply.
                // Programmatic SelectSourceKind must not overwrite builtIn before save.
                var host = new FakeHost { Live = SeedDoc() };
                var view = new DisplayPriorityV2View();
                view.Bind(host);

                Assert.True(view.OpenEntrypointFormCore("seat-full", "sum-1", isNew: false));
                view.PickerResultCore("Fuel", PropertyKind.BuiltIn);
                view.EntrypointSaveCore();

                var sum = host.Live.Priority.Rows
                    .First(r => r.Id == "seat-full")
                    .Summons.First(s => s.Id == "sum-1");
                Assert.Equal(ValueSourceKind.BuiltIn, sum.Condition.Source.Kind);
                Assert.Equal("Fuel", sum.Condition.Source.Name);
            });
        }

        [Fact]
        public void PlainDoor_OpensRealEntrypointCreateFlow()
        {
            OnSta(() =>
            {
                var host = new FakeHost { Live = SeedDoc() };
                var view = new DisplayPriorityV2View();
                view.Bind(host);

                Assert.True(view.OpenFirstEntrypointFormCore());
            });
        }

        [Fact]
        public void SplitSummonCore_SplitsChosenDisabledSummon()
        {
            OnSta(() =>
            {
                var live = SeedDoc();
                live.Priority.Rows[0].Summons.Add(new Summon
                {
                    Id = "sum-2",
                    Name = "disabled authored summon",
                    Enabled = false,
                    Condition = new Condition
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = "Speed",
                        },
                        Operator = ConditionOperator.GreaterThan,
                        Value = 100,
                    },
                });
                var host = new FakeHost { Live = live };
                var view = new DisplayPriorityV2View();
                view.Bind(host);

                view.SplitSummonCore("seat-full", "sum-2");

                var home = host.Live.Priority.Rows.Single(r => r.Id == "seat-full");
                Assert.Single(home.Summons);
                Assert.Equal("sum-1", home.Summons[0].Id);
                var satellite = Assert.Single(host.Live.Priority.Rows,
                    r => r.Kind == PriorityRowKind.Satellite);
                var chosen = Assert.Single(satellite.Summons);
                Assert.Equal("sum-2", chosen.Id);
                Assert.False(chosen.Enabled);
            });
        }

        [Fact]
        public void PagesAndFieldsDestination_NotLive_NavigateIsNoOp()
        {
            OnSta(() =>
            {
                // Q6 N1: undestined — NavigateToPagesAndFields does not raise.
                var host = new FakeHost { Live = SeedDoc() };
                var view = new DisplayPriorityV2View();
                view.Bind(host);
                view.SetPagesAndFieldsDestinationLive(false);
                int raised = 0;
                view.PagesAndFieldsRequested += (s, e) => raised++;
                view.NavigateToPagesAndFieldsForTest();
                Assert.Equal(0, raised);
            });
        }

        [Fact]
        public void PagesAndFieldsDestination_Live_RaisesOnNavigate()
        {
            OnSta(() =>
            {
                var host = new FakeHost { Live = SeedDoc() };
                var view = new DisplayPriorityV2View();
                view.Bind(host);
                view.SetPagesAndFieldsDestinationLive(true);
                int raised = 0;
                view.PagesAndFieldsRequested += (s, e) => raised++;
                view.NavigateToPagesAndFieldsForTest();
                Assert.Equal(1, raised);
            });
        }

        [Fact]
        public void PlaylistPicker_ExpandDoesNotWrite_ConfirmWritesAndKeepsOpen()
        {
            OnSta(() =>
            {
                var doc = SeedDoc();
                doc.Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-evening",
                        Name = "Evening loop",
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                            },
                        },
                    },
                };
                doc.Priority.Rest = new RestBlock
                {
                    Idle = new IdleSpec { Kind = IdleKind.Blank },
                };
                var host = new FakeHost { Live = doc };
                var view = new DisplayPriorityV2View();
                view.Bind(host);

                Assert.True(view.OpenIdlePickerCore());
                var item = view.ActivePickerForTest.Groups
                    .SelectMany(g => g.Items)
                    .Single(i => i.PlaylistId == "pl-evening");
                string before = DisplayConfigV2Serializer.Save(host.Live);

                Assert.True(view.ExpandPlaylistPickerItemCore(item));
                Assert.Equal(before, DisplayConfigV2Serializer.Save(host.Live));

                Assert.True(view.ConfirmPlaylistPickerItemCore(item));
                Assert.Equal(IdleKind.Playlist, host.Live.Priority.Rest.Idle.Kind);
                Assert.Equal("pl-evening", host.Live.Priority.Rest.Idle.Playlist);
                Assert.True(view.PlaylistInspectionExpandedForTest);
            });
        }
    }
}
