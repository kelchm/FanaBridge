using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Schema2;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// FA3 / FREEZE AMENDMENT 3: strip-order seed pins (not document order; skip degraded;
    /// zero-hosted → null).
    /// </summary>
    public class LegacySeedResolverTests
    {
        [Fact]
        public void StripOrder_NotDocumentOrder_WalkOrderWins()
        {
            // Document order: p-doc-first, p-doc-second.
            // pageOrder (strip): p-doc-second, p-doc-first → seed must be p-doc-second.
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    Hosted("p-doc-first"),
                    Hosted("p-doc-second"),
                },
                PageOrder = new List<PageRef>
                {
                    new PageRef { Kind = PageRefKind.HostedPage, Id = "p-doc-second" },
                    new PageRef { Kind = PageRefKind.HostedPage, Id = "p-doc-first" },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        // inSessionPage must NOT influence the seed (FA3).
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage,
                            Id = "p-doc-first",
                        },
                    },
                },
            };

            Assert.Equal(
                DestinationIds.Hosted("p-doc-second"),
                LegacySeedResolver.ResolveSeedDestination(cfg));
            Assert.Equal("p-doc-second", LegacySeedResolver.ResolveSeedHostedPageId(cfg));
        }

        [Fact]
        public void DegradedFirstHosted_IsSkipped()
        {
            var first = Hosted("p-dead");
            first.DegradedAtLoad = true;
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    first,
                    Hosted("p-live"),
                },
            };

            Assert.Equal(
                DestinationIds.Hosted("p-live"),
                LegacySeedResolver.ResolveSeedDestination(cfg));
        }

        [Fact]
        public void DegradedPageOrderEntry_IsSkipped()
        {
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    Hosted("p-a"),
                    Hosted("p-b"),
                },
                PageOrder = new List<PageRef>
                {
                    new PageRef
                    {
                        Kind = PageRefKind.HostedPage,
                        Id = "p-a",
                        // Simulates validator mark on a pageOrder entry.
                    },
                    new PageRef { Kind = PageRefKind.HostedPage, Id = "p-b" },
                },
            };
            cfg.PageOrder[0].DegradedAtLoad = true;

            Assert.Equal(
                DestinationIds.Hosted("p-b"),
                LegacySeedResolver.ResolveSeedDestination(cfg));
        }

        [Fact]
        public void ZeroHosted_ReturnsNull()
        {
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.ItmPage,
                        CatalogPageId = "lapInfo",
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
                    },
                },
            };

            Assert.Null(LegacySeedResolver.ResolveSeedDestination(cfg));
            Assert.Null(LegacySeedResolver.ResolveSeedHostedPageId(cfg));
        }

        [Fact]
        public void EmptyPageOrder_ReturnsNull_EvenWithHostedPages()
        {
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry> { Hosted("p-a") },
                PageOrder = new List<PageRef>(), // [] = empty walk
            };

            Assert.Null(LegacySeedResolver.ResolveSeedDestination(cfg));
        }

        [Fact]
        public void InSessionPage_PlaysNoRole()
        {
            var cfg = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    Hosted("p-strip"),
                    Hosted("p-other"),
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage,
                            Id = "p-other",
                        },
                    },
                },
            };

            // Absent pageOrder → first hosted in pages[] = p-strip, not inSession p-other.
            Assert.Equal(
                DestinationIds.Hosted("p-strip"),
                LegacySeedResolver.ResolveSeedDestination(cfg));
        }

        private static PageEntry Hosted(string id)
            => new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = id,
                Name = id,
            };
    }
}
