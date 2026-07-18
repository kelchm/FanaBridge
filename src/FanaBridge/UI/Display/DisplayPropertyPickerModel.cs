using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>What one row in the property picker's list represents — a group heading
    /// (non-selectable) or a pickable property.</summary>
    internal enum PickerRowKind
    {
        GroupHeader,
        Property,
    }

    /// <summary>
    /// One flat row for the property picker's (virtualized) list: either a group header or a
    /// selectable property. A property row carries the exact name to commit and the
    /// <see cref="PropertyKind"/> to stamp on the <see cref="PropertySpec"/> — a curated
    /// FanaBridge built-in writes <see cref="PropertyKind.BuiltIn"/>, everything else
    /// <see cref="PropertyKind.SimHubProperty"/>.
    /// </summary>
    internal sealed class PickerRow
    {
        public PickerRowKind Kind { get; set; }

        /// <summary>Group name (header rows) or the property name (property rows).</summary>
        public string Text { get; set; }

        /// <summary>The name a property row commits (null on headers).</summary>
        public string PropertyName { get; set; }

        /// <summary>The kind a property row commits (ignored on headers).</summary>
        public PropertyKind PropertyKind { get; set; }

        public bool IsHeader => Kind == PickerRowKind.GroupHeader;
        public bool IsProperty => Kind == PickerRowKind.Property;

        /// <summary>The label content bound by the (virtualized) list template: the v9 property
        /// grammar for a property row (dim-ns/bright-leaf, budget generous so the picker never
        /// left-elides), or a single plain run of the group name for a header (which keeps its
        /// inherited bold-uppercase styling). Computed lazily; recycled containers re-read it.</summary>
        public PropertyLabelContent LabelContent
            => IsProperty
                ? PropertyGrammar.ContentFor(PropertyName, PropertyGrammar.KindFor(PropertyKind), int.MaxValue)
                : new PropertyLabelContent(
                    new[] { new GrammarRun(Text ?? "", GrammarEmphasis.Plain) }, null);
    }

    /// <summary>
    /// The testable core of the SimHub property picker: it groups the available property
    /// names (group = first dotted segment) with a curated "FanaBridge" group pinned FIRST
    /// carrying the built-in property names, and answers a substring filter. Pure — no WPF,
    /// no SimHub — so the grouping and filtering are unit-pinned; the modal
    /// (<see cref="PropertyPickerDialog"/>) is a thin view over it. The full property list is
    /// captured once at construction (it can hold thousands of names), so filtering is a
    /// cheap re-scan with no fetch.
    /// </summary>
    internal sealed class DisplayPropertyPickerModel
    {
        /// <summary>The curated built-ins group, always first — the friendly names the
        /// display itself shows (<see cref="BuiltInProperties"/>).</summary>
        public const string BuiltInGroup = "FanaBridge";

        /// <summary>Group for a property name with no dotted namespace.</summary>
        public const string UngroupedName = "General";

        private readonly IReadOnlyList<string> _builtIns;
        // SimHub groups: group name → its property names, both in stable (ordinal) order.
        private readonly List<KeyValuePair<string, List<string>>> _groups;

        public DisplayPropertyPickerModel(IReadOnlyList<string> builtIns,
            IReadOnlyList<string> allProperties)
        {
            _builtIns = Dedup(builtIns);
            _groups = BuildGroups(allProperties);
        }

        /// <summary>
        /// The picker rows for a filter: the FanaBridge built-ins group first, then the
        /// SimHub property groups in alphabetical order, each preceded by its header. A
        /// group whose members are all filtered out is omitted entirely (no orphan header).
        /// An empty / whitespace filter returns everything. Matching is substring,
        /// case-insensitive, and naturally spans dotted segments.
        /// </summary>
        public IReadOnlyList<PickerRow> Rows(string filter)
        {
            string f = filter?.Trim();
            bool hasFilter = !string.IsNullOrEmpty(f);
            var rows = new List<PickerRow>();

            // FanaBridge built-ins first.
            List<PickerRow> builtIn = null;
            foreach (var name in _builtIns)
                if (!hasFilter || Match(name, f))
                    (builtIn ?? (builtIn = new List<PickerRow>())).Add(new PickerRow
                    {
                        Kind = PickerRowKind.Property,
                        Text = name,
                        PropertyName = name,
                        PropertyKind = PropertyKind.BuiltIn,
                    });
            if (builtIn != null)
            {
                rows.Add(Header(BuiltInGroup));
                rows.AddRange(builtIn);
            }

            foreach (var group in _groups)
            {
                List<PickerRow> items = null;
                foreach (var name in group.Value)
                    if (!hasFilter || Match(name, f))
                        (items ?? (items = new List<PickerRow>())).Add(new PickerRow
                        {
                            Kind = PickerRowKind.Property,
                            Text = name,
                            PropertyName = name,
                            PropertyKind = PropertyKind.SimHubProperty,
                        });
                if (items != null)
                {
                    rows.Add(Header(group.Key));
                    rows.AddRange(items);
                }
            }
            return rows;
        }

        /// <summary>The number of selectable property rows a filter yields (headers
        /// excluded) — the keyboard-navigation and "no matches" gate.</summary>
        public int PropertyCount(string filter)
        {
            int n = 0;
            foreach (var row in Rows(filter))
                if (row.IsProperty)
                    n++;
            return n;
        }

        // ── Internals ─────────────────────────────────────────────────────

        private static PickerRow Header(string text)
            => new PickerRow { Kind = PickerRowKind.GroupHeader, Text = text };

        private static bool Match(string name, string filter)
            => name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        // The group a property name sorts under: the text before its first dot, or the
        // ungrouped bucket when it has none.
        private static string GroupOf(string name)
        {
            int dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : UngroupedName;
        }

        private static List<KeyValuePair<string, List<string>>> BuildGroups(
            IReadOnlyList<string> allProperties)
        {
            var byGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (allProperties != null)
                foreach (var name in allProperties)
                {
                    if (string.IsNullOrEmpty(name) || !seen.Add(name))
                        continue;
                    string group = GroupOf(name);
                    if (!byGroup.TryGetValue(group, out var list))
                        byGroup[group] = list = new List<string>();
                    list.Add(name);
                }

            var groupNames = new List<string>(byGroup.Keys);
            groupNames.Sort(StringComparer.OrdinalIgnoreCase);
            var result = new List<KeyValuePair<string, List<string>>>(groupNames.Count);
            foreach (var group in groupNames)
            {
                var list = byGroup[group];
                list.Sort(StringComparer.OrdinalIgnoreCase);
                result.Add(new KeyValuePair<string, List<string>>(group, list));
            }
            return result;
        }

        private static IReadOnlyList<string> Dedup(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<string>();
            var result = new List<string>(values.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v) && seen.Add(v))
                    result.Add(v);
            return result;
        }
    }
}
