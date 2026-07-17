using System.Collections.Generic;
using System.Linq;
using FanaBridge.Adapters;
using FanaBridge.Tests.CmFakes;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The mapped-control role resolution behind <see cref="IMappedRoleCatalog.GetMappedRoles"/>:
    /// the pure <see cref="MappedRoleResolver"/> decision layer (match key, RIW-off
    /// fallback, vendor guard, catalog fallback, distinctness) and the defensive
    /// <see cref="ControlMapperRoleReader"/> reflecting the same Control Mapper shape the
    /// bridge does, driven through the shared <see cref="CmFakes"/> doubles.
    /// </summary>
    public class MappedRolesTests
    {
        private const int Fanatec = FanaBridgeVariantProvider.FanatecVendorId;
        private const int Other = 0x1234;

        private static ControllerMappingView Mapping(int vendorId, string? variant,
            string? interfacePath, params (string role, bool assigned)[] buttons)
            => new ControllerMappingView(vendorId, variant, interfacePath,
                buttons.Select(b => new MappedButtonView(b.role, b.assigned)).ToList());

        // ── Pure resolver ─────────────────────────────────────────────────

        [Fact]
        public void Resolve_ThisRimsMappedRoles_DistinctInFirstSeenOrder_SkippingUnassigned()
        {
            var mappings = new[]
            {
                Mapping(Fanatec, "FS_WHEEL_SWTYPE_PHUB_PBMR", @"\\?\hid#pbmr",
                    ("Up Shift", true), ("Headlights", true),
                    ("", true),               // assigned-but-blank: skipped
                    ("Wipers", false),        // not assigned: skipped
                    ("Up Shift", true)),      // duplicate: collapsed
            };

            var result = MappedRoleResolver.Resolve(mappings,
                "FS_WHEEL_SWTYPE_PHUB_PBMR", interfacePath: null,
                catalog: () => new[] { "Everything" });

            Assert.Equal(MappedRolesSource.MappedOnThisWheel, result.Source);
            Assert.Equal(new[] { "Up Shift", "Headlights" }, result.Roles.ToArray());
        }

        [Fact]
        public void Resolve_VariantMatch_IgnoresOtherRimsAndNonFanatec()
        {
            var mappings = new[]
            {
                Mapping(Fanatec, "FS_WHEEL_SWTYPE_PSWBMW", @"\\?\hid#bmw", ("Pit Limiter", true)),
                Mapping(Other,   "FS_WHEEL_SWTYPE_PHUB_PBMR", @"\\?\hid#x", ("Foreign", true)),
                Mapping(Fanatec, "FS_WHEEL_SWTYPE_PHUB_PBMR", @"\\?\hid#pbmr", ("Up Shift", true)),
            };

            var result = MappedRoleResolver.Resolve(mappings,
                "FS_WHEEL_SWTYPE_PHUB_PBMR", interfacePath: null, catalog: () => null!);

            Assert.Equal(MappedRolesSource.MappedOnThisWheel, result.Source);
            Assert.Equal(new[] { "Up Shift" }, result.Roles.ToArray());
        }

        [Fact]
        public void Resolve_RiwOff_MatchesTheSingleNoVariantBaseRow_NotAVariantRow()
        {
            var mappings = new[]
            {
                Mapping(Fanatec, "FS_WHEEL_SWTYPE_PHUB_PBMR", @"\\?\hid#base", ("Stale", true)),
                Mapping(Fanatec, null, @"\\?\hid#base", ("Up Shift", true), ("Brake Bias +", true)),
            };

            // RIW off is modeled by a null variant (the reader nulls it).
            var result = MappedRoleResolver.Resolve(mappings, variant: null,
                interfacePath: null, catalog: () => new[] { "Everything" });

            Assert.Equal(MappedRolesSource.MappedOnThisWheel, result.Source);
            Assert.Equal(new[] { "Up Shift", "Brake Bias +" }, result.Roles.ToArray());
        }

        [Fact]
        public void Resolve_RiwOff_MultipleFanatecBases_UnionsButReportsAggregated()
        {
            // Two Fanatec bases, RIW off (no variant), no interface path to tell them apart.
            // The roles are real and get unioned — but they span MORE THAN ONE base, so the
            // result must NOT claim to be the roles mapped on THIS wheel; it's an honest
            // aggregate (full interface-path disambiguation lands in R2). Pre-fix this union
            // was mislabeled MappedOnThisWheel.
            var mappings = new[]
            {
                Mapping(Fanatec, null, @"\\?\hid#baseA", ("Up Shift", true)),
                Mapping(Fanatec, null, @"\\?\hid#baseB", ("Brake Bias +", true)),
            };

            var result = MappedRoleResolver.Resolve(mappings, variant: null,
                interfacePath: null, catalog: () => new[] { "Everything" });

            Assert.Equal(MappedRolesSource.AggregatedAcrossBases, result.Source);
            Assert.Equal(new[] { "Up Shift", "Brake Bias +" }, result.Roles.ToArray());
        }

        [Fact]
        public void Resolve_InterfacePath_NarrowsWhenBothKnown_ButFallsBackWhenNothingMatches()
        {
            var mappings = new[]
            {
                Mapping(Fanatec, "V", @"\\?\hid#baseA", ("RoleA", true)),
                Mapping(Fanatec, "V", @"\\?\hid#baseB", ("RoleB", true)),
            };

            // Path known and present → narrowed to that base.
            var narrowed = MappedRoleResolver.Resolve(mappings, "V", @"\\?\hid#baseB", () => null);
            Assert.Equal(new[] { "RoleB" }, narrowed.Roles.ToArray());

            // Path known but matches none → keep the variant matches rather than drop to catalog.
            var fallback = MappedRoleResolver.Resolve(mappings, "V", @"\\?\hid#unknown", () => null);
            Assert.Equal(new[] { "RoleA", "RoleB" }, fallback.Roles.ToArray());
        }

        [Fact]
        public void Resolve_NoMappings_FallsBackToCatalog_Distinct()
        {
            var result = MappedRoleResolver.Resolve(mappings: null, variant: "V",
                interfacePath: null, catalog: () => new[] { "A", "B", "A", "", "C" });

            Assert.Equal(MappedRolesSource.AllRoles, result.Source);
            Assert.Equal(new[] { "A", "B", "C" }, result.Roles.ToArray());
        }

        [Fact]
        public void Resolve_RimHasNoRoles_FallsThroughToCatalog()
        {
            var mappings = new[] { Mapping(Fanatec, "V", @"\\?\hid#base") };   // matched, but empty

            var result = MappedRoleResolver.Resolve(mappings, "V", null,
                () => new[] { "Catalog Role" });

            Assert.Equal(MappedRolesSource.AllRoles, result.Source);
            Assert.Equal(new[] { "Catalog Role" }, result.Roles.ToArray());
        }

        [Fact]
        public void Resolve_NothingAnywhere_IsNone()
        {
            var result = MappedRoleResolver.Resolve(mappings: null, variant: "V",
                interfacePath: null, catalog: () => null!);

            Assert.Equal(MappedRolesSource.None, result.Source);
            Assert.Empty(result.Roles);
        }

        [Fact]
        public void Resolve_CatalogThrows_DegradesToNone_NotAnException()
        {
            var result = MappedRoleResolver.Resolve(mappings: null, variant: "V",
                interfacePath: null,
                catalog: () => throw new System.InvalidOperationException("SimHub blew up"));

            Assert.Equal(MappedRolesSource.None, result.Source);
            Assert.Empty(result.Roles);
        }

        // ── Reflection reader (the same shape ControlMapperBridge walks) ──

        private static (ControlMapperRoleReader reader, FakePluginManager pm, FakeSettings settings)
            NewReader(bool riwOn = true)
        {
            IFakeControlMapper cm = CmFake.NewPlugin();
            cm.Settings.RecognizeIndiviualWheels = riwOn;
            return (new ControlMapperRoleReader(), new FakePluginManager(cm), cm.Settings);
        }

        private static FakeControllerSourceMapping Source(int vendorId, string? variant,
            string? interfacePath, params (string role, bool assigned)[] buttons)
        {
            var mapping = new FakeControllerMapping();
            foreach (var b in buttons)
                mapping.Buttons.Add(new FakeButtonMap { TargetRole = b.role, HasRoleAssigned = b.assigned });
            return new FakeControllerSourceMapping
            {
                ControllerDescription = new FakeControllerDescription
                {
                    VendorID = vendorId, Variant = variant, InterfacePath = interfacePath,
                },
                ControllerMapping = mapping,
            };
        }

        [Fact]
        public void Reader_ReadsThisRimsMappedRoles_RiwOn()
        {
            var (reader, pm, settings) = NewReader(riwOn: true);
            settings.ControllerMappings.Add(
                Source(Fanatec, "FS_WHEEL_SWTYPE_PHUB_PBMR", @"\\?\hid#pbmr",
                    ("Up Shift", true), ("Down Shift", true)));

            var result = reader.Read(pm, "FS_WHEEL_SWTYPE_PHUB_PBMR", interfacePath: null);

            Assert.Equal(MappedRolesSource.MappedOnThisWheel, result.Source);
            Assert.Equal(new[] { "Up Shift", "Down Shift" }, result.Roles.ToArray());
        }

        [Fact]
        public void Reader_RiwOff_NullsTheVariant_MatchesTheNoVariantRow()
        {
            var (reader, pm, settings) = NewReader(riwOn: false);
            // RIW off: the base collapses to one no-variant row; the reader must ignore the
            // computed variant and match it.
            settings.ControllerMappings.Add(
                Source(Fanatec, null, @"\\?\hid#base", ("Up Shift", true)));

            var result = reader.Read(pm, computedVariant: "FS_WHEEL_SWTYPE_PHUB_PBMR",
                interfacePath: null);

            Assert.Equal(MappedRolesSource.MappedOnThisWheel, result.Source);
            Assert.Equal(new[] { "Up Shift" }, result.Roles.ToArray());
        }

        [Fact]
        public void Reader_NoControlMapperPlugin_FallsBackToCatalog()
        {
            // GetPlugin<T>() returns null (Control Mapper not loaded), but the sanctioned
            // interface is present — the reader must offer the catalog.
            var reader = new ControlMapperRoleReader();
            var pm = new FakePluginManager(null)
            {
                ControlMapperInterface = new FakeControlMapperInterface { Roles = { "Cat A", "Cat B" } },
            };

            var result = reader.Read(pm, "FS_WHEEL_SWTYPE_PHUB_PBMR", interfacePath: null);

            Assert.Equal(MappedRolesSource.AllRoles, result.Source);
            Assert.Equal(new[] { "Cat A", "Cat B" }, result.Roles.ToArray());
        }

        [Fact]
        public void Reader_RimUnmapped_FallsBackToCatalog()
        {
            var (reader, pm, settings) = NewReader(riwOn: true);
            settings.ControllerMappings.Add(
                Source(Fanatec, "SOME_OTHER_RIM", @"\\?\hid#other", ("X", true)));
            pm.ControlMapperInterface = new FakeControlMapperInterface { Roles = { "All Roles" } };

            var result = reader.Read(pm, "FS_WHEEL_SWTYPE_PHUB_PBMR", interfacePath: null);

            Assert.Equal(MappedRolesSource.AllRoles, result.Source);
            Assert.Equal(new[] { "All Roles" }, result.Roles.ToArray());
        }

        [Fact]
        public void Reader_NothingAnywhere_IsNone()
        {
            var (reader, pm, _) = NewReader(riwOn: true);   // no mappings, no catalog

            var result = reader.Read(pm, "FS_WHEEL_SWTYPE_PHUB_PBMR", interfacePath: null);

            Assert.Equal(MappedRolesSource.None, result.Source);
            Assert.Empty(result.Roles);
        }

        [Fact]
        public void Reader_NullPluginManager_IsNone()
        {
            Assert.Same(MappedRoles.None,
                new ControlMapperRoleReader().Read(null, "V", null));
        }
    }
}
