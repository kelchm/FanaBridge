using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure Priority (v2) projection — no WPF. Boards 5b (ITM) / 5j (segment) /
    /// 5n (idle picker) / 5f (entrypoint form shape). Structure follows digest §2;
    /// adjudication Q* triage is binding. Writes go through
    /// <see cref="DisplayConfigV2EditSession"/> (never raw document mutation).
    /// </summary>
    public sealed class DisplayPriorityV2Model
    {
        private static readonly IReadOnlyList<PriorityRowModel> NoRows =
            new ReadOnlyCollection<PriorityRowModel>(Array.Empty<PriorityRowModel>());

        private static readonly IReadOnlyList<PriorityExplainerCardModel> NoCards =
            new ReadOnlyCollection<PriorityExplainerCardModel>(
                Array.Empty<PriorityExplainerCardModel>());

        private static readonly IReadOnlyList<string> NoBadges =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        private static readonly IReadOnlyList<PriorityChildRowModel> NoChildren =
            new ReadOnlyCollection<PriorityChildRowModel>(
                Array.Empty<PriorityChildRowModel>());

        private static readonly IReadOnlyList<PrioritySplitSummonModel> NoSplitSummons =
            new ReadOnlyCollection<PrioritySplitSummonModel>(
                Array.Empty<PrioritySplitSummonModel>());

        private static readonly IReadOnlyList<PriorityPickerItemModel> NoPickerItems =
            new ReadOnlyCollection<PriorityPickerItemModel>(
                Array.Empty<PriorityPickerItemModel>());

        private static readonly IReadOnlyList<PriorityPickerGroupModel> NoPickerGroups =
            new ReadOnlyCollection<PriorityPickerGroupModel>(
                Array.Empty<PriorityPickerGroupModel>());

        /// <summary>
        /// Rebuild the Priority projection. Null config yields a minimal empty model.
        /// </summary>
        /// <param name="expandedRowIds">Row ids currently expanded (session UI state).</param>
        public static DisplayPriorityV2Model Project(
            DisplayConfigV2 config,
            DisplayResolutionSnapshotModel resolution,
            DisplayValuesSnapshot values,
            DisplayType displayType,
            WheelCatalog catalog = null,
            AliasTable aliases = null,
            bool nextPageMapped = false,
            bool prevPageMapped = false,
            ISet<string> expandedRowIds = null,
            int? rememberedManualSeconds = null)
        {
            resolution = resolution ?? DisplayResolutionSnapshotModel.Empty;
            bool isItm = displayType == DisplayType.Itm;
            var mode = config?.Settings?.Mode ?? SettingsMode.On;

            // Q1 PROVISIONAL: Off → header + empty-state, no ladder.
            bool modeOff = mode == SettingsMode.Off;
            // Q1 PROVISIONAL: Legacy Only → ITM rows dimmed + CAN'T RUN HERE.
            bool legacyOnly = mode == SettingsMode.LegacyOnly;
            // Q1 PROVISIONAL: disconnected → document ladder with "no wheel" status.
            bool disconnected = !resolution.IsConnected;

            string surfaceWord = isItm && !legacyOnly
                ? DisplayCopy.ItmDisplay
                : DisplayCopy.SegmentDisplay;
            string situation = resolution.InGame ? DisplayCopy.InGame : DisplayCopy.SituationIdle;

            // The preview follows the active surface: segment-only wheels and
            // Legacy Only on an ITM wheel both paint the segment display.
            bool showSegmentPreview = (!isItm || legacyOnly) && !modeOff;
            string previewCaption = showSegmentPreview
                ? BuildPreviewCaption(values, config, catalog, resolution)
                : null;

            // Column metric: ITM 236/104; segment 196/112 (digest §2).
            int pageColWidth = isItm ? 236 : 196;
            int statusColWidth = isItm ? 104 : 112;
            // Kind badges only when kinds mix (ITM wheels).
            bool showKindBadges = isItm;

            string ladderSubtitle = isItm
                ? DisplayCopy.PriorityLadderSubtitle
                : DisplayCopy.PriorityLadderSubtitleShort;

            var rows = modeOff
                ? NoRows
                : BuildLadder(
                    config, resolution, catalog, aliases, legacyOnly, disconnected,
                    nextPageMapped, prevPageMapped, showKindBadges, expandedRowIds,
                    rememberedManualSeconds);

            int rankedCount = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].IsPinned)
                    rankedCount++;
            }

            var explainers = modeOff
                ? NoCards
                : BuildExplainers(isItm);

            var idlePicker = BuildIdlePicker(config, catalog, resolution);
            var basePicker = BuildBasePagePicker(config, catalog);

            return new DisplayPriorityV2Model(
                surfaceWord: surfaceWord,
                situationCopy: situation,
                inGame: resolution.InGame,
                isConnected: resolution.IsConnected,
                isItmWheel: isItm,
                mode: mode,
                showLadder: !modeOff,
                modeOffEmptyState: modeOff ? DisplayCopy.ModeOffEmptyState : null,
                ladderHeader: DisplayCopy.LadderHeaderCount(rankedCount),
                ladderSubtitle: ladderSubtitle,
                // Surface B: plain door live — + Add a page routes to 5h.
                addPageEnabled: true,
                addPageTooltip: null,
                pageColWidth: pageColWidth,
                statusColWidth: statusColWidth,
                showKindBadges: showKindBadges,
                showSegmentPreview: showSegmentPreview,
                previewCaption: previewCaption,
                values: values,
                rows: rows,
                explainers: explainers,
                idlePicker: idlePicker,
                basePagePicker: basePicker,
                nextPageMapped: nextPageMapped,
                prevPageMapped: prevPageMapped);
        }

        private DisplayPriorityV2Model(
            string surfaceWord,
            string situationCopy,
            bool inGame,
            bool isConnected,
            bool isItmWheel,
            SettingsMode mode,
            bool showLadder,
            string modeOffEmptyState,
            string ladderHeader,
            string ladderSubtitle,
            bool addPageEnabled,
            string addPageTooltip,
            int pageColWidth,
            int statusColWidth,
            bool showKindBadges,
            bool showSegmentPreview,
            string previewCaption,
            DisplayValuesSnapshot values,
            IReadOnlyList<PriorityRowModel> rows,
            IReadOnlyList<PriorityExplainerCardModel> explainers,
            PriorityPickerModel idlePicker,
            PriorityPickerModel basePagePicker,
            bool nextPageMapped,
            bool prevPageMapped)
        {
            SurfaceWord = surfaceWord;
            SituationCopy = situationCopy;
            InGame = inGame;
            IsConnected = isConnected;
            IsItmWheel = isItmWheel;
            Mode = mode;
            ShowLadder = showLadder;
            ModeOffEmptyState = modeOffEmptyState;
            LadderHeader = ladderHeader;
            LadderSubtitle = ladderSubtitle;
            AddPageEnabled = addPageEnabled;
            AddPageTooltip = addPageTooltip;
            PageColWidth = pageColWidth;
            StatusColWidth = statusColWidth;
            ShowKindBadges = showKindBadges;
            ShowSegmentPreview = showSegmentPreview;
            PreviewCaption = previewCaption;
            Values = values;
            Rows = rows ?? NoRows;
            Explainers = explainers ?? NoCards;
            IdlePicker = idlePicker;
            BasePagePicker = basePagePicker;
            NextPageMapped = nextPageMapped;
            PrevPageMapped = prevPageMapped;
        }

        // ── Header ───────────────────────────────────────────────────────

        public string SurfaceWord { get; }
        public string SituationCopy { get; }
        public bool InGame { get; }
        public bool IsConnected { get; }
        public bool IsItmWheel { get; }
        public SettingsMode Mode { get; }

        // ── Ladder chrome ────────────────────────────────────────────────

        public bool ShowLadder { get; }
        public string ModeOffEmptyState { get; }
        public string LadderHeader { get; }
        public string LadderSubtitle { get; }

        /// <summary>Surface B: true — routes to the Add-a-page flow (5h).</summary>
        public bool AddPageEnabled { get; }
        public string AddPageTooltip { get; }

        /// <summary>PAGE column width — 236 ITM / 196 segment.</summary>
        public int PageColWidth { get; }

        /// <summary>RIGHT NOW column width — 104 ITM / 112 segment.</summary>
        public int StatusColWidth { get; }

        public bool ShowKindBadges { get; }
        public bool ShowSegmentPreview { get; }
        public string PreviewCaption { get; }
        public DisplayValuesSnapshot Values { get; }

        public IReadOnlyList<PriorityRowModel> Rows { get; }
        public IReadOnlyList<PriorityExplainerCardModel> Explainers { get; }

        /// <summary>5n idle picker groups (pages + screens; playlists group via DisplayCopy for task #22).</summary>
        public PriorityPickerModel IdlePicker { get; }

        /// <summary>
        /// UNBOARDED base-page picker (owner ruling #2): 5n shell minus screens/playlists.
        /// // FIDELITY-BREAK (owner order): no board draws this editor; design session
        /// // owes the drawn board. Built from the idle picker shell with pages only.
        /// </summary>
        public PriorityPickerModel BasePagePicker { get; }

        public bool NextPageMapped { get; }
        public bool PrevPageMapped { get; }

        // ── Ladder composition ───────────────────────────────────────────

        private static IReadOnlyList<PriorityRowModel> BuildLadder(
            DisplayConfigV2 config,
            DisplayResolutionSnapshotModel resolution,
            WheelCatalog catalog,
            AliasTable aliases,
            bool legacyOnly,
            bool disconnected,
            bool nextMapped,
            bool prevMapped,
            bool showKindBadges,
            ISet<string> expandedRowIds,
            int? rememberedManualSeconds = null)
        {
            if (config?.Priority == null)
                return NoRows;

            var list = new List<PriorityRowModel>();
            var rows = config.Priority.EffectiveRows;
            string winnerId = FindDisplayWinnerCarrierId(resolution);
            string winnerDest = FindDisplayWinnerDestinationId(resolution);
            var carrierById = IndexCarriers(resolution);
            var aggregateBySeat = IndexAggregates(resolution);

            // Names above the manual row (for Manual shield consequence copy).
            var namesAboveManual = new List<string>();
            int manualIndex = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && rows[i].Kind == PriorityRowKind.Manual)
                {
                    manualIndex = i;
                    break;
                }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (manualIndex >= 0 && i >= manualIndex)
                    break;
                var r = rows[i];
                if (r == null) continue;
                var d = ResolveDestination(r, config, catalog, showKindBadges);
                if (!string.IsNullOrEmpty(d.Name))
                    namesAboveManual.Add(d.Name);
            }

            // firstMention once per VIEW projection for cycle glossary form.
            bool cycleMentioned = false;
            int rank = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null) continue;
                rank++;
                // OWNER-WAIVED FIDELITY (Surface C / D19): satellites do not expand —
                // single child already stated in the detail cell; disclosure slot empty.
                bool expanded = row.Kind != PriorityRowKind.Satellite
                    && expandedRowIds != null
                    && !string.IsNullOrEmpty(row.Id)
                    && expandedRowIds.Contains(row.Id);
                // Manual has no stable id — expand via kind key.
                if (row.Kind == PriorityRowKind.Manual
                    && expandedRowIds != null
                    && expandedRowIds.Contains(PriorityRowModel.ManualExpandKey))
                    expanded = true;

                list.Add(ProjectRankedRow(
                    rank, row, config, catalog, aliases, carrierById, aggregateBySeat,
                    resolution.Carriers, winnerId, legacyOnly, disconnected,
                    nextMapped, prevMapped, resolution.Manual, showKindBadges,
                    expanded, namesAboveManual, rows.Count - 1 - i,
                    ref cycleMentioned, rememberedManualSeconds));
            }

            list.Add(ProjectBaseRow(
                config, catalog, winnerId, winnerDest, showKindBadges, legacyOnly));
            list.Add(ProjectIdleRow(config, catalog, winnerDest, legacyOnly));

            return new ReadOnlyCollection<PriorityRowModel>(list);
        }

        private static PriorityRowModel ProjectRankedRow(
            int rank,
            PriorityRow row,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases,
            Dictionary<string, CarrierResolutionRowModel> carriers,
            Dictionary<string, AggregateMembershipModel> aggregates,
            IReadOnlyList<CarrierResolutionRowModel> allCarriers,
            string winnerId,
            bool legacyOnly,
            bool disconnected,
            bool nextMapped,
            bool prevMapped,
            ManualRowStateModel manual,
            bool showKindBadges,
            bool expanded,
            IReadOnlyList<string> namesAboveManual,
            int rowsBelow,
            ref bool cycleMentioned,
            int? rememberedManualSeconds)
        {
            string carrierId = CarrierIdForRow(row);
            carriers.TryGetValue(carrierId ?? string.Empty, out var carrier);

            bool isOff = carrier != null
                && ContainsLabel(carrier.RowLabelCopies, DisplayCopy.Off);

            bool isItmDest = row.Target != null && row.Target.Kind == PageRefKind.ItmPage;
            bool provisionalCantRun = legacyOnly && isItmDest;

            bool isWinner = !provisionalCantRun
                && !disconnected
                && !string.IsNullOrEmpty(winnerId)
                && string.Equals(carrierId, winnerId, StringComparison.Ordinal);

            var state = isWinner
                ? PriorityRowState.Winner
                : (isOff || provisionalCantRun ? PriorityRowState.Off : PriorityRowState.Normal);

            string status = disconnected
                ? DisplayCopy.NoWheel
                : ResolveStatus(carrier, isWinner, isOff, provisionalCantRun);

            // OWNER-WAIVED FIDELITY (Surface C): ChildRef satellites resolve host page
            // from the child; summons satellites keep Target (same page as home).
            var dest = ResolveDestination(row, config, catalog, showKindBadges);
            string referenceName = null;
            if (row.Kind == PriorityRowKind.Satellite)
            {
                referenceName = ResolveSatelliteReferenceName(row, config, catalog, aliases);
                // ChildRef with no stored target: dest may be empty — fill from child host.
                if (string.IsNullOrEmpty(dest.Name) && row.ChildRef != null)
                    dest = ResolveChildRefHostDestination(row.ChildRef, config, catalog, showKindBadges);
            }

            bool hasAggregate = row.Kind == PriorityRowKind.Seat
                && !string.IsNullOrEmpty(row.Id)
                && aggregates.TryGetValue(row.Id, out var _)
                && aggregates[row.Id].TotalCount > 0;

            string detail = ResolveDetail(
                row, config, aliases, carriers, aggregates, allCarriers,
                manual, nextMapped, prevMapped, expanded, hasAggregate, catalog,
                ref cycleMentioned);
            if (row.Kind == PriorityRowKind.Satellite && row.ChildRef != null)
                detail = ResolveChildRefDetail(row.ChildRef, config, catalog, aliases);

            // Lifetime tail is already composed into detail for condition rows.
            var entrypoints = expanded && row.Kind == PriorityRowKind.Seat
                ? BuildEntrypointChildren(row, config, aliases, carriers, aggregates, catalog)
                : NoChildren;
            var overrides = expanded && row.Kind == PriorityRowKind.Seat && isItmDest
                ? BuildOverrideChildren(row, config, aliases, carriers, catalog)
                : NoChildren;
            var layers = expanded && row.Kind == PriorityRowKind.Seat
                && row.Target != null && row.Target.Kind == PageRefKind.HostedPage
                ? BuildLayerChildren(row, config, aliases, carriers)
                : NoChildren;

            bool showBaseBlock = expanded
                && layers.Count > 0
                && IsLayersOnlyPage(row, config);

            ManualOptionsModel manualOpts = null;
            if (row.Kind == PriorityRowKind.Manual && expanded)
            {
                manualOpts = BuildManualOptions(
                    row, nextMapped, prevMapped, namesAboveManual, rowsBelow,
                    rememberedManualSeconds);
            }

            // Seat + satellite menus (Q3 / Surface C). Manual/base/idle glyph inert.
            bool showMenu = row.Kind == PriorityRowKind.Seat
                || row.Kind == PriorityRowKind.Satellite;

            string primarySummonId = FirstEnabledSummonId(row);
            bool primaryEnabled = primarySummonId != null
                && IsSummonEnabled(row, primarySummonId);

            var splitSummons = BuildSplitSummons(row, aliases);
            // OWNER-WAIVED FIDELITY C-O2: authored count includes disabled summons.
            bool canSplit = row.Kind == PriorityRowKind.Seat
                && splitSummons.Count >= 2;
            // OWNER-WAIVED FIDELITY: rejoin only on satellites.
            bool canRejoin = row.Kind == PriorityRowKind.Satellite;

            // Degrade-visible: ChildRefAmbiguous / TargetIgnored / SummonsIgnored →
            // CantRunHere in status (honesty set §B8).
            if (row.Kind == PriorityRowKind.Satellite
                && (row.ChildRefAmbiguous || row.TargetIgnored || row.SummonsIgnored
                    || row.DegradedAtLoad)
                && !disconnected)
            {
                status = DisplayCopy.CantRunHereWithReason(
                    ResolveSatelliteDegradedReason(row));
                state = PriorityRowState.Off;
            }

            return new PriorityRowModel(
                rowId: row.Id ?? (row.Kind == PriorityRowKind.Manual
                    ? PriorityRowModel.ManualExpandKey : null),
                rankText: rank.ToString(CultureInfo.InvariantCulture),
                rankNumber: rank,
                kind: row.Kind,
                destination: dest,
                detail: detail,
                statusCopy: status,
                state: state,
                carrierId: carrierId,
                isPinned: false,
                showGrip: true,
                isExpanded: expanded,
                // OWNER-WAIVED FIDELITY: satellites never show disclosure.
                showDisclosure: expanded && row.Kind != PriorityRowKind.Satellite,
                isMaterialized: row.MaterializedAtLoad,
                target: row.Target,
                entrypoints: entrypoints,
                overrides: overrides,
                layers: layers,
                showBaseBlock: showBaseBlock,
                baseBlockBody: showBaseBlock ? DisplayCopy.BaseBlockBlank : null,
                manualOptions: manualOpts,
                showOverflowMenu: showMenu,
                primarySummonId: primarySummonId,
                primarySummonEnabled: primaryEnabled,
                returnToRestAfterMs: row.ReturnToRestAfterMs,
                pageName: dest.Name,
                // OWNER-WAIVED FIDELITY: reference marker › + child/summon name.
                splitReferenceName: referenceName,
                splitSummons: splitSummons,
                canSplitEntrypoint: canSplit,
                canRejoinHome: canRejoin);
        }

        private static PriorityRowModel ProjectBaseRow(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            string winnerId,
            string winnerDest,
            bool showKindBadges,
            bool legacyOnly)
        {
            var pageRef = config?.Priority?.Rest?.InSessionPage;
            var dest = ResolvePageRefDestination(pageRef, config, catalog, showKindBadges);
            bool provisionalCantRun = legacyOnly
                && pageRef != null
                && pageRef.Kind == PageRefKind.ItmPage;
            bool isWinner = !provisionalCantRun && (
                string.Equals(winnerId, SeatArbiter.RestCarrierId, StringComparison.Ordinal)
                || string.Equals(winnerDest, DestinationIds.RestInSession, StringComparison.Ordinal));

            return new PriorityRowModel(
                rowId: PriorityRowModel.BaseExpandKey,
                rankText: DisplayCopy.PriorityBaseRank,
                rankNumber: 0,
                kind: PriorityRowKind.Unknown, // pinned base — not a ranked kind
                destination: dest,
                detail: DisplayCopy.WhenNothingAboveIsLive,
                statusCopy: provisionalCantRun ? DisplayCopy.CantRunHere : DisplayCopy.StatusDash,
                state: provisionalCantRun
                    ? PriorityRowState.Off
                    : (isWinner ? PriorityRowState.Winner : PriorityRowState.Pinned),
                carrierId: SeatArbiter.RestCarrierId,
                isPinned: true,
                showGrip: false,
                isExpanded: false,
                showDisclosure: false,
                isMaterialized: false,
                target: pageRef,
                entrypoints: NoChildren,
                overrides: NoChildren,
                layers: NoChildren,
                showBaseBlock: false,
                baseBlockBody: null,
                manualOptions: null,
                // UNBOARDED: base-row menu with Choose the Base page… (owner ruling #2).
                showOverflowMenu: true,
                primarySummonId: null,
                primarySummonEnabled: true,
                returnToRestAfterMs: null,
                pageName: dest.Name,
                isBaseRow: true,
                isIdleRow: false);
        }

        private static PriorityRowModel ProjectIdleRow(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            string winnerDest,
            bool legacyOnly)
        {
            var idle = config?.Priority?.Rest?.Idle;
            var dest = new PriorityDestinationModel(
                badges: NoBadges,
                name: DisplayCopy.OutsideASession,
                isCycle: false,
                isLegacy: false,
                showPlaylistBadge: false);

            // Detail cell is the idle target editor (combobox), not a sentence.
            string idleLabel = IdleTargetLabel(idle, config, catalog);
            bool provisionalCantRun = legacyOnly
                && idle?.Kind == IdleKind.Page
                && idle.Page?.Kind == PageRefKind.ItmPage;
            bool isWinner = !provisionalCantRun
                && string.Equals(winnerDest, DestinationIds.RestIdle, StringComparison.Ordinal);

            bool showPlaylistBadge = idle != null
                && idle.Kind == IdleKind.Playlist
                && !string.IsNullOrWhiteSpace(idle.Playlist);
            string idleNote = null;
            // 5j draws "no playlist on this profile" when idle is not a playlist target.
            if (!showPlaylistBadge)
                idleNote = DisplayCopy.NoPlaylistOnThisProfile;

            return new PriorityRowModel(
                rowId: PriorityRowModel.IdleExpandKey,
                rankText: string.Empty,
                rankNumber: 0,
                kind: PriorityRowKind.Unknown,
                destination: dest,
                detail: idleLabel,
                statusCopy: provisionalCantRun ? DisplayCopy.CantRunHere : DisplayCopy.StatusDash,
                state: provisionalCantRun
                    ? PriorityRowState.Off
                    : (isWinner ? PriorityRowState.Winner : PriorityRowState.Pinned),
                carrierId: DestinationIds.RestIdle,
                isPinned: true,
                showGrip: false,
                isExpanded: false,
                showDisclosure: false,
                isMaterialized: false,
                target: null,
                entrypoints: NoChildren,
                overrides: NoChildren,
                layers: NoChildren,
                showBaseBlock: false,
                baseBlockBody: null,
                manualOptions: null,
                // Q3: idle ⋯ inert this phase (combobox edits it).
                showOverflowMenu: false,
                primarySummonId: null,
                primarySummonEnabled: true,
                returnToRestAfterMs: null,
                pageName: idleLabel,
                isBaseRow: false,
                isIdleRow: true,
                idleTargetLabel: idleLabel,
                idleTrailingNote: idleNote,
                showPlaylistBadge: showPlaylistBadge);
        }

        private static string ResolveDetail(
            PriorityRow row,
            DisplayConfigV2 config,
            AliasTable aliases,
            Dictionary<string, CarrierResolutionRowModel> carriers,
            Dictionary<string, AggregateMembershipModel> aggregates,
            IReadOnlyList<CarrierResolutionRowModel> allCarriers,
            ManualRowStateModel manual,
            bool nextMapped,
            bool prevMapped,
            bool expanded,
            bool hasAggregate,
            WheelCatalog catalog,
            ref bool cycleMentioned)
        {
            if (row.Kind == PriorityRowKind.Manual)
                return DisplayCopy.ManualPagingStanding;

            // Expanded seat → count summary (digest §2 / Q9).
            if (expanded && row.Kind == PriorityRowKind.Seat)
            {
                int ep = CountEntrypoints(row, aggregates);
                int ov = CountOverridesOnPage(row, config, catalog);
                return DisplayCopy.SeatCountSummary(ep, ov);
            }

            // Q7/P4: ruled firing line on both surfaces (membership sentence dies).
            if (row.Kind == PriorityRowKind.Seat
                && !string.IsNullOrEmpty(row.Id)
                && aggregates.TryGetValue(row.Id, out var agg)
                && agg.TotalCount > 0
                && agg.ActiveCount > 0)
            {
                string line = DisplayCopy.EntrypointsFiringLine(agg.ActiveCount, agg.TotalCount);
                line += DisplayCopy.LifetimeWhileOneActive;
                return line;
            }

            // Primary condition + lifetime tail (Q8).
            string primary = null;
            Lifetime primaryLifetime = null;
            var summons = row.Summons;
            if (summons != null)
            {
                for (int i = 0; i < summons.Count; i++)
                {
                    var s = summons[i];
                    if (s == null || !s.EffectivelyEnabled)
                        continue;
                    primaryLifetime = s.Lifetime;
                    if (!string.IsNullOrWhiteSpace(s.Name))
                    {
                        primary = s.Name;
                        break;
                    }
                    primary = ConditionSentence.From(s.Condition, s.Lifetime, aliases);
                    if (!string.IsNullOrEmpty(primary))
                        break;
                }
            }

            // Cycle: glossary form once per VIEW projection; subsequent cycles use the
            // short form. Period composes with the selected summon's primary lifetime.
            bool isCycle = row.Target != null && row.Target.Kind == PageRefKind.Cycle;
            if (isCycle)
            {
                bool first = !cycleMentioned;
                cycleMentioned = true;
                primary = DisplayCopy.ConditionWithCycleSuffix(primary, firstMention: first);
                int periodMs = FindCyclePeriodMs(config, row.Target?.Id);
                if (periodMs > 0)
                {
                    var kind = primaryLifetime?.Kind ?? LifetimeKind.WhileTrue;
                    int durMs = primaryLifetime != null && primaryLifetime.DurationMsPresent
                        ? primaryLifetime.DurationMs
                        : 0;
                    primary += DisplayCopy.LifetimeCycleLadderSuffix(periodMs, kind, durMs);
                }
            }
            else if (!string.IsNullOrEmpty(primary) && primaryLifetime != null)
            {
                primary += LifetimeSuffix(primaryLifetime);
            }

            if (string.IsNullOrEmpty(primary))
                primary = string.Empty;

            return primary ?? string.Empty;
        }

        private static string LifetimeSuffix(Lifetime lifetime)
        {
            if (lifetime == null)
                return DisplayCopy.LifetimeLadderSuffix(LifetimeKind.WhileTrue);
            int ms = lifetime.DurationMsPresent ? lifetime.DurationMs : 0;
            if (lifetime.Kind == LifetimeKind.ForDuration && ms <= 0)
                ms = Lifetime.DefaultDurationMs;
            if (lifetime.Kind == LifetimeKind.OnChange)
            {
                // Ladder short form is kind-only; direction lives on the condition sentence.
                return DisplayCopy.LifetimeLadderSuffix(LifetimeKind.OnChange);
            }
            return DisplayCopy.LifetimeLadderSuffix(lifetime.Kind, ms);
        }

        private static int CountEntrypoints(
            PriorityRow row, Dictionary<string, AggregateMembershipModel> aggregates)
        {
            // Q9 PROVISIONAL: effectively-enabled summons + 1 when seat has live derived aggregate.
            int n = 0;
            if (row.Summons != null)
            {
                for (int i = 0; i < row.Summons.Count; i++)
                {
                    if (row.Summons[i] != null && row.Summons[i].EffectivelyEnabled)
                        n++;
                }
            }
            if (!string.IsNullOrEmpty(row.Id)
                && aggregates != null
                && aggregates.TryGetValue(row.Id, out var agg)
                && agg.TotalCount > 0)
                n++;
            return n;
        }

        private static int CountOverridesOnPage(
            PriorityRow row, DisplayConfigV2 config, WheelCatalog catalog)
        {
            // Q9 PROVISIONAL: all overrides on the destination page.
            if (row?.Target == null
                || (config?.Fields == null && config?.SharedFields == null))
                return 0;

            if (row.Target.Kind == PageRefKind.HostedPage)
            {
                // Hosted: layers are not field overrides; count is 0 for the override noun.
                return 0;
            }

            if (row.Target.Kind != PageRefKind.ItmPage
                || string.IsNullOrEmpty(row.Target.CatalogPageId)
                || catalog?.Itm?.Pages == null)
            {
                // Without catalog placement, count every override (conservative upper bound
                // is wrong) — return 0 so the summary does not invent a denominator.
                // When the seat's page entry is known only via document, still 0.
                return 0;
            }

            var paramsOnPage = new HashSet<ushort>();
            for (int i = 0; i < catalog.Itm.Pages.Count; i++)
            {
                var p = catalog.Itm.Pages[i];
                if (p == null
                    || !string.Equals(p.Id, row.Target.CatalogPageId, StringComparison.Ordinal))
                    continue;
                if (p.Placements == null) break;
                var defs = CatalogFields.IndexByLogicalId(catalog);
                for (int f = 0; f < p.Placements.Count; f++)
                {
                    var pl = p.Placements[f];
                    if (pl == null || string.IsNullOrEmpty(pl.Field))
                        continue;
                    if (defs.TryGetValue(pl.Field, out var def) && def != null)
                        paramsOnPage.Add(def.ParamId);
                }
                break;
            }

            int count = 0;
            // One-ladder: fields + sharedFields (shared wins; inert side contributes 0).
            foreach (var kv in FieldLadderMap.Build(config, catalog))
            {
                if (!paramsOnPage.Contains(kv.Key))
                    continue;
                if (kv.Value?.Overrides != null)
                    count += kv.Value.Overrides.Count;
            }
            return count;
        }

        private static IReadOnlyList<PriorityChildRowModel> BuildEntrypointChildren(
            PriorityRow row,
            DisplayConfigV2 config,
            AliasTable aliases,
            Dictionary<string, CarrierResolutionRowModel> carriers,
            Dictionary<string, AggregateMembershipModel> aggregates,
            WheelCatalog catalog)
        {
            var list = new List<PriorityChildRowModel>();
            if (row.Summons != null)
            {
                for (int i = 0; i < row.Summons.Count; i++)
                {
                    var s = row.Summons[i];
                    if (s == null) continue;
                    string sentence = !string.IsNullOrWhiteSpace(s.Name)
                        ? s.Name
                        : ConditionSentence.From(s.Condition, s.Lifetime, aliases);
                    sentence += LifetimeSuffix(s.Lifetime);
                    string status = DisplayCopy.Waiting;
                    if (carriers.TryGetValue(s.Id ?? string.Empty, out var c)
                        && !string.IsNullOrEmpty(c.PresenceCopy))
                        status = c.PresenceCopy;
                    if (!s.EffectivelyEnabled)
                        status = DisplayCopy.Off;

                    list.Add(new PriorityChildRowModel(
                        id: s.Id,
                        kind: PriorityChildKind.Entrypoint,
                        label: sentence,
                        statusCopy: status,
                        isClickable: false,
                        actsAsEntrypoint: true,
                        chipLabel: null,
                        writesLabel: null));
                }
            }

            // Derived flagged-children aggregate listed as ordinary entrypoint when present.
            if (!string.IsNullOrEmpty(row.Id)
                && aggregates != null
                && aggregates.TryGetValue(row.Id, out var agg)
                && agg.TotalCount > 0)
            {
                string line = DisplayCopy.EntrypointsFiringLine(agg.ActiveCount, agg.TotalCount)
                    + DisplayCopy.LifetimeWhileOneActive;
                list.Add(new PriorityChildRowModel(
                    id: null,
                    kind: PriorityChildKind.DerivedAggregate,
                    label: line,
                    statusCopy: DisplayCopy.Waiting,
                    isClickable: false,
                    actsAsEntrypoint: true,
                    chipLabel: null,
                    writesLabel: null));
            }

            return list.Count == 0
                ? NoChildren
                : new ReadOnlyCollection<PriorityChildRowModel>(list);
        }

        private static IReadOnlyList<PriorityChildRowModel> BuildOverrideChildren(
            PriorityRow row,
            DisplayConfigV2 config,
            AliasTable aliases,
            Dictionary<string, CarrierResolutionRowModel> carriers,
            WheelCatalog catalog)
        {
            var list = new List<PriorityChildRowModel>();
            if (row?.Target == null || config?.Fields == null || catalog?.Itm?.Pages == null)
                return NoChildren;
            if (row.Target.Kind != PageRefKind.ItmPage
                || string.IsNullOrEmpty(row.Target.CatalogPageId))
                return NoChildren;

            var fieldLabels = new Dictionary<ushort, string>();
            var defs = CatalogFields.IndexByLogicalId(catalog);
            for (int i = 0; i < catalog.Itm.Pages.Count; i++)
            {
                var p = catalog.Itm.Pages[i];
                if (p == null
                    || !string.Equals(p.Id, row.Target.CatalogPageId, StringComparison.Ordinal))
                    continue;
                if (p.Placements == null) break;
                for (int f = 0; f < p.Placements.Count; f++)
                {
                    var pl = p.Placements[f];
                    if (pl == null || string.IsNullOrEmpty(pl.Field))
                        continue;
                    if (!defs.TryGetValue(pl.Field, out var def) || def == null)
                        continue;
                    string label = !string.IsNullOrEmpty(def.ShortCode)
                        ? def.ShortCode
                        : (def.FirmwareLabel ?? def.ParamId.ToString(CultureInfo.InvariantCulture));
                    // Multi-page reach line for shared fields (design 8c; existing-view touchpoint).
                    if (CatalogFields.TryGetReach(catalog, def.Id, out int placed, out int total)
                        && placed > 1)
                        label = label + " · " + DisplayCopy.ReachLine(placed, total);
                    fieldLabels[def.ParamId] = label;
                }
                break;
            }

            // One ladder: fields + sharedFields (shared wins).
            foreach (var kv in FieldLadderMap.Build(config, catalog))
            {
                if (!fieldLabels.ContainsKey(kv.Key))
                    continue;
                var entry = kv.Value;
                if (entry?.Overrides == null) continue;
                for (int i = 0; i < entry.Overrides.Count; i++)
                {
                    var ov = entry.Overrides[i];
                    if (ov == null) continue;
                    string sentence = ConditionSentence.From(ov.Condition, ov.Lifetime, aliases);
                    sentence += LifetimeSuffix(ov.Lifetime);
                    // P7: glyph only — no inline "acts as an entrypoint" words.
                    string status = DisplayCopy.Waiting;
                    if (carriers.TryGetValue(ov.Id ?? string.Empty, out var c)
                        && !string.IsNullOrEmpty(c.PresenceCopy))
                        status = c.PresenceCopy;

                    string writes = ov.Writes == FieldWrites.Suffix
                        || ov.Writes == FieldWrites.Both
                        ? DisplayCopy.WritesSuffix
                        : null;

                    list.Add(new PriorityChildRowModel(
                        id: ov.Id,
                        kind: PriorityChildKind.Override,
                        label: sentence,
                        statusCopy: status,
                        // Q6: 5b override sub-rows stay read-only (not clickable).
                        isClickable: false,
                        actsAsEntrypoint: ov.ActsAsEntrypoint && !ov.ActsAsEntrypointIgnored,
                        chipLabel: fieldLabels[kv.Key],
                        writesLabel: writes));
                }
            }

            return list.Count == 0
                ? NoChildren
                : new ReadOnlyCollection<PriorityChildRowModel>(list);
        }

        private static IReadOnlyList<PriorityChildRowModel> BuildLayerChildren(
            PriorityRow row,
            DisplayConfigV2 config,
            AliasTable aliases,
            Dictionary<string, CarrierResolutionRowModel> carriers)
        {
            var list = new List<PriorityChildRowModel>();
            if (row?.Target == null || config?.Pages == null)
                return NoChildren;
            if (row.Target.Kind != PageRefKind.HostedPage
                || string.IsNullOrEmpty(row.Target.Id))
                return NoChildren;

            for (int p = 0; p < config.Pages.Count; p++)
            {
                var page = config.Pages[p];
                if (page == null
                    || page.Kind != PageEntryKind.HostedPage
                    || !string.Equals(page.Id, row.Target.Id, StringComparison.Ordinal))
                    continue;
                if (page.Layers == null) break;
                for (int i = 0; i < page.Layers.Count; i++)
                {
                    var layer = page.Layers[i];
                    if (layer == null) continue;
                    string sentence = !string.IsNullOrWhiteSpace(layer.Name)
                        ? layer.Name
                        : ConditionSentence.From(layer.Condition, layer.Lifetime, aliases);
                    sentence += LifetimeSuffix(layer.Lifetime);
                    string status = DisplayCopy.Waiting;
                    if (carriers.TryGetValue(layer.Id ?? string.Empty, out var c)
                        && !string.IsNullOrEmpty(c.PresenceCopy))
                        status = c.PresenceCopy;

                    list.Add(new PriorityChildRowModel(
                        id: layer.Id,
                        kind: PriorityChildKind.Layer,
                        label: sentence,
                        statusCopy: status,
                        // Q6: 5j layer sub-rows are whole-row click targets.
                        isClickable: true,
                        actsAsEntrypoint: layer.ActsAsEntrypoint && !layer.ActsAsEntrypointIgnored,
                        chipLabel: DisplayCopy.LayerChip,
                        writesLabel: layer.Name));
                }
                break;
            }

            return list.Count == 0
                ? NoChildren
                : new ReadOnlyCollection<PriorityChildRowModel>(list);
        }

        private static bool IsLayersOnlyPage(PriorityRow row, DisplayConfigV2 config)
        {
            if (row?.Target == null || config?.Pages == null)
                return false;
            if (row.Target.Kind != PageRefKind.HostedPage)
                return false;
            for (int i = 0; i < config.Pages.Count; i++)
            {
                var p = config.Pages[i];
                if (p == null
                    || !string.Equals(p.Id, row.Target.Id, StringComparison.Ordinal))
                    continue;
                // Blank base when Base is null or has no content text.
                return p.Base == null
                    || p.Base.Content == null
                    || (string.IsNullOrEmpty(p.Base.Content.Text)
                        && string.IsNullOrEmpty(p.Base.Content.EffectiveText));
            }
            return false;
        }

        private static ManualOptionsModel BuildManualOptions(
            PriorityRow row,
            bool nextMapped,
            bool prevMapped,
            IReadOnlyList<string> namesAbove,
            int rowsBelow,
            int? rememberedManualSeconds = null)
        {
            // Q10 PROVISIONAL: checkbox binds ReturnToRestAfterMs != null; unchecked
            // keeps last value shown greyed (remembered across Poll rebuilds; 30 s when
            // never set). Pin: rememberedManualSeconds survives uncheck without discard.
            bool enabled = row.ReturnToRestAfterMs != null;
            int shownSeconds;
            if (enabled)
                shownSeconds = Math.Max(1, (row.ReturnToRestAfterMs.Value + 500) / 1000);
            else if (rememberedManualSeconds.HasValue && rememberedManualSeconds.Value > 0)
                shownSeconds = rememberedManualSeconds.Value;
            else
                shownSeconds = 30;

            string consequence;
            if (rowsBelow <= 0 && namesAbove != null && namesAbove.Count > 0)
            {
                string joined = namesAbove.Count == 1
                    ? namesAbove[0]
                    : string.Join(" and ", namesAbove);
                // "A and B both sit above…" — ManualShieldNothingBelowNamed expects the names phrase.
                consequence = DisplayCopy.ManualShieldNothingBelowNamed(
                    namesAbove.Count == 1 ? namesAbove[0] + " and nothing else" : joined);
                // Prefer the drawn 5j form when we have names.
                if (namesAbove.Count >= 2)
                {
                    consequence = string.Format(
                        CultureInfo.InvariantCulture,
                        "Nothing is ranked below this row, so browsing interrupts nothing. {0} and {1} both sit above it and can still take the display.",
                        namesAbove[0],
                        namesAbove[namesAbove.Count - 1]);
                }
                else
                {
                    consequence = DisplayCopy.ManualShieldNoneBelow;
                }
            }
            else
            {
                consequence = DisplayCopy.ManualShieldNoneBelow;
            }

            bool showAmber = !nextMapped && !prevMapped;

            return new ManualOptionsModel(
                returnEnabled: enabled,
                shownSeconds: shownSeconds,
                consequence: consequence,
                showUnmappedAmber: showAmber);
        }

        private static IReadOnlyList<PriorityExplainerCardModel> BuildExplainers(bool isItm)
        {
            if (isItm)
            {
                return new ReadOnlyCollection<PriorityExplainerCardModel>(new[]
                {
                    new PriorityExplainerCardModel(
                        DisplayCopy.TwoPinnedRows, DisplayCopy.TwoPinnedRowsBody),
                    new PriorityExplainerCardModel(
                        DisplayCopy.Dismissing, DisplayCopy.DismissingBody),
                });
            }

            return new ReadOnlyCollection<PriorityExplainerCardModel>(new[]
            {
                new PriorityExplainerCardModel(
                    DisplayCopy.OneLaw, DisplayCopy.OneLawBody),
            });
        }

        // ── Pickers ──────────────────────────────────────────────────────

        private static PriorityPickerModel BuildIdlePicker(
            DisplayConfigV2 config, WheelCatalog catalog, DisplayResolutionSnapshotModel resolution)
        {
            var groups = new List<PriorityPickerGroupModel>();
            var idle = config?.Priority?.Rest?.Idle;
            string selectedKey = IdleSelectionKey(idle);

            // PAGES ON THIS WHEEL
            var pageItems = new List<PriorityPickerItemModel>();
            string baseKey = PageRefKey(config?.Priority?.Rest?.InSessionPage);
            if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var p = config.Pages[i];
                    if (p == null) continue;
                    string key;
                    string badge = null;
                    string name;
                    PageRef pageRef;
                    if (p.Kind == PageEntryKind.ItmPage)
                    {
                        key = "page:itm:" + (p.CatalogPageId ?? string.Empty);
                        int index = CatalogIndex(catalog, p.CatalogPageId);
                        badge = index > 0
                            ? DisplayCopy.ItmPageBadge(index)
                            : DisplayCopy.ItmBadge;
                        name = !string.IsNullOrEmpty(p.NameOverride)
                            ? p.NameOverride
                            : ResolveItmPageName(p.CatalogPageId, config, catalog);
                        pageRef = new PageRef
                        {
                            Kind = PageRefKind.ItmPage,
                            CatalogPageId = p.CatalogPageId,
                        };
                    }
                    else if (p.Kind == PageEntryKind.HostedPage)
                    {
                        key = "page:hosted:" + (p.Id ?? string.Empty);
                        badge = DisplayCopy.LegacyBadge;
                        name = p.Name ?? p.Id ?? string.Empty;
                        pageRef = new PageRef
                        {
                            Kind = PageRefKind.HostedPage,
                            Id = p.Id,
                        };
                    }
                    else continue;

                    bool isBase = string.Equals(key, "page:" + baseKey, StringComparison.Ordinal)
                        || string.Equals(PageRefKey(pageRef), baseKey, StringComparison.Ordinal);
                    bool selected = string.Equals(selectedKey, key, StringComparison.Ordinal);
                    pageItems.Add(new PriorityPickerItemModel(
                        key: key,
                        badge: badge,
                        name: name,
                        trailingNote: isBase ? DisplayCopy.AlsoTheBasePage
                            : (selected ? DisplayCopy.Selected : null),
                        isSelected: selected,
                        isEnabled: true,
                        capabilityNote: null,
                        idleKind: IdleKind.Page,
                        pageRef: pageRef,
                        screen: WheelScreenCommand.Unknown,
                        playlistId: null));
                }
            }

            // Also surface catalog ITM pages not yet in document.Pages.
            if (catalog?.Itm?.Pages != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < pageItems.Count; i++)
                    seen.Add(pageItems[i].Key);
                for (int i = 0; i < catalog.Itm.Pages.Count; i++)
                {
                    var cp = catalog.Itm.Pages[i];
                    if (cp == null) continue;
                    string key = "page:itm:" + cp.Id;
                    if (!seen.Add(key)) continue;
                    string badge = cp.Index > 0
                        ? DisplayCopy.ItmPageBadge(cp.Index)
                        : DisplayCopy.ItmBadge;
                    var pageRef = new PageRef
                    {
                        Kind = PageRefKind.ItmPage,
                        CatalogPageId = cp.Id,
                    };
                    bool isBase = string.Equals(PageRefKey(pageRef), baseKey, StringComparison.Ordinal);
                    bool selected = string.Equals(selectedKey, key, StringComparison.Ordinal);
                    pageItems.Add(new PriorityPickerItemModel(
                        key: key,
                        badge: badge,
                        name: cp.Name ?? cp.Id,
                        trailingNote: isBase ? DisplayCopy.AlsoTheBasePage
                            : (selected ? DisplayCopy.Selected : null),
                        isSelected: selected,
                        isEnabled: true,
                        capabilityNote: null,
                        idleKind: IdleKind.Page,
                        pageRef: pageRef,
                        screen: WheelScreenCommand.Unknown,
                        playlistId: null));
                }
            }

            groups.Add(new PriorityPickerGroupModel(
                DisplayCopy.PagesOnThisWheel,
                new ReadOnlyCollection<PriorityPickerItemModel>(pageItems)));

            // BUILT-IN SCREENS (P5: no "Keep the last page shown"; P8: bind tri-state).
            var screens = catalog?.ScreenCommands;
            var screenItems = new List<PriorityPickerItemModel>
            {
                ScreenItem(WheelScreenCommand.Logo, DisplayCopy.TheWheelsLogo,
                    screens?.Logo, selectedKey),
                ScreenItem(WheelScreenCommand.Blank, DisplayCopy.ABlankDisplay,
                    screens?.Blank, selectedKey),
            };
            // White / LogoInverted available when catalog lists them; still tri-state.
            if (screens != null)
            {
                screenItems.Add(ScreenItem(
                    WheelScreenCommand.White, DisplayCopy.WhiteScreen,
                    screens.White, selectedKey));
                screenItems.Add(ScreenItem(
                    WheelScreenCommand.LogoInverted, DisplayCopy.LogoInvertedScreen,
                    screens.LogoInverted, selectedKey));
            }

            groups.Add(new PriorityPickerGroupModel(
                DisplayCopy.BuiltInScreens,
                new ReadOnlyCollection<PriorityPickerItemModel>(screenItems)));

            // PLAYLISTS group — document playlists, read-only (setup-authored; no editor).
            // P6 rider (b): degraded / all-skipped playlists stay VISIBLE and marked with
            // their steps + skip labels — never dropped from the presentation.
            var playlistItems = new List<PriorityPickerItemModel>();
            if (config?.Playlists != null)
            {
                for (int i = 0; i < config.Playlists.Count; i++)
                {
                    var pl = config.Playlists[i];
                    if (pl == null || string.IsNullOrWhiteSpace(pl.Id))
                        continue;
                    string key = "playlist:" + pl.Id;
                    bool selected = string.Equals(selectedKey, key, StringComparison.Ordinal);
                    string name = !string.IsNullOrEmpty(pl.Name)
                        ? pl.Name
                        : GeneratedPlaylistName(pl, config, catalog);
                    string stepSummary = PlaylistStepSummary(pl, catalog);
                    // Degraded whole (0 resolvable / duplicate / reserved): still listed;
                    // capability note carries skip/degrade labels so honesty set holds.
                    bool enabled = !pl.DegradedAtLoad;
                    string note = selected
                        ? DisplayCopy.Selected
                        : (stepSummary ?? (pl.DegradedAtLoad ? DisplayCopy.PlaylistStepSkipped : null));
                    playlistItems.Add(new PriorityPickerItemModel(
                        key: key,
                        badge: DisplayCopy.PlaylistBadge,
                        name: name,
                        trailingNote: note,
                        isSelected: selected,
                        isEnabled: enabled,
                        capabilityNote: stepSummary
                            ?? (pl.DegradedAtLoad ? DisplayCopy.PlaylistStepSkipped : null),
                        idleKind: IdleKind.Playlist,
                        pageRef: null,
                        screen: WheelScreenCommand.Unknown,
                        playlistId: pl.Id));
                }
            }

            groups.Add(new PriorityPickerGroupModel(
                DisplayCopy.PlaylistsGroup,
                playlistItems.Count == 0
                    ? NoPickerItems
                    : new ReadOnlyCollection<PriorityPickerItemModel>(playlistItems),
                emptyState: playlistItems.Count == 0 ? DisplayCopy.NoPlaylistsYet : null));

            return new PriorityPickerModel(
                searchPlaceholder: DisplayCopy.SearchPagesScreensPlaylists,
                groups: new ReadOnlyCollection<PriorityPickerGroupModel>(groups),
                footer: DisplayCopy.PlaylistsWrittenBySetups,
                includeScreens: true,
                includePlaylists: true);
        }

        private static PriorityPickerModel BuildBasePagePicker(
            DisplayConfigV2 config, WheelCatalog catalog)
        {
            // FIDELITY-BREAK (owner order, irreducible #2): Base-page editor is unboarded.
            // Reuses the 5n picker shell MINUS screens/playlists groups (InSessionPage
            // takes page refs only). Design session owes the drawn board.
            var idle = BuildIdlePicker(config, catalog, DisplayResolutionSnapshotModel.Empty);
            var pageGroupOnly = new List<PriorityPickerGroupModel>();
            for (int i = 0; i < idle.Groups.Count; i++)
            {
                if (string.Equals(
                        idle.Groups[i].Header, DisplayCopy.PagesOnThisWheel,
                        StringComparison.Ordinal))
                    pageGroupOnly.Add(idle.Groups[i]);
            }

            // Re-mark selection against InSessionPage, not Idle.
            string baseKey = "page:" + (PageRefKey(config?.Priority?.Rest?.InSessionPage) ?? string.Empty);
            var remapped = new List<PriorityPickerGroupModel>();
            for (int g = 0; g < pageGroupOnly.Count; g++)
            {
                var items = new List<PriorityPickerItemModel>();
                for (int i = 0; i < pageGroupOnly[g].Items.Count; i++)
                {
                    var src = pageGroupOnly[g].Items[i];
                    bool selected = string.Equals(src.Key, baseKey, StringComparison.Ordinal)
                        || (src.PageRef != null
                            && string.Equals(
                                "page:" + PageRefKey(src.PageRef),
                                baseKey, StringComparison.Ordinal));
                    items.Add(new PriorityPickerItemModel(
                        key: src.Key,
                        badge: src.Badge,
                        name: src.Name,
                        trailingNote: selected ? DisplayCopy.Selected : src.TrailingNote,
                        isSelected: selected,
                        isEnabled: src.IsEnabled,
                        capabilityNote: src.CapabilityNote,
                        idleKind: IdleKind.Page,
                        pageRef: src.PageRef,
                        screen: WheelScreenCommand.Unknown,
                        playlistId: null));
                }
                remapped.Add(new PriorityPickerGroupModel(
                    pageGroupOnly[g].Header,
                    new ReadOnlyCollection<PriorityPickerItemModel>(items)));
            }

            return new PriorityPickerModel(
                searchPlaceholder: DisplayCopy.SearchPagesScreensPlaylists,
                groups: new ReadOnlyCollection<PriorityPickerGroupModel>(remapped),
                footer: null,
                includeScreens: false,
                includePlaylists: false);
        }

        private static PriorityPickerItemModel ScreenItem(
            WheelScreenCommand screen, string name, bool? capability, string selectedKey)
        {
            string key = "screen:" + screen.ToString().ToLowerInvariant();
            // P8: true → supported here; null → untested; false → greyed.
            bool enabled = capability != false;
            string note = capability == true
                ? DisplayCopy.SupportedHere
                : (capability == false ? null : DisplayCopy.UntestedOnThisWheel);
            bool selected = string.Equals(selectedKey, key, StringComparison.Ordinal)
                || (selectedKey == "blank" && screen == WheelScreenCommand.Blank);
            return new PriorityPickerItemModel(
                key: key,
                badge: null,
                name: name,
                trailingNote: selected ? DisplayCopy.Selected : note,
                isSelected: selected,
                isEnabled: enabled,
                capabilityNote: note,
                idleKind: screen == WheelScreenCommand.Blank ? IdleKind.Blank : IdleKind.Screen,
                pageRef: null,
                screen: screen,
                playlistId: null);
        }

        // ── Shared projection helpers (lifted from Overview idiom) ───────

        private static string ResolveStatus(
            CarrierResolutionRowModel carrier,
            bool isWinner,
            bool isOff,
            bool provisionalCantRun)
        {
            if (provisionalCantRun)
                return DisplayCopy.CantRunHere;
            // P1: winner status column empty (OnScreen=""); highlight is structural.
            if (isWinner)
                return DisplayCopy.OnScreen;
            if (isOff)
                return DisplayCopy.Off;
            if (carrier == null)
                return DisplayCopy.Waiting;
            if (carrier.RowLabelCopies != null && carrier.RowLabelCopies.Count > 0)
            {
                for (int i = 0; i < carrier.RowLabelCopies.Count; i++)
                {
                    var lab = carrier.RowLabelCopies[i];
                    if (lab == DisplayCopy.Off
                        || lab == DisplayCopy.Dismissed
                        || lab == DisplayCopy.CantRunHere)
                        return lab;
                }
            }
            if (!string.IsNullOrEmpty(carrier.PresenceCopy))
                return carrier.PresenceCopy;
            return DisplayCopy.Waiting;
        }

        private static PriorityDestinationModel ResolveDestination(
            PriorityRow row,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            bool showKindBadges)
        {
            if (row.Kind == PriorityRowKind.Manual)
            {
                return new PriorityDestinationModel(
                    NoBadges, DisplayCopy.ManualPaging, false, false, false);
            }
            return ResolvePageRefDestination(row.Target, config, catalog, showKindBadges);
        }

        private static PriorityDestinationModel ResolvePageRefDestination(
            PageRef pageRef,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            bool showKindBadges)
        {
            if (pageRef == null)
                return new PriorityDestinationModel(NoBadges, string.Empty, false, false, false);

            switch (pageRef.Kind)
            {
                case PageRefKind.ItmPage:
                {
                    int index = CatalogIndex(catalog, pageRef.CatalogPageId);
                    var badges = showKindBadges
                        ? new ReadOnlyCollection<string>(new[]
                        {
                            index > 0
                                ? DisplayCopy.ItmPageBadge(index)
                                : DisplayCopy.ItmBadge,
                        })
                        : NoBadges;
                    string name = ResolveItmPageName(pageRef.CatalogPageId, config, catalog);
                    return new PriorityDestinationModel(
                        badges, name, false, false, false);
                }
                case PageRefKind.HostedPage:
                {
                    var badges = showKindBadges
                        ? new ReadOnlyCollection<string>(new[] { DisplayCopy.LegacyBadge })
                        : NoBadges;
                    string name = ResolveHostedPageName(pageRef.Id, config);
                    return new PriorityDestinationModel(
                        badges, name, false, true, false);
                }
                case PageRefKind.Cycle:
                {
                    var badges = showKindBadges
                        ? ResolveCycleBadges(pageRef.Id, config, catalog)
                        : NoBadges;
                    return new PriorityDestinationModel(
                        badges, string.Empty, true, false, false);
                }
                default:
                    return new PriorityDestinationModel(
                        NoBadges, pageRef.Id ?? string.Empty, false, false, false);
            }
        }

        private static IReadOnlyList<string> ResolveCycleBadges(
            string cycleId, DisplayConfigV2 config, WheelCatalog catalog)
        {
            var cycle = FindCycle(config, cycleId);
            if (cycle?.Members == null || cycle.Members.Count == 0)
                return NoBadges;
            var list = new List<string>();
            for (int i = 0; i < cycle.Members.Count; i++)
            {
                var m = cycle.Members[i];
                if (m == null) continue;
                string badge = null;
                if (m.Kind == PageRefKind.ItmPage)
                {
                    int index = CatalogIndex(catalog, m.CatalogPageId);
                    badge = index > 0 ? DisplayCopy.ItmPageBadge(index) : DisplayCopy.ItmBadge;
                }
                else if (m.Kind == PageRefKind.HostedPage)
                {
                    badge = DisplayCopy.LegacyBadge;
                }
                if (badge == null) continue;
                if (list.Count > 0)
                    list.Add(DisplayCopy.CycleBadgeJoin);
                list.Add(badge);
            }
            return list.Count == 0
                ? NoBadges
                : new ReadOnlyCollection<string>(list);
        }

        private static string IdleTargetLabel(
            IdleSpec idle, DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (idle == null || idle.Kind == IdleKind.Unknown || idle.Kind == IdleKind.Blank)
                return DisplayCopy.ABlankDisplay;
            switch (idle.Kind)
            {
                case IdleKind.Screen:
                    return ScreenName(idle.Screen);
                case IdleKind.Page:
                {
                    var dest = ResolvePageRefDestination(idle.Page, config, catalog, true);
                    return dest.Badges.Count > 0
                        ? DisplayCopy.PageCaption(dest.Badges[0], dest.Name)
                        : dest.Name;
                }
                case IdleKind.Playlist:
                {
                    var pl = FindPlaylist(config, idle.Playlist);
                    if (pl == null)
                        return idle.Playlist ?? DisplayCopy.ABlankDisplay;
                    return !string.IsNullOrEmpty(pl.Name)
                        ? pl.Name
                        : GeneratedPlaylistName(pl, config, catalog);
                }
                default:
                    return DisplayCopy.ABlankDisplay;
            }
        }

        private static string IdleSelectionKey(IdleSpec idle)
        {
            if (idle == null || idle.Kind == IdleKind.Unknown || idle.Kind == IdleKind.Blank)
                return "screen:blank";
            switch (idle.Kind)
            {
                case IdleKind.Screen:
                    return "screen:" + idle.Screen.ToString().ToLowerInvariant();
                case IdleKind.Page:
                    return "page:" + (PageRefKey(idle.Page) ?? string.Empty);
                case IdleKind.Playlist:
                    return "playlist:" + (idle.Playlist ?? string.Empty);
                default:
                    return "screen:blank";
            }
        }

        private static string PageRefKey(PageRef pageRef)
        {
            if (pageRef == null) return null;
            switch (pageRef.Kind)
            {
                case PageRefKind.ItmPage:
                    return string.IsNullOrEmpty(pageRef.CatalogPageId)
                        ? null : "itm:" + pageRef.CatalogPageId;
                case PageRefKind.HostedPage:
                    return string.IsNullOrEmpty(pageRef.Id)
                        ? null : "hosted:" + pageRef.Id;
                case PageRefKind.Cycle:
                    return string.IsNullOrEmpty(pageRef.Id)
                        ? null : "cycle:" + pageRef.Id;
                default:
                    return null;
            }
        }

        private static string BuildPreviewCaption(
            DisplayValuesSnapshot values,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            DisplayResolutionSnapshotModel resolution)
        {
            if (values != null && !string.IsNullOrEmpty(values.PageName))
                return values.PageName;
            string dest = FindDisplayWinnerDestinationId(resolution);
            if (string.IsNullOrEmpty(dest))
                return string.Empty;
            if (dest.StartsWith("hosted:", StringComparison.Ordinal))
                return ResolveHostedPageName(dest.Substring(7), config);
            if (dest.StartsWith("itm:", StringComparison.Ordinal))
                return ResolveItmPageName(dest.Substring(4), config, catalog);
            return string.Empty;
        }

        private static string CarrierIdForRow(PriorityRow row)
        {
            if (row == null) return null;
            if (row.Kind == PriorityRowKind.Manual)
                return SeatArbiter.ManualCarrierId;
            return row.Id;
        }

        private static string FirstEnabledSummonId(PriorityRow row)
        {
            if (row?.Summons == null) return null;
            for (int i = 0; i < row.Summons.Count; i++)
            {
                var s = row.Summons[i];
                if (s != null && s.EffectivelyEnabled)
                    return s.Id;
            }
            // Fall back to first summon even if off (for toggle-on).
            for (int i = 0; i < row.Summons.Count; i++)
            {
                if (row.Summons[i] != null)
                    return row.Summons[i].Id;
            }
            return null;
        }

        private static bool IsSummonEnabled(PriorityRow row, string summonId)
        {
            if (row?.Summons == null || string.IsNullOrEmpty(summonId))
                return true;
            for (int i = 0; i < row.Summons.Count; i++)
            {
                var s = row.Summons[i];
                if (s != null && string.Equals(s.Id, summonId, StringComparison.Ordinal))
                    return s.Enabled;
            }
            return true;
        }

        private static string FindDisplayWinnerCarrierId(DisplayResolutionSnapshotModel resolution)
        {
            if (resolution?.SurfaceWinners == null) return null;
            for (int i = 0; i < resolution.SurfaceWinners.Count; i++)
            {
                var w = resolution.SurfaceWinners[i];
                if (w != null
                    && string.Equals(w.SurfaceId, SeatArbiter.DisplaySurfaceId, StringComparison.Ordinal))
                    return w.WinnerCarrierId;
            }
            return null;
        }

        private static string FindDisplayWinnerDestinationId(DisplayResolutionSnapshotModel resolution)
        {
            if (resolution?.SurfaceWinners == null) return null;
            for (int i = 0; i < resolution.SurfaceWinners.Count; i++)
            {
                var w = resolution.SurfaceWinners[i];
                if (w != null
                    && string.Equals(w.SurfaceId, SeatArbiter.DisplaySurfaceId, StringComparison.Ordinal))
                    return w.DestinationId;
            }
            return null;
        }

        private static Dictionary<string, CarrierResolutionRowModel> IndexCarriers(
            DisplayResolutionSnapshotModel resolution)
        {
            var map = new Dictionary<string, CarrierResolutionRowModel>(StringComparer.Ordinal);
            if (resolution?.Carriers == null) return map;
            for (int i = 0; i < resolution.Carriers.Count; i++)
            {
                var c = resolution.Carriers[i];
                if (c?.CarrierId == null) continue;
                if (!map.ContainsKey(c.CarrierId)
                    || string.Equals(c.SurfaceId, SeatArbiter.DisplaySurfaceId, StringComparison.Ordinal))
                    map[c.CarrierId] = c;
            }
            return map;
        }

        private static Dictionary<string, AggregateMembershipModel> IndexAggregates(
            DisplayResolutionSnapshotModel resolution)
        {
            var map = new Dictionary<string, AggregateMembershipModel>(StringComparer.Ordinal);
            if (resolution?.Aggregates == null) return map;
            for (int i = 0; i < resolution.Aggregates.Count; i++)
            {
                var a = resolution.Aggregates[i];
                if (a?.SeatId == null) continue;
                map[a.SeatId] = a;
            }
            return map;
        }

        private static bool ContainsLabel(IReadOnlyList<string> labels, string want)
        {
            if (labels == null) return false;
            for (int i = 0; i < labels.Count; i++)
            {
                if (string.Equals(labels[i], want, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static int CatalogIndex(WheelCatalog catalog, string catalogPageId)
        {
            if (catalog?.Itm?.Pages == null || string.IsNullOrEmpty(catalogPageId))
                return 0;
            var pages = catalog.Itm.Pages;
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i] != null
                    && string.Equals(pages[i].Id, catalogPageId, StringComparison.Ordinal))
                    return pages[i].Index;
            }
            return 0;
        }

        private static string ResolveItmPageName(
            string catalogPageId, DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var p = config.Pages[i];
                    if (p == null || p.Kind != PageEntryKind.ItmPage) continue;
                    if (!string.Equals(p.CatalogPageId, catalogPageId, StringComparison.Ordinal))
                        continue;
                    if (!string.IsNullOrEmpty(p.NameOverride))
                        return p.NameOverride;
                }
            }
            if (catalog?.Itm?.Pages != null)
            {
                for (int i = 0; i < catalog.Itm.Pages.Count; i++)
                {
                    var p = catalog.Itm.Pages[i];
                    if (p != null
                        && string.Equals(p.Id, catalogPageId, StringComparison.Ordinal))
                        return p.Name ?? catalogPageId;
                }
            }
            return catalogPageId ?? string.Empty;
        }

        private static string ResolveHostedPageName(string id, DisplayConfigV2 config)
        {
            if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var p = config.Pages[i];
                    if (p == null || p.Kind != PageEntryKind.HostedPage) continue;
                    if (string.Equals(p.Id, id, StringComparison.Ordinal))
                        return p.Name ?? id;
                }
            }
            return id ?? string.Empty;
        }

        private static CycleEntry FindCycle(DisplayConfigV2 config, string cycleId)
        {
            if (config?.Cycles == null || string.IsNullOrEmpty(cycleId))
                return null;
            for (int i = 0; i < config.Cycles.Count; i++)
            {
                var c = config.Cycles[i];
                if (c != null && string.Equals(c.Id, cycleId, StringComparison.Ordinal))
                    return c;
            }
            return null;
        }

        private static int FindCyclePeriodMs(DisplayConfigV2 config, string cycleId)
        {
            var cycle = FindCycle(config, cycleId);
            return cycle?.PeriodMs ?? 0;
        }

        private static string ScreenName(WheelScreenCommand screen)
        {
            switch (screen)
            {
                case WheelScreenCommand.Logo: return DisplayCopy.TheWheelsLogo;
                case WheelScreenCommand.Blank: return DisplayCopy.ABlankDisplay;
                case WheelScreenCommand.White: return DisplayCopy.WhiteScreen;
                case WheelScreenCommand.LogoInverted: return DisplayCopy.LogoInvertedScreen;
                default: return DisplayCopy.ABlankDisplay;
            }
        }

        /// <summary>
        /// Map EffectiveRows display index (ranked only) → authored Rows index.
        /// Returns -1 when the display row is materialized (not yet in Rows).
        /// </summary>
        public static int AuthoredIndexOf(DisplayConfigV2 config, string rowId)
        {
            if (config?.Priority?.Rows == null || string.IsNullOrEmpty(rowId))
                return -1;
            var rows = config.Priority.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null
                    && string.Equals(rows[i].Id, rowId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Count priority rows targeting a page (for removal confirm copy).
        /// </summary>
        public static int CountRowsForTarget(DisplayConfigV2 config, PageRef target)
        {
            if (config?.Priority?.Rows == null || target == null)
                return 0;
            string key = PageRefKey(target);
            if (key == null) return 0;
            int n = 0;
            for (int i = 0; i < config.Priority.Rows.Count; i++)
            {
                var r = config.Priority.Rows[i];
                if (r == null || r.Kind == PriorityRowKind.Manual) continue;
                if (string.Equals(PageRefKey(r.Target), key, StringComparison.Ordinal))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Count authored overrides attributed to a page (for rows-only confirm copy —
        /// all overrides on the page's reach, including shared ladders).
        /// </summary>
        public static int CountOverridesForTarget(
            DisplayConfigV2 config, PageRef target, WheelCatalog catalog)
        {
            if (target == null) return 0;
            if (target.Kind == PageRefKind.HostedPage && config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var p = config.Pages[i];
                    if (p != null
                        && string.Equals(p.Id, target.Id, StringComparison.Ordinal)
                        && p.Layers != null)
                        return p.Layers.Count;
                }
                return 0;
            }
            // Reuse seat counter shape with a synthetic row.
            var fake = new PriorityRow { Target = target, Kind = PriorityRowKind.Seat };
            return CountOverridesOnPage(fake, config, catalog);
        }

        /// <summary>
        /// Derive next/previous page mapped flags from SimHub's plugin-action mapping
        /// targets (digest §5 / <see cref="IMappedRoleCatalog"/>). InputActionMapping
        /// persists the generated-action key (<c>plugin type.action name</c>).
        /// </summary>
        internal static void ResolvePageControlMapping(
            IReadOnlyList<string> targets,
            out bool nextPageMapped,
            out bool prevPageMapped)
        {
            nextPageMapped = false;
            prevPageMapped = false;
            if (targets == null || targets.Count == 0)
                return;

            for (int i = 0; i < targets.Count; i++)
            {
                string target = targets[i];
                if (string.IsNullOrEmpty(target))
                    continue;
                if (IsNextPageTarget(target))
                    nextPageMapped = true;
                else if (IsPrevPageTarget(target))
                    prevPageMapped = true;
            }
        }

        private static bool IsNextPageTarget(string target)
        {
            return string.Equals(
                target,
                DisplayPageActionHub.NextMappedTarget,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrevPageTarget(string target)
        {
            return string.Equals(
                target,
                DisplayPageActionHub.PreviousMappedTarget,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Build IdleSpec from a picker selection.</summary>
        public static IdleSpec IdleFromPickerItem(PriorityPickerItemModel item)
        {
            if (item == null)
                return new IdleSpec { Kind = IdleKind.Blank };
            if (item.IdleKind == IdleKind.Playlist
                && !string.IsNullOrWhiteSpace(item.PlaylistId))
            {
                return new IdleSpec
                {
                    Kind = IdleKind.Playlist,
                    Playlist = item.PlaylistId,
                };
            }
            if (item.IdleKind == IdleKind.Page && item.PageRef != null)
            {
                return new IdleSpec
                {
                    Kind = IdleKind.Page,
                    Page = item.PageRef,
                };
            }
            if (item.IdleKind == IdleKind.Blank
                || item.Screen == WheelScreenCommand.Blank)
            {
                return new IdleSpec { Kind = IdleKind.Blank };
            }
            return new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = item.Screen,
            };
        }

        private static PlaylistEntry FindPlaylist(DisplayConfigV2 config, string id)
        {
            if (config?.Playlists == null || string.IsNullOrWhiteSpace(id))
                return null;
            for (int i = 0; i < config.Playlists.Count; i++)
            {
                var pl = config.Playlists[i];
                if (pl != null
                    && string.Equals(pl.Id, id, StringComparison.OrdinalIgnoreCase))
                    return pl;
            }
            return null;
        }

        private static string GeneratedPlaylistName(
            PlaylistEntry pl, DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (pl?.Steps == null || pl.Steps.Count == 0)
                return pl?.Id ?? string.Empty;
            // Short join of step destination names (same spirit as summon name generation).
            var parts = new List<string>(pl.Steps.Count);
            for (int i = 0; i < pl.Steps.Count && parts.Count < 3; i++)
            {
                var step = pl.Steps[i];
                if (step?.Destination == null) continue;
                parts.Add(StepDestinationName(step.Destination, config, catalog));
            }
            return parts.Count == 0 ? (pl.Id ?? string.Empty) : string.Join(" → ", parts);
        }

        /// <summary>
        /// Read-only step summary with skip / clamp labels (P6 rider b, P2) for the
        /// picker trailing note. Authored steps are never dropped; degraded/skipped
        /// steps carry <see cref="DisplayCopy.PlaylistStepSkipped"/>; sub-floor
        /// durations show the clamped value + degrade-visible marker.
        /// </summary>
        private static string PlaylistStepSummary(PlaylistEntry pl, WheelCatalog catalog)
        {
            if (pl?.Steps == null || pl.Steps.Count == 0)
                return null;
            var parts = new List<string>(pl.Steps.Count);
            for (int i = 0; i < pl.Steps.Count; i++)
            {
                var step = pl.Steps[i];
                string name = StepDestinationName(step?.Destination, null, catalog);
                bool skipped = step?.Destination == null
                    || step.DegradedAtLoad
                    || step.Destination.DegradedAtLoad
                    || StepCapabilitySkipped(step.Destination, catalog);
                if (skipped)
                {
                    parts.Add(DisplayCopy.PlaylistStepLine(name, DisplayCopy.PlaylistStepSkipped));
                }
                else if (step.DurationMsPresent)
                {
                    parts.Add(DisplayCopy.PlaylistStepLine(
                        name, DisplayCopy.PlaylistStepDurationLabel(step)));
                }
                else
                {
                    parts.Add(name);
                }
            }
            return parts.Count == 0 ? null : string.Join(" → ", parts);
        }

        private static bool StepCapabilitySkipped(IdleSpec dest, WheelCatalog catalog)
        {
            if (dest == null || catalog?.ScreenCommands == null)
                return false;
            if (dest.Kind != IdleKind.Screen)
                return false;
            if (dest.Screen == WheelScreenCommand.Unknown || dest.Screen == WheelScreenCommand.Blank)
                return true;
            bool? supported = IdleCompile.CapabilityOf(catalog.ScreenCommands, dest.Screen);
            return supported == false;
        }

        private static string StepDestinationName(
            IdleSpec dest, DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (dest == null) return DisplayCopy.UnavailablePlaylistDestination;
            switch (dest.Kind)
            {
                case IdleKind.Blank:
                    return DisplayCopy.ABlankDisplay;
                case IdleKind.Screen:
                    return ScreenName(dest.Screen);
                case IdleKind.Page:
                {
                    var d = ResolvePageRefDestination(dest.Page, config, catalog, true);
                    return string.IsNullOrEmpty(d.Name) ? (dest.Page?.Id ?? dest.Page?.CatalogPageId ?? string.Empty) : d.Name;
                }
                default:
                    return dest.KindRaw ?? string.Empty;
            }
        }

        // ── Surface C helpers (OWNER-WAIVED FIDELITY) ────────────────────

        private static IReadOnlyList<PrioritySplitSummonModel> BuildSplitSummons(
            PriorityRow row, AliasTable aliases)
        {
            if (row?.Summons == null || row.Kind != PriorityRowKind.Seat)
                return NoSplitSummons;
            var choices = new List<PrioritySplitSummonModel>();
            for (int i = 0; i < row.Summons.Count; i++)
            {
                var summon = row.Summons[i];
                if (summon == null || string.IsNullOrEmpty(summon.Id))
                    continue;
                string label = !string.IsNullOrWhiteSpace(summon.Name)
                    ? summon.Name
                    : ConditionSentence.From(summon.Condition, summon.Lifetime, aliases);
                if (string.IsNullOrWhiteSpace(label))
                    label = summon.Id;
                choices.Add(new PrioritySplitSummonModel(
                    summon.Id, label, summon.EffectivelyEnabled));
            }
            return choices.Count == 0
                ? NoSplitSummons
                : new ReadOnlyCollection<PrioritySplitSummonModel>(choices);
        }

        /// <summary>
        /// OWNER-WAIVED FIDELITY: name after the › marker — summon name/sentence, or
        /// the child's own name (5j: "names the layer it came from").
        /// </summary>
        private static string ResolveSatelliteReferenceName(
            PriorityRow row,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases)
        {
            if (row == null)
                return null;

            if (row.ChildRef != null)
            {
                if (!string.IsNullOrEmpty(row.ChildRef.Field)
                    && !string.IsNullOrEmpty(row.ChildRef.OverrideId))
                {
                    if (FieldLadderMap.TryFindOverride(
                            config, catalog, ParseParamId(row.ChildRef.Field),
                            row.ChildRef.OverrideId, out var ov)
                        && ov != null)
                    {
                        return ResolveFieldName(
                            catalog, ParseParamId(row.ChildRef.Field), row.ChildRef.OverrideId);
                    }
                    return row.ChildRef.OverrideId;
                }

                if (!string.IsNullOrEmpty(row.ChildRef.PageId)
                    && !string.IsNullOrEmpty(row.ChildRef.LayerId)
                    && config?.Pages != null)
                {
                    for (int i = 0; i < config.Pages.Count; i++)
                    {
                        var p = config.Pages[i];
                        if (p == null || p.Layers == null
                            || !string.Equals(p.Id, row.ChildRef.PageId, StringComparison.Ordinal))
                            continue;
                        for (int l = 0; l < p.Layers.Count; l++)
                        {
                            var layer = p.Layers[l];
                            if (layer != null
                                && string.Equals(
                                    layer.Id, row.ChildRef.LayerId, StringComparison.Ordinal))
                            {
                                return !string.IsNullOrWhiteSpace(layer.Name)
                                    ? layer.Name
                                    : layer.Id;
                            }
                        }
                    }
                    return row.ChildRef.LayerId;
                }
            }

            // Summons-satellite: first effectively-enabled summon name/sentence.
            if (row.Summons != null)
            {
                for (int i = 0; i < row.Summons.Count; i++)
                {
                    var s = row.Summons[i];
                    if (s == null || !s.EffectivelyEnabled)
                        continue;
                    if (!string.IsNullOrWhiteSpace(s.Name))
                        return s.Name;
                    string sentence = ConditionSentence.From(s.Condition, s.Lifetime, aliases);
                    if (!string.IsNullOrEmpty(sentence))
                        return sentence;
                    return s.Id;
                }
            }

            return null;
        }

        private static string ResolveChildRefDetail(
            ChildRef childRef,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            AliasTable aliases)
        {
            if (childRef == null)
                return string.Empty;
            if (!string.IsNullOrEmpty(childRef.Field)
                && !string.IsNullOrEmpty(childRef.OverrideId)
                && FieldLadderMap.TryFindOverride(
                    config, catalog, ParseParamId(childRef.Field),
                    childRef.OverrideId, out var ov)
                && ov != null)
            {
                string sentence = ConditionSentence.From(ov.Condition, ov.Lifetime, aliases);
                return (sentence ?? string.Empty) + LifetimeSuffix(ov.Lifetime);
            }
            if (!string.IsNullOrEmpty(childRef.PageId)
                && !string.IsNullOrEmpty(childRef.LayerId)
                && config?.Pages != null)
            {
                for (int p = 0; p < config.Pages.Count; p++)
                {
                    var page = config.Pages[p];
                    if (page?.Layers == null
                        || !string.Equals(page.Id, childRef.PageId, StringComparison.Ordinal))
                        continue;
                    for (int l = 0; l < page.Layers.Count; l++)
                    {
                        var layer = page.Layers[l];
                        if (layer == null
                            || !string.Equals(layer.Id, childRef.LayerId, StringComparison.Ordinal))
                            continue;
                        string sentence = ConditionSentence.From(
                            layer.Condition, layer.Lifetime, aliases);
                        return (sentence ?? string.Empty) + LifetimeSuffix(layer.Lifetime);
                    }
                }
            }
            return string.Empty;
        }

        private static string ResolveFieldName(
            WheelCatalog catalog, ushort paramId, string fallback)
        {
            var fields = catalog?.Itm?.Fields;
            if (fields != null)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    if (field == null || field.ParamId != paramId)
                        continue;
                    if (!string.IsNullOrWhiteSpace(field.DisplayLabel))
                        return field.DisplayLabel;
                    if (!string.IsNullOrWhiteSpace(field.ShortCode))
                        return field.ShortCode;
                    if (!string.IsNullOrWhiteSpace(field.FirmwareLabel))
                        return field.FirmwareLabel;
                    if (!string.IsNullOrWhiteSpace(field.Id))
                        return field.Id;
                }
            }
            return fallback;
        }

        private static string ResolveSatelliteDegradedReason(PriorityRow row)
        {
            if (row == null)
                return DisplayCopy.SatelliteReasonUnavailable;
            if (row.ChildRefAmbiguous)
                return DisplayCopy.SatelliteReasonAmbiguousChild;
            if (row.TargetIgnored)
                return DisplayCopy.SatelliteReasonTargetIgnored;
            if (row.SummonsIgnored)
                return DisplayCopy.SatelliteReasonSummonsIgnored;
            return DisplayCopy.SatelliteReasonUnavailable;
        }

        private static ushort ParseParamId(string field)
        {
            if (ushort.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id))
                return id;
            return 0;
        }

        private static PriorityDestinationModel ResolveChildRefHostDestination(
            ChildRef childRef,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            bool showKindBadges)
        {
            if (childRef == null)
                return new PriorityDestinationModel(NoBadges, string.Empty, false, false, false);

            if (!string.IsNullOrEmpty(childRef.PageId))
            {
                return ResolvePageRefDestination(
                    new PageRef { Kind = PageRefKind.HostedPage, Id = childRef.PageId },
                    config, catalog, showKindBadges);
            }

            // Field child: host page from catalog placement of the param (first host).
            if (!string.IsNullOrEmpty(childRef.Field) && catalog != null)
            {
                ushort paramId = ParseParamId(childRef.Field);
                string logical = CatalogFields.LogicalIdForParam(catalog, paramId);
                if (!string.IsNullOrEmpty(logical))
                {
                    var hosts = CatalogFields.HostPageIds(catalog, logical);
                    if (hosts != null && hosts.Count > 0)
                    {
                        return ResolvePageRefDestination(
                            new PageRef
                            {
                                Kind = PageRefKind.ItmPage,
                                CatalogPageId = hosts[0],
                            },
                            config, catalog, showKindBadges);
                    }
                }
            }

            return new PriorityDestinationModel(NoBadges, string.Empty, false, false, false);
        }

        // ── Surface D: playlist read-only card ───────────────────────────

        /// <summary>
        /// Project the 5o read-only playlist card for a document playlist (picker expanded
        /// detail). Null when the playlist id is missing.
        /// </summary>
        public static PlaylistReadOnlyCardModel ProjectPlaylistCard(
            DisplayConfigV2 config,
            string playlistId,
            WheelCatalog catalog = null)
        {
            if (config?.Playlists == null || string.IsNullOrEmpty(playlistId))
                return null;

            PlaylistEntry pl = null;
            for (int i = 0; i < config.Playlists.Count; i++)
            {
                var p = config.Playlists[i];
                if (p != null && string.Equals(p.Id, playlistId, StringComparison.Ordinal))
                {
                    pl = p;
                    break;
                }
            }
            if (pl == null)
                return null;

            string name = !string.IsNullOrEmpty(pl.Name)
                ? pl.Name
                : GeneratedPlaylistName(pl, config, catalog);

            var steps = new List<PlaylistCardStepModel>();
            if (pl.Steps != null)
            {
                for (int i = 0; i < pl.Steps.Count; i++)
                {
                    var step = pl.Steps[i];
                    string destName = StepDestinationName(step?.Destination, config, catalog);
                    bool isLast = i == pl.Steps.Count - 1;
                    bool holds = isLast
                        && (pl.Terminal == PlaylistTerminal.Hold
                            || pl.Terminal == PlaylistTerminal.Unknown);
                    bool skipped = step?.Destination == null
                        || step.DegradedAtLoad
                        || step.Destination.DegradedAtLoad
                        || StepCapabilitySkipped(step.Destination, catalog);

                    string duration;
                    if (skipped)
                        duration = DisplayCopy.PlaylistStepSkipped;
                    else if (holds && !step.DurationMsPresent)
                        duration = DisplayCopy.PlaylistStepHolds;
                    else if (step.DurationMsPresent)
                        duration = DisplayCopy.PlaylistStepDurationLabel(step)
                            ?? DisplayCopy.PlaylistStepDuration(step.DurationMs);
                    else
                        duration = DisplayCopy.PlaylistStepHolds;

                    steps.Add(new PlaylistCardStepModel(
                        numeral: (i + 1).ToString(CultureInfo.InvariantCulture),
                        destinationName: destName,
                        durationLabel: duration,
                        isLast: isLast,
                        isSkipped: skipped));
                }
            }

            // The schema has no setup identity. Do not invent one from playlist data.
            string provenance = null;

            // Consumer: idle row when rest.idle targets this playlist.
            string consumer = DisplayCopy.OutsideASession;
            bool usedByIdle = config.Priority?.Rest?.Idle != null
                && config.Priority.Rest.Idle.Kind == IdleKind.Playlist
                && string.Equals(
                    config.Priority.Rest.Idle.Playlist, playlistId, StringComparison.Ordinal);
            string usedBy = usedByIdle
                ? DisplayCopy.UsedByOnThisProfile(consumer)
                : null;

            return new PlaylistReadOnlyCardModel(
                badge: DisplayCopy.PlaylistBadge,
                name: name,
                readOnlyChip: DisplayCopy.ReadOnlyChip,
                stepsLabel: DisplayCopy.StepsLabel,
                stepsCaption: DisplayCopy.StepsInOrderLastHolds,
                steps: new ReadOnlyCollection<PlaylistCardStepModel>(steps),
                provenance: provenance,
                usedByLine: usedBy,
                reRunLabel: DisplayCopy.ReRunTheSetup,
                // No setup writer — disabled with SpokeArrivingLater (phase-1 precedent).
                reRunEnabled: false,
                reRunTooltip: DisplayCopy.SpokeArrivingLater("Setups"));
        }
    }

    // ── Row / child / picker models ──────────────────────────────────────

    /// <summary>Priority ladder row visual state (digest §2; Normal bg differs from Overview).</summary>
    public enum PriorityRowState
    {
        Normal,
        Winner,
        Off,
        Pinned,
    }

    public enum PriorityChildKind
    {
        Entrypoint,
        Override,
        Layer,
        DerivedAggregate,
    }

    public sealed class PriorityRowModel
    {
        public const string ManualExpandKey = "__manual__";
        public const string BaseExpandKey = "__base__";
        public const string IdleExpandKey = "__idle__";

        public PriorityRowModel(
            string rowId,
            string rankText,
            int rankNumber,
            PriorityRowKind kind,
            PriorityDestinationModel destination,
            string detail,
            string statusCopy,
            PriorityRowState state,
            string carrierId,
            bool isPinned,
            bool showGrip,
            bool isExpanded,
            bool showDisclosure,
            bool isMaterialized,
            PageRef target,
            IReadOnlyList<PriorityChildRowModel> entrypoints,
            IReadOnlyList<PriorityChildRowModel> overrides,
            IReadOnlyList<PriorityChildRowModel> layers,
            bool showBaseBlock,
            string baseBlockBody,
            ManualOptionsModel manualOptions,
            bool showOverflowMenu,
            string primarySummonId,
            bool primarySummonEnabled,
            int? returnToRestAfterMs,
            string pageName,
            bool isBaseRow = false,
            bool isIdleRow = false,
            string idleTargetLabel = null,
            string idleTrailingNote = null,
            bool showPlaylistBadge = false,
            string splitReferenceName = null,
            IReadOnlyList<PrioritySplitSummonModel> splitSummons = null,
            bool canSplitEntrypoint = false,
            bool canRejoinHome = false)
        {
            RowId = rowId;
            RankText = rankText ?? string.Empty;
            RankNumber = rankNumber;
            Kind = kind;
            Destination = destination ?? new PriorityDestinationModel(
                Array.Empty<string>(), string.Empty, false, false, false);
            Detail = detail ?? string.Empty;
            StatusCopy = statusCopy ?? string.Empty;
            State = state;
            CarrierId = carrierId;
            IsPinned = isPinned;
            ShowGrip = showGrip;
            IsExpanded = isExpanded;
            ShowDisclosure = showDisclosure;
            IsMaterialized = isMaterialized;
            Target = target;
            Entrypoints = entrypoints ?? new ReadOnlyCollection<PriorityChildRowModel>(
                Array.Empty<PriorityChildRowModel>());
            Overrides = overrides ?? new ReadOnlyCollection<PriorityChildRowModel>(
                Array.Empty<PriorityChildRowModel>());
            Layers = layers ?? new ReadOnlyCollection<PriorityChildRowModel>(
                Array.Empty<PriorityChildRowModel>());
            ShowBaseBlock = showBaseBlock;
            BaseBlockBody = baseBlockBody;
            ManualOptions = manualOptions;
            ShowOverflowMenu = showOverflowMenu;
            PrimarySummonId = primarySummonId;
            PrimarySummonEnabled = primarySummonEnabled;
            ReturnToRestAfterMs = returnToRestAfterMs;
            PageName = pageName ?? string.Empty;
            IsBaseRow = isBaseRow;
            IsIdleRow = isIdleRow;
            IdleTargetLabel = idleTargetLabel;
            IdleTrailingNote = idleTrailingNote;
            ShowPlaylistBadge = showPlaylistBadge;
            // OWNER-WAIVED FIDELITY (Surface C / D19).
            SplitReferenceName = splitReferenceName;
            SplitSummons = splitSummons ?? new ReadOnlyCollection<PrioritySplitSummonModel>(
                Array.Empty<PrioritySplitSummonModel>());
            CanSplitEntrypoint = canSplitEntrypoint;
            CanRejoinHome = canRejoinHome;
        }

        public string RowId { get; }
        public string RankText { get; }
        public int RankNumber { get; }
        public PriorityRowKind Kind { get; }
        public PriorityDestinationModel Destination { get; }
        public string Detail { get; }
        public string StatusCopy { get; }
        public PriorityRowState State { get; }
        public string CarrierId { get; }
        public bool IsPinned { get; }
        public bool ShowGrip { get; }
        public bool IsExpanded { get; }
        /// <summary>Q4: ▼ only when expanded; collapsed slot stays empty.</summary>
        public bool ShowDisclosure { get; }
        public bool IsMaterialized { get; }
        public PageRef Target { get; }
        public IReadOnlyList<PriorityChildRowModel> Entrypoints { get; }
        public IReadOnlyList<PriorityChildRowModel> Overrides { get; }
        public IReadOnlyList<PriorityChildRowModel> Layers { get; }
        public bool ShowBaseBlock { get; }
        public string BaseBlockBody { get; }
        public ManualOptionsModel ManualOptions { get; }
        public bool ShowOverflowMenu { get; }
        public string PrimarySummonId { get; }
        public bool PrimarySummonEnabled { get; }
        public int? ReturnToRestAfterMs { get; }
        public string PageName { get; }
        public bool IsBaseRow { get; }
        public bool IsIdleRow { get; }
        public string IdleTargetLabel { get; }
        public string IdleTrailingNote { get; }
        public bool ShowPlaylistBadge { get; }

        /// <summary>
        /// OWNER-WAIVED FIDELITY: child/summon name after the › reference marker in the
        /// PAGE cell. Null when not a satellite.
        /// </summary>
        public string SplitReferenceName { get; }

        /// <summary>Every authored summon that can be selected for splitting.</summary>
        public IReadOnlyList<PrioritySplitSummonModel> SplitSummons { get; }

        /// <summary>OWNER-WAIVED FIDELITY: seat with 2+ summons may split.</summary>
        public bool CanSplitEntrypoint { get; }

        /// <summary>OWNER-WAIVED FIDELITY: satellite may rejoin the home row.</summary>
        public bool CanRejoinHome { get; }

        public bool IsManual => Kind == PriorityRowKind.Manual;
        public bool IsSeat => Kind == PriorityRowKind.Seat || Kind == PriorityRowKind.Satellite;
        /// <summary>OWNER-WAIVED FIDELITY: true only for Kind = Satellite.</summary>
        public bool IsSatellite => Kind == PriorityRowKind.Satellite;

        public bool IsOutlinedStatusChip
            => string.Equals(StatusCopy, DisplayCopy.Off, StringComparison.Ordinal);

        public bool HasExpansionBody
            => IsExpanded
               && (Entrypoints.Count > 0
                   || Overrides.Count > 0
                   || Layers.Count > 0
                   || ShowBaseBlock
                   || ManualOptions != null
                   || IsSeat);
    }

    public sealed class PrioritySplitSummonModel
    {
        public PrioritySplitSummonModel(string summonId, string label, bool isEnabled)
        {
            SummonId = summonId;
            Label = label ?? string.Empty;
            IsEnabled = isEnabled;
        }

        public string SummonId { get; }
        public string Label { get; }
        public bool IsEnabled { get; }
    }

    public sealed class PriorityDestinationModel
    {
        public PriorityDestinationModel(
            IReadOnlyList<string> badges,
            string name,
            bool isCycle,
            bool isLegacy,
            bool showPlaylistBadge)
        {
            Badges = badges ?? new ReadOnlyCollection<string>(Array.Empty<string>());
            Name = name ?? string.Empty;
            IsCycle = isCycle;
            IsLegacy = isLegacy;
            ShowPlaylistBadge = showPlaylistBadge;
        }

        public IReadOnlyList<string> Badges { get; }
        public string Name { get; }
        public bool IsCycle { get; }
        public bool IsLegacy { get; }
        public bool ShowPlaylistBadge { get; }
    }

    public sealed class PriorityChildRowModel
    {
        public PriorityChildRowModel(
            string id,
            PriorityChildKind kind,
            string label,
            string statusCopy,
            bool isClickable,
            bool actsAsEntrypoint,
            string chipLabel,
            string writesLabel)
        {
            Id = id;
            Kind = kind;
            Label = label ?? string.Empty;
            StatusCopy = statusCopy ?? string.Empty;
            IsClickable = isClickable;
            ActsAsEntrypoint = actsAsEntrypoint;
            ChipLabel = chipLabel;
            WritesLabel = writesLabel;
        }

        public string Id { get; }
        public PriorityChildKind Kind { get; }
        public string Label { get; }
        public string StatusCopy { get; }
        public bool IsClickable { get; }
        public bool ActsAsEntrypoint { get; }
        public string ChipLabel { get; }
        public string WritesLabel { get; }
        public string EntrypointGlyph
            => ActsAsEntrypoint ? DisplayCopy.EntrypointGlyph : string.Empty;
    }

    public sealed class ManualOptionsModel
    {
        public ManualOptionsModel(
            bool returnEnabled,
            int shownSeconds,
            string consequence,
            bool showUnmappedAmber)
        {
            ReturnEnabled = returnEnabled;
            ShownSeconds = shownSeconds;
            Consequence = consequence ?? string.Empty;
            ShowUnmappedAmber = showUnmappedAmber;
        }

        public bool ReturnEnabled { get; }
        public int ShownSeconds { get; }
        public string Consequence { get; }
        public bool ShowUnmappedAmber { get; }
    }

    public sealed class PriorityExplainerCardModel
    {
        public PriorityExplainerCardModel(string label, string body)
        {
            Label = label ?? string.Empty;
            Body = body ?? string.Empty;
        }

        public string Label { get; }
        public string Body { get; }
    }

    public sealed class PriorityPickerModel
    {
        public PriorityPickerModel(
            string searchPlaceholder,
            IReadOnlyList<PriorityPickerGroupModel> groups,
            string footer,
            bool includeScreens,
            bool includePlaylists)
        {
            SearchPlaceholder = searchPlaceholder ?? string.Empty;
            Groups = groups ?? new ReadOnlyCollection<PriorityPickerGroupModel>(
                Array.Empty<PriorityPickerGroupModel>());
            Footer = footer;
            IncludeScreens = includeScreens;
            IncludePlaylists = includePlaylists;
        }

        public string SearchPlaceholder { get; }
        public IReadOnlyList<PriorityPickerGroupModel> Groups { get; }
        public string Footer { get; }
        public bool IncludeScreens { get; }
        public bool IncludePlaylists { get; }
    }

    public sealed class PriorityPickerGroupModel
    {
        public PriorityPickerGroupModel(
            string header,
            IReadOnlyList<PriorityPickerItemModel> items,
            string emptyState = null)
        {
            Header = header ?? string.Empty;
            Items = items ?? new ReadOnlyCollection<PriorityPickerItemModel>(
                Array.Empty<PriorityPickerItemModel>());
            EmptyState = emptyState;
        }

        public string Header { get; }
        public IReadOnlyList<PriorityPickerItemModel> Items { get; }
        public string EmptyState { get; }
    }

    public sealed class PriorityPickerItemModel
    {
        public PriorityPickerItemModel(
            string key,
            string badge,
            string name,
            string trailingNote,
            bool isSelected,
            bool isEnabled,
            string capabilityNote,
            IdleKind idleKind,
            PageRef pageRef,
            WheelScreenCommand screen,
            string playlistId = null)
        {
            Key = key ?? string.Empty;
            Badge = badge;
            Name = name ?? string.Empty;
            TrailingNote = trailingNote;
            IsSelected = isSelected;
            IsEnabled = isEnabled;
            CapabilityNote = capabilityNote;
            IdleKind = idleKind;
            PageRef = pageRef;
            Screen = screen;
            PlaylistId = playlistId;
        }

        public string Key { get; }
        public string Badge { get; }
        public string Name { get; }
        public string TrailingNote { get; }
        public bool IsSelected { get; }
        public bool IsEnabled { get; }
        public string CapabilityNote { get; }
        public IdleKind IdleKind { get; }
        public PageRef PageRef { get; }
        public WheelScreenCommand Screen { get; }
        /// <summary>Playlist id when <see cref="IdleKind"/> is <see cref="IdleKind.Playlist"/>.</summary>
        public string PlaylistId { get; }
    }

    /// <summary>
    /// Surface D: 5o read-only playlist card (expanded PLAYLISTS picker row).
    /// </summary>
    public sealed class PlaylistReadOnlyCardModel
    {
        public PlaylistReadOnlyCardModel(
            string badge,
            string name,
            string readOnlyChip,
            string stepsLabel,
            string stepsCaption,
            IReadOnlyList<PlaylistCardStepModel> steps,
            string provenance,
            string usedByLine,
            string reRunLabel,
            bool reRunEnabled,
            string reRunTooltip)
        {
            Badge = badge ?? string.Empty;
            Name = name ?? string.Empty;
            ReadOnlyChip = readOnlyChip ?? string.Empty;
            StepsLabel = stepsLabel ?? string.Empty;
            StepsCaption = stepsCaption ?? string.Empty;
            Steps = steps ?? new ReadOnlyCollection<PlaylistCardStepModel>(
                Array.Empty<PlaylistCardStepModel>());
            Provenance = provenance;
            UsedByLine = usedByLine;
            ReRunLabel = reRunLabel ?? string.Empty;
            ReRunEnabled = reRunEnabled;
            ReRunTooltip = reRunTooltip;
        }

        public string Badge { get; }
        public string Name { get; }
        public string ReadOnlyChip { get; }
        public string StepsLabel { get; }
        public string StepsCaption { get; }
        public IReadOnlyList<PlaylistCardStepModel> Steps { get; }
        public string Provenance { get; }
        public string UsedByLine { get; }
        public string ReRunLabel { get; }
        public bool ReRunEnabled { get; }
        public string ReRunTooltip { get; }
    }

    public sealed class PlaylistCardStepModel
    {
        public PlaylistCardStepModel(
            string numeral,
            string destinationName,
            string durationLabel,
            bool isLast,
            bool isSkipped)
        {
            Numeral = numeral ?? string.Empty;
            DestinationName = destinationName ?? string.Empty;
            DurationLabel = durationLabel ?? string.Empty;
            IsLast = isLast;
            IsSkipped = isSkipped;
        }

        public string Numeral { get; }
        public string DestinationName { get; }
        public string DurationLabel { get; }
        public bool IsLast { get; }
        public bool IsSkipped { get; }
    }
}
