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
    /// Pure Overview (v2) projection — no WPF. Rebuilds every region from a v2 document
    /// + resolution snapshot + values. Structure follows digest §2; navigation affordances
    /// only where §4 pins them (rows are inert). O1 provisional defaults carry design-backlog
    /// comments. O9: Settings.Mode is authoritative; DisplayControl write-through is an
    /// E9-exit concern owned by the view host, not this model.
    /// </summary>
    public sealed class DisplayOverviewV2Model
    {
        private static readonly IReadOnlyList<OverviewPriorityRowModel> NoRows =
            new ReadOnlyCollection<OverviewPriorityRowModel>(
                Array.Empty<OverviewPriorityRowModel>());

        private static readonly IReadOnlyList<string> NoLines =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        private static readonly IReadOnlyList<string> NoBadges =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        /// <summary>
        /// Rebuild the Overview projection. Null config yields a minimal empty model
        /// (settings-card defaults only).
        /// </summary>
        public static DisplayOverviewV2Model Project(
            DisplayConfigV2 config,
            DisplayResolutionSnapshotModel resolution,
            DisplayValuesSnapshot values,
            DisplayType displayType,
            WheelCatalog catalog = null,
            AliasTable aliases = null,
            bool nextPageMapped = false,
            bool prevPageMapped = false)
        {
            resolution = resolution ?? DisplayResolutionSnapshotModel.Empty;
            bool isItm = displayType == DisplayType.Itm;
            var mode = config?.Settings?.Mode ?? SettingsMode.On;
            bool reject = config?.Settings?.RejectUncommandedChanges ?? false;

            // O1 PROVISIONAL (design-backlog): Off → header + neutral empty-state (no ladder).
            bool modeOff = mode == SettingsMode.Off;
            // O1 PROVISIONAL (design-backlog): Legacy Only → ITM rows dimmed + CAN'T RUN HERE.
            bool legacyOnly = mode == SettingsMode.LegacyOnly;

            string surfaceWord = isItm ? DisplayCopy.ItmDisplay : DisplayCopy.SegmentDisplay;
            string situation = resolution.InGame ? DisplayCopy.InGame : DisplayCopy.SituationIdle;

            string mirrorCaption = BuildMirrorCaption(values, config, catalog, resolution);
            var rows = modeOff
                ? NoRows
                : BuildLadder(config, resolution, catalog, aliases, legacyOnly,
                    nextPageMapped, prevPageMapped);

            var consequenceLines = BuildConsequenceLines(reject, nextPageMapped, prevPageMapped);

            return new DisplayOverviewV2Model(
                surfaceWord: surfaceWord,
                situationCopy: situation,
                inGame: resolution.InGame,
                isConnected: resolution.IsConnected,
                mode: mode,
                isItmWheel: isItm,
                modeHint: isItm ? DisplayCopy.ModeHintItm : DisplayCopy.ModeHintSegment,
                rejectUncommandedChanges: reject,
                showLadder: !modeOff,
                modeOffEmptyState: modeOff ? DisplayCopy.ModeOffEmptyState : null,
                mirrorCaption: mirrorCaption,
                values: values,
                priorityRows: rows,
                nextPageMapped: nextPageMapped,
                prevPageMapped: prevPageMapped,
                consequenceLines: consequenceLines,
                showNothingMappedAmber: !nextPageMapped && !prevPageMapped);
        }

        private DisplayOverviewV2Model(
            string surfaceWord,
            string situationCopy,
            bool inGame,
            bool isConnected,
            SettingsMode mode,
            bool isItmWheel,
            string modeHint,
            bool rejectUncommandedChanges,
            bool showLadder,
            string modeOffEmptyState,
            string mirrorCaption,
            DisplayValuesSnapshot values,
            IReadOnlyList<OverviewPriorityRowModel> priorityRows,
            bool nextPageMapped,
            bool prevPageMapped,
            IReadOnlyList<string> consequenceLines,
            bool showNothingMappedAmber)
        {
            SurfaceWord = surfaceWord;
            SituationCopy = situationCopy;
            InGame = inGame;
            IsConnected = isConnected;
            Mode = mode;
            IsItmWheel = isItmWheel;
            ModeHint = modeHint;
            RejectUncommandedChanges = rejectUncommandedChanges;
            ShowLadder = showLadder;
            ModeOffEmptyState = modeOffEmptyState;
            MirrorCaption = mirrorCaption;
            Values = values;
            PriorityRows = priorityRows ?? NoRows;
            NextPageMapped = nextPageMapped;
            PrevPageMapped = prevPageMapped;
            ConsequenceLines = consequenceLines ?? NoLines;
            ShowNothingMappedAmber = showNothingMappedAmber;
        }

        // ── Header ───────────────────────────────────────────────────────

        public string SurfaceWord { get; }
        public string SituationCopy { get; }
        public bool InGame { get; }
        public bool IsConnected { get; }

        // ── Settings (O9: Settings.Mode authoritative) ───────────────────

        public SettingsMode Mode { get; }
        public bool IsItmWheel { get; }
        public string ModeHint { get; }
        public bool RejectUncommandedChanges { get; }

        /// <summary>O1 provisional: false when Mode is Off (no ladder).</summary>
        public bool ShowLadder { get; }

        /// <summary>O1 provisional empty-state copy when Mode is Off; null otherwise.</summary>
        public string ModeOffEmptyState { get; }

        // ── Mirror ───────────────────────────────────────────────────────

        public string MirrorCaption { get; }
        public DisplayValuesSnapshot Values { get; }

        // ── Priority ladder ──────────────────────────────────────────────

        public IReadOnlyList<OverviewPriorityRowModel> PriorityRows { get; }

        // ── Controls ─────────────────────────────────────────────────────

        public bool NextPageMapped { get; }
        public bool PrevPageMapped { get; }
        public string NextPageValue => NextPageMapped ? string.Empty : DisplayCopy.NotMapped;
        public string PrevPageValue => PrevPageMapped ? string.Empty : DisplayCopy.NotMapped;
        public IReadOnlyList<string> ConsequenceLines { get; }
        public bool ShowNothingMappedAmber { get; }

        /// <summary>
        /// Apply a mode change to a document clone. Caller publishes via
        /// <c>ApplyDisplayConfigV2</c>. Does not write DisplayControl (view host does
        /// write-through while the v1 tab lives — E9-exit).
        /// </summary>
        public static DisplayConfigV2 WithMode(DisplayConfigV2 config, SettingsMode mode)
        {
            if (config == null)
                config = new DisplayConfigV2();
            var next = CloneShallow(config);
            if (next.Settings == null)
                next.Settings = new SettingsBlock();
            next.Settings.Mode = mode;
            return next;
        }

        /// <summary>Apply reject-toggle to a document clone.</summary>
        public static DisplayConfigV2 WithRejectUncommanded(
            DisplayConfigV2 config, bool reject)
        {
            if (config == null)
                config = new DisplayConfigV2();
            var next = CloneShallow(config);
            if (next.Settings == null)
                next.Settings = new SettingsBlock();
            next.Settings.RejectUncommandedChanges = reject;
            return next;
        }

        /// <summary>
        /// Map SettingsMode → DisplaySettings.DisplayControl for write-through while
        /// the pre-epic tab lives. // E9-exit: this mapping dies with the codec trim.
        /// </summary>
        public static string DisplayControlForMode(SettingsMode mode)
        {
            switch (mode)
            {
                case SettingsMode.LegacyOnly:
                    return DisplaySettings.ControlLegacy;
                case SettingsMode.Off:
                    return DisplaySettings.ControlOff;
                default:
                    return DisplaySettings.ControlItm;
            }
        }

        /// <summary>Map DisplayControl → SettingsMode (for seeding / parity checks).</summary>
        public static SettingsMode ModeForDisplayControl(string control)
        {
            if (string.Equals(control, DisplaySettings.ControlLegacy, StringComparison.OrdinalIgnoreCase))
                return SettingsMode.LegacyOnly;
            if (string.Equals(control, DisplaySettings.ControlOff, StringComparison.OrdinalIgnoreCase))
                return SettingsMode.Off;
            return SettingsMode.On;
        }

        /// <summary>
        /// O2: IdleSpec rendering (Screen / Blank / Page / Playlist).
        /// </summary>
        public static string IdleDetail(
            IdleSpec idle,
            DisplayConfigV2 config = null,
            WheelCatalog catalog = null)
        {
            if (idle == null || idle.Kind == IdleKind.Unknown || idle.Kind == IdleKind.Blank)
                return DisplayCopy.IdleTargetLine(DisplayCopy.ABlankDisplay, null);

            switch (idle.Kind)
            {
                case IdleKind.Screen:
                    return DisplayCopy.IdleTargetLine(ScreenName(idle.Screen), null);
                case IdleKind.Page:
                {
                    var dest = ResolvePageRefDestination(idle.Page, config, catalog);
                    string name = dest.Badges.Count > 0
                        ? DisplayCopy.PageCaption(dest.Badges[0], dest.Name)
                        : dest.Name;
                    return DisplayCopy.IdleTargetLine(name, null);
                }
                case IdleKind.Playlist:
                {
                    string playlistName = idle.Playlist;
                    string summary = null;
                    if (config?.Playlists != null && !string.IsNullOrWhiteSpace(idle.Playlist))
                    {
                        for (int i = 0; i < config.Playlists.Count; i++)
                        {
                            var pl = config.Playlists[i];
                            if (pl == null) continue;
                            if (!string.Equals(pl.Id, idle.Playlist, StringComparison.OrdinalIgnoreCase))
                                continue;
                            playlistName = !string.IsNullOrEmpty(pl.Name) ? pl.Name : pl.Id;
                            summary = PlaylistSummary(pl);
                            break;
                        }
                    }
                    return DisplayCopy.IdleTargetLine(playlistName, summary);
                }
                default:
                    return DisplayCopy.IdleTargetLine(DisplayCopy.ABlankDisplay, null);
            }
        }

        private static string PlaylistSummary(PlaylistEntry pl)
        {
            if (pl?.Steps == null || pl.Steps.Count == 0)
                return null;
            var parts = new List<string>(pl.Steps.Count);
            for (int i = 0; i < pl.Steps.Count; i++)
            {
                var step = pl.Steps[i];
                if (step?.Destination == null) continue;
                string name = StepDestName(step.Destination);
                if (step.DegradedAtLoad || step.Destination.DegradedAtLoad)
                    parts.Add(DisplayCopy.PlaylistStepLine(name, DisplayCopy.PlaylistStepSkipped));
                else if (step.DurationMsPresent)
                    parts.Add(DisplayCopy.PlaylistStepLine(
                        name, DisplayCopy.PlaylistStepDurationLabel(step)));
                else
                    parts.Add(name);
            }
            return parts.Count == 0 ? null : string.Join(" → ", parts);
        }

        private static string StepDestName(IdleSpec dest)
        {
            if (dest == null) return string.Empty;
            switch (dest.Kind)
            {
                case IdleKind.Blank: return DisplayCopy.ABlankDisplay;
                case IdleKind.Screen: return ScreenName(dest.Screen);
                case IdleKind.Page:
                    return dest.Page?.CatalogPageId ?? dest.Page?.Id ?? string.Empty;
                default: return dest.KindRaw ?? string.Empty;
            }
        }

        // ── Ladder composition ───────────────────────────────────────────

        private static IReadOnlyList<OverviewPriorityRowModel> BuildLadder(
            DisplayConfigV2 config,
            DisplayResolutionSnapshotModel resolution,
            WheelCatalog catalog,
            AliasTable aliases,
            bool legacyOnly,
            bool nextMapped,
            bool prevMapped)
        {
            if (config?.Priority == null)
                return NoRows;

            var list = new List<OverviewPriorityRowModel>();
            var rows = config.Priority.EffectiveRows;
            string winnerId = FindDisplayWinnerCarrierId(resolution);
            string winnerDest = FindDisplayWinnerDestinationId(resolution);
            var carrierById = IndexCarriers(resolution);
            var aggregateBySeat = IndexAggregates(resolution);

            int rank = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null) continue;
                rank++;
                var allCarriers = resolution.Carriers;
            list.Add(ProjectRankedRow(
                    rank, row, config, catalog, aliases, carrierById, aggregateBySeat,
                    allCarriers, winnerId, legacyOnly, nextMapped, prevMapped, resolution.Manual));
            }

            list.Add(ProjectBaseRow(config, catalog, winnerId, winnerDest));
            list.Add(ProjectIdleRow(config, catalog, winnerDest));

            return new ReadOnlyCollection<OverviewPriorityRowModel>(list);
        }

        private static OverviewPriorityRowModel ProjectRankedRow(
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
            bool nextMapped,
            bool prevMapped,
            ManualRowStateModel manual)
        {
            string carrierId = CarrierIdForRow(row);
            carriers.TryGetValue(carrierId ?? string.Empty, out var carrier);

            bool isOff = carrier != null
                && ContainsLabel(carrier.RowLabelCopies, DisplayCopy.Off);

            // O1 PROVISIONAL (design-backlog): Legacy Only dims ITM destinations +
            // stamps CAN'T RUN HERE. Design session owns the real board.
            // Provisional CAN'T RUN HERE takes precedence over a stale winner snapshot
            // whenever mode != On (immediate post-switch poll still holds the prior winner).
            bool isItmDest = row.Target != null && row.Target.Kind == PageRefKind.ItmPage;
            bool provisionalCantRun = legacyOnly && isItmDest;

            bool isWinner = !provisionalCantRun
                && !string.IsNullOrEmpty(winnerId)
                && string.Equals(carrierId, winnerId, StringComparison.Ordinal);

            var state = isWinner
                ? OverviewRowState.Winner
                : (isOff || provisionalCantRun ? OverviewRowState.Off : OverviewRowState.Normal);

            string status = ResolveStatus(carrier, isWinner, isOff, provisionalCantRun);
            var dest = ResolveDestination(row, config, catalog);
            string detail = ResolveDetail(
                row, config, aliases, carriers, aggregates, allCarriers,
                manual, nextMapped, prevMapped);

            return new OverviewPriorityRowModel(
                rankText: rank.ToString(CultureInfo.InvariantCulture),
                destination: dest,
                detail: detail,
                statusCopy: status,
                state: state,
                carrierId: carrierId,
                isPinned: false);
        }

        private static OverviewPriorityRowModel ProjectBaseRow(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            string winnerId,
            string winnerDest)
        {
            var pageRef = config?.Priority?.Rest?.InSessionPage;
            var dest = ResolvePageRefDestination(pageRef, config, catalog);
            bool isWinner =
                string.Equals(winnerId, SeatArbiter.RestCarrierId, StringComparison.Ordinal)
                || string.Equals(winnerDest, DestinationIds.RestInSession, StringComparison.Ordinal);

            return new OverviewPriorityRowModel(
                rankText: DisplayCopy.PriorityBaseRank,
                destination: dest,
                detail: DisplayCopy.WhenNothingAboveIsLive,
                statusCopy: DisplayCopy.StatusDash,
                state: isWinner ? OverviewRowState.Winner : OverviewRowState.Pinned,
                carrierId: SeatArbiter.RestCarrierId,
                isPinned: true);
        }

        private static OverviewPriorityRowModel ProjectIdleRow(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            string winnerDest)
        {
            var idle = config?.Priority?.Rest?.Idle;
            // Destination cell: bare "Outside a session" label (no badge) — digest §2.
            var dest = new OverviewDestinationModel(
                badges: NoBadges,
                name: DisplayCopy.OutsideASession,
                isCycle: false,
                isLegacy: false,
                showPlaylistBadge: false);

            string detail = IdleDetail(idle, config, catalog);
            bool isWinner = string.Equals(winnerDest, DestinationIds.RestIdle, StringComparison.Ordinal);

            return new OverviewPriorityRowModel(
                rankText: string.Empty,
                destination: dest,
                detail: detail,
                statusCopy: DisplayCopy.StatusDash,
                state: isWinner ? OverviewRowState.Winner : OverviewRowState.Pinned,
                carrierId: DestinationIds.RestIdle,
                isPinned: true);
        }

        private static string ResolveStatus(
            CarrierResolutionRowModel carrier,
            bool isWinner,
            bool isOff,
            bool provisionalCantRun)
        {
            // Provisional CAN'T RUN HERE beats a stale winner snapshot (mode != On).
            if (provisionalCantRun)
                return DisplayCopy.CantRunHere;

            // C1: winner status column is empty (OnScreen=""); highlight is structural.
            if (isWinner)
                return DisplayCopy.OnScreen;

            if (isOff)
                return DisplayCopy.Off;

            if (carrier == null)
                return DisplayCopy.Waiting;

            // Row labels (OFF / DISMISSED / …) take the status chip when present.
            if (carrier.RowLabelCopies != null && carrier.RowLabelCopies.Count > 0)
            {
                // Prefer Off / Dismissed / CantRunHere over presence.
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

        private static string ResolveDetail(
            PriorityRow row,
            DisplayConfigV2 config,
            AliasTable aliases,
            Dictionary<string, CarrierResolutionRowModel> carriers,
            Dictionary<string, AggregateMembershipModel> aggregates,
            IReadOnlyList<CarrierResolutionRowModel> allCarriers,
            ManualRowStateModel manual,
            bool nextMapped,
            bool prevMapped)
        {
            if (row.Kind == PriorityRowKind.Manual)
            {
                return DisplayCopy.ManualPagingDetail(
                    manual != null && manual.HasRememberedTarget,
                    nextMapped,
                    prevMapped);
            }

            // Aggregate "N of its M entrypoint overrides are firing" for seats with membership.
            if (row.Kind == PriorityRowKind.Seat
                && !string.IsNullOrEmpty(row.Id)
                && aggregates.TryGetValue(row.Id, out var agg)
                && agg.TotalCount > 0
                && agg.ActiveCount > 0)
            {
                string line = DisplayCopy.EntrypointsFiringLine(agg.ActiveCount, agg.TotalCount);
                // Outranked second clause when presence says so.
                if (carriers.TryGetValue(CarrierIdForRow(row) ?? string.Empty, out var c)
                    && string.Equals(c.PresenceCopy, DisplayCopy.Outranked, StringComparison.Ordinal))
                {
                    return DisplayCopy.DetailWithClause(
                        line, OutrankedClause(row, config, allCarriers));
                }
                return line;
            }

            // Primary condition from the first effectively-enabled summon.
            string primary = null;
            var summons = row.Summons;
            if (summons != null)
            {
                for (int i = 0; i < summons.Count; i++)
                {
                    var s = summons[i];
                    if (s == null || !s.EffectivelyEnabled)
                        continue;
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

            // Cycle suffix (C3: ruled "cycle" / first-mention glossary form).
            bool isCycle = row.Target != null && row.Target.Kind == PageRefKind.Cycle;
            if (isCycle)
                primary = DisplayCopy.ConditionWithCycleSuffix(primary, firstMention: true);

            if (string.IsNullOrEmpty(primary))
                primary = string.Empty;

            string carrierId = CarrierIdForRow(row);
            if (carriers.TryGetValue(carrierId ?? string.Empty, out var carrier)
                && string.Equals(carrier.PresenceCopy, DisplayCopy.Outranked, StringComparison.Ordinal))
            {
                return DisplayCopy.DetailWithClause(
                    primary, OutrankedClause(row, config, allCarriers));
            }

            return primary;
        }

        /// <summary>
        /// Digest §2: "this entrypoint is outranked; the page's FN1 override is off-screen".
        /// Projects the first off-screen child on the row's destination, when present.
        /// </summary>
        private static string OutrankedClause(
            PriorityRow row,
            DisplayConfigV2 config,
            IReadOnlyList<CarrierResolutionRowModel> allCarriers)
        {
            string childLabel = FindOffScreenChildLabel(row, config, allCarriers);
            return DisplayCopy.OutrankedOffScreenClause(childLabel);
        }

        private static string FindOffScreenChildLabel(
            PriorityRow row,
            DisplayConfigV2 config,
            IReadOnlyList<CarrierResolutionRowModel> allCarriers)
        {
            if (allCarriers == null || allCarriers.Count == 0)
                return null;

            string destId = DestinationIds.FromPageRef(row?.Target);
            for (int i = 0; i < allCarriers.Count; i++)
            {
                var c = allCarriers[i];
                if (c == null)
                    continue;
                if (!string.Equals(c.PresenceCopy, DisplayCopy.OffScreen, StringComparison.Ordinal))
                    continue;
                if (destId != null
                    && !string.Equals(c.DestinationId, destId, StringComparison.Ordinal))
                    continue;

                // Skip the seat/derived carrier itself — want a child (field override / layer).
                string seatCarrier = CarrierIdForRow(row);
                if (string.Equals(c.CarrierId, seatCarrier, StringComparison.Ordinal))
                    continue;

                string name = ResolveChildDisplayName(c.CarrierId, config);
                if (string.IsNullOrEmpty(name))
                    name = c.CarrierId;
                return DisplayCopy.OverrideChildLabel(name);
            }
            return null;
        }

        private static string ResolveChildDisplayName(string carrierId, DisplayConfigV2 config)
        {
            if (string.IsNullOrEmpty(carrierId) || config?.Fields == null)
                return null;

            foreach (var kv in config.Fields)
            {
                var field = kv.Value;
                if (field?.Overrides == null)
                    continue;
                for (int i = 0; i < field.Overrides.Count; i++)
                {
                    var ov = field.Overrides[i];
                    if (ov == null || !string.Equals(ov.Id, carrierId, StringComparison.Ordinal))
                        continue;
                    string text = ov.Content?.EffectiveText ?? ov.Content?.Text;
                    if (!string.IsNullOrEmpty(text))
                        return text;
                    return ov.Id;
                }
            }

            // Hosted-page layers share carrier ids with their document entries.
            if (config.Pages != null)
            {
                for (int p = 0; p < config.Pages.Count; p++)
                {
                    var page = config.Pages[p];
                    if (page?.Layers == null)
                        continue;
                    for (int i = 0; i < page.Layers.Count; i++)
                    {
                        var layer = page.Layers[i];
                        if (layer == null
                            || !string.Equals(layer.Id, carrierId, StringComparison.Ordinal))
                            continue;
                        if (!string.IsNullOrEmpty(layer.Name))
                            return layer.Name;
                        return layer.Id;
                    }
                }
            }

            return null;
        }

        private static OverviewDestinationModel ResolveDestination(
            PriorityRow row,
            DisplayConfigV2 config,
            WheelCatalog catalog)
        {
            if (row.Kind == PriorityRowKind.Manual)
            {
                return new OverviewDestinationModel(
                    NoBadges, DisplayCopy.ManualPaging, false, false, false);
            }

            return ResolvePageRefDestination(row.Target, config, catalog);
        }

        private static OverviewDestinationModel ResolvePageRefDestination(
            PageRef pageRef,
            DisplayConfigV2 config,
            WheelCatalog catalog)
        {
            if (pageRef == null)
                return new OverviewDestinationModel(NoBadges, string.Empty, false, false, false);

            switch (pageRef.Kind)
            {
                case PageRefKind.ItmPage:
                {
                    int index = CatalogIndex(catalog, pageRef.CatalogPageId);
                    string badge = index > 0
                        ? DisplayCopy.ItmPageBadge(index)
                        : DisplayCopy.ItmBadge;
                    string name = ResolveItmPageName(pageRef.CatalogPageId, config, catalog);
                    return new OverviewDestinationModel(
                        new ReadOnlyCollection<string>(new[] { badge }),
                        name, isCycle: false, isLegacy: false, showPlaylistBadge: false);
                }
                case PageRefKind.HostedPage:
                {
                    string name = ResolveHostedPageName(pageRef.Id, config);
                    return new OverviewDestinationModel(
                        new ReadOnlyCollection<string>(new[] { DisplayCopy.LegacyBadge }),
                        name, isCycle: false, isLegacy: true, showPlaylistBadge: false);
                }
                case PageRefKind.Cycle:
                {
                    var badges = ResolveCycleBadges(pageRef.Id, config, catalog);
                    return new OverviewDestinationModel(
                        badges, string.Empty, isCycle: true, isLegacy: false, showPlaylistBadge: false);
                }
                default:
                    return new OverviewDestinationModel(NoBadges, pageRef.Id ?? string.Empty, false, false, false);
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
                if (badge == null)
                    continue;
                // Digest §2: cycle badges carry the drawn ⇄ glyph between members.
                if (list.Count > 0)
                    list.Add(DisplayCopy.CycleBadgeJoin);
                list.Add(badge);
            }
            return list.Count == 0
                ? NoBadges
                : new ReadOnlyCollection<string>(list);
        }

        private static string BuildMirrorCaption(
            DisplayValuesSnapshot values,
            DisplayConfigV2 config,
            WheelCatalog catalog,
            DisplayResolutionSnapshotModel resolution)
        {
            // Prefer live values snapshot page name; badge from catalog when possible.
            if (values != null && !string.IsNullOrEmpty(values.PageName))
            {
                string badge = null;
                string dest = FindDisplayWinnerDestinationId(resolution);
                if (dest != null && dest.StartsWith("itm:", StringComparison.Ordinal))
                {
                    string catId = dest.Substring(4);
                    int index = CatalogIndex(catalog, catId);
                    if (index > 0)
                        badge = DisplayCopy.ItmPageBadge(index);
                }
                else if (dest != null && dest.StartsWith("hosted:", StringComparison.Ordinal))
                {
                    badge = DisplayCopy.LegacyBadge;
                }

                return DisplayCopy.PageCaption(badge, values.PageName);
            }

            // Fallback: winning destination.
            string destinationId = FindDisplayWinnerDestinationId(resolution);
            if (string.IsNullOrEmpty(destinationId))
                return string.Empty;
            if (destinationId.StartsWith("itm:", StringComparison.Ordinal))
            {
                string catId = destinationId.Substring(4);
                int index = CatalogIndex(catalog, catId);
                string badge = index > 0 ? DisplayCopy.ItmPageBadge(index) : DisplayCopy.ItmBadge;
                return DisplayCopy.PageCaption(badge, ResolveItmPageName(catId, config, catalog));
            }
            if (destinationId.StartsWith("hosted:", StringComparison.Ordinal))
            {
                string id = destinationId.Substring(7);
                return DisplayCopy.PageCaption(DisplayCopy.LegacyBadge, ResolveHostedPageName(id, config));
            }
            return string.Empty;
        }

        private static IReadOnlyList<string> BuildConsequenceLines(
            bool reject, bool nextMapped, bool prevMapped)
        {
            var lines = new List<string>(3)
            {
                DisplayCopy.ControlsConsequenceRejectOn,
                DisplayCopy.ControlsConsequenceRejectOff,
            };
            if (!nextMapped && !prevMapped)
                lines.Add(DisplayCopy.ControlsConsequenceNothingMapped);
            return new ReadOnlyCollection<string>(lines);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static string CarrierIdForRow(PriorityRow row)
        {
            if (row == null) return null;
            if (row.Kind == PriorityRowKind.Manual)
                return SeatArbiter.ManualCarrierId;
            return row.Id;
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
                // Prefer display-surface rows when duplicates exist.
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

        private static DisplayConfigV2 CloneShallow(DisplayConfigV2 config)
        {
            return new DisplayConfigV2
            {
                SchemaVersion = config.SchemaVersion,
                ProfileId = config.ProfileId,
                Pages = config.Pages,
                Cycles = config.Cycles,
                Priority = config.Priority,
                PageOrder = config.PageOrder,
                Fields = config.Fields,
                SharedFields = config.SharedFields,
                WheelScreen = config.WheelScreen,
                Settings = config.Settings == null
                    ? new SettingsBlock()
                    : new SettingsBlock
                    {
                        RejectUncommandedChanges = config.Settings.RejectUncommandedChanges,
                        Mode = config.Settings.Mode,
                        ExtensionData = config.Settings.ExtensionData,
                    },
                ExtensionData = config.ExtensionData,
            };
        }
    }

    /// <summary>Row visual state drawn on board 5a (digest §2).</summary>
    public enum OverviewRowState
    {
        Normal,
        Winner,
        Off,
        Pinned,
    }

    /// <summary>One Overview priority-ladder row (inert — no click target; §4 / O5).</summary>
    public sealed class OverviewPriorityRowModel
    {
        public OverviewPriorityRowModel(
            string rankText,
            OverviewDestinationModel destination,
            string detail,
            string statusCopy,
            OverviewRowState state,
            string carrierId,
            bool isPinned)
        {
            RankText = rankText ?? string.Empty;
            Destination = destination ?? new OverviewDestinationModel(
                Array.Empty<string>(), string.Empty, false, false, false);
            Detail = detail ?? string.Empty;
            StatusCopy = statusCopy ?? string.Empty;
            State = state;
            CarrierId = carrierId;
            IsPinned = isPinned;
        }

        public string RankText { get; }
        public OverviewDestinationModel Destination { get; }
        public string Detail { get; }
        /// <summary>Status column: empty for winner (C1), word, or chip label.</summary>
        public string StatusCopy { get; }
        public OverviewRowState State { get; }
        public string CarrierId { get; }
        public bool IsPinned { get; }

        /// <summary>
        /// Digest §2: OFF is an outlined row-label chip (not a plain status word).
        /// </summary>
        public bool IsOutlinedStatusChip
            => string.Equals(StatusCopy, DisplayCopy.Off, StringComparison.Ordinal);
    }

    /// <summary>Destination cell: 0..2 badges + optional name + cycle flag.</summary>
    public sealed class OverviewDestinationModel
    {
        public OverviewDestinationModel(
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
            // task #22: playlist badge path (ratified amendment shape; inert until schema lands).
            ShowPlaylistBadge = showPlaylistBadge;
        }

        public IReadOnlyList<string> Badges { get; }
        public string Name { get; }
        public bool IsCycle { get; }
        public bool IsLegacy { get; }
        public bool ShowPlaylistBadge { get; }
    }
}
