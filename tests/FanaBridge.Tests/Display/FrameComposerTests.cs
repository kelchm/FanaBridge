// Scaffolding — deleted at E8b.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Host;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using GameReaderCommon;
using FanaBridge.Tests.Display.TestSupport;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Scaffolding — deleted at E8b. v9 DisplayRuleStack parity harness fixtures.
    /// </summary>
    public class FrameComposerTests
    {
        // ── Snapshot / content helpers ───────────────────────────────────

        private static CarrierTickSnapshot Snap(
            string id, bool active, bool fired = false, bool fresh = false,
            bool eligible = true, int? remaining = null)
            => new CarrierTickSnapshot(
                id, conditionSatisfied: active, active, fresh, fired,
                legacySupersededV9: false, eligible, expiresAtMs: 0, remaining);

        private static SegmentContentContext Ctx(
            double? speed = null, string? gear = null, double? rpm = null,
            double? pos = null, double? fuel = null, bool inGame = true,
            IPropertyReader? props = null)
            => new SegmentContentContext
            {
                InGame = inGame,
                SpeedLocal = speed,
                Gear = gear,
                Rpms = rpm,
                Position = pos,
                Fuel = fuel,
                Properties = props,
            };

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc)
            => DisplayConfigV2Validator.Normalize(doc, _ => { });

        private static FrameComposer Composer(
            DisplayConfigV2 doc,
            IReadOnlyDictionary<ushort, FieldCapability>? caps = null,
            Action<string>? warn = null,
            IReadOnlyDictionary<ushort, string>? primaryHost = null)
        {
            var options = new FrameComposerOptions
            {
                Capabilities = caps,
                DeviceKey = "test",
                Warn = warn,
            };
            if (primaryHost != null)
                options.PrimaryHostByParam = primaryHost;
            else if (caps != null)
                options.PrimaryHostByParam =
                    FieldCapability.PrimaryHostMapFromCapabilities(caps);
            return new FrameComposer(Normalize(doc), options);
        }

        private static FrameComposerTickInput In(
            long now,
            string? segmentPage,
            string? displayed,
            SegmentContentContext? content = null,
            IReadOnlyCollection<string>? dismissed = null,
            bool wheelScreenHolds = false,
            params CarrierTickSnapshot[] snaps)
            => new FrameComposerTickInput
            {
                NowMs = now,
                SegmentHostedPageId = segmentPage,
                DisplayedDestinationId = displayed,
                Content = content ?? Ctx(speed: 88),
                CarrierSnapshots = snaps,
                DismissedCarrierIds = dismissed ?? Array.Empty<string>(),
                SegmentSurfaceHeldByWheelScreen = wheelScreenHolds,
            };

        private static ContentObject Text(string t)
            => new ContentObject { Kind = ContentKind.Text, Text = t };

        private static ContentObject Speed()
            => new ContentObject { Kind = ContentKind.Speed };

        private static LayerEntry Layer(
            string id, string text, ContentEffect effect = ContentEffect.None)
            => new LayerEntry
            {
                Id = id,
                Name = id,
                Content = Text(text),
                Effect = effect,
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = BuiltInProperties.PitLimiterOn,
                    },
                    Operator = ConditionOperator.IsTrue,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        private static FieldOverride Ov(
            string id, FieldWrites writes, string? text,
            ContentEffect effect = ContentEffect.None,
            FieldAlignment align = FieldAlignment.Left,
            ContentObject? content = null)
            => new FieldOverride
            {
                Id = id,
                Writes = writes,
                Content = content ?? Text(text ?? ""),
                Effect = effect,
                Alignment = align,
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = BuiltInProperties.PitLimiterOn,
                    },
                    Operator = ConditionOperator.IsTrue,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        private static DisplayConfigV2 HostedDoc(
            string pageId, ContentWithEffect? bas, params LayerEntry[] layers)
            => new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = pageId,
                        Name = pageId,
                        Base = bas,
                        Layers = layers?.ToList() ?? new List<LayerEntry>(),
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = pageId,
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

        private static FieldCapability Cap(
            ushort paramId,
            bool? suffixSupported = true,
            int? suffixWidth = 5,
            bool? numeric = true,
            bool? ascii = false,
            string? primaryHost = "tyreTemps",
            bool? overridable = true,
            params string[] hosts)
            => new FieldCapability
            {
                ParamId = paramId,
                SuffixSupported = suffixSupported,
                SuffixWidth = suffixWidth,
                ValueNumeric = numeric,
                ValueAscii = ascii,
                Overridable = overridable,
                PrimaryHostCatalogPageId = primaryHost,
                HostCatalogPageIds = hosts.Length > 0
                    ? hosts.ToList()
                    : (primaryHost != null
                        ? new List<string> { primaryHost }
                        : new List<string>()),
            };

        private static (byte, byte, byte) Triple(byte[] f)
            => (f[0], f[1], f[2]);

        private static DisplayConfigV2 FieldOnlyDoc(
            ushort paramId, params FieldOverride[] overrides)
            => new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-x", Name = "X",
                        Base = new ContentWithEffect { Content = Text("XXX") },
                    },
                },
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [paramId] = new FieldEntry
                    {
                        Base = new FieldBase { BaseSuffix = "C" },
                        Overrides = overrides.ToList(),
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-x",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

        private static ComposedResolutionRecord MergeRecords(
            ComposedResolutionRecord? e4, ComposedResolutionRecord? e5)
        {
            // Minimal pure merge matching contract §6.1 for the unit test.
            var rows = new Dictionary<(string, string), CarrierResolutionStatus>();
            void ingest(IReadOnlyList<CarrierResolutionStatus>? list)
            {
                if (list == null) return;
                foreach (var s in list)
                {
                    var key = (s.CarrierId, s.SurfaceId);
                    if (!rows.TryGetValue(key, out var existing))
                    {
                        rows[key] = s;
                        continue;
                    }
                    var presence = existing.Presence ?? s.Presence;
                    if (existing.Presence != null && s.Presence != null
                        && existing.Presence != s.Presence)
                        throw new InvalidOperationException(
                            "presence conflict for " + key);
                    rows[key] = new CarrierResolutionStatus(
                        s.CarrierId,
                        s.SurfaceId,
                        existing.DestinationId ?? s.DestinationId,
                        presence,
                        existing.RemainingMs ?? s.RemainingMs,
                        existing.RowLabels | s.RowLabels);
                }
            }
            ingest(e4?.CarrierStatuses);
            ingest(e5?.CarrierStatuses);

            var winners = new Dictionary<string, SurfaceWinner>(StringComparer.Ordinal);
            void ingestW(IReadOnlyList<SurfaceWinner>? list)
            {
                if (list == null) return;
                foreach (var w in list)
                    winners[w.SurfaceId] = w;
            }
            ingestW(e4?.SurfaceWinners);
            ingestW(e5?.SurfaceWinners);

            var snaps = new Dictionary<string, CarrierTickSnapshot>(StringComparer.Ordinal);
            void ingestS(IReadOnlyList<CarrierTickSnapshot>? list)
            {
                if (list == null) return;
                foreach (var s in list)
                    if (s.CarrierId != null) snaps[s.CarrierId] = s;
            }
            ingestS(e4?.CarrierSnapshots);
            ingestS(e5?.CarrierSnapshots);

            return new ComposedResolutionRecord(
                e4?.TickMs ?? e5?.TickMs ?? 0,
                e4?.DeviceKey ?? e5?.DeviceKey ?? "",
                winners.Values.ToList(),
                rows.Values.ToList(),
                snaps.Values.ToList());
        }

        // ═════════════════════════════════════════════════════════════════
        // Parity battery — real v9 DisplayRuleStack harness
        // FA2: v2 side is a FROZEN fixture pair (parity-pairs/*.v2.json), not a
        // test-time migration. v1 JSON stays inline and drives the v9 reference.
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Loads a frozen v2 parity-pair fixture (embedded under
        /// Display/Fixtures/parity-pairs/). FA2: counterparts were produced once
        /// offline; the harness never migrates at test time.
        /// </summary>
        private static DisplayConfigV2 LoadParityV2(string fileName)
        {
            var asm = typeof(FrameComposerTests).Assembly;
            // Embedded-resource names fold path separators and '-' in folders to '_'.
            string suffix = ".Display.Fixtures.parity_pairs." + fileName;
            string resource = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using (var stream = asm.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream!))
                return DisplayConfigV2Serializer.Load(reader.ReadToEnd(), _ => { });
        }

        /// <summary>
        /// E5-001: isolated v9 DisplayRuleStack harness vs frozen-fixture FrameComposer.
        /// Fixed expected byte records at fixed ticks; genuine v1 docs with rules
        /// (v9 reference); winner handoffs, gear-overlay window, blink/scroll boundaries.
        /// </summary>
        [Fact]
        public void Parity_V9StackHarness_WinnerHandoff_GearOverlay_BlinkScroll()
        {
            // v1 document with base speed, gear-change overlay (2s), blink pit rule,
            // and a scroll message screen for boundary coverage. Drives v9 only.
            const string v1Json = @"{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {
    ""baseScreenId"": ""spd"",
    ""screens"": [
      { ""id"": ""spd"", ""name"": ""Speed"", ""contentKind"": ""speed"", ""inRotation"": true },
      { ""id"": ""gear"", ""name"": ""Gear"", ""contentKind"": ""gear"", ""inRotation"": false },
      { ""id"": ""pit"", ""name"": ""Pit"", ""contentKind"": ""text"", ""text"": ""PIT"", ""effect"": ""blink"", ""inRotation"": false },
      { ""id"": ""msg"", ""name"": ""Msg"", ""contentKind"": ""message"", ""text"": ""HELLO"", ""effect"": ""scroll"", ""inRotation"": false }
    ],
    ""rules"": [
      {
        ""id"": ""r-gear"",
        ""name"": ""Gear change"",
        ""when"": { ""kind"": ""changes"", ""source"": { ""kind"": ""builtIn"", ""name"": ""Gear"" } },
        ""show"": { ""kind"": ""segmentScreen"", ""screenId"": ""gear"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 2000 }
      },
      {
        ""id"": ""r-pit"",
        ""name"": ""Pit"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""PitLimiterOn"" } },
        ""show"": { ""kind"": ""segmentScreen"", ""screenId"": ""pit"" },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ]
  }
}";
            var v2 = LoadParityV2("winner-handoff-gear-overlay-blink-scroll.v2.json");
            var composer = new FrameComposer(v2, new FrameComposerOptions { DeviceKey = "parity" });

            var harness = V9Harness.Create(v1Json);
            // Capture col01 writes from the stack.
            var written = new List<(long t, byte a, byte b, byte c)>();
            harness.Stack.TryWriteLegacySegments = (a, b, c) =>
            {
                written.Add((harness.T, a, b, c));
                return true;
            };

            // Fixed tick plan with expected stack frames (goldens).
            // t=0: gear baseline sample (first edge does not fire) → speed 123
            // t=16: gear changes 1→3 → gear overlay window
            // t=1000: still in overlay (gear "3")
            // t=2100: overlay expired → speed
            // t=3000: pit on → blink on phase at 3000 (3000/500=6 even → on)
            // t=3500: pit still on, blink off
            // t=4000: pit off → speed

            byte[] Expect(string text, LegacyEffect effect, long now)
                => text == null
                    ? new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank }
                    : LegacyEffectClock.Apply(text, effect, now);

            // Drive stack + composer in lockstep; compare to fixed expectations.
            void Step(long t, GameData data, string composerPage, SegmentContentContext ctx,
                byte[] expected)
            {
                harness.T = t;
                written.Clear();
                harness.Stack.Tick(null, data);
                Assert.True(written.Count >= 1, "stack must write a frame at t=" + t);
                var last = written[written.Count - 1];
                Assert.Equal(expected, new[] { last.a, last.b, last.c });

                var result = composer.Tick(new FrameComposerTickInput
                {
                    NowMs = t,
                    SegmentHostedPageId = composerPage,
                    DisplayedDestinationId = DestinationIds.Hosted(composerPage),
                    Content = ctx,
                });
                Assert.Equal(expected, result.SegmentFrame);
            }

            var speedCtx = Ctx(speed: 123, gear: "1", inGame: true);
            var gear3Ctx = Ctx(speed: 123, gear: "3", inGame: true);

            // t=0: establish gear baseline
            Step(0, Live(gear: "1", speed: 123), "spd", speedCtx,
                Expect(LegacyValueFormatter.FormatSpeed(123), LegacyEffect.None, 0));

            // t=16: gear edge → overlay
            Step(16, Live(gear: "3", speed: 123), "gear", gear3Ctx,
                Expect(LegacyValueFormatter.FormatGear("3"), LegacyEffect.None, 16));

            // t=1000: still overlay
            Step(1000, Live(gear: "3", speed: 123), "gear", gear3Ctx,
                Expect(LegacyValueFormatter.FormatGear("3"), LegacyEffect.None, 1000));

            // t=2100: past 2000 ms hold → base speed
            Step(2100, Live(gear: "3", speed: 123), "spd", gear3Ctx,
                Expect(LegacyValueFormatter.FormatSpeed(123), LegacyEffect.None, 2100));

            // t=3000: pit limiter → blink on (phase even)
            Step(3000, Live(gear: "3", speed: 123, pit: 1), "pit", gear3Ctx,
                Expect("PIT", LegacyEffect.Blink, 3000));
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Triple(Expect("PIT", LegacyEffect.Blink, 3000)));

            // t=3500: blink off half
            Step(3500, Live(gear: "3", speed: 123, pit: 1), "pit", gear3Ctx,
                Expect("PIT", LegacyEffect.Blink, 3500));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Triple(Expect("PIT", LegacyEffect.Blink, 3500)));

            // t=4000: pit off → speed
            Step(4000, Live(gear: "3", speed: 123, pit: 0), "spd", gear3Ctx,
                Expect(LegacyValueFormatter.FormatSpeed(123), LegacyEffect.None, 4000));

            // Scroll boundary: compose msg page at step 0 and ScrollStepMs.
            var scroll0 = Expect("HELLO", LegacyEffect.Scroll, 0);
            var scroll1 = Expect("HELLO", LegacyEffect.Scroll, LegacyEffectClock.ScrollStepMs);
            Assert.NotEqual(scroll0, scroll1);
            var msg0 = composer.Tick(new FrameComposerTickInput
            {
                NowMs = 0,
                SegmentHostedPageId = "msg",
                DisplayedDestinationId = DestinationIds.Hosted("msg"),
                Content = Ctx(),
            });
            var msg1 = composer.Tick(new FrameComposerTickInput
            {
                NowMs = LegacyEffectClock.ScrollStepMs,
                SegmentHostedPageId = "msg",
                DisplayedDestinationId = DestinationIds.Hosted("msg"),
                Content = Ctx(),
            });
            Assert.Equal(scroll0, msg0.SegmentFrame);
            Assert.Equal(scroll1, msg1.SegmentFrame);
        }

        [Theory]
        [InlineData("text_none", "HI", "text", "none")]
        [InlineData("text_blink", "PIT", "text", "blink")]
        [InlineData("message_scroll", "HELLO", "message", "scroll")]
        [InlineData("speed_none", "", "speed", "none")]
        [InlineData("gear_none", "", "gear", "none")]
        [InlineData("fuel_none", "", "fuel", "none")]
        public void Parity_MigratedV1Screen_MatchesStackAndFixedBytes(
            string fixtureName, string text, string contentKind, string effect)
        {
            Assert.False(string.IsNullOrEmpty(fixtureName));

            // v1 drives the v9 stack reference; v2 is the frozen FA2 fixture pair.
            string v1Json = BuildV1SingleScreen(text, contentKind, effect);
            var v2 = LoadParityV2(fixtureName + ".v2.json");
            string hostedId = v2.Pages.First(p => p.Kind == PageEntryKind.HostedPage).Id;

            var content = Ctx(
                speed: 123.4, gear: "3", rpm: 7000, pos: 5, fuel: 42.2, inGame: true);
            var composer = new FrameComposer(v2, new FrameComposerOptions { DeviceKey = "parity" });

            var harness = V9Harness.Create(v1Json);
            byte[]? last = null;
            harness.Stack.TryWriteLegacySegments = (a, b, c) =>
            {
                last = new[] { a, b, c };
                return true;
            };

            long[] ticks =
            {
                0,
                LegacyEffectClock.BlinkHalfPeriodMs - 1,
                LegacyEffectClock.BlinkHalfPeriodMs,
                LegacyEffectClock.BlinkHalfPeriodMs * 2,
                LegacyEffectClock.ScrollStepMs,
                LegacyEffectClock.ScrollStepMs * 2,
                4000,
            };

            foreach (long now in ticks)
            {
                harness.T = now;
                last = null;
                harness.Stack.Tick(null, Live(gear: "3", speed: 123.4, rpm: 7000, pos: 5, fuel: 42.2));
                Assert.NotNull(last);

                var result = composer.Tick(In(
                    now, hostedId, DestinationIds.Hosted(hostedId), content));
                Assert.Equal(last, result.SegmentFrame);
            }
        }

        [Fact]
        public void Parity_PropertyKind_ByteIdentical()
        {
            var reader = new DictReader(42);
            // v1 drives the formatter reference; v2 is the frozen FA2 fixture pair.
            string v1Json = @"{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {
    ""screens"": [
      {
        ""id"": ""s-prop"",
        ""name"": ""PROP"",
        ""contentKind"": ""property"",
        ""source"": { ""kind"": ""builtIn"", ""name"": ""Fuel"" },
        ""effect"": ""none"",
        ""inRotation"": true
      }
    ],
    ""baseScreenId"": ""s-prop"",
    ""rules"": []
  }
}";
            var v1 = DisplayConfigSerializer.Load(v1Json, _ => { });
            var v2 = LoadParityV2("property-kind.v2.json");
            var screen = v1.Legacy.Screens[0];
            string hostedId = v2.Pages.First(p => p.Kind == PageEntryKind.HostedPage).Id;

            var content = Ctx(inGame: true, props: reader);
            var composer = new FrameComposer(v2);

            string v9Text = LegacyValueFormatter.FormatProperty(reader, screen.Source);
            byte[] v9Frame = LegacyEffectClock.Apply(v9Text, LegacyEffect.None, 0);

            var result = composer.Tick(In(0, hostedId, DestinationIds.Hosted(hostedId), content));
            Assert.Equal(v9Frame, result.SegmentFrame);
        }

        private static string BuildV1SingleScreen(
            string text, string contentKind, string effect)
        {
            string textJson = !string.IsNullOrEmpty(text)
                ? $@", ""text"": ""{text}"""
                : "";
            return $@"{{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {{
    ""screens"": [
      {{
        ""id"": ""s1"",
        ""name"": ""S1"",
        ""contentKind"": ""{contentKind}""{textJson},
        ""effect"": ""{effect}"",
        ""inRotation"": true
      }}
    ],
    ""baseScreenId"": ""s1"",
    ""rules"": []
  }}
}}";
        }

        // ── v9 stack harness (real DisplayRuleStack) ─────────────────────

        private sealed class FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Synced;
            public byte? CurrentWirePage { get; set; } = 1;
            public long SyncGeneration { get; set; }
            public void RequestPage(byte wirePage) { }
        }

        private sealed class V9Harness
        {
            public long T;
            public DisplayRuleStack Stack = null!;
            public readonly FakePageControl Control = new FakePageControl();

            public static V9Harness Create(string configJson)
            {
                var h = new V9Harness();
                var config = DisplayConfigSerializer.Load(configJson, _ => { });
                h.Stack = new DisplayRuleStack(
                    config, h.Control, itmDeviceId: 2, defaultWirePage: 1,
                    log: _ => { }, nowMs: () => h.T);
                return h;
            }
        }

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p).GetSetMethod(true).Invoke(s, new[] { v });

        private static GameData Live(
            string gear = "1",
            double speed = 0,
            double rpm = 0,
            int pos = 0,
            double fuel = 0,
            int pit = 0)
        {
            var s = NewStatus();
            Set(s, "Gear", gear);
            Set(s, "SpeedLocal", speed);
            Set(s, "Rpms", rpm);
            Set(s, "Position", pos);
            Set(s, "Fuel", fuel);
            Set(s, "IsInPitLane", pit);
            // PitLimiterOn is the built-in the pit rule keys on (not IsInPitLane alone).
            Set(s, "PitLimiterOn", pit);
            var d = new GameData { NewData = (StatusDataBase)s };
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { true });
            return d;
        }



    }
}
