using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.UI.Display.Shared;
using Xunit;

namespace FanaBridge.Tests.UI.Display.Shared
{
    /// <summary>
    /// The v9 property display grammar (<see cref="PropertyGrammar"/>): the namespace split, the
    /// GameData / ControlMapper friendly collapses, left-elision at the char budget with the leaf
    /// never truncating, the placeholder, and the full-path tooltip helper. Pure — no WPF.
    /// </summary>
    public class PropertyGrammarTests
    {
        private static (string text, GrammarEmphasis emphasis)[] Runs(string name,
            PropertyDisplayKind kind = PropertyDisplayKind.SimHubProperty, int budget = 1000)
            => PropertyGrammar.Format(name, kind, budget)
                .Select(r => (r.Text, r.Emphasis)).ToArray();

        [Fact]
        public void NamespaceSplit_OnLastDot_DimPrefixBrightLeaf()
        {
            var runs = Runs("DataCorePlugin.GameRawData.Physics.Rpms");
            Assert.Equal(new[]
            {
                ("DataCorePlugin.GameRawData.Physics.", GrammarEmphasis.Dim),
                ("Rpms", GrammarEmphasis.Bright),
            }, runs);
        }

        [Fact]
        public void NoNamespace_SingleBrightLeaf()
        {
            var runs = Runs("SessionOdo");
            Assert.Equal(new[] { ("SessionOdo", GrammarEmphasis.Bright) }, runs);
        }

        [Fact]
        public void GameData_CollapsesBothSpellings_ToFriendlyDimNamespace()
        {
            Assert.Equal(new[]
            {
                ("GameData.", GrammarEmphasis.Dim),
                ("SpeedKmh", GrammarEmphasis.Bright),
            }, Runs("DataCorePlugin.GameData.SpeedKmh"));

            Assert.Equal(new[]
            {
                ("GameData.", GrammarEmphasis.Dim),
                ("Gear", GrammarEmphasis.Bright),
            }, Runs("GameData.Gear"));
        }

        [Fact]
        public void BuiltInKind_BrightLeafOnly_EvenIfDotted()
        {
            Assert.Equal(new[] { ("Fuel", GrammarEmphasis.Bright) },
                Runs("Fuel", PropertyDisplayKind.BuiltIn));
            // The built-in kind never namespaces, even for a name that happens to contain a dot.
            Assert.Equal(new[] { ("A.B", GrammarEmphasis.Bright) },
                Runs("A.B", PropertyDisplayKind.BuiltIn));
        }

        [Fact]
        public void ControlMapperRole_CollapsesPrefix_LeafKeptWhole_WithSpacesOrDots()
        {
            Assert.Equal(new[]
            {
                ("ControlMapper.", GrammarEmphasis.Dim),
                ("Up Shift", GrammarEmphasis.Bright),   // space preserved
            }, Runs("InputStatus.ControlMapperPlugin.Up Shift"));

            // A role that itself contains a dot stays whole (not re-split on the last dot).
            Assert.Equal(new[]
            {
                ("ControlMapper.", GrammarEmphasis.Dim),
                ("Foo.Bar", GrammarEmphasis.Bright),
            }, Runs("InputStatus.ControlMapperPlugin.Foo.Bar"));
        }

        [Fact]
        public void LeftElision_DropsLeftmostSegments_KeepsNearest_AtBudgetBoundaries()
        {
            // "A.B.C.leaf": dim "A.B.C." (6) + "leaf" (4) = 10.
            Assert.Equal(new[]
            {
                ("A.B.C.", GrammarEmphasis.Dim),
                ("leaf", GrammarEmphasis.Bright),
            }, Runs("A.B.C.leaf", budget: 10));          // exactly fits → no elision

            // Budget 9: drop one segment → "…B.C." (5) + 4 = 9.
            Assert.Equal(new[]
            {
                ("…B.C.", GrammarEmphasis.Dim),
                ("leaf", GrammarEmphasis.Bright),
            }, Runs("A.B.C.leaf", budget: 9));

            // Budget 7: drop two → "…C." (3) + 4 = 7.
            Assert.Equal(new[]
            {
                ("…C.", GrammarEmphasis.Dim),
                ("leaf", GrammarEmphasis.Bright),
            }, Runs("A.B.C.leaf", budget: 7));
        }

