using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure minimal diagnostics projection — no WPF. Surfaces what
    /// <see cref="DisplayResolutionSnapshotModel"/> already publishes from the
    /// composed-resolution record (plus config-side condition sentences for
    /// carriers that have triggers). Thin table rows only: reskin-friendly, no
    /// engine surface. Record-gap facts (capability envelope, ITM device id,
    /// SurfaceHeld/ReleaseEdge, latch ids, full carrier snapshots) project when
    /// present on the record.
    /// </summary>
    public sealed class DisplayDiagnosticsModel
    {
        private static readonly IReadOnlyList<DiagnosticsCarrierRowModel> NoRows =
            new ReadOnlyCollection<DiagnosticsCarrierRowModel>(
                Array.Empty<DiagnosticsCarrierRowModel>());

        private static readonly IReadOnlyList<string> NoLines =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        /// <summary>
        /// Rebuild the diagnostics projection. Null / empty resolution yields a
        /// non-blank empty-state line (never a blank panel).
        /// </summary>
        public static DisplayDiagnosticsModel Project(
            DisplayResolutionSnapshotModel resolution,
            DisplayConfigV2 config = null,
            AliasTable aliases = null)
        {
            resolution = resolution ?? DisplayResolutionSnapshotModel.Empty;

            // A null ComposedResolution is never a live resolution — Manual (and other
            // seat bookkeeping) alone must not bypass the ruled no-resolution state.
            // Evidence of a composed record: carriers, surface winners, device block, or
            // a non-zero tick stamp from the merger.
            bool hasResolution = resolution.IsConnected
                && (resolution.Carriers.Count > 0
                    || resolution.HasDeviceBlock
                    || resolution.SurfaceWinners.Count > 0
                    || resolution.TickMs > 0);

            if (!hasResolution)
            {
                return new DisplayDiagnosticsModel(
                    hasResolution: false,
                    emptyStateLine: DisplayCopy.DiagnosticsEmptyState,
                    ladderRows: NoRows,
                    deviceLines: NoLines,
                    wheelScreenLines: NoLines,
                    manualLines: NoLines,
                    floorLines: NoLines);
            }

            var ladder = BuildLadderRows(resolution, config, aliases);
            var device = BuildDeviceLines(resolution);
            var wheel = BuildWheelScreenLines(resolution);
            var manual = BuildManualLines(resolution);
            var floor = BuildFloorLines(resolution, config);

            return new DisplayDiagnosticsModel(
                hasResolution: true,
                emptyStateLine: null,
                ladderRows: ladder,
                deviceLines: device,
                wheelScreenLines: wheel,
                manualLines: manual,
                floorLines: floor);
        }

        private DisplayDiagnosticsModel(
            bool hasResolution,
            string emptyStateLine,
            IReadOnlyList<DiagnosticsCarrierRowModel> ladderRows,
            IReadOnlyList<string> deviceLines,
            IReadOnlyList<string> wheelScreenLines,
            IReadOnlyList<string> manualLines,
            IReadOnlyList<string> floorLines)
        {
            HasResolution = hasResolution;
            EmptyStateLine = emptyStateLine;
            LadderRows = ladderRows ?? NoRows;
            DeviceLines = deviceLines ?? NoLines;
            WheelScreenLines = wheelScreenLines ?? NoLines;
            ManualLines = manualLines ?? NoLines;
            FloorLines = floorLines ?? NoLines;
        }

        /// <summary>False when nothing published — view shows <see cref="EmptyStateLine"/>.</summary>
        public bool HasResolution { get; }

        /// <summary>Ruled empty-state body when <see cref="HasResolution"/> is false; null otherwise.</summary>
        public string EmptyStateLine { get; }

        /// <summary>One row per ladder participant from the composed record.</summary>
        public IReadOnlyList<DiagnosticsCarrierRowModel> LadderRows { get; }

        /// <summary>Device-block fact lines (DeviceKey + page knowledge + edge flags).</summary>
        public IReadOnlyList<string> DeviceLines { get; }

        /// <summary>Wheel-screen plane fact lines (owner, held/released, dismissal latch).</summary>
        public IReadOnlyList<string> WheelScreenLines { get; }

        /// <summary>Manual-row bookkeeping from the snapshot.</summary>
        public IReadOnlyList<string> ManualLines { get; }

        /// <summary>Base / idle floor lines from surface winners + rest destinations.</summary>
        public IReadOnlyList<string> FloorLines { get; }

        // ── Ladder rows ──────────────────────────────────────────────────

        private static IReadOnlyList<DiagnosticsCarrierRowModel> BuildLadderRows(
            DisplayResolutionSnapshotModel resolution,
            DisplayConfigV2 config,
            AliasTable aliases)
        {
            var carriers = resolution.Carriers;
            if (carriers == null || carriers.Count == 0)
                return NoRows;

            // Index snapshots by carrier id for timing/detail beyond RemainingMs.
            var snapById = IndexSnapshots(resolution.CarrierSnapshots);

            var list = new List<DiagnosticsCarrierRowModel>(carriers.Count);
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                if (c == null) continue;

                string label = ResolveCarrierLabel(c.CarrierId, config);
                string presence = ResolvePresenceDisplay(c);
                string labels = JoinLabels(c.RowLabelCopies);
                string condition = ResolveConditionSentence(c.CarrierId, config, aliases);
                string destination = c.DestinationId ?? string.Empty;
                string timing = BuildTimingDetail(c, snapById);

                list.Add(new DiagnosticsCarrierRowModel(
                    label: label,
                    presenceCopy: presence,
                    rowLabelsCopy: labels,
                    conditionSentence: condition,
                    destinationId: destination,
                    timingDetail: timing,
                    surfaceId: c.SurfaceId ?? string.Empty,
                    carrierId: c.CarrierId ?? string.Empty));
            }

            return list.Count == 0
                ? NoRows
                : new ReadOnlyCollection<DiagnosticsCarrierRowModel>(list);
        }

        private static Dictionary<string, CarrierSnapshotRowModel> IndexSnapshots(
            IReadOnlyList<CarrierSnapshotRowModel> snapshots)
        {
            var map = new Dictionary<string, CarrierSnapshotRowModel>(StringComparer.Ordinal);
            if (snapshots == null)
                return map;
            for (int i = 0; i < snapshots.Count; i++)
            {
                var s = snapshots[i];
                if (s == null || string.IsNullOrEmpty(s.CarrierId))
                    continue;
                // First wins — snapshots are already union-by-id from the record.
                if (!map.ContainsKey(s.CarrierId))
                    map[s.CarrierId] = s;
            }
            return map;
        }

        private static string BuildTimingDetail(
            CarrierResolutionRowModel carrier,
            Dictionary<string, CarrierSnapshotRowModel> snapById)
        {
            CarrierSnapshotRowModel snap = null;
            if (carrier.CarrierId != null
                && snapById != null
                && snapById.TryGetValue(carrier.CarrierId, out var found))
                snap = found;

            if (snap != null)
            {
                return DisplayCopy.DiagnosticsSnapshotDetail(
                    snap.ConditionSatisfied,
                    snap.Active,
                    snap.Eligible,
                    snap.FreshFire,
                    snap.FiredThisTick,
                    snap.RemainingMs ?? carrier.RemainingMs);
            }

            return carrier.RemainingMs.HasValue
                ? DisplayCopy.DiagnosticsRemainingMs(carrier.RemainingMs.Value)
                : string.Empty;
        }

        /// <summary>
        /// Presence column follows snapshot semantics: ruled check words only.
        /// <see cref="CarrierPresence.Dismissed"/> maps to empty presence; DISMISSED
        /// appears solely as a row label (never promoted into this column).
        /// </summary>
        private static string ResolvePresenceDisplay(CarrierResolutionRowModel carrier)
            => carrier?.PresenceCopy ?? string.Empty;

        private static string JoinLabels(IReadOnlyList<string> labels)
        {
            if (labels == null || labels.Count == 0)
                return string.Empty;
            if (labels.Count == 1)
                return labels[0] ?? string.Empty;
            return string.Join(" · ", labels);
        }

        private static string ResolveCarrierLabel(string carrierId, DisplayConfigV2 config)
        {
            if (string.IsNullOrEmpty(carrierId))
                return string.Empty;

            if (string.Equals(carrierId, SeatArbiter.ManualCarrierId, StringComparison.Ordinal))
                return DisplayCopy.ManualPaging;
            if (string.Equals(carrierId, SeatArbiter.RestCarrierId, StringComparison.Ordinal))
                return DisplayCopy.BasePage;
            if (string.Equals(carrierId, DestinationIds.RestIdle, StringComparison.Ordinal)
                || string.Equals(carrierId, DestinationIds.RestInSession, StringComparison.Ordinal))
                return DisplayCopy.BasePage;

            if (config?.Priority?.EffectiveRows != null)
            {
                var rows = config.Priority.EffectiveRows;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null) continue;
                    if (row.Kind == PriorityRowKind.Manual
                        && string.Equals(carrierId, SeatArbiter.ManualCarrierId, StringComparison.Ordinal))
                        return DisplayCopy.ManualPaging;

                    // Production carriers key by Summon.Id (SeatArbiter), not PriorityRow.Id.
                    var summons = row.Summons;
                    if (summons != null)
                    {
                        for (int s = 0; s < summons.Count; s++)
                        {
                            var summon = summons[s];
                            if (summon == null
                                || !string.Equals(summon.Id, carrierId, StringComparison.Ordinal))
                                continue;
                            if (!string.IsNullOrWhiteSpace(summon.Name))
                                return summon.Name;
                            if (!string.IsNullOrEmpty(row.Id))
                                return row.Id;
                            return carrierId;
                        }
                    }
                }
            }

            // Wheel-screen / field / layer ids: document name when present.
            if (config?.WheelScreen?.Rules != null)
            {
                for (int i = 0; i < config.WheelScreen.Rules.Count; i++)
                {
                    var r = config.WheelScreen.Rules[i];
                    if (r == null) continue;
                    if (!string.Equals(r.Id, carrierId, StringComparison.Ordinal))
                        continue;
                    if (!string.IsNullOrEmpty(r.Name))
                        return r.Name;
                    return r.Id;
                }
            }

            return carrierId;
        }

        private static string ResolveConditionSentence(
            string carrierId,
            DisplayConfigV2 config,
            AliasTable aliases)
        {
            if (string.IsNullOrEmpty(carrierId) || config == null)
                return string.Empty;

            if (config.Priority?.EffectiveRows != null)
            {
                var rows = config.Priority.EffectiveRows;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null) continue;

                    if (row.Kind == PriorityRowKind.Manual
                        && string.Equals(carrierId, SeatArbiter.ManualCarrierId, StringComparison.Ordinal))
                    {
                        return ConditionFromFirstEnabledSummon(row, aliases);
                    }

                    // Production keys by Summon.Id — match the summon on its owning row.
                    var summons = row.Summons;
                    if (summons == null) continue;
                    for (int s = 0; s < summons.Count; s++)
                    {
                        var summon = summons[s];
                        if (summon == null
                            || !string.Equals(summon.Id, carrierId, StringComparison.Ordinal)
                            || !summon.EffectivelyEnabled)
                            continue;
                        string sentence = ConditionSentence.From(
                            summon.Condition, summon.Lifetime, aliases);
                        if (!string.IsNullOrEmpty(sentence))
                            return sentence;
                    }
                }
            }

            if (config.WheelScreen?.Rules != null)
            {
                for (int i = 0; i < config.WheelScreen.Rules.Count; i++)
                {
                    var r = config.WheelScreen.Rules[i];
                    if (r == null || !string.Equals(r.Id, carrierId, StringComparison.Ordinal))
                        continue;
                    return ConditionSentence.From(r.Condition, r.Lifetime, aliases);
                }
            }

            return string.Empty;
        }

        private static string ConditionFromFirstEnabledSummon(PriorityRow row, AliasTable aliases)
        {
            var summons = row?.Summons;
            if (summons == null) return string.Empty;
            for (int s = 0; s < summons.Count; s++)
            {
                var summon = summons[s];
                if (summon == null || !summon.EffectivelyEnabled)
                    continue;
                string sentence = ConditionSentence.From(
                    summon.Condition, summon.Lifetime, aliases);
                if (!string.IsNullOrEmpty(sentence))
                    return sentence;
            }
            return string.Empty;
        }

        // ── Device block ─────────────────────────────────────────────────

        private static IReadOnlyList<string> BuildDeviceLines(
            DisplayResolutionSnapshotModel resolution)
        {
            var lines = new List<string>(8);

            // DeviceKey is stamped from WheelCode at composition build (runtime seam).
            string key = string.IsNullOrEmpty(resolution.DeviceKey)
                ? DisplayCopy.StatusDash
                : resolution.DeviceKey;
            lines.Add(DisplayCopy.DiagnosticsFactLine(DisplayCopy.DiagnosticsDeviceKey, key));

            // Distinct ITM device id (DeviceKey is wheel code only).
            if (resolution.ItmDeviceId.HasValue)
            {
                lines.Add(DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsItmDeviceId,
                    DisplayCopy.DiagnosticsItmDeviceIdValue(resolution.ItmDeviceId.Value)));
            }

            // Capability-envelope summary (field count + screen-command tri-states).
            if (resolution.HasCapabilityEnvelope && resolution.CapabilityEnvelope != null)
            {
                var env = resolution.CapabilityEnvelope;
                lines.Add(DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsCapabilityEnvelope,
                    DisplayCopy.DiagnosticsCapabilityEnvelopeSummary(
                        env.FieldParamCount,
                        env.ScreenLogo,
                        env.ScreenBlank,
                        env.ScreenWhite,
                        env.ScreenLogoInverted)));
            }

            if (!resolution.HasDeviceBlock)
            {
                lines.Add(DisplayCopy.DiagnosticsNoDeviceBlock);
                return new ReadOnlyCollection<string>(lines);
            }

            lines.Add(DisplayCopy.DiagnosticsFactLine(
                DisplayCopy.DiagnosticsPageKnowledge,
                FormatPageKnowledge(resolution.PageKnowledge)));
            lines.Add(DisplayCopy.DiagnosticsFactLine(
                DisplayCopy.DiagnosticsRevertedThisTick,
                resolution.RevertedThisTick ? DisplayCopy.DiagnosticsYes : DisplayCopy.DiagnosticsNo));
            lines.Add(DisplayCopy.DiagnosticsFactLine(
                DisplayCopy.DiagnosticsAdoptWarnedThisTick,
                resolution.AdoptWarnedThisTick ? DisplayCopy.DiagnosticsYes : DisplayCopy.DiagnosticsNo));

            return new ReadOnlyCollection<string>(lines);
        }

        private static string FormatPageKnowledge(CurrentPageKnowledge page)
        {
            if (!page.IsKnown)
                return DisplayCopy.DiagnosticsPageUnknown;
            if (!page.WirePage.HasValue)
                return DisplayCopy.DiagnosticsPageUncataloged;

            string catalog = page.Page.HasValue
                ? page.Page.Value.ToString()
                : null;
            return DisplayCopy.DiagnosticsPageKnown(page.WirePage.Value, catalog);
        }

        // ── Wheel-screen plane ───────────────────────────────────────────

        private static IReadOnlyList<string> BuildWheelScreenLines(
            DisplayResolutionSnapshotModel resolution)
        {
            SurfaceWinnerModel winner = FindWinner(
                resolution, DestinationIds.WheelScreenSurfaceId);

            // Absence of a wheel-screen slice publishes nothing — no invented
            // 'released' / 'clear'. Section stays empty (ruled empty presentation).
            // SurfaceHeld/ReleaseEdge alone (without a winner row) still need a winner
            // surface entry from the record; composition always emits one when E6 runs.
            if (winner == null)
                return NoLines;

            string owner = !string.IsNullOrEmpty(winner.WinnerCarrierId)
                ? winner.WinnerCarrierId
                : (!string.IsNullOrEmpty(winner.DestinationId)
                    ? RuledDestinationDisplay(winner.DestinationId)
                    : DisplayCopy.StatusDash);

            // Explicit SurfaceHeld from the record (no longer inferred from destination).
            bool held = resolution.SurfaceHeld;

            // Latch active from the published id list (preferred) or DISMISSED labels.
            var latchIds = resolution.DismissedCarrierIds;
            bool latchActive = latchIds != null && latchIds.Count > 0;
            if (!latchActive)
            {
                latchActive = AnyDismissedOnSurface(
                    resolution, DestinationIds.WheelScreenSurfaceId);
            }

            var lines = new List<string>(5)
            {
                DisplayCopy.DiagnosticsFactLine(DisplayCopy.DiagnosticsOwner, owner),
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsHoldState,
                    held ? DisplayCopy.DiagnosticsHeld : DisplayCopy.DiagnosticsReleased),
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsReleaseEdge,
                    resolution.ReleaseEdge
                        ? DisplayCopy.DiagnosticsYes
                        : DisplayCopy.DiagnosticsNo),
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsDismissalLatch,
                    latchActive
                        ? DisplayCopy.DiagnosticsLatchActive
                        : DisplayCopy.DiagnosticsLatchClear),
            };

            if (latchIds != null && latchIds.Count > 0)
            {
                lines.Add(DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsDismissalLatchIds,
                    DisplayCopy.DiagnosticsLatchIdList(latchIds)));
            }

            return new ReadOnlyCollection<string>(lines);
        }

        private static bool AnyDismissedOnSurface(
            DisplayResolutionSnapshotModel resolution, string surfaceId)
        {
            var carriers = resolution.Carriers;
            if (carriers == null) return false;
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                if (c == null) continue;
                if (!string.Equals(c.SurfaceId, surfaceId, StringComparison.Ordinal))
                    continue;
                if (ContainsLabel(c.RowLabelCopies, DisplayCopy.Dismissed))
                    return true;
                // Presence-Dismissed leaves PresenceCopy empty; RowLabels carry the word.
            }
            return false;
        }

        // ── Manual row ───────────────────────────────────────────────────

        private static IReadOnlyList<string> BuildManualLines(
            DisplayResolutionSnapshotModel resolution)
        {
            var manual = resolution.Manual;
            if (manual == null)
            {
                // Still show the standing Manual paging concept when a manual carrier
                // is present on the ladder, even without bookkeeping.
                bool hasManualCarrier = false;
                var carriers = resolution.Carriers;
                if (carriers != null)
                {
                    for (int i = 0; i < carriers.Count; i++)
                    {
                        if (carriers[i] != null
                            && string.Equals(
                                carriers[i].CarrierId,
                                SeatArbiter.ManualCarrierId,
                                StringComparison.Ordinal))
                        {
                            hasManualCarrier = true;
                            break;
                        }
                    }
                }

                if (!hasManualCarrier)
                    return NoLines;

                return new ReadOnlyCollection<string>(new[]
                {
                    DisplayCopy.DiagnosticsFactLine(
                        DisplayCopy.ManualPaging,
                        DisplayCopy.DiagnosticsManualNothingPaged),
                });
            }

            string target = manual.HasRememberedTarget
                && !string.IsNullOrEmpty(manual.RememberedDestinationId)
                    ? manual.RememberedDestinationId
                    : DisplayCopy.DiagnosticsManualNothingPaged;

            var lines = new List<string>(4)
            {
                DisplayCopy.DiagnosticsFactLine(DisplayCopy.ManualPaging, target),
                DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsOwnsDisplay,
                    manual.OwnsDisplay ? DisplayCopy.DiagnosticsYes : DisplayCopy.DiagnosticsNo),
            };

            if (manual.MsSinceLastPress.HasValue)
            {
                lines.Add(DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsSinceLastPress,
                    DisplayCopy.DiagnosticsMs(manual.MsSinceLastPress.Value)));
            }

            if (manual.ReturnedToRest)
            {
                lines.Add(DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.BasePage, DisplayCopy.DiagnosticsReturnedToBase));
            }

            return new ReadOnlyCollection<string>(lines);
        }

        // ── Base / idle floor ────────────────────────────────────────────

        private static IReadOnlyList<string> BuildFloorLines(
            DisplayResolutionSnapshotModel resolution,
            DisplayConfigV2 config)
        {
            var lines = new List<string>(4);

            // Display-surface winner when it is a rest floor carrier / destination.
            var display = FindWinner(resolution, SeatArbiter.DisplaySurfaceId);
            if (display != null)
            {
                if (string.Equals(display.WinnerCarrierId, SeatArbiter.RestCarrierId, StringComparison.Ordinal)
                    || string.Equals(display.DestinationId, DestinationIds.RestInSession, StringComparison.Ordinal))
                {
                    lines.Add(DisplayCopy.DiagnosticsFactLine(
                        DisplayCopy.BasePage,
                        RuledDestinationDisplay(
                            display.DestinationId, DisplayCopy.WhenNothingAboveIsLive)));
                }
                else if (string.Equals(display.DestinationId, DestinationIds.RestIdle, StringComparison.Ordinal)
                    || string.Equals(display.WinnerCarrierId, DestinationIds.RestIdle, StringComparison.Ordinal))
                {
                    lines.Add(DisplayCopy.DiagnosticsFactLine(
                        DisplayCopy.OutsideASession,
                        IdleFloorDisplay(display.DestinationId, config)));
                }
            }

            // Wheel-screen idle floor when released (slice present).
            // Record carries the resolved floor (active step destination for playlists);
            // model projection only — no engine record growth (task #22 deliverable 5).
            var wheel = FindWinner(resolution, DestinationIds.WheelScreenSurfaceId);
            if (wheel != null
                && (string.IsNullOrEmpty(wheel.WinnerCarrierId)
                    || string.Equals(wheel.WinnerCarrierId, DestinationIds.RestIdle, StringComparison.Ordinal)
                    || string.Equals(wheel.DestinationId, DestinationIds.RestIdle, StringComparison.Ordinal)
                    || (wheel.DestinationId != null
                        && wheel.DestinationId.StartsWith("screen:", StringComparison.Ordinal))))
            {
                string dest = IdleFloorDisplay(wheel.DestinationId, config);
                string line = DisplayCopy.DiagnosticsFactLine(
                    DisplayCopy.DiagnosticsWheelScreenSection, dest);
                if (!ContainsLine(lines, line))
                    lines.Add(line);
            }

            // Explicit rest carriers on the ladder (presence owners stamp them).
            var carriers = resolution.Carriers;
            if (carriers != null)
            {
                for (int i = 0; i < carriers.Count; i++)
                {
                    var c = carriers[i];
                    if (c == null) continue;
                    if (string.Equals(c.CarrierId, SeatArbiter.RestCarrierId, StringComparison.Ordinal))
                    {
                        string line = DisplayCopy.DiagnosticsFactLine(
                            DisplayCopy.BasePage,
                            RuledDestinationDisplay(
                                c.DestinationId, DisplayCopy.WhenNothingAboveIsLive));
                        if (!ContainsLine(lines, line))
                            lines.Add(line);
                    }
                    else if (string.Equals(c.CarrierId, DestinationIds.RestIdle, StringComparison.Ordinal))
                    {
                        string line = DisplayCopy.DiagnosticsFactLine(
                            DisplayCopy.OutsideASession,
                            IdleFloorDisplay(c.DestinationId, config));
                        if (!ContainsLine(lines, line))
                            lines.Add(line);
                    }
                }
            }

            return lines.Count == 0
                ? NoLines
                : new ReadOnlyCollection<string>(lines);
        }

        /// <summary>
        /// Idle/floor value for diagnostics. When rest.idle is a playlist, show the
        /// playlist name + active step (from the resolved floor destination on the
        /// record) + skip labels — model projection only.
        /// </summary>
        private static string IdleFloorDisplay(string destinationId, DisplayConfigV2 config)
        {
            var idle = config?.Priority?.Rest?.Idle;
            if (idle != null
                && idle.Kind == IdleKind.Playlist
                && !string.IsNullOrWhiteSpace(idle.Playlist)
                && config.Playlists != null)
            {
                PlaylistEntry pl = null;
                for (int i = 0; i < config.Playlists.Count; i++)
                {
                    var cand = config.Playlists[i];
                    if (cand != null
                        && string.Equals(cand.Id, idle.Playlist, StringComparison.OrdinalIgnoreCase))
                    {
                        pl = cand;
                        break;
                    }
                }

                if (pl != null)
                {
                    string playlistName = !string.IsNullOrEmpty(pl.Name) ? pl.Name : pl.Id;
                    string activeName = MatchActiveStepName(pl, destinationId)
                        ?? RuledDestinationDisplay(destinationId, DisplayCopy.OutsideASession);
                    string skips = PlaylistSkipSummary(pl);
                    return DisplayCopy.DiagnosticsPlaylistFloor(playlistName, activeName, skips);
                }
            }

            return RuledDestinationDisplay(destinationId, DisplayCopy.OutsideASession);
        }

        private static string MatchActiveStepName(PlaylistEntry pl, string destinationId)
        {
            if (pl?.Steps == null || string.IsNullOrEmpty(destinationId))
                return null;
            for (int i = 0; i < pl.Steps.Count; i++)
            {
                var step = pl.Steps[i];
                if (step?.Destination == null || step.DegradedAtLoad)
                    continue;
                string stepDest = StepDestinationId(step.Destination);
                if (stepDest != null
                    && string.Equals(stepDest, destinationId, StringComparison.Ordinal))
                    return StepDisplayName(step.Destination);
            }
            // Firmware blank / screen destinations from DestinationIds.Screen.
            if (destinationId.StartsWith("screen:", StringComparison.Ordinal))
            {
                string spelling = destinationId.Substring("screen:".Length);
                return spelling;
            }
            return null;
        }

        private static string StepDestinationId(IdleSpec dest)
        {
            if (dest == null) return null;
            switch (dest.Kind)
            {
                case IdleKind.Page:
                    return DestinationIds.FromPageRef(dest.Page);
                case IdleKind.Screen:
                {
                    string spelling = WheelScreenArbiter.ScreenSpelling(dest.Screen);
                    return spelling == null ? null : DestinationIds.Screen(spelling);
                }
                case IdleKind.Blank:
                    return DestinationIds.Screen("blank");
                default:
                    return null;
            }
        }

        private static string StepDisplayName(IdleSpec dest)
        {
            if (dest == null) return string.Empty;
            switch (dest.Kind)
            {
                case IdleKind.Blank: return DisplayCopy.ABlankDisplay;
                case IdleKind.Screen:
                    switch (dest.Screen)
                    {
                        case WheelScreenCommand.Logo: return DisplayCopy.TheWheelsLogo;
                        case WheelScreenCommand.Blank: return DisplayCopy.ABlankDisplay;
                        case WheelScreenCommand.White: return DisplayCopy.WhiteScreen;
                        case WheelScreenCommand.LogoInverted: return DisplayCopy.LogoInvertedScreen;
                        default: return dest.ScreenRaw ?? string.Empty;
                    }
                case IdleKind.Page:
                    return dest.Page?.CatalogPageId ?? dest.Page?.Id ?? string.Empty;
                default:
                    return dest.KindRaw ?? string.Empty;
            }
        }

        private static string PlaylistSkipSummary(PlaylistEntry pl)
        {
            if (pl?.Steps == null) return null;
            var skipped = new List<string>();
            for (int i = 0; i < pl.Steps.Count; i++)
            {
                var step = pl.Steps[i];
                if (step == null) continue;
                if (step.DegradedAtLoad || (step.Destination != null && step.Destination.DegradedAtLoad))
                    skipped.Add(StepDisplayName(step.Destination));
            }
            if (skipped.Count == 0) return null;
            return DisplayCopy.PlaylistStepSkipped + ": " + string.Join(", ", skipped);
        }

        /// <summary>
        /// Never publish <c>rest:*</c> DestinationId internals. Map to DisplayCopy
        /// Base-page vocabulary; pass through already-resolved page destination names.
        /// </summary>
        private static string RuledDestinationDisplay(string destinationId, string restFallback = null)
        {
            if (string.IsNullOrEmpty(destinationId)
                || string.Equals(destinationId, DestinationIds.RestInSession, StringComparison.Ordinal))
            {
                return restFallback ?? DisplayCopy.WhenNothingAboveIsLive;
            }

            if (string.Equals(destinationId, DestinationIds.RestIdle, StringComparison.Ordinal))
                return DisplayCopy.OutsideASession;

            return destinationId;
        }

        private static bool ContainsLine(List<string> lines, string line)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.Equals(lines[i], line, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static SurfaceWinnerModel FindWinner(
            DisplayResolutionSnapshotModel resolution, string surfaceId)
        {
            var winners = resolution.SurfaceWinners;
            if (winners == null) return null;
            for (int i = 0; i < winners.Count; i++)
            {
                var w = winners[i];
                if (w != null
                    && string.Equals(w.SurfaceId, surfaceId, StringComparison.Ordinal))
                    return w;
            }
            return null;
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
    }

    /// <summary>One ladder-participant row on the diagnostics table.</summary>
    public sealed class DiagnosticsCarrierRowModel
    {
        public DiagnosticsCarrierRowModel(
            string label,
            string presenceCopy,
            string rowLabelsCopy,
            string conditionSentence,
            string destinationId,
            string timingDetail,
            string surfaceId,
            string carrierId)
        {
            Label = label ?? string.Empty;
            PresenceCopy = presenceCopy ?? string.Empty;
            RowLabelsCopy = rowLabelsCopy ?? string.Empty;
            ConditionSentence = conditionSentence ?? string.Empty;
            DestinationId = destinationId ?? string.Empty;
            TimingDetail = timingDetail ?? string.Empty;
            SurfaceId = surfaceId ?? string.Empty;
            CarrierId = carrierId ?? string.Empty;
        }

        public string Label { get; }
        public string PresenceCopy { get; }
        public string RowLabelsCopy { get; }
        public string ConditionSentence { get; }
        public string DestinationId { get; }
        public string TimingDetail { get; }
        public string SurfaceId { get; }
        public string CarrierId { get; }
    }
}
