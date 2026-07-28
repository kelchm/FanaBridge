using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Catalog draft fixtures (embedded copies of the scratch drafts): parse, round-trip
    /// unknown members, and stay consistent with the code's ITM page/param vocabulary
    /// (<see cref="ItmDeviceCatalog"/>, <see cref="ItmTelemetry"/>, <see cref="ItmParam"/>).
    /// </summary>
    public class CatalogConsistencyTests
    {
        private static string LoadFixture(string fileName)
        {
            var asm = typeof(CatalogConsistencyTests).Assembly;
            string suffix = ".Display.Fixtures." + fileName;
            string resource = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using (var stream = asm.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream!))
                return reader.ReadToEnd();
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
            var catalog = CatalogLoader.LoadWheelCatalog(LoadFixture("pbme-catalog-draft.json"), _ => { });
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
            var catalog = CatalogLoader.LoadWheelCatalog(
                LoadFixture("bentley-catalog-draft.json"), _ => { });
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
            var table = CatalogLoader.LoadAliasTable(LoadFixture("alias-table-draft.json"), _ => { });
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
            var catalog = CatalogLoader.LoadWheelCatalog(LoadFixture("pbme-catalog-draft.json"), _ => { });
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
            var catalog = CatalogLoader.LoadWheelCatalog(LoadFixture("pbme-catalog-draft.json"), _ => { });
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
            var catalog = CatalogLoader.LoadWheelCatalog(
                LoadFixture("bentley-catalog-draft.json"), _ => { });
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
            var catalog = CatalogLoader.LoadWheelCatalog(
                LoadFixture("bentley-catalog-draft.json"), _ => { });
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
            foreach (var file in new[] { "pbme-catalog-draft.json", "bentley-catalog-draft.json" })
            {
                var catalog = CatalogLoader.LoadWheelCatalog(LoadFixture(file), _ => { });
                foreach (var page in catalog.Itm!.Pages)
                {
                    foreach (var field in page.Fields)
                    {
                        Assert.True(known.Contains(field.ParamId),
                            file + " page " + page.Id + " field " + field.FieldId
                            + " has paramId " + field.ParamId + " not in ItmParam");
                    }
                }
            }
        }

        [Fact]
        public void EveryParamId_HasExactlyOnePrimaryHost_InBothCatalogs()
        {
            foreach (var file in new[] { "pbme-catalog-draft.json", "bentley-catalog-draft.json" })
            {
                var catalog = CatalogLoader.LoadWheelCatalog(LoadFixture(file), _ => { });
                var byParam = catalog.Itm!.Pages
                    .SelectMany(p => p.Fields.Select(f => new { PageId = p.Id, Field = f }))
                    .GroupBy(x => x.Field.ParamId);

                foreach (var group in byParam)
                {
                    int hosts = group.Count(x => x.Field.PrimaryHost == true);
                    Assert.True(hosts == 1,
                        file + " paramId " + group.Key + " has " + hosts
                        + " primaryHost designation(s); expected exactly 1. Hosts: "
                        + string.Join(", ",
                            group.Where(x => x.Field.PrimaryHost == true)
                                .Select(x => x.PageId + ":" + x.Field.FieldId)));
                }
            }
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
