using System.Collections.Generic;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>One option in a <see cref="ChoiceList"/> — a stable id plus its display label
    /// and optional leading glyph, enabled flag, and secondary hint.</summary>
    public sealed class Choice
    {
        public Choice(string id, string label, string glyph = null, bool enabled = true, string hint = null)
        {
            Id = id;
            Label = label;
            Glyph = glyph;
            Enabled = enabled;
            Hint = hint;
        }

        /// <summary>The value committed when this option is chosen.</summary>
        public string Id { get; }

        /// <summary>The human-readable option text.</summary>
        public string Label { get; }

        /// <summary>An optional leading glyph (e.g. a page icon); null/empty for none.</summary>
        public string Glyph { get; }

        /// <summary>Whether the option can be selected (a disabled option renders muted).</summary>
        public bool Enabled { get; }

        /// <summary>Optional secondary text (tooltip / sub-label); null for none.</summary>
        public string Hint { get; }
    }

    /// <summary>
    /// The SimHub/WPF-free model behind an anchored dropdown (<see cref="DropDownCell"/>): the
    /// ordered options and which is selected, plus the formatting of the selected value the
    /// closed cell shows. Pure — the cell is a thin view over it, so the option set, selection,
    /// and glyph composition are unit-pinned.
    /// </summary>
    public sealed class ChoiceList
    {
        public ChoiceList(IReadOnlyList<Choice> items, string selectedId)
        {
            Items = items ?? new Choice[0];
            SelectedId = selectedId;
        }

        /// <summary>The options in display order.</summary>
        public IReadOnlyList<Choice> Items { get; }

        /// <summary>The id of the selected option (may match none — then <see cref="Selected"/>
        /// is null and the cell shows nothing).</summary>
        public string SelectedId { get; }

        /// <summary>The selected option, or null when <see cref="SelectedId"/> matches none.</summary>
        public Choice Selected
        {
            get
            {
                if (SelectedId == null)
                    return null;
                foreach (var c in Items)
                    if (c.Id == SelectedId)
                        return c;
                return null;
            }
        }

        /// <summary>The closed cell's caption: the selected option's glyph + label ("▾ Fuel"),
        /// its label alone when it has no glyph, or "" when nothing is selected.</summary>
        public string SelectedLabelWithGlyph()
        {
            var s = Selected;
            if (s == null)
                return "";
            return string.IsNullOrEmpty(s.Glyph) ? s.Label : s.Glyph + " " + s.Label;
        }

        /// <summary>Start an ordered builder.</summary>
        public static Builder Build() => new Builder();

        /// <summary>Accumulates options in order, then seals to a <see cref="ChoiceList"/> with
        /// a chosen selection.</summary>
        public sealed class Builder
        {
            private readonly List<Choice> _items = new List<Choice>();

            public Builder Add(string id, string label, string glyph = null, bool enabled = true, string hint = null)
            {
                _items.Add(new Choice(id, label, glyph, enabled, hint));
                return this;
            }

            public Builder Add(Choice choice)
            {
                _items.Add(choice);
                return this;
            }

            public ChoiceList Selected(string selectedId) => new ChoiceList(_items, selectedId);
        }
    }
}
