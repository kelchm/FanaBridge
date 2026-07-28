using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using FanaBridge.Tests.Display.TestSupport;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E7a: pure WalkCompiler — every §5b law as a named fixture, wrap/step
    /// rules, recompile determinism, Sam/Alex example walks, and a two-tick
    /// SeatArbiter integration (compiled walk in, manual row remembers resolved target).
    /// </summary>
    public class WalkCompilerTests
    {
        // ── Fixtures ─────────────────────────────────────────────────────

        private static WheelCatalog LoadPbmeCatalog()
        {
            Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
            return catalog!;
        }

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc, WheelCatalog catalog = null)
            => DisplayConfigV2Validator.Normalize(doc, _ => { }, catalog);

        private static DisplayConfigV2 LoadExample(string fileName, WheelCatalog catalog = null)
        {
            var path = Path.Combine(
                TestPaths.RepoRoot(), "scratch", "plans", "display-customization",
                "examples", fileName);
            var json = File.ReadAllText(path);
            var doc = DisplayConfigV2Serializer.Load(json, _ => { });
            if (catalog != null)
                return Normalize(doc, catalog);
            return doc;
        }

        private static PageRef HostedRef(string id)
            => new PageRef { Kind = PageRefKind.HostedPage, Id = id };

        private static PageRef ItmRef(string catalogPageId)
            => new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = catalogPageId };

        private static PageEntry HostedPage(string id)
            => new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = id,
                Name = id,
                Layers = new List<LayerEntry>(),
            };

        private static PageEntry ItmPage(string catalogPageId, bool removed = false)
            => new PageEntry
            {
                Kind = PageEntryKind.ItmPage,
                CatalogPageId = catalogPageId,
                Removed = removed,
            };

        private static DisplayConfigV2 BaseDoc(params PageEntry[] pages)
            => new DisplayConfigV2
            {
                Pages = new List<PageEntry>(pages),
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

        private static WheelCatalog MiniCatalog(params (string id, int index)[] pages)
        {
            var catalog = new WheelCatalog
            {
                WheelId = "test",
                Itm = new ItmCatalogSection
                {
                    Pages = pages.Select(p => new CatalogPage
                    {
                        Id = p.id,
                        Index = p.index,
                        Name = p.id,
                    }).ToList(),
                },
            };
            return catalog;
        }

        // ── §5b compile laws ─────────────────────────────────────────────

        [Fact]
        public void Law_ExplicitPageOrder_ValidatedRefsOnly_OrderPreserved()
        {
            // Explicit pageOrder: membership = presence; order = array order.
            var catalog = MiniCatalog(("lapInfo", 1), ("tyreTemps", 5), ("fuelErsDrs", 2));
            var doc = BaseDoc(
                HostedPage("p-a"),
                HostedPage("p-b"),
                ItmPage("lapInfo"),
                ItmPage("tyreTemps"),
                ItmPage("fuelErsDrs"));
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-b"),
                ItmRef("fuelErsDrs"),
                HostedRef("p-a"),
            };
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(WalkCompileSource.Explicit, walk.Source);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-b"),
                    DestinationIds.Itm("fuelErsDrs"),
                    DestinationIds.Hosted("p-a"),
                },
                walk.DestinationIds.ToArray());
        }

        [Fact]
        public void Law_AbsentPageOrder_IsCompiledDefault_CatalogIndexThenHostedPagesOrder()
        {
            // ABSENT pageOrder = catalog pages by index (excl. removed) + hosted in pages[] order.
            var catalog = MiniCatalog(
                ("tyreTemps", 5),
                ("lapInfo", 1),
                ("fuelErsDrs", 2));
            var doc = BaseDoc(
                HostedPage("p-first"),
                HostedPage("p-second"),
                ItmPage("lapInfo"),
                ItmPage("fuelErsDrs"),
                ItmPage("tyreTemps"));
            doc.PageOrder = null;
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(WalkCompileSource.Default, walk.Source);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("fuelErsDrs"),
                    DestinationIds.Itm("tyreTemps"),
                    DestinationIds.Hosted("p-first"),
                    DestinationIds.Hosted("p-second"),
                },
                walk.DestinationIds.ToArray());
        }

        [Fact]
        public void Law_EmptyPageOrder_IsEmptyWalk()
        {
            // Explicit [] = EMPTY walk (the document's one deliberate absent≠empty pair).
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), ItmPage("lapInfo"));
            doc.PageOrder = new List<PageRef>();
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(WalkCompileSource.Empty, walk.Source);
            Assert.True(walk.IsEmpty);
            Assert.Empty(walk.DestinationIds);
        }

        [Fact]
        public void Law_RemovedItm_ExcludedFromExplicitPageOrder()
        {
            var catalog = MiniCatalog(("lapInfo", 1), ("fuelErsDrs", 2), ("tyreTemps", 5));
            var doc = BaseDoc(
                ItmPage("lapInfo"),
                ItmPage("fuelErsDrs", removed: true),
                ItmPage("tyreTemps"),
                HostedPage("p-a"));
            doc.PageOrder = new List<PageRef>
            {
                ItmRef("lapInfo"),
                ItmRef("fuelErsDrs"), // removed — must skip
                ItmRef("tyreTemps"),
            };
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("tyreTemps"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(DestinationIds.Itm("fuelErsDrs"), walk.DestinationIds);
        }

        [Fact]
        public void Law_RemovedItm_ExcludedFromDefaultWalk()
        {
            var catalog = MiniCatalog(("lapInfo", 1), ("fuelErsDrs", 2), ("tyreTemps", 5));
            var doc = BaseDoc(
                ItmPage("lapInfo"),
                ItmPage("fuelErsDrs", removed: true),
                ItmPage("tyreTemps"),
                HostedPage("p-a"));
            doc.PageOrder = null;
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(WalkCompileSource.Default, walk.Source);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("tyreTemps"),
                    DestinationIds.Hosted("p-a"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(DestinationIds.Itm("fuelErsDrs"), walk.DestinationIds);
        }

        [Fact]
        public void Law_Duplicates_FirstKept()
        {
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), HostedPage("p-b"), ItmPage("lapInfo"));
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-a"),
                ItmRef("lapInfo"),
                HostedRef("p-a"), // duplicate
                HostedRef("p-b"),
                ItmRef("lapInfo"), // duplicate
            };
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-a"),
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Hosted("p-b"),
                },
                walk.DestinationIds.ToArray());
        }

        [Fact]
        public void Law_UnresolvedRefs_Skipped()
        {
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), ItmPage("lapInfo"));
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-a"),
                HostedRef("p-missing"),       // unresolved hosted
                ItmRef("noSuchPage"),         // unresolved ITM (not on catalog)
                ItmRef("lapInfo"),
            };
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-a"),
                    DestinationIds.Itm("lapInfo"),
                },
                walk.DestinationIds.ToArray());
        }

        [Fact]
        public void Law_CycleRef_Skipped()
        {
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), ItmPage("lapInfo"));
            doc.Cycles = new List<CycleEntry>
            {
                new CycleEntry
                {
                    Id = "c-pit",
                    Members = new List<PageRef>
                    {
                        ItmRef("lapInfo"),
                        HostedRef("p-a"),
                    },
                    PeriodMs = 5000,
                },
            };
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-a"),
                new PageRef { Kind = PageRefKind.Cycle, Id = "c-pit" },
                ItmRef("lapInfo"),
            };
            doc = Normalize(doc, catalog);

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-a"),
                    DestinationIds.Itm("lapInfo"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(walk.DestinationIds, d => DestinationIds.IsCycle(d));
        }

        // ── Step laws ────────────────────────────────────────────────────

        [Fact]
        public void Step_Wrap_BothDirections()
        {
            var walk = new[]
            {
                DestinationIds.Hosted("p-a"),
                DestinationIds.Hosted("p-b"),
                DestinationIds.Hosted("p-c"),
            };

            var nextFromLast = WalkCompiler.Step(walk, DestinationIds.Hosted("p-c"), +1);
            Assert.Equal(DestinationIds.Hosted("p-a"), nextFromLast.DestinationId);

            var prevFromFirst = WalkCompiler.Step(walk, DestinationIds.Hosted("p-a"), -1);
            Assert.Equal(DestinationIds.Hosted("p-c"), prevFromFirst.DestinationId);

            Assert.Equal(
                DestinationIds.Hosted("p-b"),
                WalkCompiler.Step(walk, DestinationIds.Hosted("p-a"), +1).DestinationId);
            Assert.Equal(
                DestinationIds.Hosted("p-b"),
                WalkCompiler.Step(walk, DestinationIds.Hosted("p-c"), -1).DestinationId);
        }

        [Fact]
        public void Step_FromOutsideWalk_DirectionAwareNearestIndex_BothDirections()
        {
            // Rule (pinned): off-walk re-entry is direction-aware + nearest catalog index.
            // NEXT = min index strictly greater (wrap lowest ITM / walk[0]);
            // PREV = max index strictly lesser (wrap highest ITM / walk[last]).
            // Landing consumes direction (no second step). Authored order is tie-break only.
            var catalog = MiniCatalog(
                ("lapInfo", 1),
                ("fuelErsDrs", 2),
                ("carSettings", 3),
                ("lapTimes", 4),
                ("tyreTemps", 5));

            // Walk skips fuelErsDrs and carSettings — off-walk mid-roster.
            // Catalog-sorted authored order here; non-catalog-order covered separately.
            var walk = new[]
            {
                DestinationIds.Itm("lapInfo"),
                DestinationIds.Itm("lapTimes"),
                DestinationIds.Itm("tyreTemps"),
                DestinationIds.Hosted("p-shift"),
            };

            // On fuelErsDrs (index 2, off-walk): NEXT → min greater = lapTimes (4).
            var nextFromFuel = WalkCompiler.Step(
                walk, DestinationIds.Itm("fuelErsDrs"), +1, catalog);
            Assert.Equal(DestinationIds.Itm("lapTimes"), nextFromFuel.DestinationId);

            // PREV from index 2 → max lesser = lapInfo (1). Direction-aware (E7A-001).
            var prevFromFuel = WalkCompiler.Step(
                walk, DestinationIds.Itm("fuelErsDrs"), -1, catalog);
            Assert.Equal(DestinationIds.Itm("lapInfo"), prevFromFuel.DestinationId);

            // On carSettings (index 3): NEXT → lapTimes (4); PREV → lapInfo (1).
            Assert.Equal(
                DestinationIds.Itm("lapTimes"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("carSettings"), +1, catalog).DestinationId);
            Assert.Equal(
                DestinationIds.Itm("lapInfo"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("carSettings"), -1, catalog).DestinationId);

            // Past last ITM index: NEXT wraps to lowest-index ITM (lapInfo).
            var highCatalog = MiniCatalog(
                ("lapInfo", 1), ("lapTimes", 4), ("tyreTemps", 5), ("extra", 9));
            Assert.Equal(
                DestinationIds.Itm("lapInfo"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("extra"), +1, highCatalog).DestinationId);
            // PREV from past end → highest-index ITM still in walk (tyreTemps).
            Assert.Equal(
                DestinationIds.Itm("tyreTemps"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("extra"), -1, highCatalog).DestinationId);

            // Below first ITM: PREV wraps to highest-index ITM (tyreTemps).
            var lowCatalog = MiniCatalog(
                ("before", 0), ("lapInfo", 1), ("lapTimes", 4), ("tyreTemps", 5));
            Assert.Equal(
                DestinationIds.Itm("tyreTemps"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("before"), -1, lowCatalog).DestinationId);
            Assert.Equal(
                DestinationIds.Itm("lapInfo"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("before"), +1, lowCatalog).DestinationId);

            // Hosted / unknown current: walk[0] (NEXT) / walk[last] (PREV).
            Assert.Equal(
                DestinationIds.Itm("lapInfo"),
                WalkCompiler.Step(
                    walk, DestinationIds.Hosted("p-alerts"), +1, catalog).DestinationId);
            Assert.Equal(
                DestinationIds.Hosted("p-shift"),
                WalkCompiler.Step(
                    walk, DestinationIds.Hosted("p-alerts"), -1, catalog).DestinationId);
            Assert.Equal(
                DestinationIds.Itm("lapInfo"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("nope"), +1, catalog).DestinationId);
            Assert.Equal(
                DestinationIds.Hosted("p-shift"),
                WalkCompiler.Step(
                    walk, DestinationIds.Itm("nope"), -1, catalog).DestinationId);
        }

        [Fact]
        public void Step_FromOutsideWalk_NonCatalogOrderedWalk_NearestIndexBothDirections()
        {
            // E7A-002 / E7A-005: authored order ≠ catalog order. Nearest-index wins;
            // first-encountered-greater is wrong.
            var catalog = MiniCatalog(
                ("a", 1), ("b", 2), ("c", 3), ("d", 4), ("e", 5), ("f", 6));

            // Walk authored [e@5, d@4] — reverse of catalog order.
            var walk = new[]
            {
                DestinationIds.Itm("e"),
                DestinationIds.Itm("d"),
            };

            // Current at index 3 (off-walk): NEXT → min greater = d@4 (not first-seen e@5).
            Assert.Equal(
                DestinationIds.Itm("d"),
                WalkCompiler.Step(walk, DestinationIds.Itm("c"), +1, catalog).DestinationId);
            // PREV from index 3: no lesser → wrap highest ITM = e@5.
            Assert.Equal(
                DestinationIds.Itm("e"),
                WalkCompiler.Step(walk, DestinationIds.Itm("c"), -1, catalog).DestinationId);

            // Current at index 6: NEXT wraps lowest = d@4; PREV max lesser = e@5.
            Assert.Equal(
                DestinationIds.Itm("d"),
                WalkCompiler.Step(walk, DestinationIds.Itm("f"), +1, catalog).DestinationId);
            Assert.Equal(
                DestinationIds.Itm("e"),
                WalkCompiler.Step(walk, DestinationIds.Itm("f"), -1, catalog).DestinationId);

            // Current between members (index 4.5 equivalent — use index 4.5 via page
            // with index between? d is 4 in walk. Current index 4.5 not representable;
            // current at b@2: NEXT min greater among {5,4} = d@4; PREV wrap highest e@5.
            Assert.Equal(
                DestinationIds.Itm("d"),
                WalkCompiler.Step(walk, DestinationIds.Itm("b"), +1, catalog).DestinationId);
            Assert.Equal(
                DestinationIds.Itm("e"),
                WalkCompiler.Step(walk, DestinationIds.Itm("b"), -1, catalog).DestinationId);
        }

        [Fact]
        public void Step_EmptyWalk_NowhereWithReason()
        {
            var empty = Array.Empty<string>();
            var r = WalkCompiler.Step(empty, DestinationIds.Hosted("p-a"), +1);
            Assert.False(r.Stepped);
            Assert.Null(r.DestinationId);
            Assert.Equal(WalkCompiler.EmptyWalkReason, r.EmptyReason);

            r = WalkCompiler.Step(null, DestinationIds.Hosted("p-a"), -1);
            Assert.False(r.Stepped);
            Assert.Equal(WalkCompiler.EmptyWalkReason, r.EmptyReason);
        }

        // ── Example documents ────────────────────────────────────────────

        [Fact]
        public void Example_SamWalk_ThreePages_AlertsOffWalk()
        {
            // sam-pswbmw.v2.json: pageOrder = p-speed, p-fuel, p-temp; p-alerts off-walk.
            var doc = LoadExample("sam-pswbmw.v2.json");
            var walk = WalkCompiler.Compile(doc, catalog: null);

            Assert.Equal(WalkCompileSource.Explicit, walk.Source);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-speed"),
                    DestinationIds.Hosted("p-fuel"),
                    DestinationIds.Hosted("p-temp"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(DestinationIds.Hosted("p-alerts"), walk.DestinationIds);
        }

        [Fact]
        public void Example_AlexWalk_CatalogOrderMinusNothing_PlusTwoHosted()
        {
            // alex-pbme.v2.json: explicit pageOrder = all five PBME catalog pages in
            // catalog-index order + p-shift + p-delta (p-limiter off-walk).
            var catalog = LoadPbmeCatalog();
            var doc = LoadExample("alex-pbme.v2.json", catalog);
            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(WalkCompileSource.Explicit, walk.Source);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("fuelErsDrs"),
                    DestinationIds.Itm("carSettings"),
                    DestinationIds.Itm("lapTimes"),
                    DestinationIds.Itm("tyreTemps"),
                    DestinationIds.Hosted("p-shift"),
                    DestinationIds.Hosted("p-delta"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(DestinationIds.Hosted("p-limiter"), walk.DestinationIds);

            // Authoring note: Alex's list matches the default ITM order + two hosted;
            // a true ABSENT default would also include p-limiter (all hosted in pages[]).
            var absent = LoadExample("alex-pbme.v2.json", catalog);
            absent.PageOrder = null;
            absent = Normalize(absent, catalog);
            var defaultWalk = WalkCompiler.Compile(absent, catalog);
            Assert.Equal(WalkCompileSource.Default, defaultWalk.Source);
            Assert.Contains(DestinationIds.Hosted("p-limiter"), defaultWalk.DestinationIds);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("fuelErsDrs"),
                    DestinationIds.Itm("carSettings"),
                    DestinationIds.Itm("lapTimes"),
                    DestinationIds.Itm("tyreTemps"),
                    DestinationIds.Hosted("p-limiter"),
                    DestinationIds.Hosted("p-shift"),
                    DestinationIds.Hosted("p-delta"),
                },
                defaultWalk.DestinationIds.ToArray());
        }

        // ── Recompile / determinism ──────────────────────────────────────

        [Fact]
        public void Recompile_Determinism_SameInputs_SameWalk_ListInstanceIrrelevant()
        {
            var catalog = LoadPbmeCatalog();
            var doc = LoadExample("alex-pbme.v2.json", catalog);

            var a = WalkCompiler.Compile(doc, catalog);
            var b = WalkCompiler.Compile(doc, catalog);

            // Different list instances…
            Assert.False(ReferenceEquals(a.DestinationIds, b.DestinationIds));
            // …same content (recompile contract: content equality, not instance equality).
            Assert.Equal(a.DestinationIds.ToArray(), b.DestinationIds.ToArray());
            Assert.Equal(a.Source, b.Source);

            // Same logical catalog content via a re-parsed instance still matches.
            var catalog2 = LoadPbmeCatalog();
            var c = WalkCompiler.Compile(doc, catalog2);
            Assert.Equal(a.DestinationIds.ToArray(), c.DestinationIds.ToArray());
        }

        // ── Removal collection: first-wins / ignore degraded losers (E7A-004) ─

        [Fact]
        public void Law_RemovedDuplicateLoser_DoesNotExcludeKeptItm_ExplicitAndDefault()
        {
            // First ITM overlay keeps page; later duplicate with removed:true is a
            // degraded identity loser and must not remove the page from either walk.
            var catalog = MiniCatalog(("lapInfo", 1), ("fuelErsDrs", 2));

            // Explicit walk — raw (no Normalize): first-wins inside CollectRemovedItmIds.
            var rawExplicit = BaseDoc(
                ItmPage("lapInfo", removed: false),
                ItmPage("lapInfo", removed: true), // duplicate loser
                ItmPage("fuelErsDrs"),
                HostedPage("p-a"));
            rawExplicit.PageOrder = new List<PageRef>
            {
                ItmRef("lapInfo"),
                ItmRef("fuelErsDrs"),
            };
            var explicitWalk = WalkCompiler.Compile(rawExplicit, catalog);
            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("fuelErsDrs"),
                },
                explicitWalk.DestinationIds.ToArray());

            // Default walk — same first-wins removal collection.
            var rawDefault = BaseDoc(
                ItmPage("lapInfo", removed: false),
                ItmPage("lapInfo", removed: true),
                ItmPage("fuelErsDrs"),
                HostedPage("p-a"));
            rawDefault.PageOrder = null;
            var defaultWalk = WalkCompiler.Compile(rawDefault, catalog);
            Assert.Equal(WalkCompileSource.Default, defaultWalk.Source);
            Assert.Contains(DestinationIds.Itm("lapInfo"), defaultWalk.DestinationIds);
            Assert.Contains(DestinationIds.Itm("fuelErsDrs"), defaultWalk.DestinationIds);

            // Normalized path: validator marks the loser DegradedAtLoad; still kept.
            var normalized = BaseDoc(
                ItmPage("lapInfo", removed: false),
                ItmPage("lapInfo", removed: true),
                ItmPage("fuelErsDrs"));
            normalized.PageOrder = new List<PageRef>
            {
                ItmRef("lapInfo"),
                ItmRef("fuelErsDrs"),
            };
            normalized = Normalize(normalized, catalog);
            Assert.True(normalized.Pages.Count(p =>
                p.Kind == PageEntryKind.ItmPage
                && string.Equals(p.CatalogPageId, "lapInfo", StringComparison.OrdinalIgnoreCase)
                && p.DegradedAtLoad) >= 1);
            var normWalk = WalkCompiler.Compile(normalized, catalog);
            Assert.Contains(DestinationIds.Itm("lapInfo"), normWalk.DestinationIds);
        }

        // ── Direct compiler guards WITHOUT prior normalization (E7A-005) ─

        [Fact]
        public void Law_DirectCompiler_Duplicate_FirstKept_NoNormalize()
        {
            // No Normalize — BuildExplicitWalk's own duplicate (seen) guard.
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), HostedPage("p-b"), ItmPage("lapInfo"));
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-a"),
                ItmRef("lapInfo"),
                HostedRef("p-a"),
                HostedRef("p-b"),
                ItmRef("lapInfo"),
            };

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-a"),
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Hosted("p-b"),
                },
                walk.DestinationIds.ToArray());
        }

        [Fact]
        public void Law_DirectCompiler_UnresolvedRefs_Skipped_NoNormalize()
        {
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), ItmPage("lapInfo"));
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-a"),
                HostedRef("p-missing"),
                ItmRef("noSuchPage"),
                ItmRef("lapInfo"),
            };

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-a"),
                    DestinationIds.Itm("lapInfo"),
                },
                walk.DestinationIds.ToArray());
        }

        [Fact]
        public void Law_DirectCompiler_CycleRef_Skipped_NoNormalize()
        {
            var catalog = MiniCatalog(("lapInfo", 1));
            var doc = BaseDoc(HostedPage("p-a"), ItmPage("lapInfo"));
            doc.PageOrder = new List<PageRef>
            {
                HostedRef("p-a"),
                new PageRef { Kind = PageRefKind.Cycle, Id = "c-pit" },
                ItmRef("lapInfo"),
            };

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Hosted("p-a"),
                    DestinationIds.Itm("lapInfo"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(walk.DestinationIds, d => DestinationIds.IsCycle(d));
        }

        [Fact]
        public void Law_DirectCompiler_ExplicitRemoved_Skipped_NoNormalize()
        {
            var catalog = MiniCatalog(("lapInfo", 1), ("fuelErsDrs", 2), ("tyreTemps", 5));
            var doc = BaseDoc(
                ItmPage("lapInfo"),
                ItmPage("fuelErsDrs", removed: true),
                ItmPage("tyreTemps"));
            doc.PageOrder = new List<PageRef>
            {
                ItmRef("lapInfo"),
                ItmRef("fuelErsDrs"),
                ItmRef("tyreTemps"),
            };

            var walk = WalkCompiler.Compile(doc, catalog);

            Assert.Equal(
                new[]
                {
                    DestinationIds.Itm("lapInfo"),
                    DestinationIds.Itm("tyreTemps"),
                },
                walk.DestinationIds.ToArray());
            Assert.DoesNotContain(DestinationIds.Itm("fuelErsDrs"), walk.DestinationIds);
        }

        // ── E4 seam: SeatArbiter integration (authoritative Step) ────────

        [Fact]
        public void Integration_TwoTick_SeatArbiter_StepWalk_RemembersResolvedTarget()
        {
            // Contract §6.2: director/press feeds the *next* tick's E4 input.
            // E7a: SeatArbiter.StepWalk delegates to WalkCompiler.Step.
            var doc = LoadExample("sam-pswbmw.v2.json");
            var compiled = WalkCompiler.Compile(doc, catalog: null);
            Assert.Equal(3, compiled.Count);

            var arb = new SeatArbiter(doc);
            var walk = compiled.DestinationIds;

            // Tick 0: rest floor (inSession = p-speed). No press yet.
            var t0 = new SeatArbiterTickInput
            {
                NowMs = 0,
                InGame = true,
                CompiledWalk = walk,
            };
            var r0 = arb.Tick(t0);
            Assert.Null(r0.Manual.RememberedDestinationId);
            Assert.False(r0.Manual.HasRememberedTarget);

            // Tick 1 (press feeds this tick): StepWalk +1 from never-navigated.
            // EffectiveManualDestination = landing ?? inSession = p-speed → next = p-fuel.
            var t1 = new SeatArbiterTickInput
            {
                NowMs = 100,
                InGame = true,
                Manual = SeatManualInput.StepWalk(+1),
                CompiledWalk = walk,
            };
            var r1 = arb.Tick(t1);
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r1.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r1.Manual.RememberedDestinationId);
            Assert.True(r1.Manual.HasRememberedTarget);
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r1.Intent.DestinationId);
            Assert.Equal(SeatArbiter.ManualCarrierId, r1.Intent.WinnerCarrierId);

            // Tick 2: second StepWalk +1 → p-temp; remembered updates.
            var t2 = new SeatArbiterTickInput
            {
                NowMs = 200,
                InGame = true,
                Manual = SeatManualInput.StepWalk(+1),
                CompiledWalk = walk,
            };
            var r2 = arb.Tick(t2);
            Assert.Equal(DestinationIds.Hosted("p-temp"), r2.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-temp"), r2.Manual.RememberedDestinationId);
            Assert.True(r2.Manual.HasRememberedTarget);
            Assert.Equal(DestinationIds.Hosted("p-temp"), r2.Intent.DestinationId);

            // Pure compiler agrees with what the arbiter resolved (in-walk wrap path).
            Assert.Equal(
                DestinationIds.Hosted("p-fuel"),
                WalkCompiler.Step(walk, DestinationIds.Hosted("p-speed"), +1).DestinationId);
            Assert.Equal(
                DestinationIds.Hosted("p-temp"),
                WalkCompiler.Step(walk, DestinationIds.Hosted("p-fuel"), +1).DestinationId);
        }

        [Fact]
        public void Integration_SeatArbiter_EmptyWalk_StepsNowhere_RemembersNothing()
        {
            // E7A-003: WalkCompiler empty-walk law wins through the arbiter seam
            // (historical StepWalk returned current and could remember it).
            var doc = LoadExample("sam-pswbmw.v2.json");
            var arb = new SeatArbiter(doc);
            var empty = Array.Empty<string>();

            var t = new SeatArbiterTickInput
            {
                NowMs = 100,
                InGame = true,
                Manual = SeatManualInput.StepWalk(+1),
                CompiledWalk = empty,
            };
            var r = arb.Tick(t);
            Assert.Null(r.WalkStepResolvedDestinationId);
            Assert.Null(r.Manual.RememberedDestinationId);
            Assert.False(r.Manual.HasRememberedTarget);

            // Null walk same as empty.
            arb = new SeatArbiter(doc);
            t = new SeatArbiterTickInput
            {
                NowMs = 100,
                InGame = true,
                Manual = SeatManualInput.StepWalk(-1),
                CompiledWalk = null,
            };
            r = arb.Tick(t);
            Assert.Null(r.WalkStepResolvedDestinationId);
            Assert.Null(r.Manual.RememberedDestinationId);
            Assert.False(r.Manual.HasRememberedTarget);
        }

        [Fact]
        public void Integration_SeatArbiter_OffWalkReentry_BothDirections()
        {
            // E7A-003: off-walk re-entry through SeatArbiter (null catalog path).
            // Landing IS the step: NEXT → walk[0], PREV → walk[last] (no double step).
            // Historical: anchor walk[0] then apply direction (NEXT → walk[1]).
            var doc = LoadExample("sam-pswbmw.v2.json");
            var walk = new[]
            {
                DestinationIds.Hosted("p-speed"),
                DestinationIds.Hosted("p-fuel"),
                DestinationIds.Hosted("p-temp"),
            };

            // Park on off-walk p-alerts, then NEXT → walk[0] = p-speed.
            var arb = new SeatArbiter(doc);
            var nav = new SeatArbiterTickInput
            {
                NowMs = 0,
                InGame = true,
                Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-alerts")),
                CompiledWalk = walk,
            };
            arb.Tick(nav);

            var next = new SeatArbiterTickInput
            {
                NowMs = 100,
                InGame = true,
                Manual = SeatManualInput.StepWalk(+1),
                CompiledWalk = walk,
            };
            var rNext = arb.Tick(next);
            Assert.Equal(DestinationIds.Hosted("p-speed"), rNext.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-speed"), rNext.Manual.RememberedDestinationId);

            // Fresh arbiter: off-walk PREV → walk[last] = p-temp.
            arb = new SeatArbiter(doc);
            nav = new SeatArbiterTickInput
            {
                NowMs = 0,
                InGame = true,
                Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-alerts")),
                CompiledWalk = walk,
            };
            arb.Tick(nav);

            var prev = new SeatArbiterTickInput
            {
                NowMs = 100,
                InGame = true,
                Manual = SeatManualInput.StepWalk(-1),
                CompiledWalk = walk,
            };
            var rPrev = arb.Tick(prev);
            Assert.Equal(DestinationIds.Hosted("p-temp"), rPrev.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-temp"), rPrev.Manual.RememberedDestinationId);

            // Compiler agreement (null catalog).
            Assert.Equal(
                DestinationIds.Hosted("p-speed"),
                WalkCompiler.Step(
                    walk, DestinationIds.Hosted("p-alerts"), +1).DestinationId);
            Assert.Equal(
                DestinationIds.Hosted("p-temp"),
                WalkCompiler.Step(
                    walk, DestinationIds.Hosted("p-alerts"), -1).DestinationId);
        }
    }
}
