using System.Linq;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Surface B: Add-a-page pure projection — inert setup porch, live plain door.
    /// </summary>
    public class DisplayAddPageV2ModelTests
    {
        [Fact]
        public void SetupPorch_IsInert_WithSpokeArrivingLater()
        {
            var model = DisplayAddPageV2Model.Project(
                MinimalDoc(), EmptyConnected(), DisplayType.Itm);

            Assert.False(model.SetupPorchEnabled);
            Assert.Equal(DisplayCopy.SpokeArrivingLater("Setups"), model.SetupPorchNote);
            Assert.Contains("later build", model.SetupPorchNote);
        }

        [Fact]
        public void PlainDoor_ThreeCards_AllEnabled()
        {
            var model = DisplayAddPageV2Model.Project(
                MinimalDoc(), EmptyConnected(), DisplayType.Itm);

            Assert.Equal(3, model.Doors.Count);
            Assert.All(model.Doors, d => Assert.True(d.Enabled));
            Assert.Equal(DisplayCopy.DoorAPage, model.Doors[0].Title);
            Assert.Equal(DisplayCopy.DoorAnEntrypoint, model.Doors[1].Title);
            Assert.Equal(DisplayCopy.DoorAnOverride, model.Doors[2].Title);
            Assert.Equal(DisplayCopy.OrAddOneThing, model.PlainDoorLabel);
            Assert.Equal(DisplayCopy.NothingCreatedUntilSave, model.PlainDoorNote);
        }

        [Fact]
        public void Header_SurfaceWord_Itm()
        {
            var model = DisplayAddPageV2Model.Project(
                MinimalDoc(), EmptyConnected(), DisplayType.Itm);
            Assert.Equal(DisplayCopy.ItmDisplay, model.SurfaceWord);
        }

        [Fact]
        public void ItmChoices_IncludeOnlyExplicitlyRemovedPages()
        {
            var doc = MinimalDoc();
            doc.Pages = new List<PageEntry>
            {
                new PageEntry
                {
                    Kind = PageEntryKind.ItmPage,
                    CatalogPageId = "removed",
                    Removed = true,
                },
                new PageEntry
                {
                    Kind = PageEntryKind.ItmPage,
                    CatalogPageId = "placed",
                    Removed = false,
                },
            };
            var catalog = new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage { Id = "removed", Name = "Removed page" },
                        new CatalogPage { Id = "placed", Name = "Placed page" },
                        new CatalogPage { Id = "implicit", Name = "Implicit page" },
                    },
                },
            };

            var model = DisplayAddPageV2Model.Project(
                doc, EmptyConnected(), DisplayType.Itm, catalog);

            var choice = Assert.Single(model.ItmChoices);
            Assert.Equal("removed", choice.CatalogPageId);
        }

        private static DisplayConfigV2 MinimalDoc()
        {
            return DisplayConfigV2Validator.Normalize(new DisplayConfigV2
            {
                Priority = new PriorityLadder
                {
                    Rows = new System.Collections.Generic.List<PriorityRow>
                    {
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            }, _ => { });
        }

        private static DisplayResolutionSnapshotModel EmptyConnected()
            => DisplayResolutionSnapshotModel.From(
                null, inGame: false, isConnected: true, aggregates: null, manual: null);
    }
}
