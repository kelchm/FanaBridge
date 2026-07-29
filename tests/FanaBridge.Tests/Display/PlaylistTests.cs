using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Amendment A1 / task #22 — playlists schema, validator, IdleCompile expansion,
    /// arbiter publish path, picker group, and diagnostics projection.
    /// </summary>
    public class PlaylistTests
    {
        // ── Schema round-trip ────────────────────────────────────────────

        [Fact]
        public void Schema_PlaylistRoundTrip_PreservesUnknownMembersOnSteps()
        {
            const string json = @"{
  ""schemaVersion"": 2,
  ""playlists"": [
    {
      ""id"": ""pl-screensaver"",
      ""name"": ""Screensaver"",
      ""steps"": [
        {
          ""destination"": { ""kind"": ""screen"", ""screen"": ""logo"" },
          ""durationMs"": 60000,
          ""v3Step"": true
        },
        {
          ""destination"": { ""kind"": ""blank"" },
          ""futureFlag"": 1
        }
      ],
      ""terminal"": ""once-then-silence"",
      ""v3Playlist"": ""keep""
    }
  ],
  ""priority"": {
    ""rest"": {
      ""idle"": { ""kind"": ""playlist"", ""playlist"": ""pl-screensaver"", ""v3Idle"": 9 }
    }
  }
}";
            var loaded = DisplayConfigV2Serializer.Load(json, _ => { });
            string saved = DisplayConfigV2Serializer.Save(loaded);
            var again = DisplayConfigV2Serializer.Load(saved, _ => { });

            Assert.NotNull(again.Playlists);
            Assert.Single(again.Playlists);
            var pl = again.Playlists[0];
            Assert.Equal("pl-screensaver", pl.Id);
            Assert.Equal("Screensaver", pl.Name);
            Assert.Equal("once-then-silence", pl.TerminalRaw); // unknown raw preserved
            Assert.Equal(PlaylistTerminal.Unknown, pl.Terminal);
            Assert.NotNull(pl.ExtensionData);
            Assert.True(pl.ExtensionData.ContainsKey("v3Playlist"));

            Assert.Equal(2, pl.Steps.Count);
            Assert.True(pl.Steps[0].DurationMsPresent);
            Assert.Equal(60000, pl.Steps[0].DurationMs);
            Assert.NotNull(pl.Steps[0].ExtensionData);
            Assert.True(pl.Steps[0].ExtensionData.ContainsKey("v3Step"));
            Assert.False(pl.Steps[1].DurationMsPresent);
            Assert.NotNull(pl.Steps[1].ExtensionData);
            Assert.True(pl.Steps[1].ExtensionData.ContainsKey("futureFlag"));

            Assert.Equal(IdleKind.Playlist, again.Priority.Rest.Idle.Kind);
            Assert.Equal("pl-screensaver", again.Priority.Rest.Idle.Playlist);
            Assert.True(again.Priority.Rest.Idle.ExtensionData.ContainsKey("v3Idle"));

            // terminal hold default suppressed; absent playlists not emitted on empty.
            var empty = DisplayConfigV2Serializer.Load(@"{""schemaVersion"":2}", _ => { });
            string emptySaved = DisplayConfigV2Serializer.Save(empty);
            Assert.DoesNotContain("playlists", emptySaved);
        }

        [Fact]
        public void Schema_TerminalHold_IsSuppressedOnWrite()
        {
            var cfg = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-1",
                        Terminal = PlaylistTerminal.Hold,
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                            },
                        },
                    },
                },
            };
            string json = DisplayConfigV2Serializer.Save(cfg);
            Assert.DoesNotContain("\"terminal\"", json);
            Assert.Contains("\"playlists\"", json);
        }

        [Fact]
        public void Schema_TerminalLoop_EmitsRawString()
        {
            var cfg = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-loop",
                        Terminal = PlaylistTerminal.Loop,
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.Logo,
                                },
                                DurationMs = 1000,
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                                DurationMs = 1000,
                            },
                        },
                    },
                },
            };
            string json = DisplayConfigV2Serializer.Save(cfg);
            Assert.Contains("\"terminal\": \"loop\"", json.Replace("\r\n", "\n"));
        }

        // ── Validator matrix ─────────────────────────────────────────────

        [Fact]
        public void Validator_PlaylistRef_OnlyLegalOnIdleSlot()
        {
            // rest.idle.playlist is legal; step nested playlist degrades the STEP.
            var cfg = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-ok",
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Playlist,
                                    Playlist = "pl-nested",
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
                            Playlist = "pl-ok",
                        },
                    },
                },
            };
            var warns = new List<string>();
            DisplayConfigV2Validator.Normalize(cfg, m => warns.Add(m));

            Assert.False(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.True(cfg.Playlists[0].Steps[0].DegradedAtLoad);
            Assert.Contains(warns, w => w.IndexOf("nested playlist", StringComparison.OrdinalIgnoreCase) >= 0
                || w.IndexOf("playlist ref is legal only", StringComparison.OrdinalIgnoreCase) >= 0);
            // Survivor step keeps the playlist alive (1-step after skip is legal).
            Assert.False(cfg.Playlists[0].DegradedAtLoad);
        }

        [Fact]
        public void Validator_DurationBelowFloor_NotesClamp_DoesNotRewrite()
        {
            var cfg = ScreensaverDoc(durationMs: 100); // below MinDwellMs=500
            var warns = new List<string>();
            DisplayConfigV2Validator.Normalize(cfg, m => warns.Add(m));

            Assert.Equal(100, cfg.Playlists[0].Steps[0].DurationMs); // authored intact
            Assert.Contains(warns, w => w.IndexOf("below destination floor", StringComparison.Ordinal) >= 0
                && w.IndexOf("500", StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public void Validator_UnresolvableStepDestination_DegradesStep_SurvivorsRemain()
        {
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A" },
                },
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-mixed",
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Page,
                                    Page = new PageRef
                                    {
                                        Kind = PageRefKind.HostedPage,
                                        Id = "p-missing",
                                    },
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
            };
            var warns = new List<string>();
            DisplayConfigV2Validator.Normalize(cfg, m => warns.Add(m));

            Assert.True(cfg.Playlists[0].Steps[0].DegradedAtLoad);
            Assert.False(cfg.Playlists[0].Steps[1].DegradedAtLoad);
            Assert.False(cfg.Playlists[0].DegradedAtLoad); // survivor keeps playlist
        }

        [Fact]
        public void Validator_OneStepPlaylist_IsLegal()
        {
            var cfg = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-one",
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                            },
                        },
                    },
                },
            };
            var warns = new List<string>();
            DisplayConfigV2Validator.Normalize(cfg, m => warns.Add(m));
            Assert.False(cfg.Playlists[0].DegradedAtLoad);
            Assert.DoesNotContain(warns, w => w.IndexOf("no resolvable steps", StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public void Validator_ZeroSteps_DegradesPlaylist()
        {
            var cfg = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-empty",
                        Steps = new List<PlaylistStep>(),
                    },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Playlist,
                            Playlist = "pl-empty",
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, _ => { });
            Assert.True(cfg.Playlists[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
        }

        [Fact]
        public void Validator_UnresolvableIdlePlaylist_DegradesIdle()
        {
            var cfg = new DisplayConfigV2
            {
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Playlist,
                            Playlist = "pl-gone",
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, _ => { });
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.Equal("pl-gone", cfg.Priority.Rest.Idle.Playlist); // ref kept
        }

        // ── Engine: IdleCompile expansion ────────────────────────────────

        private static IReadOnlyDictionary<string, PlaylistEntry> Map(params PlaylistEntry[] entries)
        {
            var d = new Dictionary<string, PlaylistEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e?.Id != null)
                    d[e.Id] = e;
            }
            return d;
        }

        private static PlaylistEntry LogoThenBlank(
            string id = "pl-ss",
            int logoMs = 60000,
            PlaylistTerminal terminal = PlaylistTerminal.Hold)
        {
            return new PlaylistEntry
            {
                Id = id,
                Name = "Screensaver",
                Terminal = terminal,
                Steps = new List<PlaylistStep>
                {
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.Logo,
                        },
                        DurationMs = logoMs,
                    },
                    new PlaylistStep
                    {
                        Destination = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
        }

        [Fact]
        public void Engine_RestartOnIdleReEntry()
        {
            var pl = LogoThenBlank(logoMs: 1000);
            var idle = new IdleSpec { Kind = IdleKind.Playlist, Playlist = pl.Id };
            var sc = new ScreenCommandsCapability { Logo = true, Blank = true };
            var map = Map(pl);

            // First idle entry at t=0 → logo.
            var r0 = IdleCompile.Resolve(idle, sc, map, nowMs: 0, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareScreen, r0.Kind);
            Assert.Equal(WheelScreenCommand.Logo, r0.ScreenCommand);
            Assert.Equal(IdleKind.Screen, r0.PublishedIdleKind); // never Playlist

            // Mid-program at t=1500 → blank (hold final).
            var r1 = IdleCompile.Resolve(idle, sc, map, nowMs: 1500, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareBlank, r1.Kind);

            // Re-entry: new anchor at 10000 → restarts at logo (OQ-P1).
            var r2 = IdleCompile.Resolve(idle, sc, map, nowMs: 10000, anchorMs: 10000);
            Assert.Equal(IdleCompileKind.FirmwareScreen, r2.Kind);
            Assert.Equal(WheelScreenCommand.Logo, r2.ScreenCommand);
        }

        [Fact]
        public void Engine_LoopWraps()
        {
            var pl = new PlaylistEntry
            {
                Id = "pl-loop",
                Terminal = PlaylistTerminal.Loop,
                Steps = new List<PlaylistStep>
                {
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.Logo,
                        },
                        DurationMs = 1000,
                    },
                    new PlaylistStep
                    {
                        Destination = new IdleSpec { Kind = IdleKind.Blank },
                        DurationMs = 1000,
                    },
                },
            };
            var idle = new IdleSpec { Kind = IdleKind.Playlist, Playlist = pl.Id };
            var sc = new ScreenCommandsCapability { Logo = true, Blank = true };
            var map = Map(pl);

            Assert.Equal(WheelScreenCommand.Logo,
                IdleCompile.Resolve(idle, sc, map, 0, 0).ScreenCommand);
            Assert.Equal(IdleCompileKind.FirmwareBlank,
                IdleCompile.Resolve(idle, sc, map, 1000, 0).Kind);
            // Wrap: t=2000 ≡ t=0
            Assert.Equal(WheelScreenCommand.Logo,
                IdleCompile.Resolve(idle, sc, map, 2000, 0).ScreenCommand);
            Assert.Equal(IdleCompileKind.FirmwareBlank,
                IdleCompile.Resolve(idle, sc, map, 3000, 0).Kind);
        }

        [Fact]
        public void Engine_HoldTerminal_LastStepPersists()
        {
            var pl = LogoThenBlank(logoMs: 1000);
            var idle = new IdleSpec { Kind = IdleKind.Playlist, Playlist = pl.Id };
            var sc = new ScreenCommandsCapability { Logo = true, Blank = true };
            var map = Map(pl);

            var far = IdleCompile.Resolve(idle, sc, map, nowMs: 999_999, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareBlank, far.Kind);
            Assert.Equal(IdleKind.Blank, far.PublishedIdleKind);
        }

        [Fact]
        public void Engine_SkipUnsupportedStep_Advances()
        {
            var pl = new PlaylistEntry
            {
                Id = "pl-skip",
                Terminal = PlaylistTerminal.Hold,
                Steps = new List<PlaylistStep>
                {
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.LogoInverted,
                        },
                        DurationMs = 5000,
                    },
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.Logo,
                        },
                        DurationMs = 1000,
                    },
                    new PlaylistStep
                    {
                        Destination = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
            var idle = new IdleSpec { Kind = IdleKind.Playlist, Playlist = pl.Id };
            // logoInverted unsupported → skipped; program starts on Logo.
            var sc = new ScreenCommandsCapability
            {
                Logo = true,
                LogoInverted = false,
                Blank = true,
            };
            var map = Map(pl);

            var r = IdleCompile.Resolve(idle, sc, map, nowMs: 0, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareScreen, r.Kind);
            Assert.Equal(WheelScreenCommand.Logo, r.ScreenCommand);
            // Never Silence from a program with survivors.
            Assert.NotEqual(IdleCompileKind.Silence, r.Kind);
        }

        [Fact]
        public void Engine_AllSkipped_CompilesToIdleFloor_NeverSilence()
        {
            var pl = new PlaylistEntry
            {
                Id = "pl-all-skip",
                Steps = new List<PlaylistStep>
                {
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.Logo,
                        },
                        DurationMs = 1000,
                    },
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.White,
                        },
                        DurationMs = 1000,
                    },
                },
            };
            var idle = new IdleSpec { Kind = IdleKind.Playlist, Playlist = pl.Id };
            var sc = new ScreenCommandsCapability
            {
                Logo = false,
                White = false,
                Blank = true,
            };
            var r = IdleCompile.Resolve(idle, sc, Map(pl), nowMs: 0, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareBlank, r.Kind);
            Assert.NotEqual(IdleCompileKind.Silence, r.Kind);
            Assert.Equal(IdleKind.Blank, r.PublishedIdleKind);
        }

        [Fact]
        public void Engine_PublishPath_NeverLeaksRawPlaylistKind()
        {
            var doc = ScreensaverDoc();
            DisplayConfigV2Validator.Normalize(doc, _ => { });
            var arb = new SeatArbiter(doc);
            var r = arb.Tick(new SeatArbiterTickInput
            {
                NowMs = 0,
                InGame = false,
                CarrierSnapshots = Array.Empty<CarrierTickSnapshot>(),
            });
            Assert.NotNull(r.Intent.IdleKind);
            Assert.NotEqual(IdleKind.Playlist, r.Intent.IdleKind.Value);
            // Active step is logo → Screen.
            Assert.Equal(IdleKind.Screen, r.Intent.IdleKind.Value);
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.IdleScreen);
        }

        [Fact]
        public void Engine_FloorClamp_RuntimeOnly()
        {
            var pl = LogoThenBlank(logoMs: 100); // below 500
            var idle = new IdleSpec { Kind = IdleKind.Playlist, Playlist = pl.Id };
            var sc = new ScreenCommandsCapability { Logo = true, Blank = true };
            // At t=100 authored would advance; clamp holds logo until 500.
            var stillLogo = IdleCompile.Resolve(idle, sc, Map(pl), nowMs: 100, anchorMs: 0);
            Assert.Equal(WheelScreenCommand.Logo, stillLogo.ScreenCommand);
            var blank = IdleCompile.Resolve(idle, sc, Map(pl), nowMs: 500, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareBlank, blank.Kind);
            Assert.Equal(100, pl.Steps[0].DurationMs); // document unchanged
        }

        // ── Ordinary page idle: pre-playlist EffectivePage path (no promotion) ─

        [Fact]
        public void Engine_OrdinaryPageIdle_EffectivePageStaysRestIdle_ByteIdenticalPrePlaylistPath()
        {
            // BEFORE playlist promotion: ordinary rest.idle page keeps
            // EffectivePageDestinationId = rest:idle. Page is metadata on
            // IdlePageDestinationId only — E5 + director use the rest path.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "p-a",
                        Name = "A",
                        Base = new ContentWithEffect
                        {
                            Content = new ContentObject
                            {
                                Kind = ContentKind.Text,
                                Text = "AAA",
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
                            Kind = IdleKind.Page,
                            Page = new PageRef
                            {
                                Kind = PageRefKind.HostedPage,
                                Id = "p-a",
                            },
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(doc, _ => { });

            var control = new B2FakePageControl();
            var props = new B2FakeProps();
            var composition = new DisplayCompositionV2(
                doc,
                catalog: new WheelCatalog
                {
                    WheelId = "test",
                    ScreenCommands = new ScreenCommandsCapability
                    {
                        Logo = true,
                        Blank = true,
                    },
                },
                pageControl: control,
                itmDeviceId: 3,
                nowMs: () => 0,
                log: _ => { },
                properties: props,
                options: new DisplayCompositionV2Options { DeviceKey = "test" });

            composition.Tick(new DisplayCompositionV2TickInput { InGame = false });

            // Seat: effective stays rest:idle; page is IdlePage only.
            Assert.Equal(
                DestinationIds.RestIdle,
                composition.LastSeatResult.Intent.DestinationId);
            Assert.Equal(
                DestinationIds.RestIdle,
                composition.LastSeatResult.Intent.EffectivePageDestinationId);
            Assert.Equal(IdleKind.Page, composition.LastSeatResult.Intent.IdleKind);
            Assert.Equal(
                DestinationIds.Hosted("p-a"),
                composition.LastSeatResult.Intent.IdlePageDestinationId);

            // E5 input: pre-change DisplayedDestinationId is rest:idle (not hosted:p-a).
            Assert.Equal(
                DestinationIds.RestIdle,
                composition.LastFrameInput.DisplayedDestinationId);

            // Director: rest/unknown path — Page kind, no concrete page/screen.
            Assert.Equal(DirectorIntentKind.Page, composition.LastDirectorIntent.Kind);
            Assert.Null(composition.LastDirectorIntent.Page);
            Assert.Null(composition.LastDirectorIntent.ScreenId);
            Assert.Null(composition.LastDirectorIntent.SourceRuleId);
        }

        // ── B2: step-boundary release / reclaim end-to-end via composition ─

        [Fact]
        public void Engine_B2_ScreenPageAlternation_CompositionEndToEnd()
        {
            // Composition-level pin: page steps promote into EffectivePageDestinationId
            // so E5 composes + director navigates them; screen→page asserts E6 release,
            // ReclaimFrame, merged OnScreen for the hosted page, and director intent.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "p-a",
                        Name = "A",
                        Base = new ContentWithEffect
                        {
                            Content = new ContentObject
                            {
                                Kind = ContentKind.Text,
                                Text = "AAA",
                            },
                        },
                    },
                },
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-alt",
                        Terminal = PlaylistTerminal.Hold,
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.Logo,
                                },
                                DurationMs = 1000,
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Page,
                                    Page = new PageRef
                                    {
                                        Kind = PageRefKind.HostedPage,
                                        Id = "p-a",
                                    },
                                },
                                DurationMs = 1000,
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.Logo,
                                },
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
                            Playlist = "pl-alt",
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(doc, _ => { });

            long clock = 0;
            var control = new B2FakePageControl();
            var props = new B2FakeProps();
            int specialReleases = 0;
            var specialWrites = new List<byte>();
            var segmentWrites = new List<(byte, byte, byte)>();
            var composition = new DisplayCompositionV2(
                doc,
                catalog: new WheelCatalog
                {
                    WheelId = "test",
                    ScreenCommands = new ScreenCommandsCapability
                    {
                        Logo = true,
                        Blank = true,
                    },
                },
                pageControl: control,
                itmDeviceId: 3,
                nowMs: () => clock,
                log: _ => { },
                properties: props,
                options: new DisplayCompositionV2Options { DeviceKey = "test" });
            composition.TryShowSpecialScreen = p =>
            {
                specialWrites.Add(p);
                return true;
            };
            composition.OnSpecialReleased = () => specialReleases++;
            composition.TryWriteLegacySegments = (a, b, c) =>
            {
                segmentWrites.Add((a, b, c));
                return true;
            };

            // t=0: logo screen holds col01 (Special director; surface held).
            var r0 = composition.Tick(new DisplayCompositionV2TickInput { InGame = false });
            Assert.True(composition.LastWheelScreenResult.SurfaceHeld);
            Assert.Equal(WheelScreenCommand.Logo, composition.LastWheelScreenResult.Intent.Command);
            Assert.Equal(DirectorIntentKind.Special, composition.LastDirectorIntent.Kind);
            Assert.NotEmpty(specialWrites);
            // Accept send so latch sticks for the hold.
            clock = 16;
            composition.Tick(new DisplayCompositionV2TickInput { InGame = false });

            // t=1000: page step → E6 release + ReclaimFrame + EffectivePage = hosted:p-a
            // + director SegmentScreen + merged OnScreen for the page plane.
            specialReleases = 0;
            segmentWrites.Clear();
            clock = 1000;
            var rPage = composition.Tick(new DisplayCompositionV2TickInput { InGame = false });

            Assert.False(composition.LastWheelScreenResult.SurfaceHeld);
            Assert.True(composition.LastWheelScreenResult.ReleaseEdge);
            Assert.True(composition.LastFrameResult.ReclaimFrame);
            Assert.True(composition.LastFrameResult.SegmentFrameWritable);
            Assert.Equal(1, specialReleases);
            Assert.NotEmpty(segmentWrites);

            Assert.Equal(
                DestinationIds.Hosted("p-a"),
                composition.LastSeatResult.Intent.EffectivePageDestinationId);
            Assert.Equal(IdleKind.Page, composition.LastSeatResult.Intent.IdleKind);
            Assert.Equal(
                DestinationIds.Hosted("p-a"),
                composition.LastSeatResult.Intent.IdlePageDestinationId);
            Assert.NotEqual(IdleKind.Playlist, composition.LastSeatResult.Intent.IdleKind);

            Assert.Equal(DirectorIntentKind.SegmentScreen, composition.LastDirectorIntent.Kind);
            Assert.Equal("p-a", composition.LastDirectorIntent.ScreenId);

            // Merged record honesty: rest floor is OnScreen on the display plane, and
            // E5 was fed the hosted page as DisplayedDestinationId (not rest:idle).
            // SurfaceWinners carry the page surface with dest hosted:p-a.
            Assert.Equal(
                DestinationIds.Hosted("p-a"),
                composition.LastFrameInput.DisplayedDestinationId);
            Assert.Contains(
                rPage.CarrierStatuses,
                s => s.Presence == CarrierPresence.OnScreen
                    && string.Equals(s.CarrierId, SeatArbiter.RestCarrierId, StringComparison.Ordinal));
            Assert.Contains(
                rPage.SurfaceWinners,
                w => string.Equals(w.DestinationId, DestinationIds.Hosted("p-a"),
                    StringComparison.Ordinal)
                    && string.Equals(w.SurfaceId, "page:p-a", StringComparison.Ordinal));

            // t=2000: screen again → wheel-screen reclaim (surface held + win-edge).
            clock = 2000;
            composition.Tick(new DisplayCompositionV2TickInput { InGame = false });
            Assert.True(composition.LastWheelScreenResult.SurfaceHeld);
            Assert.Equal(WheelScreenCommand.Logo, composition.LastWheelScreenResult.Intent.Command);
            Assert.True(composition.LastWheelScreenResult.SendRequested);
            Assert.False(composition.LastWheelScreenResult.ReleaseEdge);
            Assert.Equal(DirectorIntentKind.Special, composition.LastDirectorIntent.Kind);
        }

        // ── Review pins: duplicates, park floor, shared capability, held-final, UI ─

        [Fact]
        public void Validator_DuplicatePlaylistIds_FirstWins_BothDirections()
        {
            // Valid first, invalid later: first stays resolvable; later degrades itself only.
            var cfgValidFirst = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-dup",
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                            },
                        },
                    },
                    new PlaylistEntry
                    {
                        Id = "pl-dup",
                        Steps = new List<PlaylistStep>(), // zero steps
                    },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Playlist,
                            Playlist = "pl-dup",
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfgValidFirst, _ => { });
            Assert.False(cfgValidFirst.Playlists[0].DegradedAtLoad);
            Assert.True(cfgValidFirst.Playlists[1].DegradedAtLoad);
            Assert.False(cfgValidFirst.Priority.Rest.Idle.DegradedAtLoad);

            // Invalid first still consumes the id; later twin cannot win.
            var cfgInvalidFirst = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-dup",
                        Steps = new List<PlaylistStep>(), // zero steps → not resolvable
                    },
                    new PlaylistEntry
                    {
                        Id = "pl-dup",
                        Steps = new List<PlaylistStep>
                        {
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
                            Playlist = "pl-dup",
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfgInvalidFirst, _ => { });
            Assert.True(cfgInvalidFirst.Playlists[0].DegradedAtLoad);
            Assert.True(cfgInvalidFirst.Playlists[1].DegradedAtLoad); // duplicate, not winner
            Assert.True(cfgInvalidFirst.Priority.Rest.Idle.DegradedAtLoad);
        }

        [Fact]
        public void Engine_AllSkipped_CommandLessItm_ParksOnLegacy()
        {
            var pl = new PlaylistEntry
            {
                Id = "pl-all-skip",
                Steps = new List<PlaylistStep>
                {
                    new PlaylistStep
                    {
                        Destination = new IdleSpec
                        {
                            Kind = IdleKind.Screen,
                            Screen = WheelScreenCommand.Logo,
                        },
                        DurationMs = 1000,
                    },
                },
            };
            var idle = new IdleSpec
            {
                Kind = IdleKind.Playlist,
                Playlist = pl.Id,
                ParkOnLegacyForBlank = true, // validator stamps this for command-less ITM
            };
            var sc = new ScreenCommandsCapability
            {
                Logo = false,
                Blank = false,
            };
            var r = IdleCompile.Resolve(idle, sc, Map(pl), nowMs: 0, anchorMs: 0);
            Assert.Equal(IdleCompileKind.ParkOnLegacyForBlank, r.Kind);
            Assert.True(r.ParkOnLegacyForBlank);
            Assert.NotEqual(IdleCompileKind.Silence, r.Kind);
            Assert.NotEqual(IdleCompileKind.PaintBlankFrame, r.Kind);
        }

        [Fact]
        public void Engine_SeatWheel_SharedCapability_NoCatalogConfig_IdenticalStep()
        {
            // No-catalog normalize leaves screen capability untested at load; both planes
            // must share the same runtime envelope so step selection cannot diverge.
            var doc = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-cap",
                        Terminal = PlaylistTerminal.Hold,
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.LogoInverted,
                                },
                                DurationMs = 5000,
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    Screen = WheelScreenCommand.Logo,
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
                            Playlist = "pl-cap",
                        },
                    },
                },
            };
            // No catalog at normalize — both screens survive load.
            DisplayConfigV2Validator.Normalize(doc, _ => { });

            var sc = new ScreenCommandsCapability
            {
                Logo = true,
                LogoInverted = false,
                Blank = true,
            };
            var seat = new SeatArbiter(doc, new SeatArbiterOptions { ScreenCommands = sc });
            var wheel = new WheelScreenArbiter(doc, new WheelScreenArbiterOptions
            {
                ScreenCommands = sc,
            });

            var seatR = seat.Tick(new SeatArbiterTickInput { NowMs = 0, InGame = false });
            var wheelR = wheel.Tick(new WheelScreenArbiterTickInput { NowMs = 0, InGame = false });

            // LogoInverted skipped on both; active step is Logo on both planes.
            Assert.Equal(IdleKind.Screen, seatR.Intent.IdleKind);
            Assert.Equal(WheelScreenCommand.Logo, seatR.Intent.IdleScreen);
            Assert.Equal(WheelScreenCommand.Logo, wheelR.Intent.Command);
            Assert.True(wheelR.SurfaceHeld);
        }

        [Fact]
        public void ValidatorAndEngine_HeldFinal_FilterFirst_MissingDurationLegal()
        {
            // Missing-duration step becomes held final after a later unresolvable destination
            // is filtered — legal under hold; not load-degraded for authored position.
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A" },
                },
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-held",
                        Terminal = PlaylistTerminal.Hold,
                        Steps = new List<PlaylistStep>
                        {
                            new PlaylistStep
                            {
                                Destination = new IdleSpec { Kind = IdleKind.Blank },
                                // no duration — becomes held final after filter
                            },
                            new PlaylistStep
                            {
                                Destination = new IdleSpec
                                {
                                    Kind = IdleKind.Page,
                                    Page = new PageRef
                                    {
                                        Kind = PageRefKind.HostedPage,
                                        Id = "p-missing",
                                    },
                                },
                                DurationMs = 1000,
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
                            Playlist = "pl-held",
                        },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, _ => { });
            Assert.False(cfg.Playlists[0].Steps[0].DegradedAtLoad);
            Assert.True(cfg.Playlists[0].Steps[1].DegradedAtLoad);
            Assert.False(cfg.Playlists[0].DegradedAtLoad);

            var idle = cfg.Priority.Rest.Idle;
            var sc = new ScreenCommandsCapability { Blank = true };
            var map = Map(cfg.Playlists[0]);
            var r = IdleCompile.Resolve(idle, sc, map, nowMs: 0, anchorMs: 0);
            Assert.Equal(IdleCompileKind.FirmwareBlank, r.Kind);
            Assert.Equal(IdleKind.Blank, r.PublishedIdleKind);
        }

        [Fact]
        public void Picker_DegradedPlaylist_VisibleWithStepLabels()
        {
            var doc = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-empty",
                        Name = "Broken",
                        Steps = new List<PlaylistStep>(),
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(doc, _ => { });
            Assert.True(doc.Playlists[0].DegradedAtLoad);

            var model = DisplayPriorityV2Model.Project(
                doc,
                DisplayResolutionSnapshotModel.Empty,
                null,
                DisplayType.Itm,
                catalog: null);

            var group = model.IdlePicker.Groups
                .First(g => g.Header == DisplayCopy.PlaylistsGroup);
            Assert.NotEmpty(group.Items);
            var item = group.Items[0];
            Assert.Equal("pl-empty", item.PlaylistId);
            Assert.Equal("Broken", item.Name);
            Assert.False(item.IsEnabled);
            Assert.Contains(
                DisplayCopy.PlaylistStepSkipped,
                item.CapabilityNote ?? item.TrailingNote ?? string.Empty);
        }

        [Fact]
        public void Picker_SubFloorDuration_ShowsClampedValueAndMarker()
        {
            var step = new PlaylistStep
            {
                Destination = new IdleSpec
                {
                    Kind = IdleKind.Screen,
                    Screen = WheelScreenCommand.Logo,
                },
                DurationMs = 100,
            };
            string label = DisplayCopy.PlaylistStepDurationLabel(step);
            Assert.Contains(DisplayCopy.PlaylistStepDuration(SeatArbiter.MinDwellMs), label);
            Assert.Contains(DisplayCopy.PlaylistStepDurationClamped, label);
            Assert.DoesNotContain("100 ms", label); // shows clamped, not authored
            Assert.Equal(100, step.DurationMs); // document intact

            // Overview IdleDetail uses the same DisplayCopy path as the picker.
            var doc = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-clamp",
                        Name = "Clamp",
                        Steps = new List<PlaylistStep> { step },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Playlist,
                            Playlist = "pl-clamp",
                        },
                    },
                },
            };
            string detail = DisplayOverviewV2Model.IdleDetail(
                doc.Priority.Rest.Idle, doc);
            Assert.Contains(DisplayCopy.PlaylistStepDuration(SeatArbiter.MinDwellMs), detail);
            Assert.Contains(DisplayCopy.PlaylistStepDurationClamped, detail);
            Assert.DoesNotContain("100 ms", detail);
        }

        private sealed class B2FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Idle;
            public byte? CurrentWirePage { get; set; }
            public long SyncGeneration { get; set; }
            public void RequestPage(byte wirePage) { }
            public void Land(byte wirePage)
            {
                State = ItmLifecycleState.Synced;
                CurrentWirePage = wirePage;
                SyncGeneration++;
            }
        }

        private sealed class B2FakeProps : IPropertyReader
        {
            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                return false;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                return false;
            }
        }

        [Fact]
        public void Engine_ArbiterRestart_OnSessionReEntry()
        {
            var doc = ScreensaverDoc(durationMs: 1000);
            DisplayConfigV2Validator.Normalize(doc, _ => { });
            var a = new WheelScreenArbiter(doc, new WheelScreenArbiterOptions
            {
                ScreenCommands = new ScreenCommandsCapability { Logo = true, Blank = true },
            });

            a.Tick(new WheelScreenArbiterTickInput { NowMs = 0, InGame = false });
            var mid = a.Tick(new WheelScreenArbiterTickInput { NowMs = 1500, InGame = false });
            Assert.Equal(WheelScreenCommand.Blank, mid.Intent.Command);

            // Session returns then idle again → restart at logo.
            a.Tick(new WheelScreenArbiterTickInput { NowMs = 2000, InGame = true });
            var again = a.Tick(new WheelScreenArbiterTickInput { NowMs = 3000, InGame = false });
            Assert.Equal(WheelScreenCommand.Logo, again.Intent.Command);
        }

        // ── Picker + labels ──────────────────────────────────────────────

        [Fact]
        public void Picker_PlaylistsGroup_ListsDocumentPlaylists_WithSkipLabels()
        {
            var doc = new DisplayConfigV2
            {
                Playlists = new List<PlaylistEntry>
                {
                    new PlaylistEntry
                    {
                        Id = "pl-ss",
                        Name = "Screensaver",
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
            var catalog = new WheelCatalog
            {
                ScreenCommands = new ScreenCommandsCapability
                {
                    Logo = true,
                    LogoInverted = false,
                    Blank = true,
                },
            };
            DisplayConfigV2Validator.Normalize(doc, _ => { }, catalog);

            var model = DisplayPriorityV2Model.Project(
                doc,
                DisplayResolutionSnapshotModel.Empty,
                null,
                DisplayType.Itm,
                catalog);

            var group = model.IdlePicker.Groups
                .First(g => g.Header == DisplayCopy.PlaylistsGroup);
            Assert.NotEmpty(group.Items);
            var item = group.Items[0];
            Assert.Equal("pl-ss", item.PlaylistId);
            Assert.Equal("Screensaver", item.Name);
            Assert.Equal(IdleKind.Playlist, item.IdleKind);
            Assert.Contains(DisplayCopy.PlaylistStepSkipped, item.CapabilityNote
                ?? item.TrailingNote ?? string.Empty);

            // Idle row shows playlist badge when target is a playlist.
            var idleRow = model.Rows.First(r => r.IsIdleRow);
            Assert.True(idleRow.ShowPlaylistBadge);
            Assert.Equal("Screensaver", idleRow.IdleTargetLabel);
        }

        [Fact]
        public void IdleFromPicker_PlaylistSelection()
        {
            var item = new PriorityPickerItemModel(
                key: "playlist:pl-x",
                badge: DisplayCopy.PlaylistBadge,
                name: "X",
                trailingNote: null,
                isSelected: true,
                isEnabled: true,
                capabilityNote: null,
                idleKind: IdleKind.Playlist,
                pageRef: null,
                screen: WheelScreenCommand.Unknown,
                playlistId: "pl-x");
            var idle = DisplayPriorityV2Model.IdleFromPickerItem(item);
            Assert.Equal(IdleKind.Playlist, idle.Kind);
            Assert.Equal("pl-x", idle.Playlist);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static DisplayConfigV2 ScreensaverDoc(int durationMs = 60000)
        {
            return new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A",
                    },
                },
                Playlists = new List<PlaylistEntry>
                {
                    LogoThenBlank(logoMs: durationMs),
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Playlist,
                            Playlist = "pl-ss",
                        },
                    },
                },
            };
        }
    }
}
