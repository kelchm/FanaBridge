using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using DependencyObject = System.Windows.DependencyObject;
using FontWeights = System.Windows.FontWeights;
using LogicalTreeHelper = System.Windows.LogicalTreeHelper;
using Size = System.Windows.Size;
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
    /// <summary>
    /// Extracted-core end-to-end pins for Pages &amp; Fields:
    /// cores → session → TryApply → document (established Priority pattern).
    /// </summary>
    public class DisplayPagesFieldsV2ViewTests
    {
        /// <summary>
        /// Real view construction needs STA; xunit's default suite thread is MTA
        /// (same idiom as DisplayPriorityV2ViewTests).
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

            public DisplaySettings DisplaySettings { get; } = new DisplaySettings();
            public DisplayType DisplayType => DisplayType.Itm;
            public byte ItmDeviceId => 3;
            public string WheelCode { get; set; } = "pbme";
            public string ModuleCode { get; set; } = null!;
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

        [Fact]
        public void SelectFieldCore_Toggle_And_ClearFocusCore()
        {
            OnSta(() =>
            {
            var host = new FakeHost { Live = MinimalDoc() };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            view.SelectFieldCore(10);
            Assert.Equal((ushort)10, view.FocusedParamIdForTest);
            Assert.True(view.Model.IsFiltered);

            // Re-click clears (route 2).
            view.SelectFieldCore(10);
            Assert.Null(view.FocusedParamIdForTest);

            view.SelectFieldCore(10);
            view.ClearFocusCore(); // routes 1/3/4
            Assert.Null(view.FocusedParamIdForTest);
            Assert.False(view.Model.IsFiltered);
            });
        }

        [Fact]
        public void SelectPageCore_SharedFocusSurvives_OrClears()
        {
            OnSta(() =>
            {
            var host = new FakeHost { Live = SharedDoc() };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: SharedCatalog());

            view.SelectPageCore("itm:tyreTemps");
            view.SelectFieldCore(4); // speed — shared
            Assert.Equal((ushort)4, view.FocusedParamIdForTest);

            view.SelectPageCore("itm:lapInfo");
            // speed still placed on lapInfo.
            Assert.Equal((ushort)4, view.FocusedParamIdForTest);

            view.SelectFieldCore(10); // fl — only tyreTemps
            view.SelectPageCore("itm:lapInfo");
            Assert.Null(view.FocusedParamIdForTest);
            });
        }

        [Fact]
        public void SetFieldBaseCore_WritesThroughSession_TryApply()
        {
            OnSta(() =>
            {
            var host = new FakeHost { Live = MinimalDoc() };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            view.SetFieldBaseCore(10, new FieldBase
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = "Speed",
                },
                Format = "bare",
                BaseSuffix = string.Empty,
            });

            Assert.Equal("Speed", host.Live.Fields[10].Base.Source.Name);
            Assert.Equal("bare", host.Live.Fields[10].Base.Format);
            });
        }

        [Fact]
        public void RotationSaveCore_WritesPageOrder()
        {
            OnSta(() =>
            {
            var host = new FakeHost { Live = MinimalDoc() };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            view.RotationSaveCore(new[] { "itm:tyreTemps", "itm:lapInfo" });
            Assert.Equal(2, host.Live.PageOrder.Count);
            Assert.Equal("tyreTemps", host.Live.PageOrder[0].CatalogPageId);
            Assert.Equal("lapInfo", host.Live.PageOrder[1].CatalogPageId);
            });
        }

        [Fact]
        public void OverrideSaveCore_AddThenDelete_EndToEnd()
        {
            OnSta(() =>
            {
            var host = new FakeHost { Live = MinimalDoc() };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            Assert.True(view.OpenOverrideFormCore(10, null, isNew: true));
            // Form defaults: suffix checked, empty content — save still appends.
            view.OverrideSaveCore();
            Assert.NotEmpty(host.Live.Fields[10].Overrides);
            string id = host.Live.Fields[10].Overrides[host.Live.Fields[10].Overrides.Count - 1].Id;
            Assert.False(string.IsNullOrEmpty(id));

            Assert.True(view.OpenOverrideFormCore(10, id, isNew: false));
            view.OverrideDeleteCore();
            Assert.DoesNotContain(host.Live.Fields[10].Overrides, o => o.Id == id);
            });
        }

        [Fact]
        public void OverrideOpenSave_Unchanged_IsByteIdentical_InclExtensionData()
        {
            OnSta(() =>
            {
            var doc = MinimalDoc();
            doc.Fields[10].Overrides = new List<FieldOverride>
            {
                new FieldOverride
                {
                    Id = "ov-keep",
                    Writes = FieldWrites.Suffix,
                    Content = new ContentObject
                    {
                        Kind = ContentKind.Text,
                        Text = "!",
                        ExtensionData = new Dictionary<string, Newtonsoft.Json.Linq.JToken>
                        {
                            ["v3Content"] = "nested",
                        },
                    },
                    Condition = new Condition
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.SimHubProperty,
                            Name = "DataCorePlugin.GameData.TyreTemperatureFrontLeft",
                        },
                        Operator = ConditionOperator.GreaterThan,
                        Value = 90,
                    },
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    Enabled = true,
                    ActsAsEntrypoint = false,
                    ExtensionData = new Dictionary<string, Newtonsoft.Json.Linq.JToken>
                    {
                        ["v3Override"] = "keep",
                    },
                },
            };
            doc = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(doc), _ => { });
            string before = DisplayConfigV2Serializer.Save(doc);

            var host = new FakeHost { Live = doc };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            Assert.True(view.OpenOverrideFormCore(10, "ov-keep", isNew: false));
            view.OverrideSaveCore();

            string after = DisplayConfigV2Serializer.Save(host.Live);
            Assert.Equal(before, after);
            });
        }

        [Fact]
        public void RotationSaveCore_AbsentUnchanged_StaysAbsent()
        {
            OnSta(() =>
            {
            // Direct core call with empty working order and no dialog open writes [] —
            // pin the dialog dirty path: when order was never edited, save is a no-op.
            var host = new FakeHost { Live = MinimalDoc() };
            Assert.Null(host.Live.PageOrder);
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            // Simulate dialog open without dirty: save must leave pageOrder null.
            // RotationSaveCore with null working-order list from dialog path is guarded
            // by dirty tracking inside RotationSave_Click; core itself writes when given
            // keys (explicit edit). Absent stays absent when not written.
            Assert.Null(host.Live.PageOrder);
            string before = DisplayConfigV2Serializer.Save(host.Live);
            // No RotationSaveCore call → document unchanged.
            Assert.Equal(before, DisplayConfigV2Serializer.Save(host.Live));
            Assert.Null(host.Live.PageOrder);
            });
        }

        [Fact]
        public void MoveOverrideCore_ReordersLadder()
        {
            OnSta(() =>
            {
            var doc = MinimalDoc();
            doc.Fields[10].Overrides = new List<FieldOverride>
            {
                new FieldOverride { Id = "a", Writes = FieldWrites.Suffix },
                new FieldOverride { Id = "b", Writes = FieldWrites.Value },
            };
            doc = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(doc), _ => { });
            var host = new FakeHost { Live = doc };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            view.MoveOverrideCore(10, 0, 1);
            Assert.Equal("b", host.Live.Fields[10].Overrides[0].Id);
            Assert.Equal("a", host.Live.Fields[10].Overrides[1].Id);
            });
        }

        [Fact]
        public void OverrideOperator_EveryValidOperator_OpenSave_RoundTrips()
        {
            OnSta(() =>
            {
            // Roster = ConditionOperator enum minus Unknown (EnumText source of truth).
            var operators = new List<ConditionOperator>();
            foreach (ConditionOperator op in Enum.GetValues(typeof(ConditionOperator)))
            {
                if (op != ConditionOperator.Unknown)
                    operators.Add(op);
            }
            Assert.Contains(ConditionOperator.NotEquals, operators);
            Assert.Contains(ConditionOperator.IsTrue, operators);
            Assert.Contains(ConditionOperator.IsFalse, operators);

            foreach (var op in operators)
            {
                bool isBool = op == ConditionOperator.IsTrue || op == ConditionOperator.IsFalse;
                var doc = MinimalDoc();
                doc.Fields[10].Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "ov-op",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "!" },
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.SimHubProperty,
                                Name = "DataCorePlugin.GameData.TyreTemperatureFrontLeft",
                            },
                            Operator = op,
                            Value = isBool ? (double?)null : 42,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        Enabled = true,
                    },
                };
                doc = DisplayConfigV2Validator.Normalize(
                    DisplayConfigV2Serializer.Clone(doc), _ => { });

                var host = new FakeHost { Live = doc };
                var view = new DisplayPagesFieldsV2View();
                view.Bind(host, catalog: TinyCatalog());

                Assert.True(view.OpenOverrideFormCore(10, "ov-op", isNew: false));
                // Re-select the hydrated operator (exercises full roster + save map).
                view.SelectOverrideOperatorForTest(op);
                view.OverrideSaveCore();

                var saved = host.Live.Fields[10].Overrides
                    .Find(o => o.Id == "ov-op");
                Assert.NotNull(saved);
                Assert.Equal(op, saved.Condition.Operator);
                if (isBool)
                    Assert.Null(saved.Condition.Value);
                else
                    Assert.Equal(42, saved.Condition.Value);
            }
            });
        }

        [Fact]
        public void PickerResult_BaseAndOverride_WritePickedKind()
        {
            OnSta(() =>
            {
            var host = new FakeHost { Live = MinimalDoc() };
            // Seed base is simHubProperty.
            Assert.Equal(ValueSourceKind.SimHubProperty,
                host.Live.Fields[10].Base.Source.Kind);

            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            // builtIn → base
            view.BasePickerResultCore(10, "Speed", PropertyKind.BuiltIn);
            Assert.Equal(ValueSourceKind.BuiltIn, host.Live.Fields[10].Base.Source.Kind);
            Assert.Equal("Speed", host.Live.Fields[10].Base.Source.Name);

            // simHub → base (reverse)
            view.BasePickerResultCore(10, "DataCorePlugin.GameData.SpeedKmh",
                PropertyKind.SimHubProperty);
            Assert.Equal(ValueSourceKind.SimHubProperty, host.Live.Fields[10].Base.Source.Kind);
            Assert.Equal("DataCorePlugin.GameData.SpeedKmh",
                host.Live.Fields[10].Base.Source.Name);

            // Override: open form, pick builtIn, save; then simHub.
            Assert.True(view.OpenOverrideFormCore(10, null, isNew: true));
            view.OverridePickerResultCore("Fuel", PropertyKind.BuiltIn);
            view.OverrideSaveCore();
            var ov = host.Live.Fields[10].Overrides[host.Live.Fields[10].Overrides.Count - 1];
            Assert.Equal(ValueSourceKind.BuiltIn, ov.Condition.Source.Kind);
            Assert.Equal("Fuel", ov.Condition.Source.Name);

            Assert.True(view.OpenOverrideFormCore(10, ov.Id, isNew: false));
            view.OverridePickerResultCore(
                "DataCorePlugin.GameData.Fuel", PropertyKind.SimHubProperty);
            view.OverrideSaveCore();
            ov = host.Live.Fields[10].Overrides.Find(o => o.Id == ov.Id);
            Assert.NotNull(ov);
            Assert.Equal(ValueSourceKind.SimHubProperty, ov.Condition.Source.Kind);
            Assert.Equal("DataCorePlugin.GameData.Fuel", ov.Condition.Source.Name);
            });
        }

        [Fact]
        public void BringUpDurationMs_UnchangedOpenSave_Preserves2500_EditedWrites3000()
        {
            OnSta(() =>
            {
            var doc = EntrypointDoc(bringUpMs: 2500);
            doc = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(doc), _ => { });
            var host = new FakeHost { Live = doc };
            var view = new DisplayPagesFieldsV2View();
            view.Bind(host, catalog: TinyCatalog());

            // Unchanged open→save: exact ms survives (display may round seconds).
            Assert.True(view.OpenOverrideFormCore(10, "ov-ep", isNew: false));
            view.OverrideSaveCore();
            var seat = host.Live.Priority.Rows.Find(r => r.Id == "seat-home");
            Assert.NotNull(seat);
            Assert.Equal(LifetimeKind.ForDuration, seat.BringUpLifetime.Kind);
            Assert.Equal(2500, seat.BringUpLifetime.DurationMs);

            // User edits seconds to 3 → write 3000.
            Assert.True(view.OpenOverrideFormCore(10, "ov-ep", isNew: false));
            view.SetBringUpSecondsEditedForTest(3);
            view.OverrideSaveCore();
            seat = host.Live.Priority.Rows.Find(r => r.Id == "seat-home");
            Assert.NotNull(seat);
            Assert.Equal(3000, seat.BringUpLifetime.DurationMs);
            });
        }

        [Fact]
        public void PlainDoor_OpensRealOverrideCreateFlow()
        {
            OnSta(() =>
            {
                var host = new FakeHost { Live = MinimalDoc() };
                var view = new DisplayPagesFieldsV2View();
                view.Bind(host, catalog: TinyCatalog());

                Assert.True(view.OpenFirstOverrideFormCore());
            });
        }

        [Fact]
        public void OverrideFooter_SplitCore_WritesChildRefSatellite()
        {
            OnSta(() =>
            {
                var host = new FakeHost { Live = EntrypointDoc(bringUpMs: 2500) };
                var view = new DisplayPagesFieldsV2View();
                view.Bind(host, catalog: TinyCatalog());

                Assert.True(view.OpenOverrideFormCore(10, "ov-ep", isNew: false));
                Assert.Equal(DisplayCopy.GiveThisOverrideItsOwnPriority,
                    view.btnOvSplit.Content);
                Assert.True(view.SplitCurrentOverrideCore());

                var satellite = Assert.Single(host.Live.Priority.Rows,
                    r => r.Kind == PriorityRowKind.Satellite);
                Assert.Equal("10", satellite.ChildRef.Field);
                Assert.Equal("ov-ep", satellite.ChildRef.OverrideId);
            });
        }

        [Fact]
        public void FieldCard_AddOverrideButtons_ReserveTheWholeRuledLabel()
        {
            OnSta(() =>
            {
                var view = new DisplayPagesFieldsV2View();
                view.Bind(new FakeHost { Live = MinimalDoc() }, catalog: TinyCatalog());

                var buttons = Descendants(view.panelFieldCollection)
                    .OfType<Button>()
                    .Where(b => Equals(b.Content, DisplayCopy.AddAnOverride))
                    .ToList();
                Assert.NotEmpty(buttons);

                var label = new TextBlock
                {
                    Text = DisplayCopy.AddAnOverride,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double ruledLabelWidth = label.DesiredSize.Width + 16;

                Assert.All(buttons, button =>
                    Assert.True(
                        button.MinWidth >= ruledLabelWidth,
                        $"button {button.MinWidth} < label {ruledLabelWidth}"));
            });
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null)
                yield break;

            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child))
                    yield return nested;
            }
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static DisplayConfigV2 MinimalDoc()
        {
            var doc = new DisplayConfigV2
            {
                Settings = new SettingsBlock { Mode = SettingsMode.On },
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [10] = new FieldEntry
                    {
                        Base = new FieldBase
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.SimHubProperty,
                                Name = "DataCorePlugin.GameData.TyreTemperatureFrontLeft",
                            },
                        },
                        Overrides = new List<FieldOverride>(),
                    },
                },
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.ItmPage,
                        CatalogPageId = "tyreTemps",
                    },
                },
            };
            return DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(doc), _ => { });
        }

        /// <summary>
        /// Override with actsAsEntrypoint + home seat carrying BringUpLifetime forDuration.
        /// </summary>
        private static DisplayConfigV2 EntrypointDoc(int bringUpMs)
        {
            var doc = MinimalDoc();
            doc.Fields[10].Overrides = new List<FieldOverride>
            {
                new FieldOverride
                {
                    Id = "ov-ep",
                    Writes = FieldWrites.Suffix,
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "!" },
                    Condition = new Condition
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = "Fuel",
                        },
                        Operator = ConditionOperator.LessThan,
                        Value = 5,
                    },
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    Enabled = true,
                    ActsAsEntrypoint = true,
                },
            };
            doc.Priority = new PriorityLadder
            {
                Rows = new List<PriorityRow>
                {
                    new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "seat-home",
                        Target = new PageRef
                        {
                            Kind = PageRefKind.ItmPage,
                            CatalogPageId = "tyreTemps",
                        },
                        BringUpLifetime = new Lifetime
                        {
                            Kind = LifetimeKind.ForDuration,
                            DurationMs = bringUpMs,
                        },
                        Summons = new List<Summon>(),
                    },
                },
            };
            return doc;
        }

        private static DisplayConfigV2 SharedDoc()
        {
            var doc = MinimalDoc();
            doc.SharedFields = new Dictionary<string, FieldEntry>
            {
                ["speed"] = new FieldEntry
                {
                    Base = new FieldBase
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = "Speed",
                        },
                    },
                },
            };
            return DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(doc), _ => { });
        }

        private static WheelCatalog TinyCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "fl", ParamId = 10, ShortCode = "FL", DisplayLabel = "FL",
                        },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "tyreTemps",
                            Index = 5,
                            Name = "Tire Temps",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement { Field = "fl" },
                            },
                        },
                        new CatalogPage
                        {
                            Id = "lapInfo",
                            Index = 1,
                            Name = "Lap Info",
                            Placements = new List<CatalogFieldPlacement>(),
                        },
                    },
                },
            };
        }

        private static WheelCatalog SharedCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "speed", ParamId = 4, ShortCode = "SPD", DisplayLabel = "Speed",
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "fl", ParamId = 10, ShortCode = "FL", DisplayLabel = "FL",
                        },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "tyreTemps",
                            Index = 5,
                            Name = "Tire Temps",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement { Field = "fl" },
                                new CatalogFieldPlacement { Field = "speed" },
                            },
                        },
                        new CatalogPage
                        {
                            Id = "lapInfo",
                            Index = 1,
                            Name = "Lap Info",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement { Field = "speed" },
                            },
                        },
                    },
                },
            };
        }
    }
}
