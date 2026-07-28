using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>Phase E7 F: shared idle-compile helper (contract §6.2 three-row table).</summary>
    public class IdleCompileTests
    {
        /// <summary>
        /// Old-vs-helper truth-table golden across both arbiters' idle branches
        /// (absent / degraded / page / supported+unsupported screen / blank / painted / park).
        /// </summary>
        public static IEnumerable<object[]> IdleTruthTable()
        {
            // label, idle, screenCommands, expected Kind, expected ParkOnLegacyForBlank
            yield return new object[]
            {
                "absent+blankSupported",
                null,
                new ScreenCommandsCapability { Blank = true },
                IdleCompileKind.FirmwareBlank,
                false,
            };
            yield return new object[]
            {
                "absent+untested",
                null,
                null,
                IdleCompileKind.FirmwareBlank,
                false,
            };
            yield return new object[]
            {
                "degraded",
                DegradedPage(),
                new ScreenCommandsCapability { Blank = true },
                IdleCompileKind.FirmwareBlank,
                false,
            };
            yield return new object[]
            {
                "page",
                new IdleSpec
                {
                    Kind = IdleKind.Page,
                    Page = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                },
                null,
                IdleCompileKind.Page,
                false,
            };
            yield return new object[]
            {
                "screenLogo+supported",
                new IdleSpec { Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo },
                new ScreenCommandsCapability { Logo = true },
                IdleCompileKind.FirmwareScreen,
                false,
            };
            yield return new object[]
            {
                "screenLogo+unsupported",
                new IdleSpec { Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo },
                new ScreenCommandsCapability { Logo = false },
                IdleCompileKind.Silence,
                false,
            };
            yield return new object[]
            {
                "blank+unsupported=paint",
                new IdleSpec { Kind = IdleKind.Blank },
                new ScreenCommandsCapability { Blank = false },
                IdleCompileKind.PaintBlankFrame,
                false,
            };
            yield return new object[]
            {
                "blank+park",
                ParkBlank(),
                new ScreenCommandsCapability { Blank = false },
                IdleCompileKind.ParkOnLegacyForBlank,
                true,
            };
            yield return new object[]
            {
                "blank+supported",
                new IdleSpec { Kind = IdleKind.Blank },
                new ScreenCommandsCapability { Blank = true },
                IdleCompileKind.FirmwareBlank,
                false,
            };
        }

        private static IdleSpec DegradedPage()
        {
            var idle = new IdleSpec { Kind = IdleKind.Page };
            idle.DegradedAtLoad = true;
            return idle;
        }

        private static IdleSpec ParkBlank()
        {
            var idle = new IdleSpec { Kind = IdleKind.Blank };
            idle.ParkOnLegacyForBlank = true;
            return idle;
        }

        [Theory]
        [MemberData(nameof(IdleTruthTable))]
        public void TruthTable_HelperBranches(
            string label,
            IdleSpec idle,
            ScreenCommandsCapability sc,
            IdleCompileKind expectedKind,
            bool expectedPark)
        {
            var r = IdleCompile.Resolve(idle, sc);
            Assert.Equal(expectedKind, r.Kind);
            Assert.Equal(expectedPark, r.ParkOnLegacyForBlank);
            Assert.True(label.Length > 0); // keep label in signature for failure messages
        }

        [Fact]
        public void AbsentIdle_IsFirmwareBlank_WhenBlankSupported()
        {
            var sc = new ScreenCommandsCapability { Blank = true };
            var r = IdleCompile.Resolve(null, sc);
            Assert.Equal(IdleCompileKind.FirmwareBlank, r.Kind);
            Assert.Equal(IdleKind.Blank, r.PublishedIdleKind);
            Assert.Equal(WheelScreenCommand.Blank, r.ScreenCommand);
            Assert.False(r.CapabilityUntested);
        }

        [Fact]
        public void AbsentIdle_UntestedBlank_StillFirmwareBlank()
        {
            // null capability = untested, warn-and-allow.
            var r = IdleCompile.Resolve(null, screenCommands: null);
            Assert.Equal(IdleCompileKind.FirmwareBlank, r.Kind);
            Assert.True(r.CapabilityUntested);
        }

        [Fact]
        public void Blank_Unsupported_IsPaintBlankFrame()
        {
            var idle = new IdleSpec { Kind = IdleKind.Blank };
            var sc = new ScreenCommandsCapability { Blank = false };
            var r = IdleCompile.Resolve(idle, sc);
            Assert.Equal(IdleCompileKind.PaintBlankFrame, r.Kind);
        }

        [Fact]
        public void Blank_ParkOnLegacy_IsParkBranch()
        {
            var idle = new IdleSpec { Kind = IdleKind.Blank };
            // Validator sets this; tests may stamp it directly via reflection-free internal set
            // through the same property Seat/E6 already use (internal set — same assembly?
            // ParkOnLegacyForBlank has internal set — tests use InternalsVisibleTo).
            idle.ParkOnLegacyForBlank = true;
            var r = IdleCompile.Resolve(idle, new ScreenCommandsCapability { Blank = false });
            Assert.Equal(IdleCompileKind.ParkOnLegacyForBlank, r.Kind);
            Assert.True(r.ParkOnLegacyForBlank);
            Assert.Equal(IdleKind.Blank, r.PublishedIdleKind);
        }

        [Fact]
        public void PageIdle_IsPageBranch()
        {
            var idle = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
            };
            var r = IdleCompile.Resolve(idle);
            Assert.Equal(IdleCompileKind.Page, r.Kind);
            Assert.Equal(IdleKind.Page, r.PublishedIdleKind);
            Assert.Equal(DestinationIds.Itm("lapInfo"), r.PageDestinationId);
        }

        [Fact]
        public void ScreenLogo_Supported_IsFirmwareScreen()
        {
            var idle = new IdleSpec { Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo };
            var sc = new ScreenCommandsCapability { Logo = true };
            var r = IdleCompile.Resolve(idle, sc);
            Assert.Equal(IdleCompileKind.FirmwareScreen, r.Kind);
            Assert.Equal(WheelScreenCommand.Logo, r.ScreenCommand);
        }

        [Fact]
        public void ScreenLogo_Unsupported_IsSilence()
        {
            var idle = new IdleSpec { Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo };
            var sc = new ScreenCommandsCapability { Logo = false };
            var r = IdleCompile.Resolve(idle, sc);
            Assert.Equal(IdleCompileKind.Silence, r.Kind);
        }

        [Fact]
        public void DegradedIdle_CompilesAsBlank()
        {
            var idle = new IdleSpec { Kind = IdleKind.Page };
            idle.DegradedAtLoad = true;
            var r = IdleCompile.Resolve(idle, new ScreenCommandsCapability { Blank = true });
            Assert.Equal(IdleCompileKind.FirmwareBlank, r.Kind);
        }
    }
}
