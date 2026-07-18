using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FanaBridge.Display.Rules;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>Emphasis of one <see cref="GrammarRun"/> — how the WPF side colours it.</summary>
    public enum GrammarEmphasis
    {
        /// <summary>The namespace prefix — dim (<see cref="DisplayPalette.NsDim"/>).</summary>
        Dim,
        /// <summary>The leaf — bright (<see cref="DisplayPalette.LeafBright"/>).</summary>
        Bright,
        /// <summary>Uncoloured text (the "(pick property)" placeholder) — inherits.</summary>
        Plain,
    }

    /// <summary>One coloured span of a formatted property name.</summary>
    public readonly struct GrammarRun
    {
        public GrammarRun(string text, GrammarEmphasis emphasis)
        {
            Text = text;
            Emphasis = emphasis;
        }

        public string Text { get; }
        public GrammarEmphasis Emphasis { get; }
    }

    /// <summary>How a property name is namespaced for display — derived from the schema
    /// <see cref="PropertyKind"/> at the edge (see <see cref="PropertyGrammar.KindFor"/>).</summary>
    public enum PropertyDisplayKind
    {
        /// <summary>A SimHub property name: last-dot namespace split, with the GameData /
        /// ControlMapper collapses.</summary>
        SimHubProperty,
        /// <summary>A FanaBridge built-in typed field: a bright leaf, no namespace.</summary>
        BuiltIn,
    }

    /// <summary>
    /// The v9 property display grammar (decisions doc §11c): turns a raw property name into a
    /// dim-namespace / bright-leaf run list the WPF <see cref="PropertyLabel"/> renders. Pure —
    /// no WPF, no SimHub — so the split, the friendly collapses, and the left-elision maths are
    /// unit-pinned. The one source of truth for how a property reads everywhere it appears
    /// (triggers rows, the Overview priority list, the property picker, the detail editor).
    /// </summary>
    public static class PropertyGrammar
    {
        /// <summary>Shown for a null/empty property name.</summary>
        public const string Placeholder = "(pick property)";

        // Core telemetry lives under DataCorePlugin.GameData.* (and the bare GameData.* form
        // some configs carry); both collapse to a friendly "GameData." namespace.
        private const string GameDataPrefixFull = "DataCorePlugin.GameData.";
        private const string GameDataPrefixBare = "GameData.";
        private const string GameDataDim = "GameData";

        // A mapped control publishes under InputStatus.ControlMapperPlugin.<role>; the role is
        // everything after this fixed prefix (it may contain spaces or dots — kept whole).
        private const string ControlMapperPrefix = "InputStatus.ControlMapperPlugin.";
        private const string ControlMapperDim = "ControlMapper";

        /// <summary>The display kind for a schema <see cref="PropertyKind"/>: only the closed
        /// built-in set shows a bare leaf; everything else namespaces.</summary>
        public static PropertyDisplayKind KindFor(PropertyKind kind)
            => kind == PropertyKind.BuiltIn ? PropertyDisplayKind.BuiltIn : PropertyDisplayKind.SimHubProperty;

        /// <summary>Convenience overload taking the schema kind the edit model already holds.</summary>
        public static IReadOnlyList<GrammarRun> Format(string propertyName, PropertyKind kind, int charBudget)
            => Format(propertyName, KindFor(kind), charBudget);

        /// <summary>
        /// The coloured run list for <paramref name="propertyName"/>: a dim namespace prefix
        /// (with trailing dot) plus a bright leaf, left-elided with a leading "…" when the whole
        /// exceeds <paramref name="charBudget"/> — segments drop from the LEFT, the segment
        /// nearest the leaf is always kept, and the leaf itself never truncates. A null/empty
        /// name yields the single Plain placeholder run.
        /// </summary>
        public static IReadOnlyList<GrammarRun> Format(string propertyName, PropertyDisplayKind kind, int charBudget)
        {
            if (string.IsNullOrEmpty(propertyName))
                return new[] { new GrammarRun(Placeholder, GrammarEmphasis.Plain) };

            GetParts(propertyName, kind, out var ns, out string leaf);
            return Compose(ns, leaf, charBudget);
        }

        /// <summary>The full, un-elided display form (dim prefix + leaf), for tooltips.</summary>
        public static string FullText(string propertyName, PropertyKind kind)
            => FullText(propertyName, KindFor(kind));

        /// <summary>The full, un-elided display form (dim prefix + leaf), for tooltips.</summary>
        public static string FullText(string propertyName, PropertyDisplayKind kind)
        {
            if (string.IsNullOrEmpty(propertyName))
                return Placeholder;
            GetParts(propertyName, kind, out var ns, out string leaf);
            return ns.Count == 0 ? leaf : string.Join(".", ns) + "." + leaf;
        }

        /// <summary>Runs + full text bundled for the <see cref="PropertyLabel"/> attached
        /// property (the property picker's data-bound rows).</summary>
        public static PropertyLabelContent ContentFor(string propertyName, PropertyDisplayKind kind, int charBudget)
            => new PropertyLabelContent(Format(propertyName, kind, charBudget), FullText(propertyName, kind));

        // Split a name into its namespace segments (dot-free) and the leaf.
        private static void GetParts(string name, PropertyDisplayKind kind, out List<string> ns, out string leaf)
        {
            ns = new List<string>();

            if (kind == PropertyDisplayKind.BuiltIn)
            {
                leaf = name;
                return;
            }

            if (name.Length > ControlMapperPrefix.Length
                && name.StartsWith(ControlMapperPrefix, StringComparison.Ordinal))
            {
                ns.Add(ControlMapperDim);
                leaf = name.Substring(ControlMapperPrefix.Length);   // role kept whole
                return;
            }

            if (name.Length > GameDataPrefixFull.Length
                && name.StartsWith(GameDataPrefixFull, StringComparison.Ordinal))
            {
                ns.Add(GameDataDim);
                leaf = name.Substring(GameDataPrefixFull.Length);
                return;
            }

            if (name.Length > GameDataPrefixBare.Length
                && name.StartsWith(GameDataPrefixBare, StringComparison.Ordinal))
            {
                ns.Add(GameDataDim);
                leaf = name.Substring(GameDataPrefixBare.Length);
                return;
            }

            int lastDot = name.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == name.Length - 1)
            {
                leaf = name;   // no namespace (or a trailing dot) — whole name is the leaf
                return;
            }
            ns.AddRange(name.Substring(0, lastDot).Split('.'));
            leaf = name.Substring(lastDot + 1);
        }

        private static IReadOnlyList<GrammarRun> Compose(IReadOnlyList<string> ns, string leaf, int budget)
        {
            if (ns.Count == 0)
                return new[] { new GrammarRun(leaf, GrammarEmphasis.Bright) };

            string fullDim = string.Join(".", ns) + ".";
            // A single friendly segment (GameData./ControlMapper.) has nothing to drop, so it
            // stays whole even over budget; the WPF ellipsis is the last resort there.
            if (ns.Count == 1 || fullDim.Length + leaf.Length <= budget)
                return Pair(fullDim, leaf);

            // Drop the fewest leftmost segments that make it fit, always keeping the nearest.
            for (int k = 1; k < ns.Count; k++)
            {
                string dim = "…" + string.Join(".", ns.Skip(k)) + ".";
                if (dim.Length + leaf.Length <= budget)
                    return Pair(dim, leaf);
            }
            // Even the nearest segment alone is over budget — keep it; the leaf never truncates.
            return Pair("…" + ns[ns.Count - 1] + ".", leaf);
        }

        private static GrammarRun[] Pair(string dim, string leaf)
            => new[]
            {
                new GrammarRun(dim, GrammarEmphasis.Dim),
                new GrammarRun(leaf, GrammarEmphasis.Bright),
            };
    }
}
