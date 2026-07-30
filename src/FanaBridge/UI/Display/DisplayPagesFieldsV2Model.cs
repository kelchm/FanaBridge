using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure Pages &amp; Fields (v2) projection — no WPF. Boards 5c/5d/5g/5p with the
    /// 8c field-filter redesign. Structure follows e9-final-wave-digest Surface A;
    /// §DIVERGENCES D1–D15 / F1–F10 are sanctioned. Writes go through
    /// <see cref="DisplayConfigV2EditSession"/> (never raw document mutation).
    /// </summary>
    public sealed class DisplayPagesFieldsV2Model
    {
        private static readonly IReadOnlyList<PagesFieldsPageButtonModel> NoPages =
            new ReadOnlyCollection<PagesFieldsPageButtonModel>(
                Array.Empty<PagesFieldsPageButtonModel>());

        private static readonly IReadOnlyList<PagesFieldsPreviewHitModel> NoHits =
            new ReadOnlyCollection<PagesFieldsPreviewHitModel>(
                Array.Empty<PagesFieldsPreviewHitModel>());

        private static readonly IReadOnlyList<PagesFieldsScopeGroupModel> NoGroups =
            new ReadOnlyCollection<PagesFieldsScopeGroupModel>(
                Array.Empty<PagesFieldsScopeGroupModel>());

        private static readonly IReadOnlyList<PagesFieldsFieldSectionModel> NoSections =
            new ReadOnlyCollection<PagesFieldsFieldSectionModel>(
                Array.Empty<PagesFieldsFieldSectionModel>());

        private static readonly IReadOnlyList<PagesFieldsOverrideRowModel> NoOverrides =
            new ReadOnlyCollection<PagesFieldsOverrideRowModel>(
                Array.Empty<PagesFieldsOverrideRowModel>());

        private static readonly IReadOnlyList<PagesFieldsEntrypointRowModel> NoEntrypoints =
            new ReadOnlyCollection<PagesFieldsEntrypointRowModel>(
                Array.Empty<PagesFieldsEntrypointRowModel>());

        private static readonly IReadOnlyList<string> NoFormats =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        private static readonly IReadOnlyList<PagesFieldsRotationItemModel> NoRotation =
            new ReadOnlyCollection<PagesFieldsRotationItemModel>(
                Array.Empty<PagesFieldsRotationItemModel>());

        /// <summary>
        /// Rebuild the Pages &amp; Fields projection. Null config yields a minimal empty
        /// model. Selection/focus are view state inputs; focus never touches the document.
        /// </summary>
        /// <param name="selectedPageKey">
        /// Stable page key: <c>itm:{catalogPageId}</c> or <c>hosted:{id}</c>. Null picks
        /// the first ITM page (then first hosted).
        /// </param>
        /// <param name="focusedParamId">
        /// Focused field identity. Focus = filter (8c items 3–5). Cleared when the
        /// selected page does not place it (with announcement for shared fields — D10).
        /// </param>
        public static DisplayPagesFieldsV2Model Project(
            DisplayConfigV2 config,
            DisplayResolutionSnapshotModel resolution,
            DisplayValuesSnapshot values,
            DisplayType displayType,
            WheelCatalog catalog = null,
            AliasTable aliases = null,
            string selectedPageKey = null,
            ushort? focusedParamId = null)
        {
            resolution = resolution ?? DisplayResolutionSnapshotModel.Empty;
            bool isItm = displayType == DisplayType.Itm;
            var mode = config?.Settings?.Mode ?? SettingsMode.On;

            // A-O3 PROVISIONAL: Off / Legacy Only / disconnected — Overview O1 /
            // Priority Q1 verbatim.
            bool modeOff = mode == SettingsMode.Off;
            bool legacyOnly = mode == SettingsMode.LegacyOnly;
            bool disconnected = !resolution.IsConnected;

            string surfaceWord = isItm ? DisplayCopy.ItmDisplay : DisplayCopy.SegmentDisplay;
            string situation = resolution.InGame ? DisplayCopy.InGame : DisplayCopy.SituationIdle;

            string resolvedPageKey = null;
            var pageButtons = modeOff
                ? NoPages
                : BuildPageStrip(config, catalog, selectedPageKey, out resolvedPageKey);

            var selectedPage = FindSelectedPage(pageButtons, resolvedPageKey);

            // Focus survival / clear (D10 / 8c item 9).
            ushort? effectiveFocus = focusedParamId;
            string focusClearAnnouncement = null;
            if (effectiveFocus.HasValue && selectedPage != null && catalog != null)
            {
                if (!PagePlacesParam(catalog, selectedPage.CatalogPageId, effectiveFocus.Value))
                {
                    string clearedName = FieldDisplayName(
                        catalog, config, effectiveFocus.Value);
                    focusClearAnnouncement =
                        DisplayCopy.SharedFocusClearedOnThisPage(clearedName);
                    effectiveFocus = null;
                }
            }
            else if (effectiveFocus.HasValue && selectedPage != null && catalog == null)
            {
                // No catalog: cannot prove placement; keep focus (fail open for view state).
            }
            else if (effectiveFocus.HasValue && selectedPage == null)
            {
                effectiveFocus = null;
            }

            var previewHits = modeOff || selectedPage == null || !selectedPage.IsItm
                ? NoHits
                : BuildPreviewHits(
                    catalog, selectedPage.CatalogPageId, effectiveFocus, values);

            IReadOnlyList<PagesFieldsFieldSectionModel> flatSections = NoSections;
            string filterStateLine = null;
            int filterIndex = 0;
            int filterCount = 0;
            var scopeGroups = modeOff || selectedPage == null
                ? NoGroups
                : BuildScopeGroups(
                    config, catalog, aliases, selectedPage, effectiveFocus,
                    out flatSections, out filterStateLine,
                    out filterIndex, out filterCount);

            // When focused, collection is a single section (siblings leave) — D5.
            // Group headers still frame the focused section's scope when catalog-backed.
            if (effectiveFocus.HasValue && scopeGroups.Count > 0)
            {
                scopeGroups = FilterGroupsToFocus(scopeGroups, effectiveFocus.Value);
            }

            var entrypoints = modeOff || selectedPage == null
                ? NoEntrypoints
                : BuildEntrypointsToPage(config, catalog, aliases, resolution, selectedPage);

            var rotationIn = modeOff
                ? NoRotation
                : BuildRotationLists(config, catalog, inRotation: true);
            var rotationOut = modeOff
                ? NoRotation
                : BuildRotationLists(config, catalog, inRotation: false);

            string whereBody = null;
            string thisPageBody = null;
            string thisWheelBody = null;
            if (selectedPage != null)
            {
                whereBody = DisplayCopy.WhereThisAppliesBody(
                    selectedPage.Badge, selectedPage.Name);
                thisPageBody = selectedPage.IsItm
                    ? DisplayCopy.ThisPageBody(selectedPage.Name, selectedPage.FirmwareIndex)
                    : null;
                if (effectiveFocus.HasValue)
                    thisWheelBody = BuildThisWheelEnvelope(catalog, effectiveFocus.Value);
            }

            return new DisplayPagesFieldsV2Model(
                surfaceWord: surfaceWord,
                situationCopy: situation,
                inGame: resolution.InGame,
                isConnected: resolution.IsConnected,
                isItmWheel: isItm,
                mode: mode,
                showContent: !modeOff,
                modeOffEmptyState: modeOff ? DisplayCopy.ModeOffEmptyState : null,
                legacyOnly: legacyOnly,
                disconnected: disconnected,
                pageButtons: pageButtons,
                selectedPageKey: resolvedPageKey,
                selectedPage: selectedPage,
                stripNote: DisplayCopy.StripHostedNote,
                previewHits: previewHits,
                previewCaption: DisplayCopy.PreviewLayoutFixedHint,
                previewWatermark: DisplayCopy.PreviewWatermark,
                focusedParamId: effectiveFocus,
                filterStateLine: filterStateLine,
                filterIndex: filterIndex,
                filterCount: filterCount,
                focusClearAnnouncement: focusClearAnnouncement,
                scopeGroups: scopeGroups,
                flatSections: flatSections ?? NoSections,
                whereThisAppliesBody: whereBody,
                thisWheelBody: thisWheelBody,
                thisPageBody: thisPageBody,
                entrypoints: entrypoints,
                entrypointsCountLabel: DisplayCopy.ReadOnlyHereCount(entrypoints.Count),
                rotationIn: rotationIn,
                rotationOut: rotationOut,
                values: values);
        }

        private DisplayPagesFieldsV2Model(
            string surfaceWord,
            string situationCopy,
            bool inGame,
            bool isConnected,
            bool isItmWheel,
            SettingsMode mode,
            bool showContent,
            string modeOffEmptyState,
            bool legacyOnly,
            bool disconnected,
            IReadOnlyList<PagesFieldsPageButtonModel> pageButtons,
            string selectedPageKey,
            PagesFieldsPageButtonModel selectedPage,
            string stripNote,
            IReadOnlyList<PagesFieldsPreviewHitModel> previewHits,
            string previewCaption,
            string previewWatermark,
            ushort? focusedParamId,
            string filterStateLine,
            int filterIndex,
            int filterCount,
            string focusClearAnnouncement,
            IReadOnlyList<PagesFieldsScopeGroupModel> scopeGroups,
            IReadOnlyList<PagesFieldsFieldSectionModel> flatSections,
            string whereThisAppliesBody,
            string thisWheelBody,
            string thisPageBody,
            IReadOnlyList<PagesFieldsEntrypointRowModel> entrypoints,
            string entrypointsCountLabel,
            IReadOnlyList<PagesFieldsRotationItemModel> rotationIn,
            IReadOnlyList<PagesFieldsRotationItemModel> rotationOut,
            DisplayValuesSnapshot values)
        {
            SurfaceWord = surfaceWord;
            SituationCopy = situationCopy;
            InGame = inGame;
            IsConnected = isConnected;
            IsItmWheel = isItmWheel;
            Mode = mode;
            ShowContent = showContent;
            ModeOffEmptyState = modeOffEmptyState;
            LegacyOnly = legacyOnly;
            Disconnected = disconnected;
            PageButtons = pageButtons ?? NoPages;
            SelectedPageKey = selectedPageKey;
            SelectedPage = selectedPage;
            StripNote = stripNote;
            PreviewHits = previewHits ?? NoHits;
            PreviewCaption = previewCaption;
            PreviewWatermark = previewWatermark;
            FocusedParamId = focusedParamId;
            FilterStateLine = filterStateLine;
            FilterIndex = filterIndex;
            FilterCount = filterCount;
            FocusClearAnnouncement = focusClearAnnouncement;
            ScopeGroups = scopeGroups ?? NoGroups;
            FlatSections = flatSections ?? NoSections;
            WhereThisAppliesBody = whereThisAppliesBody;
            ThisWheelBody = thisWheelBody;
            ThisPageBody = thisPageBody;
            Entrypoints = entrypoints ?? NoEntrypoints;
            EntrypointsCountLabel = entrypointsCountLabel;
            RotationIn = rotationIn ?? NoRotation;
            RotationOut = rotationOut ?? NoRotation;
            Values = values;
        }

        // ── Header ───────────────────────────────────────────────────────

        public string SurfaceWord { get; }
        public string SituationCopy { get; }
        public bool InGame { get; }
        public bool IsConnected { get; }
        public bool IsItmWheel { get; }
        public SettingsMode Mode { get; }
        public bool ShowContent { get; }
        public string ModeOffEmptyState { get; }
        public bool LegacyOnly { get; }
        public bool Disconnected { get; }

        // ── Page strip ───────────────────────────────────────────────────

        public IReadOnlyList<PagesFieldsPageButtonModel> PageButtons { get; }
        public string SelectedPageKey { get; }
        public PagesFieldsPageButtonModel SelectedPage { get; }
        public string StripNote { get; }

        // ── Preview (selection map) ──────────────────────────────────────

        public IReadOnlyList<PagesFieldsPreviewHitModel> PreviewHits { get; }
        public string PreviewCaption { get; }
        public string PreviewWatermark { get; }

        // ── Focus / filter ───────────────────────────────────────────────

        /// <summary>Effective focused param after page-switch survival rules.</summary>
        public ushort? FocusedParamId { get; }

        /// <summary>
        /// Sticky named state line (only while focused). Null when showing all fields
        /// — D3/D4: all-fields is the absence of this line, never a named mode.
        /// </summary>
        public string FilterStateLine { get; }

        public int FilterIndex { get; }
        public int FilterCount { get; }

        /// <summary>
        /// One-line announcement when focus cleared on a non-placing page (D10).
        /// Null otherwise. View shows and clears on next interaction.
        /// </summary>
        public string FocusClearAnnouncement { get; }

        /// <summary>True when a field is focused (filter active).</summary>
        public bool IsFiltered => FocusedParamId.HasValue;

        // ── Field collection ─────────────────────────────────────────────

        /// <summary>
        /// D14 scope groups (SHARED then THIS PAGE). Empty when no catalog (flat
        /// collection via <see cref="FlatSections"/>).
        /// </summary>
        public IReadOnlyList<PagesFieldsScopeGroupModel> ScopeGroups { get; }

        /// <summary>
        /// Flat sections when no catalog (fail-closed: no grouping, no reach lines).
        /// Empty when groups are used.
        /// </summary>
        public IReadOnlyList<PagesFieldsFieldSectionModel> FlatSections { get; }

        public string WhereThisAppliesBody { get; }
        public string ThisWheelBody { get; }
        public string ThisPageBody { get; }

        public IReadOnlyList<PagesFieldsEntrypointRowModel> Entrypoints { get; }
        public string EntrypointsCountLabel { get; }

        public IReadOnlyList<PagesFieldsRotationItemModel> RotationIn { get; }
        public IReadOnlyList<PagesFieldsRotationItemModel> RotationOut { get; }

        public DisplayValuesSnapshot Values { get; }

        // ── Selection / filter pure helpers (test seams) ─────────────────

        /// <summary>
        /// Apply a preview-hit or section-header click. Re-clicking the focused field
        /// clears (clear route 2). Returns the next focused param (null = all fields).
        /// </summary>
        public static ushort? ToggleFocus(ushort? currentFocus, ushort clickedParamId)
        {
            if (currentFocus.HasValue && currentFocus.Value == clickedParamId)
                return null;
            return clickedParamId;
        }

        /// <summary>Clear route 1 (named action) / 3 (empty chrome) / 4 (Esc).</summary>
        public static ushort? ClearFocus() => null;

        /// <summary>
        /// Whether shared focus survives a page switch to <paramref name="newCatalogPageId"/>.
        /// </summary>
        public static bool FocusSurvivesPageSwitch(
            WheelCatalog catalog, ushort focusedParamId, string newCatalogPageId)
            => PagePlacesParam(catalog, newCatalogPageId, focusedParamId);

        // ── Page strip ───────────────────────────────────────────────────

        private static IReadOnlyList<PagesFieldsPageButtonModel> BuildPageStrip(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            string preferredKey,
            out string resolvedKey)
        {
            resolvedKey = null;
            var list = new List<PagesFieldsPageButtonModel>();
            var rotationIndex = IndexPageOrder(config);

            // ITM pages first (catalog order when available; document Pages as overlay).
            if (catalog?.Itm?.Pages != null)
            {
                for (int i = 0; i < catalog.Itm.Pages.Count; i++)
                {
                    var cp = catalog.Itm.Pages[i];
                    if (cp == null || string.IsNullOrEmpty(cp.Id))
                        continue;

                    // Document Removed flag excludes the page.
                    if (IsItmPageRemoved(config, cp.Id))
                        continue;

                    string name = ResolveItmPageName(config, cp);
                    string key = "itm:" + cp.Id;
                    int? step = null;
                    if (rotationIndex.TryGetValue(key, out int s))
                        step = s;

                    bool legacyDim = false; // applied at view from model.LegacyOnly
                    list.Add(new PagesFieldsPageButtonModel(
                        key: key,
                        name: name,
                        badge: DisplayCopy.ItmPageBadge(cp.Index),
                        isItm: true,
                        catalogPageId: cp.Id,
                        hostedPageId: null,
                        firmwareIndex: cp.Index,
                        rotationStep: step,
                        isSelected: false,
                        isDimmed: legacyDim));
                }
            }
            else if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var pe = config.Pages[i];
                    if (pe == null || pe.Kind != PageEntryKind.ItmPage || pe.Removed)
                        continue;
                    if (string.IsNullOrEmpty(pe.CatalogPageId))
                        continue;
                    string key = "itm:" + pe.CatalogPageId;
                    int? step = null;
                    if (rotationIndex.TryGetValue(key, out int s))
                        step = s;
                    list.Add(new PagesFieldsPageButtonModel(
                        key: key,
                        name: pe.NameOverride ?? pe.CatalogPageId,
                        badge: DisplayCopy.ItmBadge,
                        isItm: true,
                        catalogPageId: pe.CatalogPageId,
                        hostedPageId: null,
                        firmwareIndex: 0,
                        rotationStep: step,
                        isSelected: false,
                        isDimmed: false));
                }
            }

            // Hosted after divider.
            if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var pe = config.Pages[i];
                    if (pe == null || pe.Kind != PageEntryKind.HostedPage)
                        continue;
                    if (string.IsNullOrEmpty(pe.Id))
                        continue;
                    string key = "hosted:" + pe.Id;
                    int? step = null;
                    if (rotationIndex.TryGetValue(key, out int s))
                        step = s;
                    list.Add(new PagesFieldsPageButtonModel(
                        key: key,
                        name: pe.Name ?? pe.Id,
                        badge: DisplayCopy.LegacyBadge,
                        isItm: false,
                        catalogPageId: null,
                        hostedPageId: pe.Id,
                        firmwareIndex: 0,
                        rotationStep: step,
                        isSelected: false,
                        isDimmed: false));
                }
            }

            // Resolve selection.
            if (list.Count == 0)
            {
                resolvedKey = null;
                return NoPages;
            }

            int selectedIndex = 0;
            if (!string.IsNullOrEmpty(preferredKey))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].Key, preferredKey, StringComparison.Ordinal))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            resolvedKey = list[selectedIndex].Key;
            // Rebuild selected flag immutably.
            var result = new List<PagesFieldsPageButtonModel>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                result.Add(new PagesFieldsPageButtonModel(
                    p.Key, p.Name, p.Badge, p.IsItm, p.CatalogPageId, p.HostedPageId,
                    p.FirmwareIndex, p.RotationStep, isSelected: i == selectedIndex,
                    p.IsDimmed));
            }
            return new ReadOnlyCollection<PagesFieldsPageButtonModel>(result);
        }

        private static PagesFieldsPageButtonModel FindSelectedPage(
            IReadOnlyList<PagesFieldsPageButtonModel> pages, string key)
        {
            if (pages == null || string.IsNullOrEmpty(key))
                return null;
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i] != null
                    && string.Equals(pages[i].Key, key, StringComparison.Ordinal))
                    return pages[i];
            }
            return null;
        }

        private static Dictionary<string, int> IndexPageOrder(DisplayConfigV2 config)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (config?.PageOrder == null)
                return map;
            int step = 0;
            for (int i = 0; i < config.PageOrder.Count; i++)
            {
                var r = config.PageOrder[i];
                if (r == null) continue;
                string key = PageKey(r);
                if (key == null || map.ContainsKey(key))
                    continue;
                step++;
                map[key] = step;
            }
            return map;
        }

        private static string PageKey(PageRef r)
        {
            if (r == null) return null;
            if (r.Kind == PageRefKind.ItmPage && !string.IsNullOrEmpty(r.CatalogPageId))
                return "itm:" + r.CatalogPageId;
            if (r.Kind == PageRefKind.HostedPage && !string.IsNullOrEmpty(r.Id))
                return "hosted:" + r.Id;
            return null;
        }

        private static bool IsItmPageRemoved(DisplayConfigV2 config, string catalogPageId)
        {
            if (config?.Pages == null) return false;
            for (int i = 0; i < config.Pages.Count; i++)
            {
                var pe = config.Pages[i];
                if (pe != null
                    && pe.Kind == PageEntryKind.ItmPage
                    && string.Equals(pe.CatalogPageId, catalogPageId, StringComparison.Ordinal)
                    && pe.Removed)
                    return true;
            }
            return false;
        }

        private static string ResolveItmPageName(DisplayConfigV2 config, CatalogPage cp)
        {
            if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var pe = config.Pages[i];
                    if (pe != null
                        && pe.Kind == PageEntryKind.ItmPage
                        && string.Equals(pe.CatalogPageId, cp.Id, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(pe.NameOverride))
                        return pe.NameOverride;
                }
            }
            return cp.Name ?? cp.Id;
        }

        // ── Preview hits ─────────────────────────────────────────────────

        private static IReadOnlyList<PagesFieldsPreviewHitModel> BuildPreviewHits(
            WheelCatalog catalog,
            string catalogPageId,
            ushort? focusedParamId,
            DisplayValuesSnapshot values)
        {
            if (catalog?.Itm?.Pages == null || string.IsNullOrEmpty(catalogPageId))
                return NoHits;

            CatalogPage page = null;
            for (int i = 0; i < catalog.Itm.Pages.Count; i++)
            {
                var p = catalog.Itm.Pages[i];
                if (p != null && string.Equals(p.Id, catalogPageId, StringComparison.Ordinal))
                {
                    page = p;
                    break;
                }
            }
            if (page?.Placements == null)
                return NoHits;

            var defs = CatalogFields.IndexByLogicalId(catalog);
            // A-O6: hit regions key by drawn region (row/col); first-placed wins.
            // Second field on the same region is reachable only via its section header.
            var list = new List<PagesFieldsPreviewHitModel>();
            var seenRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < page.Placements.Count; i++)
            {
                var pl = page.Placements[i];
                if (pl == null || string.IsNullOrEmpty(pl.Field))
                    continue;
                if (!defs.TryGetValue(pl.Field, out var def) || def == null)
                    continue;

                string row = pl.Region?.Row ?? string.Empty;
                string col = pl.Region?.Column ?? string.Empty;
                string regionKey = row + "\u001f" + col;
                if (!seenRegions.Add(regionKey))
                    continue; // A-O6: first-placed wins the drawn region

                bool picked = focusedParamId.HasValue && focusedParamId.Value == def.ParamId;
                // D6/D7: outlines always visible; picked = solid 2 px, else dashed.
                list.Add(new PagesFieldsPreviewHitModel(
                    paramId: def.ParamId,
                    logicalId: def.Id,
                    row: row,
                    column: col,
                    isPicked: picked,
                    displayName: FieldDisplayNameFromDef(def)));
            }
            return new ReadOnlyCollection<PagesFieldsPreviewHitModel>(list);
        }

        // ── Field collection / scope groups ──────────────────────────────

        private static IReadOnlyList<PagesFieldsScopeGroupModel> BuildScopeGroups(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases,
            PagesFieldsPageButtonModel selectedPage,
            ushort? focusedParamId,
            out IReadOnlyList<PagesFieldsFieldSectionModel> flatSections,
            out string filterStateLine,
            out int filterIndex,
            out int filterCount)
        {
            flatSections = NoSections;
            filterStateLine = null;
            filterIndex = 0;
            filterCount = 0;

            var sections = BuildFieldSections(
                config, catalog, aliases, selectedPage);
            if (sections.Count == 0)
                return NoGroups;

            // Focus filter state line.
            if (focusedParamId.HasValue)
            {
                PagesFieldsFieldSectionModel focused = null;
                for (int i = 0; i < sections.Count; i++)
                {
                    if (sections[i].ParamId == focusedParamId.Value)
                    {
                        focused = sections[i];
                        break;
                    }
                }
                if (focused != null)
                {
                    if (focused.IsShared)
                    {
                        // Real reach (placed, total) — partial announces "2 of 5".
                        filterStateLine = DisplayCopy.FilterStateLineShared(
                            focused.DisplayName,
                            focused.PlacedCount,
                            focused.TotalItmPages);
                        filterIndex = 1;
                        filterCount = 1;
                    }
                    else
                    {
                        // Index scoped to the group (A.1.2 honesty).
                        int groupCount = 0;
                        int indexInGroup = 0;
                        for (int i = 0; i < sections.Count; i++)
                        {
                            if (sections[i].IsShared == focused.IsShared)
                            {
                                groupCount++;
                                if (sections[i].ParamId == focused.ParamId)
                                    indexInGroup = groupCount;
                            }
                        }
                        filterIndex = indexInGroup;
                        filterCount = groupCount;
                        filterStateLine = DisplayCopy.FilterStateLine(
                            focused.DisplayName, filterIndex, filterCount);
                    }
                }
            }

            // No catalog → flat collection, no reach lines (fail-closed).
            if (catalog == null)
            {
                flatSections = new ReadOnlyCollection<PagesFieldsFieldSectionModel>(sections);
                return NoGroups;
            }

            var shared = new List<PagesFieldsFieldSectionModel>();
            var thisPage = new List<PagesFieldsFieldSectionModel>();
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].IsShared)
                    shared.Add(sections[i]);
                else
                    thisPage.Add(sections[i]);
            }

            var groups = new List<PagesFieldsScopeGroupModel>();
            if (shared.Count > 0)
            {
                groups.Add(new PagesFieldsScopeGroupModel(
                    DisplayCopy.ScopeGroupShared,
                    new ReadOnlyCollection<PagesFieldsFieldSectionModel>(shared)));
            }
            if (thisPage.Count > 0)
            {
                groups.Add(new PagesFieldsScopeGroupModel(
                    DisplayCopy.ScopeGroupThisPage,
                    new ReadOnlyCollection<PagesFieldsFieldSectionModel>(thisPage)));
            }
            return new ReadOnlyCollection<PagesFieldsScopeGroupModel>(groups);
        }

        private static IReadOnlyList<PagesFieldsScopeGroupModel> FilterGroupsToFocus(
            IReadOnlyList<PagesFieldsScopeGroupModel> groups, ushort focusedParamId)
        {
            var result = new List<PagesFieldsScopeGroupModel>();
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                PagesFieldsFieldSectionModel match = null;
                for (int s = 0; s < group.Sections.Count; s++)
                {
                    if (group.Sections[s].ParamId == focusedParamId)
                    {
                        match = group.Sections[s];
                        break;
                    }
                }
                if (match != null)
                {
                    result.Add(new PagesFieldsScopeGroupModel(
                        group.Header,
                        new ReadOnlyCollection<PagesFieldsFieldSectionModel>(
                            new[] { match })));
                    break;
                }
            }
            return new ReadOnlyCollection<PagesFieldsScopeGroupModel>(result);
        }

        private static List<PagesFieldsFieldSectionModel> BuildFieldSections(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases,
            PagesFieldsPageButtonModel selectedPage)
        {
            var list = new List<PagesFieldsFieldSectionModel>();
            if (selectedPage == null)
                return list;

            // Hosted pages: no firmware field layout — empty collection (base still
            // would apply for layer pages; not in Surface A field filter).
            if (!selectedPage.IsItm || string.IsNullOrEmpty(selectedPage.CatalogPageId))
                return list;

            if (catalog == null)
            {
                // Document fields only, no reach/grouping.
                if (config?.Fields != null)
                {
                    foreach (var kv in config.Fields)
                    {
                        if (kv.Value == null) continue;
                        list.Add(ProjectFieldSection(
                            kv.Key, kv.Value, config, catalog: null, aliases,
                            placed: 1, total: 1, isShared: false,
                            isInertCollision: false, inertReason: null));
                    }
                }
                return list;
            }

            var onPage = CatalogFields.ParamsOnPage(catalog, selectedPage.CatalogPageId);
            var seen = new HashSet<ushort>();

            for (int i = 0; i < onPage.Count; i++)
            {
                ushort paramId = onPage[i];
                if (!seen.Add(paramId))
                    continue;

                int placed = 1, total = catalog.Itm?.Pages?.Count ?? 1;
                CatalogFields.TryGetReachByParam(catalog, paramId, out placed, out total);
                bool isShared = placed > 1;

                var entry = FieldLadderMap.FindEntry(config, catalog, paramId);
                // Empty base section when no authored entry — still render (A.1 undrawn).
                if (entry == null)
                    entry = new FieldEntry();

                // A-O4: inert side of S1 collision.
                bool inert = false;
                string inertReason = null;
                if (config?.Fields != null
                    && config.Fields.TryGetValue(paramId, out var pageSide)
                    && pageSide != null
                    && pageSide.DegradedAtLoad
                    && isShared)
                {
                    // Shared wins; page-side is inert — surface it separately below.
                    inert = false; // the winner is not inert
                }

                list.Add(ProjectFieldSection(
                    paramId, entry, config, catalog, aliases,
                    placed, total, isShared, inert, inertReason));

                // Surface inert page-side when present (honesty §B8).
                if (config?.Fields != null
                    && config.Fields.TryGetValue(paramId, out var inertEntry)
                    && inertEntry != null
                    && inertEntry.DegradedAtLoad
                    && isShared)
                {
                    list.Add(ProjectFieldSection(
                        paramId, inertEntry, config, catalog, aliases,
                        placed, total, isShared: true,
                        isInertCollision: true,
                        inertReason: inertEntry.DegradeReason));
                }
            }

            return list;
        }

        private static PagesFieldsFieldSectionModel ProjectFieldSection(
            ushort paramId,
            FieldEntry entry,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases,
            int placed,
            int total,
            bool isShared,
            bool isInertCollision,
            string inertReason)
        {
            string name = FieldDisplayName(catalog, config, paramId);
            string reachLine = null;
            if (isShared && !isInertCollision && catalog != null && placed > 0)
                reachLine = DisplayCopy.ReachLine(placed, total);

            bool provisional = false;
            string capabilityHint = null;
            var def = CatalogFields.FindDefinitionByParam(catalog, paramId);
            if (def != null)
            {
                provisional = def.Provisional == true;
                capabilityHint = BuildCapabilityHint(def);
            }

            bool locked = FieldEnvelope.IsLocked(catalog, paramId);
            var offered = FieldEnvelope.OfferedFormats(catalog, paramId);

            var overrides = new List<PagesFieldsOverrideRowModel>();
            if (entry?.Overrides != null)
            {
                for (int i = 0; i < entry.Overrides.Count; i++)
                {
                    var ov = entry.Overrides[i];
                    if (ov == null) continue;
                    overrides.Add(ProjectOverrideRow(i + 1, ov, aliases));
                }
            }

            var baseModel = ProjectBase(entry?.Base, offered);

            return new PagesFieldsFieldSectionModel(
                paramId: paramId,
                logicalId: def?.Id,
                displayName: name,
                capabilityHint: capabilityHint,
                reachLine: reachLine,
                isShared: isShared,
                placedCount: placed,
                totalItmPages: total,
                isProvisional: provisional,
                isLocked: locked,
                isInertCollision: isInertCollision,
                inertReason: inertReason,
                overrides: new ReadOnlyCollection<PagesFieldsOverrideRowModel>(overrides),
                baseBlock: baseModel,
                offeredFormats: offered == null || offered.Count == 0
                    ? NoFormats
                    : offered,
                suffixWidth: def?.Suffix?.Supported == false ? 0 : def?.Suffix?.Width);
        }

        private static PagesFieldsOverrideRowModel ProjectOverrideRow(
            int rank, FieldOverride ov, AliasTable aliases)
        {
            string writesChip = WritesChip(ov.Writes);
            string contentText = ov.Content?.Text
                ?? ov.Content?.Source?.Name
                ?? string.Empty;
            string condition = ConditionSentence.From(ov.Condition, ov.Lifetime, aliases);
            if (ov.Lifetime != null)
            {
                condition += DisplayCopy.LifetimeLadderSuffix(
                    ov.Lifetime.Kind,
                    ov.Lifetime.Kind == LifetimeKind.ForDuration
                        ? ov.Lifetime.DurationMs
                        : 0);
            }

            return new PagesFieldsOverrideRowModel(
                overrideId: ov.Id,
                rank: rank,
                writesChip: writesChip,
                contentChip: contentText,
                conditionSentence: condition,
                actsAsEntrypoint: ov.ActsAsEntrypoint && !ov.ActsAsEntrypointIgnored,
                enabled: ov.EffectivelyEnabled,
                degraded: ov.DegradedAtLoad);
        }

        private static string WritesChip(FieldWrites writes)
        {
            switch (writes)
            {
                case FieldWrites.Suffix:
                    return DisplayCopy.WritesSuffix;
                case FieldWrites.Value:
                    return DisplayCopy.TheValue;
                case FieldWrites.Both:
                    return DisplayCopy.TheValue + " · " + DisplayCopy.WritesSuffix;
                default:
                    return DisplayCopy.WritesSuffix;
            }
        }

        private static PagesFieldsBaseBlockModel ProjectBase(
            FieldBase bas, IReadOnlyList<string> offeredFormats)
        {
            return new PagesFieldsBaseBlockModel(
                sourceName: bas?.Source?.Name,
                sourceKind: bas?.Source?.Kind ?? ValueSourceKind.Unknown,
                format: bas?.Format,
                baseSuffix: bas?.BaseSuffix,
                offeredFormats: offeredFormats ?? NoFormats);
        }

        private static string BuildCapabilityHint(CatalogFieldDefinition def)
        {
            if (def == null) return null;
            var parts = new List<string>();
            if (def.Suffix != null)
            {
                if (def.Suffix.Supported == false)
                    parts.Add(DisplayCopy.NoSuffixRegion);
                else if (def.Suffix.Width.HasValue)
                {
                    parts.Add(def.Suffix.Width.Value == 1
                        ? "1 suffix char"
                        : string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} suffix chars",
                            def.Suffix.Width.Value));
                }
                else if (def.Suffix.Supported == true)
                {
                    parts.Add(DisplayCopy.SuffixWidthUntested);
                }
            }
            if (def.Value != null)
            {
                if (def.Value.Numeric == true)
                    parts.Add(DisplayCopy.ValueKindNumbers);
                else if (def.Value.Ascii == true)
                    parts.Add(DisplayCopy.ValueKindText);
            }
            if (parts.Count == 0)
                return null;
            return string.Join(" · ", parts);
        }

        private static string BuildThisWheelEnvelope(WheelCatalog catalog, ushort paramId)
        {
            var def = CatalogFields.FindDefinitionByParam(catalog, paramId);
            if (def == null) return null;
            string valueKind = def.Value?.Numeric == true
                ? DisplayCopy.ValueKindNumbers
                : (def.Value?.Ascii == true
                    ? DisplayCopy.ValueKindText
                    : DisplayCopy.ValueKindNumbers);
            string name = FieldDisplayNameFromDef(def);

            // Honest tri-state: no region / measured width / supported-but-unmeasured.
            if (def.Suffix?.Supported == false)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: {1}; its value region takes {2}.",
                    name,
                    DisplayCopy.NoSuffixRegion,
                    valueKind);
            }
            int? suffixChars = def.Suffix?.Width;
            if (!suffixChars.HasValue)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: {1}; its value region takes {2}.",
                    name,
                    def.Suffix?.Supported == true
                        ? DisplayCopy.SuffixWidthUntested
                        : DisplayCopy.SuffixRegionUntested,
                    valueKind);
            }
            if (suffixChars.Value == 0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: {1}; its value region takes {2}.",
                    name,
                    DisplayCopy.NoSuffixRegion,
                    valueKind);
            }
            return DisplayCopy.ThisWheelEnvelope(name, suffixChars.Value, valueKind);
        }

        // ── Entrypoints to this page ─────────────────────────────────────

        private static IReadOnlyList<PagesFieldsEntrypointRowModel> BuildEntrypointsToPage(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases,
            DisplayResolutionSnapshotModel resolution,
            PagesFieldsPageButtonModel selectedPage)
        {
            if (config?.Priority == null || selectedPage == null)
                return NoEntrypoints;

            string pageKey = selectedPage.Key;
            var list = new List<PagesFieldsEntrypointRowModel>();
            var rows = config.Priority.EffectiveRows;
            string winnerId = FindDisplayWinnerCarrierId(resolution);

            int rank = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.Kind == PriorityRowKind.Manual)
                    continue;
                rank++;
                if (!RowTargetsPage(row, config, catalog, pageKey))
                    continue;

                string carrierId = row.Id;
                bool isWinner = !string.IsNullOrEmpty(winnerId)
                    && string.Equals(carrierId, winnerId, StringComparison.Ordinal);

                // F1: empty status cell for winner; structural highlight only.
                string status = isWinner
                    ? DisplayCopy.OnScreen
                    : (resolution.IsConnected ? DisplayCopy.Waiting : DisplayCopy.NoWheel);

                string detail = BuildEntrypointDetail(row, aliases);

                list.Add(new PagesFieldsEntrypointRowModel(
                    rank: rank,
                    detail: detail,
                    statusCopy: status,
                    isWinner: isWinner,
                    rowId: row.Id));
            }

            return new ReadOnlyCollection<PagesFieldsEntrypointRowModel>(list);
        }

        private static bool RowTargetsPage(
            PriorityRow row,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            string pageKey)
        {
            if (row?.Target == null)
                return false;
            if (row.Target.Kind == PageRefKind.Cycle)
            {
                // Cycle membership.
                if (config?.Cycles == null || string.IsNullOrEmpty(row.Target.Id))
                    return false;
                for (int c = 0; c < config.Cycles.Count; c++)
                {
                    var cy = config.Cycles[c];
                    if (cy == null || !string.Equals(cy.Id, row.Target.Id, StringComparison.Ordinal))
                        continue;
                    if (cy.Members == null) return false;
                    for (int m = 0; m < cy.Members.Count; m++)
                    {
                        if (string.Equals(PageKey(cy.Members[m]), pageKey, StringComparison.Ordinal))
                            return true;
                    }
                }
                return false;
            }
            return string.Equals(PageKey(row.Target), pageKey, StringComparison.Ordinal);
        }

        private static string BuildEntrypointDetail(PriorityRow row, AliasTable aliases)
        {
            if (row?.Summons != null && row.Summons.Count > 0)
            {
                var s = row.Summons[0];
                if (s?.Condition != null)
                {
                    string core = ConditionSentence.From(s.Condition, s.Lifetime, aliases);
                    if (s.Lifetime != null)
                    {
                        core += DisplayCopy.LifetimeLadderSuffix(
                            s.Lifetime.Kind,
                            s.Lifetime.Kind == LifetimeKind.ForDuration
                                ? s.Lifetime.DurationMs
                                : 0);
                    }
                    return core;
                }
            }
            return string.Empty;
        }

        private static string FindDisplayWinnerCarrierId(DisplayResolutionSnapshotModel resolution)
        {
            if (resolution?.SurfaceWinners == null)
                return null;
            for (int i = 0; i < resolution.SurfaceWinners.Count; i++)
            {
                var w = resolution.SurfaceWinners[i];
                if (w != null
                    && string.Equals(w.SurfaceId, SeatArbiter.DisplaySurfaceId, StringComparison.Ordinal))
                    return w.WinnerCarrierId;
            }
            return null;
        }

        // ── Rotation lists ───────────────────────────────────────────────

        private static IReadOnlyList<PagesFieldsRotationItemModel> BuildRotationLists(
            DisplayConfigV2 config, WheelCatalog catalog, bool inRotation)
        {
            var inKeys = new HashSet<string>(StringComparer.Ordinal);
            var inList = new List<PagesFieldsRotationItemModel>();

            // Tri-state pageOrder:
            //   null  → present the compiled default walk (not "no pages")
            //   []    → explicit empty (no members in rotation)
            //   list  → explicit membership/order
            if (config?.PageOrder == null)
            {
                var walk = WalkCompiler.Compile(
                    config ?? new DisplayConfigV2(), catalog);
                for (int i = 0; i < walk.DestinationIds.Count; i++)
                {
                    string key = walk.DestinationIds[i];
                    if (string.IsNullOrEmpty(key) || !inKeys.Add(key))
                        continue;
                    string name = ResolveDestinationName(key, config, catalog);
                    string why = BuildRotationWhy(config, key, inRotation: true);
                    inList.Add(new PagesFieldsRotationItemModel(
                        key, name, i + 1, why, isInRotation: true));
                }
            }
            else
            {
                int step = 0;
                for (int i = 0; i < config.PageOrder.Count; i++)
                {
                    var r = config.PageOrder[i];
                    if (r == null) continue;
                    string key = PageKey(r);
                    if (key == null || !inKeys.Add(key))
                        continue;
                    step++;
                    string name = ResolvePageRefName(r, config, catalog);
                    string why = BuildRotationWhy(config, key, inRotation: true);
                    inList.Add(new PagesFieldsRotationItemModel(
                        key, name, step, why, isInRotation: true));
                }
            }

            if (inRotation)
                return new ReadOnlyCollection<PagesFieldsRotationItemModel>(inList);

            // Not in rotation: all strip pages minus inKeys.
            var outList = new List<PagesFieldsRotationItemModel>();
            var strip = BuildPageStrip(config, catalog, null, out _);
            for (int i = 0; i < strip.Count; i++)
            {
                var p = strip[i];
                if (inKeys.Contains(p.Key))
                    continue;
                string why = DisplayCopy.RotationWhyArrivesViaEntrypoints;
                outList.Add(new PagesFieldsRotationItemModel(
                    p.Key, p.Name, step: null, whyLine: why, isInRotation: false));
            }
            return new ReadOnlyCollection<PagesFieldsRotationItemModel>(outList);
        }

        private static string ResolveDestinationName(
            string destinationKey, DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (string.IsNullOrEmpty(destinationKey))
                return string.Empty;
            if (destinationKey.StartsWith("itm:", StringComparison.Ordinal))
            {
                string id = destinationKey.Substring(4);
                var page = DisplayConfigV2EditSession.FindCatalogPage(catalog, id);
                if (page != null)
                    return ResolveItmPageName(config, page);
                return id;
            }
            if (destinationKey.StartsWith("hosted:", StringComparison.Ordinal))
            {
                string id = destinationKey.Substring(7);
                if (config?.Pages != null)
                {
                    for (int i = 0; i < config.Pages.Count; i++)
                    {
                        var pe = config.Pages[i];
                        if (pe != null
                            && pe.Kind == PageEntryKind.HostedPage
                            && string.Equals(pe.Id, id, StringComparison.Ordinal))
                            return pe.Name ?? pe.Id;
                    }
                }
                return id;
            }
            return destinationKey;
        }

        private static string BuildRotationWhy(
            DisplayConfigV2 config, string pageKey, bool inRotation)
        {
            var baseRef = config?.Priority?.Rest?.InSessionPage;
            if (baseRef != null && string.Equals(PageKey(baseRef), pageKey, StringComparison.Ordinal))
                return DisplayCopy.RotationWhyBasePage;

            // Has a priority seat?
            if (config?.Priority?.Rows != null)
            {
                int rank = 0;
                for (int i = 0; i < config.Priority.Rows.Count; i++)
                {
                    var row = config.Priority.Rows[i];
                    if (row == null || row.Kind == PriorityRowKind.Manual)
                        continue;
                    rank++;
                    if (string.Equals(PageKey(row.Target), pageKey, StringComparison.Ordinal))
                        return DisplayCopy.RotationWhyHasEntrypoint(rank);
                }
            }
            return DisplayCopy.RotationWhyOnlyRoute;
        }

        private static string ResolvePageRefName(
            PageRef r, DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (r == null) return string.Empty;
            if (r.Kind == PageRefKind.ItmPage)
            {
                var page = DisplayConfigV2EditSession.FindCatalogPage(catalog, r.CatalogPageId);
                if (page != null)
                    return ResolveItmPageName(config, page);
                return r.CatalogPageId ?? string.Empty;
            }
            if (r.Kind == PageRefKind.HostedPage && config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var pe = config.Pages[i];
                    if (pe != null
                        && pe.Kind == PageEntryKind.HostedPage
                        && string.Equals(pe.Id, r.Id, StringComparison.Ordinal))
                        return pe.Name ?? pe.Id;
                }
                return r.Id ?? string.Empty;
            }
            return string.Empty;
        }

        // ── Naming / placement ───────────────────────────────────────────

        private static bool PagePlacesParam(
            WheelCatalog catalog, string catalogPageId, ushort paramId)
        {
            if (catalog == null || string.IsNullOrEmpty(catalogPageId))
                return false;
            var onPage = CatalogFields.ParamsOnPage(catalog, catalogPageId);
            for (int i = 0; i < onPage.Count; i++)
            {
                if (onPage[i] == paramId)
                    return true;
            }
            return false;
        }

        private static string FieldDisplayName(
            WheelCatalog catalog, DisplayConfigV2 config, ushort paramId)
        {
            var def = CatalogFields.FindDefinitionByParam(catalog, paramId);
            if (def != null)
                return FieldDisplayNameFromDef(def);
            return paramId.ToString(CultureInfo.InvariantCulture);
        }

        private static string FieldDisplayNameFromDef(CatalogFieldDefinition def)
        {
            if (def == null) return string.Empty;
            if (!string.IsNullOrEmpty(def.DisplayLabel))
                return def.DisplayLabel;
            if (!string.IsNullOrEmpty(def.FirmwareLabel))
                return def.FirmwareLabel;
            if (!string.IsNullOrEmpty(def.ShortCode))
                return def.ShortCode;
            return def.Id ?? string.Empty;
        }
    }

    // ── Row models ───────────────────────────────────────────────────────

    public sealed class PagesFieldsPageButtonModel
    {
        public PagesFieldsPageButtonModel(
            string key, string name, string badge, bool isItm,
            string catalogPageId, string hostedPageId, int firmwareIndex,
            int? rotationStep, bool isSelected, bool isDimmed)
        {
            Key = key;
            Name = name;
            Badge = badge;
            IsItm = isItm;
            CatalogPageId = catalogPageId;
            HostedPageId = hostedPageId;
            FirmwareIndex = firmwareIndex;
            RotationStep = rotationStep;
            IsSelected = isSelected;
            IsDimmed = isDimmed;
        }

        public string Key { get; }
        public string Name { get; }
        public string Badge { get; }
        public bool IsItm { get; }
        public string CatalogPageId { get; }
        public string HostedPageId { get; }
        public int FirmwareIndex { get; }
        /// <summary>1-based rotation step; null → <see cref="DisplayCopy.RotationStepAbsent"/>.</summary>
        public int? RotationStep { get; }
        public bool IsSelected { get; }
        public bool IsDimmed { get; }
    }

    public sealed class PagesFieldsPreviewHitModel
    {
        public PagesFieldsPreviewHitModel(
            ushort paramId, string logicalId, string row, string column,
            bool isPicked, string displayName)
        {
            ParamId = paramId;
            LogicalId = logicalId;
            Row = row;
            Column = column;
            IsPicked = isPicked;
            DisplayName = displayName;
        }

        public ushort ParamId { get; }
        public string LogicalId { get; }
        public string Row { get; }
        public string Column { get; }
        public bool IsPicked { get; }
        public string DisplayName { get; }
    }

    public sealed class PagesFieldsScopeGroupModel
    {
        public PagesFieldsScopeGroupModel(
            string header, IReadOnlyList<PagesFieldsFieldSectionModel> sections)
        {
            Header = header;
            Sections = sections
                ?? new ReadOnlyCollection<PagesFieldsFieldSectionModel>(
                    Array.Empty<PagesFieldsFieldSectionModel>());
        }

        public string Header { get; }
        public IReadOnlyList<PagesFieldsFieldSectionModel> Sections { get; }
    }

    public sealed class PagesFieldsFieldSectionModel
    {
        public PagesFieldsFieldSectionModel(
            ushort paramId,
            string logicalId,
            string displayName,
            string capabilityHint,
            string reachLine,
            bool isShared,
            int placedCount,
            int totalItmPages,
            bool isProvisional,
            bool isLocked,
            bool isInertCollision,
            string inertReason,
            IReadOnlyList<PagesFieldsOverrideRowModel> overrides,
            PagesFieldsBaseBlockModel baseBlock,
            IReadOnlyList<string> offeredFormats,
            int? suffixWidth = null)
        {
            SuffixWidth = suffixWidth;
            ParamId = paramId;
            LogicalId = logicalId;
            DisplayName = displayName;
            CapabilityHint = capabilityHint;
            ReachLine = reachLine;
            IsShared = isShared;
            PlacedCount = placedCount;
            TotalItmPages = totalItmPages;
            IsProvisional = isProvisional;
            IsLocked = isLocked;
            IsInertCollision = isInertCollision;
            InertReason = inertReason;
            Overrides = overrides
                ?? new ReadOnlyCollection<PagesFieldsOverrideRowModel>(
                    Array.Empty<PagesFieldsOverrideRowModel>());
            BaseBlock = baseBlock;
            OfferedFormats = offeredFormats
                ?? new ReadOnlyCollection<string>(Array.Empty<string>());
        }

        public ushort ParamId { get; }
        public string LogicalId { get; }
        public string DisplayName { get; }
        public string CapabilityHint { get; }
        public string ReachLine { get; }
        public bool IsShared { get; }
        public int PlacedCount { get; }
        public int TotalItmPages { get; }
        public bool IsProvisional { get; }
        public bool IsLocked { get; }
        public bool IsInertCollision { get; }
        public string InertReason { get; }
        public IReadOnlyList<PagesFieldsOverrideRowModel> Overrides { get; }
        public PagesFieldsBaseBlockModel BaseBlock { get; }
        public IReadOnlyList<string> OfferedFormats { get; }

        /// <summary>
        /// Measured suffix width from the catalog: 0 = no region, positive = clamp
        /// inputs to it (law 10), null = untested — inputs stay unclamped and the
        /// wire ceiling applies.
        /// </summary>
        public int? SuffixWidth { get; }
    }

    public sealed class PagesFieldsOverrideRowModel
    {
        public PagesFieldsOverrideRowModel(
            string overrideId, int rank, string writesChip, string contentChip,
            string conditionSentence, bool actsAsEntrypoint, bool enabled, bool degraded)
        {
            OverrideId = overrideId;
            Rank = rank;
            WritesChip = writesChip;
            ContentChip = contentChip;
            ConditionSentence = conditionSentence;
            ActsAsEntrypoint = actsAsEntrypoint;
            Enabled = enabled;
            Degraded = degraded;
        }

        public string OverrideId { get; }
        public int Rank { get; }
        public string WritesChip { get; }
        public string ContentChip { get; }
        public string ConditionSentence { get; }
        public bool ActsAsEntrypoint { get; }
        public bool Enabled { get; }
        public bool Degraded { get; }
    }

    public sealed class PagesFieldsBaseBlockModel
    {
        public PagesFieldsBaseBlockModel(
            string sourceName, ValueSourceKind sourceKind, string format,
            string baseSuffix, IReadOnlyList<string> offeredFormats)
        {
            SourceName = sourceName;
            SourceKind = sourceKind;
            Format = format;
            BaseSuffix = baseSuffix;
            OfferedFormats = offeredFormats
                ?? new ReadOnlyCollection<string>(Array.Empty<string>());
        }

        public string SourceName { get; }
        public ValueSourceKind SourceKind { get; }
        public string Format { get; }
        public string BaseSuffix { get; }
        public IReadOnlyList<string> OfferedFormats { get; }
    }

    public sealed class PagesFieldsEntrypointRowModel
    {
        public PagesFieldsEntrypointRowModel(
            int rank, string detail, string statusCopy, bool isWinner, string rowId)
        {
            Rank = rank;
            Detail = detail;
            StatusCopy = statusCopy;
            IsWinner = isWinner;
            RowId = rowId;
        }

        public int Rank { get; }
        public string Detail { get; }
        public string StatusCopy { get; }
        public bool IsWinner { get; }
        public string RowId { get; }
    }

    public sealed class PagesFieldsRotationItemModel
    {
        public PagesFieldsRotationItemModel(
            string pageKey, string name, int? step, string whyLine, bool isInRotation)
        {
            PageKey = pageKey;
            Name = name;
            Step = step;
            WhyLine = whyLine;
            IsInRotation = isInRotation;
        }

        public string PageKey { get; }
        public string Name { get; }
        public int? Step { get; }
        public string WhyLine { get; }
        public bool IsInRotation { get; }
    }
}
