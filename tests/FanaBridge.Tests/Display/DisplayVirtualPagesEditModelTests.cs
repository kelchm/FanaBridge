using System.Linq;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Virtual pages editor model: CRUD, kind/effect/source echo, base row, and pure-model
    /// LIVE preview segment bytes. No WPF.
    /// </summary>
    public class DisplayVirtualPagesEditModelTests
    {
        private static DisplayCustomizationConfig Load(string json)
            => DisplayConfigSerializer.Load(json, _ => { });

        private static DisplayCustomizationConfig WithTwoScreens()
            => Load("{ \"schemaVersion\": 1, \"segmentDisplay\": { \"baseScreenId\": \"pit\", "
                + "\"screens\": [ "
                + "{ \"id\": \"spd\", \"name\": \"Speed\", \"contentKind\": \"speed\" }, "
                + "{ \"id\": \"pit\", \"name\": \"Pit\", \"text\": \"PIT\" } "
                + "] } }");

        [Fact]
        public void EmptyStart_NoScreens_SelectedNull()
        {
            var model = new DisplayVirtualPagesEditModel(null);
            Assert.Empty(model.Screens);
            Assert.Null(model.SelectedScreenId);
            Assert.Null(model.SelectedScreen);
            Assert.Empty(model.PagePills());
            Assert.Equal(
                new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank },
                model.PreviewSegments());
        }

        [Fact]
        public void Load_SelectsFirstScreen_PillsReflectSelection()
        {
            var model = new DisplayVirtualPagesEditModel(WithTwoScreens());
            Assert.Equal(2, model.Screens.Count);
            Assert.Equal("spd", model.SelectedScreenId);
            var pills = model.PagePills();
            Assert.Equal(2, pills.Count);
            Assert.True(pills[0].IsSelected);
            Assert.False(pills[1].IsSelected);
            Assert.Equal("Speed", pills[0].Name);
            Assert.Equal(1, pills[0].Index);
        }

        [Fact]
        public void AddScreen_AppendsTextNEW_AndSelectsIt()
        {
            var model = new DisplayVirtualPagesEditModel(null);
            var cfg = model.AddScreen();

            Assert.NotNull(cfg);
            Assert.Single(cfg.Legacy.Screens);
            Assert.Equal(LegacyContentKind.Text, cfg.Legacy.Screens[0].ContentKind);
            Assert.Equal("NEW", cfg.Legacy.Screens[0].Text);
            Assert.Equal(cfg.Legacy.Screens[0].Id, model.SelectedScreenId);
            // ITM set carried empty (not null-clobbered).
            Assert.NotNull(cfg.Itm);
        }

        [Fact]
        public void SetName_ContentKind_Text_Effect_Source_FreshDocuments()
        {
            var start = WithTwoScreens();
            var model = new DisplayVirtualPagesEditModel(start);
            model.SelectScreen("pit");

            var afterName = model.SetName("pit", "Pit Limiter");
            Assert.NotSame(start, afterName);
            Assert.Equal("Pit Limiter", model.SelectedScreen.Name);
            Assert.Equal("Pit", start.Legacy.Screens[1].Name); // original untouched

            var afterKind = model.SetContentKind("pit", LegacyContentKind.Message);
            Assert.Equal(LegacyContentKind.Message, model.SelectedScreen.ContentKind);

            var afterText = model.SetText("pit", "HELLO");
            Assert.Equal("HELLO", model.SelectedScreen.Text);

            var afterEffect = model.SetEffect("pit", LegacyEffect.Scroll);
            Assert.Equal(LegacyEffect.Scroll, model.SelectedScreen.Effect);

            model.SetContentKind("pit", LegacyContentKind.Property);
            var afterSrc = model.SetSource("pit", PropertyKind.BuiltIn, BuiltInProperties.Fuel);
            Assert.Equal(PropertyKind.BuiltIn, afterSrc.Legacy.Screens
                .Single(s => s.Id == "pit").Source.Kind);
            Assert.Equal(BuiltInProperties.Fuel, afterSrc.Legacy.Screens
                .Single(s => s.Id == "pit").Source.Name);
        }

        [Fact]
        public void RemoveScreen_Immediate_ClearsBaseIfMatched_SelectsNeighbour()
        {
            var model = new DisplayVirtualPagesEditModel(WithTwoScreens());
            Assert.Equal("pit", model.BaseScreenId);
            model.SelectScreen("pit");

            var cfg = model.RemoveScreen("pit");
            Assert.Single(cfg.Legacy.Screens);
            Assert.Equal("spd", cfg.Legacy.Screens[0].Id);
            Assert.Null(cfg.Legacy.BaseScreenId); // was base → Blank
            Assert.Equal("spd", model.SelectedScreenId);
        }

        [Fact]
        public void SetBaseScreenId_BlankAndScreen()
        {
            var model = new DisplayVirtualPagesEditModel(WithTwoScreens());
            Assert.Equal("pit", model.BaseScreenId);

            var blank = model.SetBaseScreenId(DisplayVirtualPagesEditModel.BaseBlankId);
            Assert.Null(blank.Legacy.BaseScreenId);

            var spd = model.SetBaseScreenId("spd");
            Assert.Equal("spd", spd.Legacy.BaseScreenId);

            // Unknown id is a no-op.
            var same = model.SetBaseScreenId("nope");
            Assert.Equal("spd", same.Legacy.BaseScreenId);
        }

        [Fact]
        public void BaseScreenChoices_BlankPlusSurvivors()
        {
            var model = new DisplayVirtualPagesEditModel(WithTwoScreens());
            var choices = model.BaseScreenChoices();
            Assert.Equal(3, choices.Items.Count);
            Assert.Equal(DisplayVirtualPagesEditModel.BaseBlankId, choices.Items[0].Id);
            Assert.Equal("Blank", choices.Items[0].Label);
            Assert.Equal("pit", choices.SelectedId);
        }

        [Fact]
        public void PreviewSegments_TextKind_MatchesSevenSegmentBytes()
        {
            var model = new DisplayVirtualPagesEditModel(WithTwoScreens());
            model.SelectScreen("pit");
            // PIT via EncodeWithDots window.
            Assert.Equal(LegacyValueFormatter.Render("PIT"), model.PreviewSegments());
        }

        [Fact]
        public void PreviewSegments_SpeedKind_UsesDemoValue()
        {
            var model = new DisplayVirtualPagesEditModel(WithTwoScreens());
            model.SelectScreen("spd");
            Assert.Equal(
                LegacyValueFormatter.Render(LegacyValueFormatter.FormatSpeed(SevenSegmentFaceRender.DemoSpeed)),
                model.PreviewSegments());
        }

        [Fact]
        public void PreviewSegments_ScrollMessage_AdvancesOnClock()
        {
            var cfg = Load("{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"m\", \"contentKind\": \"message\", \"text\": \"HELLO\", "
                + "\"effect\": \"scroll\" } ] } }");
            var model = new DisplayVirtualPagesEditModel(cfg);
            var at0 = model.PreviewSegments(0);
            var atStep = model.PreviewSegments(LegacyEffectClock.ScrollStepMs);
            Assert.NotEqual(at0, atStep);
            Assert.Equal(3, at0.Length);
            Assert.Equal(3, atStep.Length);
        }

        [Fact]
        public void SurvivorScreens_ExcludesUnknownContentKind()
        {
            // Unknown kind is kept for EnumText survival but is not a SHOW target.
            var raw = Load("{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"ok\", \"text\": \"PIT\" }, "
                + "{ \"id\": \"x\", \"text\": \"PIT\", \"contentKind\": \"hologram\" } "
                + "] } }");
            var model = new DisplayVirtualPagesEditModel(raw);
            // Validator keeps unknown-kind screens; survivors for the UI exclude them.
            var survivors = model.SurvivorScreens();
            Assert.Contains(survivors, s => s.Id == "ok");
            Assert.DoesNotContain(survivors, s => s.Id == "x");
        }

        [Fact]
        public void ContentKindAndEffectLabels_CoverOfferedKinds()
        {
            Assert.Equal("Speed", DisplayVirtualPagesEditModel.ContentKindLabel(LegacyContentKind.Speed));
            Assert.Equal("Message", DisplayVirtualPagesEditModel.ContentKindLabel(LegacyContentKind.Message));
            Assert.Equal("Scroll", DisplayVirtualPagesEditModel.EffectLabel(LegacyEffect.Scroll));
            Assert.True(DisplayVirtualPagesEditModel.ShowsTextField(LegacyContentKind.Text));
            Assert.True(DisplayVirtualPagesEditModel.ShowsTextField(LegacyContentKind.Message));
            Assert.False(DisplayVirtualPagesEditModel.ShowsTextField(LegacyContentKind.Speed));
            Assert.True(DisplayVirtualPagesEditModel.ShowsPropertyField(LegacyContentKind.Property));
        }

        [Fact]
        public void AddScreen_PreservesExistingItmRules_ByReference()
        {
            var start = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }");
            var model = new DisplayVirtualPagesEditModel(start);
            var cfg = model.AddScreen();
            Assert.Same(start.Itm, cfg.Itm);
            Assert.Single(cfg.Itm.Rules);
        }
    }
}
