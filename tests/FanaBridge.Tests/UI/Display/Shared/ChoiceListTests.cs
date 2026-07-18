using System.Linq;
using FanaBridge.UI.Display.Shared;
using Xunit;

namespace FanaBridge.Tests.UI.Display.Shared
{
    /// <summary>
    /// The dropdown option model (<see cref="ChoiceList"/>): builder ordering, selection lookup,
    /// and the closed-cell caption (glyph composition). Pure — no WPF.
    /// </summary>
    public class ChoiceListTests
    {
        [Fact]
        public void Builder_KeepsOrder_AndSelects()
        {
            var list = ChoiceList.Build()
                .Add("a", "Alpha")
                .Add("b", "Bravo")
                .Add("c", "Charlie")
                .Selected("b");

            Assert.Equal(new[] { "a", "b", "c" }, list.Items.Select(i => i.Id).ToArray());
            Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, list.Items.Select(i => i.Label).ToArray());
            Assert.Equal("b", list.SelectedId);
            Assert.NotNull(list.Selected);
            Assert.Equal("Bravo", list.Selected.Label);
        }

        [Fact]
        public void SelectedLabelWithGlyph_ComposesGlyph_ElseLabelAlone_ElseEmpty()
        {
            // No glyph → label alone.
            Assert.Equal("Bravo",
                ChoiceList.Build().Add("b", "Bravo").Selected("b").SelectedLabelWithGlyph());

            // Glyph present → "glyph label".
            Assert.Equal("◆ Fuel",
                ChoiceList.Build().Add("f", "Fuel", glyph: "◆").Selected("f").SelectedLabelWithGlyph());

            // Selection matches nothing → empty caption, null Selected.
            var unmatched = ChoiceList.Build().Add("a", "Alpha").Selected("zzz");
            Assert.Equal("", unmatched.SelectedLabelWithGlyph());
            Assert.Null(unmatched.Selected);

            // Null selection → empty.
            Assert.Equal("", ChoiceList.Build().Add("a", "Alpha").Selected(null).SelectedLabelWithGlyph());
        }

        [Fact]
        public void Choice_CarriesEnabledAndHint_DefaultsEnabled()
        {
            var list = ChoiceList.Build()
                .Add("on", "Enabled option")
                .Add("off", "Disabled option", enabled: false, hint: "not available on this wheel")
                .Selected("on");

            Assert.True(list.Items[0].Enabled);
            Assert.Null(list.Items[0].Hint);
            Assert.False(list.Items[1].Enabled);
            Assert.Equal("not available on this wheel", list.Items[1].Hint);
        }

        [Fact]
        public void EmptyList_HasNoSelection()
        {
            var list = new ChoiceList(null, "x");
            Assert.Empty(list.Items);
            Assert.Null(list.Selected);
            Assert.Equal("", list.SelectedLabelWithGlyph());
        }
    }
}
