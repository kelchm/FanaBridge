using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Shipped catalogs (Core embedded resources via <see cref="CatalogLoader.LoadShipped"/>):
    /// parse, round-trip unknown members, and stay consistent with the code's ITM
    /// page/param vocabulary (<see cref="ItmDeviceCatalog"/>, <see cref="ItmTelemetry"/>,
    /// <see cref="ItmParam"/>). catalogVersion 2: definitions + placements.
    /// </summary>
    public class CatalogConsistencyTests
    {
        private static WheelCatalog Pbme()
        {
            Assert.True(CatalogLoader.TryResolve("pbme", out var c, _ => { }));
            return c!;
        }

        private static WheelCatalog Bentley()
        {
            Assert.True(CatalogLoader.TryResolve("pswbent", out var c, _ => { }));
            return c!;
        }

        private static HashSet<ushort> KnownItmParams()
        {
            var set = new HashSet<ushort>();
            foreach (var f in typeof(ItmParam).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType == typeof(ushort) && f.IsLiteral)
                {
                    ushort v = (ushort)f.GetRawConstantValue()!;
                    if (v != ItmParam.Unsubscribe)
                        set.Add(v);
                }
            }
            return set;
        }

        private static List<ushort> ParamsFromPlacements(WheelCatalog catalog, CatalogPage page)
        {
            var defs = CatalogFields.IndexByLogicalId(catalog);
            var list = new List<ushort>();
            if (page?.Placements == null)
                return list;
            foreach (var pl in page.Placements)
            {
                if (pl == null || string.IsNullOrEmpty(pl.Field))
                    continue;
                if (defs.TryGetValue(pl.Field, out var def) && def != null)
                    list.Add(def.ParamId);
            }
            return list;
        }

        [Fact]
        public void PbmeCatalog_Parses()
        {
            var catalog = Pbme();
            Assert.Equal(2, catalog.CatalogVersion);
            Assert.Equal("pbme", catalog.WheelId);
            Assert.False(catalog.Provisional);
            Assert.NotNull(catalog.Itm);
            Assert.Equal(6, catalog.Itm!.LegacyPageIndex);
            Assert.Equal(5, catalog.Itm.Pages.Count);
            Assert.NotEmpty(catalog.Itm.Fields);
            // Shipped SpecialCommands spelling logoInverted binds to the POCO property.
            Assert.NotNull(catalog.ScreenCommands);
            Assert.Null(catalog.ScreenCommands!.LogoInverted);
            Assert.True(catalog.ScreenCommands.Provisional);
        }

        [Fact]
        public void BentleyCatalog_Parses()
        {
            var catalog = Bentley();
            Assert.Equal(2, catalog.CatalogVersion);
            Assert.Equal("pswbent", catalog.WheelId);
            Assert.True(catalog.Provisional);
            Assert.NotNull(catalog.Itm);
            Assert.Equal(5, catalog.Itm!.LegacyPageIndex);
            Assert.Equal(4, catalog.Itm.Pages.Count);
            Assert.NotEmpty(catalog.Itm.Fields);
        }

        [Fact]
        public void CatalogV2_RoundTrip_PreservesDefinitionsAndPlacements()
        {
            foreach (var name in new[] { "pbme", "pswbent" })
            {
                Assert.True(CatalogLoader.TryResolve(name, out var catalog, _ => { }));
                string json = CatalogLoader.Save(catalog!);
                var again = CatalogLoader.LoadWheelCatalog(json, _ => { });
                Assert.Equal(2, again.CatalogVersion);
                Assert.Equal(catalog!.Itm!.Fields.Count, again.Itm!.Fields.Count);
                Assert.Equal(catalog.Itm.Pages.Count, again.Itm.Pages.Count);
                for (int i = 0; i < catalog.Itm.Pages.Count; i++)
                {
                    Assert.Equal(
                        catalog.Itm.Pages[i].Placements.Count,
                        again.Itm.Pages[i].Placements.Count);
                    Assert.Equal(
                        catalog.Itm.Pages[i].Placements[0].Field,
                        again.Itm.Pages[i].Placements[0].Field);
                }
            }
        }

        [Fact]
        public void AliasTable_Parses()
        {
            var table = CatalogLoader.LoadShippedAliasTable(_ => { });
            Assert.Equal(1, table.AliasTableVersion);
            Assert.NotEmpty(table.Aliases);
            Assert.NotEmpty(table.PatternRules);
            Assert.NotEmpty(table.PrefixRules);
            Assert.Contains(table.Aliases, a => a.Ref == "PitLimiterOn" && a.Alias == "Pit limiter");
            Assert.Contains(table.Aliases, a => a.Kind == AliasKind.BuiltIn);
            Assert.Contains(table.Aliases, a => a.Kind == AliasKind.Property);
        }

        [Fact]
        public void CatalogLoader_NeverThrows_OnGarbage()
        {
            var warnings = new List<string>();
            var catalog = CatalogLoader.LoadWheelCatalog("{ not json", warnings.Add);
            Assert.NotNull(catalog);
            Assert.NotEmpty(warnings);

            warnings.Clear();
            var table = CatalogLoader.LoadAliasTable("{ not json", warnings.Add);
            Assert.NotNull(table);
            Assert.NotEmpty(warnings);
        }

        [Fact]
        public void CatalogLoader_NeverThrows_OnWrongType_ExplicitNull_ThrowingLogger()
        {
            // Wrong-type root / member — deserialize may throw or yield null; never throws out.
            var catalog = CatalogLoader.LoadWheelCatalog("{\"catalogVersion\":\"nope\"}", _ => { });
            Assert.NotNull(catalog);

            catalog = CatalogLoader.LoadWheelCatalog("null", _ => { });
            Assert.NotNull(catalog);

            catalog = CatalogLoader.LoadWheelCatalog(null, _ => { });
            Assert.NotNull(catalog);

            // Throwing logger must not break the never-throws contract.
            catalog = CatalogLoader.LoadWheelCatalog("{ not json",
                _ => throw new InvalidOperationException("logger boom"));
            Assert.NotNull(catalog);

            var table = CatalogLoader.LoadAliasTable("{ not json",
                _ => throw new InvalidOperationException("logger boom"));
            Assert.NotNull(table);

            table = CatalogLoader.LoadAliasTable("null",
                _ => throw new InvalidOperationException("logger boom"));
            Assert.NotNull(table);
        }

        /// <summary>
        /// catalogVersion gate: only version 2 is accepted. Older / missing / zero →
        /// warn + empty (fail-closed data). No dual-shape reader.
        /// </summary>
        [Theory]
        [InlineData("{\"catalogVersion\":1,\"wheelId\":\"x\"}", "1")]
        [InlineData("{\"wheelId\":\"x\"}", "0")]
        [InlineData("{\"catalogVersion\":0,\"wheelId\":\"x\"}", "0")]
        [InlineData("{\"catalogVersion\":3,\"wheelId\":\"x\"}", "3")]
        public void CatalogLoader_VersionGate_NonV2_WarnAndEmpty(string json, string versionToken)
        {
            var warnings = new List<string>();
            var catalog = CatalogLoader.LoadWheelCatalog(json, warnings.Add);
            Assert.NotNull(catalog);
            Assert.Null(catalog.Itm);
            Assert.Null(catalog.WheelId);
            Assert.Equal(0, catalog.CatalogVersion);
            Assert.Contains(warnings, w => w.Contains("catalogVersion") && w.Contains("not supported"));
            Assert.Contains(warnings, w => w.Contains(versionToken));
        }

        [Fact]
        public void CatalogLoader_VersionGate_V2_Accepted()
        {
            var warnings = new List<string>();
            var catalog = CatalogLoader.LoadWheelCatalog(
                "{\"catalogVersion\":2,\"wheelId\":\"probe\"}", warnings.Add);
            Assert.Equal(2, catalog.CatalogVersion);
            Assert.Equal("probe", catalog.WheelId);
            Assert.Empty(warnings);
        }

        /// <summary>
        /// Content preservation: every PBME definition firmwareLabel is pinned
        /// field-by-field so a single-character drift fails (e.g. lastLapTime "LAST LAP:").
        /// </summary>
        [Fact]
        public void Pbme_FirmwareLabels_PinnedFieldByField()
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speed"] = null,
                ["gear"] = null,
                ["lap"] = "LAPS:",
                ["position"] = "POSITION:",
                ["currentLapTime"] = "CURRENT LAP:",
                ["lastLapTime"] = "LAST LAP:",
                ["fuel"] = "FUEL:",
                ["ersLevel"] = "ERS:",
                ["drsZone"] = "DRS: ZONE",
                ["drsActive"] = "ACTIVE",
                ["deltaOwnBest"] = "Delta:",
                ["tcSetting"] = "TC",
                ["absSetting"] = "ABS",
                ["engineMap"] = "ENGINE MAP:",
                ["oilTemp"] = "OIL TEMP:",
                ["brakeBias"] = "BRAKE BIAS:",
                ["bestLapTime"] = "BEST LAP:",
                ["carAhead"] = "CAR AHEAD:",
                ["carBehind"] = "CAR BEHIND:",
                ["tyreFL"] = "FL TIRE TEMP:",
                ["tyreRL"] = "RL TIRE TEMP:",
                ["tyreFR"] = "FR TIRE TEMP:",
                ["tyreRR"] = "RR TIRE TEMP:",
            };

            var catalog = Pbme();
            Assert.Equal(expected.Count, catalog.Itm!.Fields.Count);
            foreach (var def in catalog.Itm.Fields)
            {
                Assert.True(expected.ContainsKey(def.Id),
                    "unexpected field id '" + def.Id + "' (not in pinned set)");
                Assert.Equal(expected[def.Id], def.FirmwareLabel);
            }
            foreach (var id in expected.Keys)
                Assert.Contains(catalog.Itm.Fields, d => d.Id == id);
        }

        [Fact]
        public void Pbme_PageIds_MatchStandardItmDeviceCatalog_ExcludingLegacy()
        {
            var catalog = Pbme();
            // Standard set (any non-Bentley device id) — PBME is device 3.
            var codePages = ItmDeviceCatalog.PagesFor(deviceId: 3)
                .Where(p => p.Page != ItmPage.Legacy)
                .Select(p => EnumText.Write(p.Page))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            var catalogIds = catalog.Itm!.Pages
                .Select(p => p.Id)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(codePages, catalogIds);
        }

        [Fact]
        public void Pbme_ParamsPerPage_MatchItmTelemetryParamsFor()
        {
            var catalog = Pbme();
            foreach (var page in catalog.Itm!.Pages)
            {
                var itmPage = EnumText.ParseNullable<ItmPage>(page.Id);
                Assert.True(itmPage.HasValue, "catalog page id is not an ItmPage spelling: " + page.Id);

                var expected = ItmTelemetry.ParamsFor(itmPage.Value).ToList();
                var actual = ParamsFromPlacements(catalog, page);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void Bentley_PageIds_MatchItmDeviceCatalog_ExcludingLegacy()
        {
            var catalog = Bentley();
            var codePages = ItmDeviceCatalog.PagesFor(deviceId: 4)
                .Where(p => p.Page != ItmPage.Legacy)
                .Select(p => EnumText.Write(p.Page))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            var catalogIds = catalog.Itm!.Pages
                .Select(p => p.Id)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(codePages, catalogIds);
        }

        [Fact]
        public void Bentley_ParamsPerPage_MatchItmTelemetryParamsFor()
        {
            var catalog = Bentley();
            foreach (var page in catalog.Itm!.Pages)
            {
                var itmPage = EnumText.ParseNullable<ItmPage>(page.Id);
                Assert.True(itmPage.HasValue, "catalog page id is not an ItmPage spelling: " + page.Id);

                var expected = ItmTelemetry.ParamsFor(itmPage.Value).ToList();
                var actual = ParamsFromPlacements(catalog, page);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void EveryParamId_InBothCatalogs_ExistsInItmParam()
        {
            var known = KnownItmParams();
            foreach (var catalog in new[] { Pbme(), Bentley() })
            {
                foreach (var def in catalog.Itm!.Fields)
                {
                    Assert.True(known.Contains(def.ParamId),
                        catalog.WheelId + " field " + def.Id
                        + " has paramId " + def.ParamId + " not in ItmParam");
                }
            }
        }

        [Fact]
        public void EveryParamId_HasExactlyOnePrimaryHost_InBothCatalogs()
        {
            foreach (var catalog in new[] { Pbme(), Bentley() })
            {
                var defs = CatalogFields.IndexByLogicalId(catalog);
                var byParam = catalog.Itm!.Pages
                    .SelectMany(p => (p.Placements ?? new List<CatalogFieldPlacement>())
                        .Where(pl => pl != null && !string.IsNullOrEmpty(pl.Field)
                            && defs.ContainsKey(pl.Field))
                        .Select(pl => new
                        {
                            PageId = p.Id,
                            ParamId = defs[pl.Field].ParamId,
                            Field = pl.Field,
                            Primary = pl.PrimaryHost == true,
                        }))
                    .GroupBy(x => x.ParamId);

                foreach (var group in byParam)
                {
                    int hosts = group.Count(x => x.Primary);
                    Assert.True(hosts == 1,
                        catalog.WheelId + " paramId " + group.Key + " has " + hosts
                        + " primaryHost designation(s); expected exactly 1. Hosts: "
                        + string.Join(", ",
                            group.Where(x => x.Primary)
                                .Select(x => x.PageId + ":" + x.Field)));
                }
            }
        }

        /// <summary>
        /// S3: the same fieldId token binds the same param in every catalog that defines it.
        /// </summary>
        [Fact]
        public void CrossCatalog_SameLogicalId_BindsSameParam()
        {
            var pbme = CatalogFields.IndexByLogicalId(Pbme());
            var bent = CatalogFields.IndexByLogicalId(Bentley());
            foreach (var kv in pbme)
            {
                if (!bent.TryGetValue(kv.Key, out var bDef) || bDef == null)
                    continue;
                Assert.True(kv.Value.ParamId == bDef.ParamId,
                    "logical id '" + kv.Key + "' binds param " + kv.Value.ParamId
                    + " on pbme but " + bDef.ParamId + " on pswbent");
            }
        }

        [Fact]
        public void Placements_ResolveToDefinitions_InBothCatalogs()
        {
            foreach (var catalog in new[] { Pbme(), Bentley() })
            {
                var defs = CatalogFields.IndexByLogicalId(catalog);
                foreach (var page in catalog.Itm!.Pages)
                {
                    foreach (var pl in page.Placements)
                    {
                        Assert.True(defs.ContainsKey(pl.Field),
                            catalog.WheelId + " page " + page.Id
                            + " placement '" + pl.Field + "' has no definition");
                    }
                }
            }
        }

        [Fact]
        public void LoadShipped_IndexesByLowercasedWheelCode()
        {
            var set = CatalogLoader.LoadShipped(_ => { });
            Assert.True(set.ContainsKey("pbme"));
            Assert.True(set.ContainsKey("pswbent"));
            Assert.Equal(2, set.Count);
            Assert.Equal((byte?)3, CatalogLoader.ReadDeclaredDeviceId(set["pbme"]));
            Assert.Equal((byte?)4, CatalogLoader.ReadDeclaredDeviceId(set["pswbent"]));
        }

        [Fact]
        public void TryResolve_KnownCode_ReturnsCatalog_CaseInsensitive()
        {
            Assert.True(CatalogLoader.TryResolve("PBME", out var pbme, _ => { }, itmDeviceId: 3));
            Assert.Equal("pbme", pbme!.WheelId);
            Assert.True(CatalogLoader.TryResolve("PswBent", out var bent, _ => { }, itmDeviceId: 4));
            Assert.Equal("pswbent", bent!.WheelId);
        }

        [Fact]
        public void TryResolve_UnknownCode_MissesWithWarning()
        {
            var warnings = new List<string>();
            Assert.False(CatalogLoader.TryResolve("no-such-wheel", out var catalog, warnings.Add));
            Assert.Null(catalog);
            Assert.Contains(warnings, w => w.Contains("no shipped catalog") && w.Contains("no-such-wheel"));
        }

        [Fact]
        public void TryResolve_DeviceIdMismatch_LogsAndStillReturns()
        {
            var warnings = new List<string>();
            Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, warnings.Add, itmDeviceId: 99));
            Assert.NotNull(catalog);
            Assert.Contains(warnings, w => w.Contains("deviceId mismatch") && w.Contains("pbme"));
        }

        [Fact]
        public void FieldCapability_FromShippedCatalogs_NonEmpty_ForPbmeAndBentley()
        {
            var pbmeCaps = FieldCapability.FromCatalog(Pbme());
            Assert.NotEmpty(pbmeCaps);
            // Multi-page reach for speed/gear derived from placements.
            Assert.Equal(5, pbmeCaps[1].HostCatalogPageIds.Count);
            Assert.Equal(5, pbmeCaps[4].HostCatalogPageIds.Count);
            var bentCaps = FieldCapability.FromCatalog(Bentley());
            Assert.NotEmpty(bentCaps);
            Assert.Equal(4, bentCaps[1].HostCatalogPageIds.Count);
        }

        /// <summary>
        /// Reflection guard: every public non-abstract class in the Catalog namespace
        /// declares a [JsonExtensionData] member. Discovered by namespace scan so a
        /// new type cannot silently opt out.
        /// </summary>
        [Fact]
        public void CatalogClosure_EveryClassDeclaresJsonExtensionData()
        {
            var closure = typeof(WheelCatalog).Assembly.GetTypes()
                .Where(t => t.IsClass
                    && t.IsPublic
                    && !t.IsAbstract
                    && t.Namespace == "FanaBridge.Display.Catalog")
                .OrderBy(t => t.Name)
                .ToList();

            Assert.NotEmpty(closure);

            var missing = new List<string>();
            foreach (var type in closure)
            {
                // Static helper classes (CatalogFields) are not JSON types.
                if (type.IsAbstract && type.IsSealed)
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) == null
                    && type.GetConstructors().Length == 0)
                    continue;
                // CatalogFields is a static class (abstract+sealed).
                if (type.Name == "CatalogFields")
                    continue;

                var has = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Any(p => p.GetCustomAttribute<Newtonsoft.Json.JsonExtensionDataAttribute>() != null);
                if (!has)
                    missing.Add(type.Name);
            }

            Assert.True(missing.Count == 0,
                "Catalog namespace type(s) missing [JsonExtensionData]: "
                + string.Join(", ", missing)
                + " (scanned: " + string.Join(", ", closure.Select(t => t.Name)) + ")");
        }
    }
}
