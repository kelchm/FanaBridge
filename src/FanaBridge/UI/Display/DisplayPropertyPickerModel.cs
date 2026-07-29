using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// Browse scope for the property picker's left rail. Non-empty search ignores the
    /// scope and filters every property (global); empty search shows this scope's content.
    /// <see cref="Root"/> carries the group name (pinned "FanaBridge" / "Mapped controls"
    /// or an auto root from the first dotted segment).
    /// </summary>
    internal enum PickerScopeKind
    {
        Favorites,
        Recents,
        ItmPages,
        AllProperties,
        Root,
    }

    /// <summary>A picker browse scope — fixed rails or a named root group.</summary>
    internal readonly struct PickerScope : IEquatable<PickerScope>
    {
        public PickerScope(PickerScopeKind kind, string rootName = null)
        {
            Kind = kind;
            RootName = rootName;
        }

        public PickerScopeKind Kind { get; }
        /// <summary>Group name when <see cref="Kind"/> is <see cref="PickerScopeKind.Root"/>;
        /// otherwise null.</summary>
        public string RootName { get; }

        public static PickerScope Favorites { get; } = new PickerScope(PickerScopeKind.Favorites);
        public static PickerScope Recents { get; } = new PickerScope(PickerScopeKind.Recents);
        public static PickerScope ItmPages { get; } = new PickerScope(PickerScopeKind.ItmPages);
        public static PickerScope AllProperties { get; } = new PickerScope(PickerScopeKind.AllProperties);

        public static PickerScope Root(string name) => new PickerScope(PickerScopeKind.Root, name);

        public bool Equals(PickerScope other)
            => Kind == other.Kind
               && string.Equals(RootName, other.RootName, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is PickerScope other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Kind;
                if (RootName != null)
                    h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(RootName);
                return h;
            }
        }
    }

    /// <summary>One left-rail entry: a selectable scope, or a non-selectable section marker.</summary>
    internal sealed class PickerRail
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Glyph { get; set; }
        /// <summary>True for the "PLUGIN ROOTS · AUTO" section marker (not selectable).</summary>
        public bool IsSection { get; set; }
        /// <summary>Scope this rail opens; ignored when <see cref="IsSection"/>.</summary>
        public PickerScope Scope { get; set; }
    }

    /// <summary>
    /// One flat row for the property picker's (virtualized) list: either a group header or a
    /// selectable property. A property row carries the exact name to commit and the
    /// <see cref="PropertyKind"/> to stamp on the <see cref="PropertySpec"/> — a curated
    /// FanaBridge built-in writes <see cref="PropertyKind.BuiltIn"/>, everything else
    /// <see cref="PropertyKind.SimHubProperty"/>.
    /// </summary>
    internal sealed class PickerRow : INotifyPropertyChanged
    {
        /// <summary>Raised for <see cref="LiveValue"/> only — the one field the dialog
        /// mutates in place (the 500ms live-value refresh), so bound rows update without
        /// an ItemsSource reset (which would throw away the list's scroll position).</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        public PickerRowKind Kind { get; set; }

        /// <summary>Group name (header rows) or the property name (property rows).</summary>
        public string Text { get; set; }

        /// <summary>The name a property row commits (null on headers).</summary>
        public string PropertyName { get; set; }

        /// <summary>The kind a property row commits (ignored on headers).</summary>
        public PropertyKind PropertyKind { get; set; }

        /// <summary>Whether this property is in the user's favorites set.</summary>
        public bool IsFavorite { get; set; }

        /// <summary>First case-insensitive match of the active filter over the property
        /// name (or role text for mapped-control rows), for
        /// <see cref="PropertyGrammar.ContentFor(string, PropertyDisplayKind, int, int, int)"/>.
        /// Negative when there is no active filter / no highlight.</summary>
        public int MatchStart { get; set; } = -1;

        /// <summary>Length of the match span; 0 when <see cref="MatchStart"/> is negative.</summary>
        public int MatchLength { get; set; }

        private string _liveValue;

        /// <summary>Live property value text filled by the dialog (empty when capped,
        /// unavailable, or null). Not set by the pure model; change-notifying so the
        /// refresh updates bound rows in place.</summary>
        public string LiveValue
        {
            get => _liveValue;
            set
            {
                if (string.Equals(_liveValue, value, StringComparison.Ordinal))
                    return;
                _liveValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LiveValue)));
            }
        }

        /// <summary>☆ / ★ glyph for the favorites toggle zone (dialog fills from
        /// <see cref="IsFavorite"/>).</summary>
        public string FavoriteGlyph => IsFavorite ? "★" : "☆";

        public bool IsHeader => Kind == PickerRowKind.GroupHeader;
        public bool IsProperty => Kind == PickerRowKind.Property;

        /// <summary>The label content bound by the (virtualized) list template: the v9 property
        /// grammar for a property row (dim-ns/bright-leaf, budget generous so the picker never
        /// left-elides), or a single plain run of the group name for a header (which keeps its
        /// inherited bold-uppercase styling). Computed lazily; recycled containers re-read it.</summary>
        public PropertyLabelContent LabelContent
        {
            get
            {
                if (!IsProperty)
                {
                    return new PropertyLabelContent(
                        new[] { new GrammarRun(Text ?? "", GrammarEmphasis.Plain) }, null);
                }

                var kind = PropertyGrammar.KindFor(PropertyKind);
                if (MatchStart >= 0 && MatchLength > 0)
                {
                    return PropertyGrammar.ContentFor(
                        PropertyName, kind, int.MaxValue, MatchStart, MatchLength);
                }

                return PropertyGrammar.ContentFor(PropertyName, kind, int.MaxValue);
            }
        }
    }

    /// <summary>
    /// The testable core of the SimHub property picker: it groups the available property
    /// names (group = first dotted segment) with a curated "FanaBridge" group pinned FIRST
    /// carrying the built-in property names, answers a substring filter, and exposes the
    /// v9 left-rail scopes (Favorites / Recents / ITM pages / All / roots). Pure — no WPF,
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

        /// <summary>The curated Control Mapper roles group, pinned second — each role maps to
        /// its live <c>InputStatus.ControlMapperPlugin.*</c> property.</summary>
        public const string MappedGroup = "Mapped controls";

        /// <summary>Group for a property name with no dotted namespace.</summary>
        public const string UngroupedName = "General";

        public const string RailFavoritesId = "favorites";
        public const string RailRecentsId = "recents";
        public const string RailItmPagesId = "itmPages";
        public const string RailSectionRootsId = "section-roots";
        public const string RailAllId = "all";
        public const string RailRootIdPrefix = "root:";

        public const string RailFavoritesGlyph = "★";
        public const string RailRecentsGlyph = "◷";
        public const string RailItmPagesGlyph = "◆";
        public const string RailAllGlyph = "≡";

        public const string RootsSectionLabel = "PLUGIN ROOTS · AUTO";

        private readonly IReadOnlyList<string> _builtIns;
        private readonly HashSet<string> _builtInSet;
        // Mapped-control rows (role → property), curated, in supplied order.
        private readonly IReadOnlyList<string> _mappedRoles;
        // SimHub groups: group name → its property names, both in stable (ordinal) order.
        private readonly List<KeyValuePair<string, List<string>>> _groups;
        private readonly IReadOnlyList<string> _favorites;
        private readonly HashSet<string> _favoriteSet;
        private readonly IReadOnlyList<string> _recents;
        private readonly IReadOnlyList<string> _itmPageProperties;

        public DisplayPropertyPickerModel(IReadOnlyList<string> builtIns,
            IReadOnlyList<string> allProperties, IReadOnlyList<string> mappedRoles = null,
            IReadOnlyList<string> favorites = null, IReadOnlyList<string> recents = null,
            IReadOnlyList<string> itmPageProperties = null)
        {
            _builtIns = Dedup(builtIns);
            _builtInSet = new HashSet<string>(_builtIns, StringComparer.Ordinal);
            _mappedRoles = Dedup(mappedRoles);
            _groups = BuildGroups(allProperties);
            _favorites = Dedup(favorites);
            _favoriteSet = new HashSet<string>(_favorites, StringComparer.Ordinal);
            _recents = Dedup(recents);
            _itmPageProperties = Dedup(itmPageProperties);
        }

        /// <summary>
        /// Default rail on open: Favorites if non-empty, else Recent if non-empty, else
        /// On your ITM pages (even when that list is empty).
        /// </summary>
        public PickerScope DefaultScope()
        {
            if (_favorites.Count > 0)
                return PickerScope.Favorites;
            if (_recents.Count > 0)
                return PickerScope.Recents;
            return PickerScope.ItmPages;
        }

        /// <summary>
        /// Ordered left-rail descriptors: the three fixed rails, the "PLUGIN ROOTS · AUTO"
        /// section marker, "All properties", pinned FanaBridge + Mapped controls, then auto
        /// roots (first dotted segment of every catalog property, alphabetical).
        /// </summary>
        public IReadOnlyList<PickerRail> Rails()
        {
            var rails = new List<PickerRail>
            {
                new PickerRail
                {
                    Id = RailFavoritesId,
                    Label = "Favorites",
                    Glyph = RailFavoritesGlyph,
                    Scope = PickerScope.Favorites,
                },
                new PickerRail
                {
                    Id = RailRecentsId,
                    Label = "Recent",
                    Glyph = RailRecentsGlyph,
                    Scope = PickerScope.Recents,
                },
                new PickerRail
                {
                    Id = RailItmPagesId,
                    Label = "On your ITM pages",
                    Glyph = RailItmPagesGlyph,
                    Scope = PickerScope.ItmPages,
                },
                new PickerRail
                {
                    Id = RailSectionRootsId,
                    Label = RootsSectionLabel,
                    IsSection = true,
                },
                new PickerRail
                {
                    Id = RailAllId,
                    Label = "All properties",
                    Glyph = RailAllGlyph,
                    Scope = PickerScope.AllProperties,
                },
                new PickerRail
                {
                    Id = RailRootIdPrefix + BuiltInGroup,
                    Label = BuiltInGroup,
                    Scope = PickerScope.Root(BuiltInGroup),
                },
                new PickerRail
                {
                    Id = RailRootIdPrefix + MappedGroup,
                    Label = MappedGroup,
                    Scope = PickerScope.Root(MappedGroup),
                },
            };

            // Skip auto roots that collide with a pinned curated group (FanaBridge publishes
            // real SimHub properties under FanaBridge.* — without this the rail would list
            // two identical "FanaBridge" entries and RootRows would only ever serve the
            // curated branch). Same-named auto content is merged under the pinned rail.
            foreach (var group in _groups)
            {
                if (IsPinnedGroupName(group.Key))
                    continue;
                rails.Add(new PickerRail
                {
                    Id = RailRootIdPrefix + group.Key,
                    Label = group.Key,
                    Scope = PickerScope.Root(group.Key),
                });
            }

            return rails;
        }

        /// <summary>
        /// The picker rows for a filter (All-properties scope). Kept so existing call sites
        /// and tests stay on the pre-rail API; new code prefers
        /// <see cref="Rows(PickerScope, string)"/>.
        /// </summary>
        public IReadOnlyList<PickerRow> Rows(string filter)
            => Rows(PickerScope.AllProperties, filter);

        /// <summary>
        /// Rows for a rail scope and optional filter. Non-empty filter → global search over
        /// every property (today's grouped output, headers preserved, each property row
        /// carries <see cref="PickerRow.MatchStart"/>). Empty filter → the selected scope's
        /// content (Favorites/Recents/ItmPages have no headers; Root/All keep group headers).
        /// Favorites/Recents names missing from the catalog still appear (kind resolves as
        /// BuiltIn when in the built-ins list, else SimHubProperty).
        /// </summary>
        public IReadOnlyList<PickerRow> Rows(PickerScope scope, string filter)
        {
            string f = filter?.Trim();
            if (!string.IsNullOrEmpty(f))
                return GlobalRows(f);

            switch (scope.Kind)
            {
                case PickerScopeKind.Favorites:
                    return NamedListRows(_favorites, withHeaders: false, filter: null);
                case PickerScopeKind.Recents:
                    return NamedListRows(_recents, withHeaders: false, filter: null);
                case PickerScopeKind.ItmPages:
                    return NamedListRows(_itmPageProperties, withHeaders: false, filter: null);
                case PickerScopeKind.Root:
                    return RootRows(scope.RootName);
                case PickerScopeKind.AllProperties:
                default:
                    return GlobalRows(null);
            }
        }

        /// <summary>The number of selectable property rows a filter yields (headers
        /// excluded) — the keyboard-navigation and "no matches" gate.</summary>
        public int PropertyCount(string filter)
            => PropertyCount(PickerScope.AllProperties, filter);

        /// <summary>Selectable property count for a scope + filter (headers excluded);
        /// mirrors <see cref="Rows(PickerScope, string)"/>.</summary>
        public int PropertyCount(PickerScope scope, string filter)
        {
            int n = 0;
            foreach (var row in Rows(scope, filter))
                if (row.IsProperty)
                    n++;
            return n;
        }

        // ── Internals ─────────────────────────────────────────────────────

        // Full grouped list (FanaBridge, Mapped controls, then alphabetical SimHub groups).
        // filter null/empty → everything; non-empty → case-insensitive substring match.
        private IReadOnlyList<PickerRow> GlobalRows(string filter)
        {
            bool hasFilter = !string.IsNullOrEmpty(filter);
            var rows = new List<PickerRow>();

            // FanaBridge built-ins first.
            List<PickerRow> builtIn = null;
            foreach (var name in _builtIns)
                if (!hasFilter || Match(name, filter))
                    (builtIn ?? (builtIn = new List<PickerRow>())).Add(
                        PropertyRow(name, name, PropertyKind.BuiltIn, filter, hasFilter));
            if (builtIn != null)
            {
                rows.Add(Header(BuiltInGroup));
                rows.AddRange(builtIn);
            }

            // Mapped controls second: each role's live property, matched on the role name so
            // the filter reads naturally ("shift" finds Up Shift). A picked mapped property is
            // represented by its live Control Mapper property name.
            List<PickerRow> mapped = null;
            foreach (var role in _mappedRoles)
                if (!hasFilter || Match(role, filter))
                    (mapped ?? (mapped = new List<PickerRow>())).Add(
                        MappedRow(role, filter, hasFilter));
            if (mapped != null)
            {
                rows.Add(Header(MappedGroup));
                rows.AddRange(mapped);
            }

            foreach (var group in _groups)
            {
                List<PickerRow> items = null;
                foreach (var name in group.Value)
                    if (!hasFilter || Match(name, filter))
                        (items ?? (items = new List<PickerRow>())).Add(
                            PropertyRow(name, name, PropertyKind.SimHubProperty, filter, hasFilter));
                if (items != null)
                {
                    rows.Add(Header(group.Key));
                    rows.AddRange(items);
                }
            }
            return rows;
        }

        // Empty-filter content of one root rail (pinned curated group or auto SimHub group).
        // BuiltInGroup merges: curated built-ins first, then any same-named auto-group
        // (FanaBridge.*) beneath it with its own header so plugin properties stay reachable.
        private IReadOnlyList<PickerRow> RootRows(string rootName)
        {
            if (string.IsNullOrEmpty(rootName))
                return Array.Empty<PickerRow>();

            if (string.Equals(rootName, BuiltInGroup, StringComparison.Ordinal))
            {
                var rows = new List<PickerRow>();
                if (_builtIns.Count > 0)
                {
                    rows.Add(Header(BuiltInGroup));
                    foreach (var name in _builtIns)
                        rows.Add(PropertyRow(name, name, PropertyKind.BuiltIn, null, false));
                }
                AppendAutoGroupRows(rows, BuiltInGroup);
                return rows;
            }

            if (string.Equals(rootName, MappedGroup, StringComparison.Ordinal))
            {
                if (_mappedRoles.Count == 0)
                    return Array.Empty<PickerRow>();
                var rows = new List<PickerRow>(_mappedRoles.Count + 1) { Header(MappedGroup) };
                foreach (var role in _mappedRoles)
                    rows.Add(MappedRow(role, null, false));
                return rows;
            }

            foreach (var group in _groups)
            {
                if (!string.Equals(group.Key, rootName, StringComparison.Ordinal))
                    continue;
                if (group.Value.Count == 0)
                    return Array.Empty<PickerRow>();
                var rows = new List<PickerRow>(group.Value.Count + 1) { Header(group.Key) };
                foreach (var name in group.Value)
                    rows.Add(PropertyRow(name, name, PropertyKind.SimHubProperty, null, false));
                return rows;
            }

            return Array.Empty<PickerRow>();
        }

        // Append the auto-catalog group that shares a pinned name (if any), with its own header.
        private void AppendAutoGroupRows(List<PickerRow> rows, string groupName)
        {
            foreach (var group in _groups)
            {
                if (!string.Equals(group.Key, groupName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (group.Value.Count == 0)
                    return;
                rows.Add(Header(group.Key));
                foreach (var name in group.Value)
                    rows.Add(PropertyRow(name, name, PropertyKind.SimHubProperty, null, false));
                return;
            }
        }

        private static bool IsPinnedGroupName(string name)
            => string.Equals(name, BuiltInGroup, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, MappedGroup, StringComparison.OrdinalIgnoreCase);

        // Favorites / Recents / ITM pages: stored order, optional headers (never for those
        // three rails). Names no longer in the catalog still appear.
        private IReadOnlyList<PickerRow> NamedListRows(
            IReadOnlyList<string> names, bool withHeaders, string filter)
        {
            bool hasFilter = !string.IsNullOrEmpty(filter);
            var rows = new List<PickerRow>();
            foreach (var name in names)
            {
                if (hasFilter && !Match(name, filter))
                    continue;
                var kind = _builtInSet.Contains(name) ? PropertyKind.BuiltIn : PropertyKind.SimHubProperty;
                rows.Add(PropertyRow(name, name, kind, filter, hasFilter));
            }
            if (withHeaders && rows.Count > 0)
            {
                // Reserved for a future grouped named-list; current rails pass false.
            }
            return rows;
        }

        private PickerRow PropertyRow(string text, string propertyName, PropertyKind kind,
            string filter, bool hasFilter)
        {
            int matchStart = -1;
            int matchLength = 0;
            if (hasFilter)
            {
                // Span over the formatted DISPLAY text (concatenation of the grammar runs),
                // not the raw property name — OverlayHighlight applies to collapsed runs
                // (GameData. / ControlMapper.). First case-insensitive hit in the display
                // form; if the filter only matched a collapsed-away raw prefix the row still
                // lists (Match() uses the raw name) but MatchStart stays -1 (no highlight).
                string display = PropertyGrammar.FullText(
                    propertyName, PropertyGrammar.KindFor(kind));
                matchStart = display.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
                if (matchStart >= 0)
                    matchLength = filter.Length;
            }

            return new PickerRow
            {
                Kind = PickerRowKind.Property,
                Text = text,
                PropertyName = propertyName,
                PropertyKind = kind,
                IsFavorite = _favoriteSet.Contains(propertyName),
                MatchStart = matchStart,
                MatchLength = matchLength,
            };
        }

        private PickerRow MappedRow(string role, string filter, bool hasFilter)
        {
            string propertyName = "InputStatus.ControlMapperPlugin." + role;
            int matchStart = -1;
            int matchLength = 0;
            if (hasFilter)
            {
                // Match on the role text; highlight against the collapsed display form
                // ("ControlMapper." + role). Prefer the role portion so a filter like
                // "control" hits "Control Mode", not the "ControlMapper." namespace prefix;
                // fall back to a whole-label search only when the role portion misses.
                int roleIdx = role.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
                if (roleIdx >= 0)
                {
                    string display = PropertyGrammar.FullText(
                        propertyName, PropertyDisplayKind.SimHubProperty);
                    int roleStart = display.Length >= role.Length
                        ? display.Length - role.Length
                        : 0;
                    int displayIdx = roleStart < display.Length
                        ? display.IndexOf(filter, roleStart, StringComparison.OrdinalIgnoreCase)
                        : -1;
                    if (displayIdx < 0)
                        displayIdx = display.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
                    if (displayIdx < 0)
                        displayIdx = roleStart + roleIdx;
                    matchStart = displayIdx;
                    matchLength = filter.Length;
                }
            }

            return new PickerRow
            {
                Kind = PickerRowKind.Property,
                Text = role,
                PropertyName = propertyName,
                PropertyKind = PropertyKind.SimHubProperty,
                IsFavorite = _favoriteSet.Contains(propertyName),
                MatchStart = matchStart,
                MatchLength = matchLength,
            };
        }

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
