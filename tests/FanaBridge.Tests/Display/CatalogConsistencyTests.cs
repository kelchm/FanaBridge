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
    /// <see cref="ItmParam"/>).
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

        [Fact]
        public void PbmeCatalog_Parses()
        {
            var catalog = Pbme();
            Assert.Equal(1, catalog.CatalogVersion);
            Assert.Equal("pbme", catalog.WheelId);
            Assert.False(catalog.Provisional);
            Assert.NotNull(catalog.Itm);
            Assert.Equal(6, catalog.Itm!.LegacyPageIndex);
            Assert.Equal(5, catalog.Itm.Pages.Count);
            // Shipped SpecialCommands spelling logoInverted binds to the POCO property.
            Assert.NotNull(catalog.ScreenCommands);
            Assert.Null(catalog.ScreenCommands!.LogoInverted);
            Assert.True(catalog.ScreenCommands.Provisional);
        }

        [Fact]
        public void BentleyCatalog_Parses()
        {
            var catalog = Bentley();
            Assert.Equal(1, catalog.CatalogVersion);
            Assert.Equal("pswbent", catalog.WheelId);
            Assert.True(catalog.Provisional);
            Assert.NotNull(catalog.Itm);
            Assert.Equal(5, catalog.Itm!.LegacyPageIndex);
            Assert.Equal(4, catalog.Itm.Pages.Count);
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
                var actual = page.Fields.Select(f => f.ParamId).ToList();
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
                var actual = page.Fields.Select(f => f.ParamId).ToList();
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void EveryParamId_InBothCatalogs_ExistsInItmParam()
        {
            var known = KnownItmParams();
            foreach (var catalog in new[] { Pbme(), Bentley() })
            {
                foreach (var page in catalog.Itm!.Pages)
                {
                    foreach (var field in page.Fields)
                    {
                        Assert.True(known.Contains(field.ParamId),
                            catalog.WheelId + " page " + page.Id + " field " + field.FieldId
                            + " has paramId " + field.ParamId + " not in ItmParam");
                    }
                }
            }
        }

        [Fact]
        public void EveryParamId_HasExactlyOnePrimaryHost_InBothCatalogs()
        {
            foreach (var catalog in new[] { Pbme(), Bentley() })
            {
                var byParam = catalog.Itm!.Pages
                    .SelectMany(p => p.Fields.Select(f => new { PageId = p.Id, Field = f }))
                    .GroupBy(x => x.Field.ParamId);

                foreach (var group in byParam)
                {
                    int hosts = group.Count(x => x.Field.PrimaryHost == true);
                    Assert.True(hosts == 1,
                        catalog.WheelId + " paramId " + group.Key + " has " + hosts
                        + " primaryHost designation(s); expected exactly 1. Hosts: "
                        + string.Join(", ",
                            group.Where(x => x.Field.PrimaryHost == true)
                                .Select(x => x.PageId + ":" + x.Field.FieldId)));
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
            var bentCaps = FieldCapability.FromCatalog(Bentley());
            Assert.NotEmpty(bentCaps);
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
