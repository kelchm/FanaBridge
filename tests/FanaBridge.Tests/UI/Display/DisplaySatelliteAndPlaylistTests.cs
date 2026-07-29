using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Surface C (OWNER-WAIVED FIDELITY satellite) + Surface D (playlist card) pure cores.
    /// </summary>
    public class DisplaySatelliteAndPlaylistTests
    {
        // ── Surface C ────────────────────────────────────────────────────

        [Fact]
        public void Satellite_Summon_ProjectsReferenceMarkerAndCanRejoin()
        {
            var doc = SeedTwoSummons();
            var session = DisplayConfigV2EditSession.Open(doc);
            session.SplitSatellite("seat-1", "sum-2");
            doc = session.Document;

            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            var sat = model.Rows.First(r => r.IsSatellite);
            Assert.True(sat.CanRejoinHome);
            Assert.False(sat.CanSplitEntrypoint);
            Assert.False(sat.ShowDisclosure);
            Assert.False(string.IsNullOrEmpty(sat.SplitReferenceName));
            Assert.Equal(DisplayCopy.SplitRowFromMarker, DisplayCopy.SplitRowFromMarker);
            Assert.Equal(DisplayCopy.RejoinTheHomeRow, DisplayCopy.RejoinTheHomeRow);
        }

        [Fact]
        public void Seat_WithTwoSummons_CanSplit()
        {
            var doc = SeedTwoSummons();
            doc.Priority.Rows[0].Summons[1].Enabled = false;
            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm);

            var seat = model.Rows.First(r => r.RowId == "seat-1");
            Assert.True(seat.CanSplitEntrypoint);
            Assert.Equal(2, seat.SplitSummons.Count);
            Assert.False(seat.SplitSummons[1].IsEnabled);
            Assert.False(seat.CanRejoinHome);
            Assert.Equal(DisplayCopy.GiveThisEntrypointItsOwnPriority,
                DisplayCopy.GiveThisEntrypointItsOwnPriority);
        }

        [Fact]
        public void SplitThenMerge_RoundTrip_BytePreservesHomeSummons()
        {
            var live = SeedTwoSummons();
            var before = DisplayConfigV2Serializer.Save(live);

            var session = DisplayConfigV2EditSession.Open(live);
            session.SplitSatellite("seat-1", "sum-1");
            var satId = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite).Id;
            session.MergeSatellite(satId);

            var seat = session.Document.Priority.Rows.First(r => r.Id == "seat-1");
            Assert.Equal(2, seat.Summons.Count);
            Assert.DoesNotContain(
                session.Document.Priority.Rows, r => r.Kind == PriorityRowKind.Satellite);

            var after = DisplayConfigV2Serializer.Save(session.Document);
            Assert.Equal(before, after);
        }

        [Fact]
        public void ChildRefSatellite_UsesChildNameHostAndCondition_AndShowsDegradedReason()
        {
            var catalog = ChildRefCatalog();
            var doc = DisplayConfigV2Validator.Normalize(
                ChildRefDoc(), _ => { }, catalog);
            var authored = doc.Priority.Rows.Single(r => r.Id == "sat-child");
            authored.ChildRefAmbiguous = true;
            authored.DegradedAtLoad = true;

            var model = DisplayPriorityV2Model.Project(
                doc, EmptyConnected(), null, DisplayType.Itm, catalog: catalog);
            var satellite = model.Rows.Single(r => r.RowId == "sat-child");

            Assert.Equal("Fuel remaining", satellite.SplitReferenceName);
            Assert.Equal("Lap Info", satellite.Destination.Name);
            Assert.Contains("OverrideConditionSource", satellite.Detail);
            Assert.Contains(DisplayCopy.SatelliteReasonAmbiguousChild,
                satellite.StatusCopy);
        }

        // ── Surface D ────────────────────────────────────────────────────

        [Fact]
        public void PlaylistCard_RendersSteps_ReadOnly_ReRunDisabled()
        {
            var doc = ScreensaverDoc();
            DisplayConfigV2Validator.Normalize(doc, _ => { });

            var card = DisplayPriorityV2Model.ProjectPlaylistCard(doc, "pl-ss");
            Assert.NotNull(card);
            Assert.Equal(DisplayCopy.PlaylistBadge, card.Badge);
            Assert.Equal(DisplayCopy.ReadOnlyChip, card.ReadOnlyChip);
            Assert.Equal(DisplayCopy.StepsLabel, card.StepsLabel);
            Assert.Equal(DisplayCopy.StepsInOrderLastHolds, card.StepsCaption);
            Assert.Equal(4, card.Steps.Count);
            Assert.Equal(DisplayCopy.PlaylistStepSkipped, card.Steps[1].DurationLabel);
            Assert.Equal(DisplayCopy.UnavailablePlaylistDestination,
                card.Steps[1].DestinationName);
            Assert.Equal(DisplayCopy.PlaylistStepHolds, card.Steps[3].DurationLabel);
            Assert.Null(card.Provenance);
            Assert.DoesNotContain("Evening loop", card.Provenance ?? string.Empty);
            Assert.DoesNotContain("Screensaver setup", card.Provenance ?? string.Empty);
            Assert.False(card.ReRunEnabled);
            Assert.Equal(DisplayCopy.SpokeArrivingLater("Setups"), card.ReRunTooltip);
            Assert.Equal(DisplayCopy.ReRunTheSetup, card.ReRunLabel);
            Assert.Contains("Outside a session", card.UsedByLine);
        }

        [Fact]
        public void PlaylistCard_MissingId_ReturnsNull()
        {
            Assert.Null(DisplayPriorityV2Model.ProjectPlaylistCard(ScreensaverDoc(), "nope"));
            Assert.Null(DisplayPriorityV2Model.ProjectPlaylistCard(null, "pl-ss"));
        }

        [Fact]
        public void PlaylistCopyKeys_Exist()
        {
            Assert.Equal("READ-ONLY", DisplayCopy.ReadOnlyChip);
            Assert.Equal("STEPS", DisplayCopy.StepsLabel);
            Assert.Equal("in order · the last one holds", DisplayCopy.StepsInOrderLastHolds);
            Assert.Equal("holds", DisplayCopy.PlaylistStepHolds);
            Assert.Equal("Re-run the setup", DisplayCopy.ReRunTheSetup);
            Assert.Contains("Screensaver", DisplayCopy.CreatedBySetupRerun("Screensaver"));
            Assert.Contains("Outside a session",
                DisplayCopy.UsedByOnThisProfile(DisplayCopy.OutsideASession));
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static DisplayConfigV2 SeedTwoSummons()
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
                            Id = "seat-1",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.ItmPage,
                                CatalogPageId = "lapInfo",
                            },
                            Summons = new List<Summon>
                            {
                                new Summon
                                {
                                    Id = "sum-1",
                                    Name = "fuel low",
                                    Enabled = true,
                                    Condition = new Condition
                                    {
                                        Source = new ValueSource
                                        {
                                            Kind = ValueSourceKind.BuiltIn,
                                            Name = "Fuel",
                                        },
                                        Operator = ConditionOperator.LessThan,
                                        Value = 10,
                                    },
                                },
                                new Summon
                                {
                                    Id = "sum-2",
                                    Name = "a lap is completed",
                                    Enabled = true,
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
                                },
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            }, _ => { });
        }

        private static DisplayConfigV2 ScreensaverDoc()
        {
            return new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-ss",
                        Name = "Evening loop",
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.Logo,
                                },
                                DurationMs = 60000,
                            },
                            new PlaylistStep
                            {
                                Destination = null,
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.LogoInverted,
                                },
                                DurationMs = 1000,
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                            },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Playlist,
                            Playlist = "pl-ss",
                        },
                    },
                },
            };
        }

        private static DisplayConfigV2 ChildRefDoc()
        {
            return new DisplayConfigV2
            {
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [10] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "ov-fuel",
                                Enabled = true,
                                ActsAsEntrypoint = true,
                                Condition = new Condition
                                {
                                    Source = new ValueSource
                                    {
                                        Kind = ValueSourceKind.BuiltIn,
                                        Name = "OverrideConditionSource",
                                    },
                                    Operator = ConditionOperator.LessThan,
                                    Value = 5,
                                },
                                Lifetime = new Lifetime
                                {
                                    Kind = LifetimeKind.ForDuration,
                                    DurationMs = 2500,
                                },
                            },
                        },
                    },
                },
                Priority = new PriorityLadder
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
                                CatalogPageId = "lapInfo",
                            },
                            Summons = new List<Summon>(),
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Satellite,
                            Id = "sat-child",
                            ChildRef = new ChildRef
                            {
                                Field = "10",
                                OverrideId = "ov-fuel",
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                },
            };
        }

        private static WheelCatalog ChildRefCatalog()
        {
            return new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "fuel",
                            ParamId = 10,
                            DisplayLabel = "Fuel remaining",
                        },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "lapInfo",
                            Name = "Lap Info",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement
                                {
                                    Field = "fuel",
                                    PrimaryHost = true,
                                },
                            },
                        },
                    },
                },
            };
        }

        private static DisplayResolutionSnapshotModel EmptyConnected()
            => DisplayResolutionSnapshotModel.From(
                null, inGame: false, isConnected: true, aggregates: null, manual: null);
    }
}