        [Fact]
        public void LeafNeverTruncates_EvenBelowBudget_KeepsNearestSegment()
        {
            // Budget far below the leaf: keep the nearest segment, show the leaf in full.
            Assert.Equal(new[]
            {
                ("…B.", GrammarEmphasis.Dim),
                ("longleaf", GrammarEmphasis.Bright),
            }, Runs("A.B.longleaf", budget: 1));
        }

        [Fact]
        public void SingleSegmentNamespace_NeverElides()
        {
            // Nothing to drop from the left of a one-segment namespace — it stays whole.
            Assert.Equal(new[]
            {
                ("Ns.", GrammarEmphasis.Dim),
                ("leaf", GrammarEmphasis.Bright),
            }, Runs("Ns.leaf", budget: 1));

            // Same for the collapsed friendly namespaces.
            Assert.Equal(new[]
            {
                ("GameData.", GrammarEmphasis.Dim),
                ("SpeedKmh", GrammarEmphasis.Bright),
            }, Runs("DataCorePlugin.GameData.SpeedKmh", budget: 1));
        }

        [Fact]
        public void EmptyOrNull_YieldsPlaceholder()
        {
            Assert.Equal(new[] { ("(pick property)", GrammarEmphasis.Plain) }, Runs(""));
            Assert.Equal(new[] { ("(pick property)", GrammarEmphasis.Plain) }, Runs(null));
        }

        [Fact]
        public void FullText_IsUnElidedDisplayForm()
        {
            // Un-elided even when Format would elide at the same budget.
            Assert.Equal("A.B.C.leaf",
                PropertyGrammar.FullText("A.B.C.leaf", PropertyDisplayKind.SimHubProperty));
            // Collapsed friendly form (not the raw DataCorePlugin path).
            Assert.Equal("GameData.SpeedKmh",
                PropertyGrammar.FullText("DataCorePlugin.GameData.SpeedKmh", PropertyDisplayKind.SimHubProperty));
            Assert.Equal("ControlMapper.Up Shift",
                PropertyGrammar.FullText("InputStatus.ControlMapperPlugin.Up Shift", PropertyDisplayKind.SimHubProperty));
            Assert.Equal("SessionOdo",
                PropertyGrammar.FullText("SessionOdo", PropertyDisplayKind.SimHubProperty));
            Assert.Equal("(pick property)",
                PropertyGrammar.FullText(null, PropertyDisplayKind.SimHubProperty));
        }

        [Fact]
        public void KindOverload_MapsSchemaKind()
        {
            // BuiltIn schema kind → bright leaf only.
            Assert.Equal(new[] { ("Gear", GrammarEmphasis.Bright) },
                PropertyGrammar.Format("Gear", PropertyKind.BuiltIn, 1000)
                    .Select(r => (r.Text, r.Emphasis)).ToArray());
            // SimHubProperty schema kind → collapse rules apply.
            Assert.Equal(new[]
            {
                ("GameData.", GrammarEmphasis.Dim),
                ("Gear", GrammarEmphasis.Bright),
            }, PropertyGrammar.Format("GameData.Gear", PropertyKind.SimHubProperty, 1000)
                    .Select(r => (r.Text, r.Emphasis)).ToArray());

            Assert.Equal(PropertyDisplayKind.BuiltIn, PropertyGrammar.KindFor(PropertyKind.BuiltIn));
            Assert.Equal(PropertyDisplayKind.SimHubProperty, PropertyGrammar.KindFor(PropertyKind.SimHubProperty));
            Assert.Equal(PropertyDisplayKind.SimHubProperty, PropertyGrammar.KindFor(PropertyKind.FanaBridgeAction));
        }
    }
}
