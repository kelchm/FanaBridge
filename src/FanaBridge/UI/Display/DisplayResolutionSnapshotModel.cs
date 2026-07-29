using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Read-side view-model seam over <see cref="ComposedResolutionRecord"/> for future
    /// v2 views and the diagnostics panel. Pure model — no WPF. Presence and row-label
    /// enums map to ruled copy via <see cref="DisplayCopy"/>; the device block passes
    /// through unchanged. A null record yields <see cref="Empty"/>.
    ///
    /// O12 publishes four engine values Overview needs (read-side only):
    /// <see cref="InGame"/> (DeviceDisplayRuntime tick: GameRunning &amp;&amp; NewData),
    /// <see cref="IsConnected"/> (envelope present — disconnected nulls the snapshot),
    /// <see cref="SurfaceWinners"/> (from <see cref="ComposedResolutionRecord.SurfaceWinners"/>),
    /// <see cref="Aggregates"/> / <see cref="Manual"/> (from SeatArbiter tick result).
    /// </summary>
    public sealed class DisplayResolutionSnapshotModel
    {
        private static readonly IReadOnlyList<CarrierResolutionRowModel> NoRows =
            new ReadOnlyCollection<CarrierResolutionRowModel>(Array.Empty<CarrierResolutionRowModel>());

        private static readonly IReadOnlyList<string> NoLabels =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        private static readonly IReadOnlyList<SurfaceWinnerModel> NoWinners =
            new ReadOnlyCollection<SurfaceWinnerModel>(Array.Empty<SurfaceWinnerModel>());

        private static readonly IReadOnlyList<AggregateMembershipModel> NoAggregates =
            new ReadOnlyCollection<AggregateMembershipModel>(Array.Empty<AggregateMembershipModel>());

        private static readonly IReadOnlyList<string> NoDismissedIds =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        private static readonly IReadOnlyList<CarrierSnapshotRowModel> NoSnapshots =
            new ReadOnlyCollection<CarrierSnapshotRowModel>(Array.Empty<CarrierSnapshotRowModel>());

        /// <summary>Empty model for a null / missing record.</summary>
        public static DisplayResolutionSnapshotModel Empty { get; } =
            new DisplayResolutionSnapshotModel(
                tickMs: 0,
                deviceKey: string.Empty,
                hasDeviceBlock: false,
                pageKnowledge: CurrentPageKnowledge.Unknown,
                revertedThisTick: false,
                adoptWarnedThisTick: false,
                carriers: NoRows,
                surfaceWinners: NoWinners,
                inGame: false,
                isConnected: false,
                aggregates: NoAggregates,
                manual: null,
                itmDeviceId: null,
                surfaceHeld: false,
                releaseEdge: false,
                dismissedCarrierIds: NoDismissedIds,
                hasCapabilityEnvelope: false,
                capabilityEnvelope: null,
                carrierSnapshots: NoSnapshots);

        private DisplayResolutionSnapshotModel(
            long tickMs,
            string deviceKey,
            bool hasDeviceBlock,
            CurrentPageKnowledge pageKnowledge,
            bool revertedThisTick,
            bool adoptWarnedThisTick,
            IReadOnlyList<CarrierResolutionRowModel> carriers,
            IReadOnlyList<SurfaceWinnerModel> surfaceWinners,
            bool inGame,
            bool isConnected,
            IReadOnlyList<AggregateMembershipModel> aggregates,
            ManualRowStateModel manual,
            byte? itmDeviceId,
            bool surfaceHeld,
            bool releaseEdge,
            IReadOnlyList<string> dismissedCarrierIds,
            bool hasCapabilityEnvelope,
            CapabilityEnvelopeSummary capabilityEnvelope,
            IReadOnlyList<CarrierSnapshotRowModel> carrierSnapshots)
        {
            TickMs = tickMs;
            DeviceKey = deviceKey ?? string.Empty;
            HasDeviceBlock = hasDeviceBlock;
            PageKnowledge = pageKnowledge;
            RevertedThisTick = revertedThisTick;
            AdoptWarnedThisTick = adoptWarnedThisTick;
            Carriers = carriers ?? NoRows;
            SurfaceWinners = surfaceWinners ?? NoWinners;
            InGame = inGame;
            IsConnected = isConnected;
            Aggregates = aggregates ?? NoAggregates;
            Manual = manual;
            ItmDeviceId = itmDeviceId;
            SurfaceHeld = surfaceHeld;
            ReleaseEdge = releaseEdge;
            DismissedCarrierIds = dismissedCarrierIds ?? NoDismissedIds;
            HasCapabilityEnvelope = hasCapabilityEnvelope;
            CapabilityEnvelope = capabilityEnvelope;
            CarrierSnapshots = carrierSnapshots ?? NoSnapshots;
        }

        /// <summary>Engine clock at tick evaluation; 0 when empty.</summary>
        public long TickMs { get; }

        /// <summary>Device identity; empty when no record.</summary>
        public string DeviceKey { get; }

        /// <summary>True when the record carries the device-level block.</summary>
        public bool HasDeviceBlock { get; }

        /// <summary>Device-block page knowledge (passthrough).</summary>
        public CurrentPageKnowledge PageKnowledge { get; }

        /// <summary>Device-block reject edge flag (passthrough).</summary>
        public bool RevertedThisTick { get; }

        /// <summary>Device-block adopt-warn edge flag (passthrough).</summary>
        public bool AdoptWarnedThisTick { get; }

        /// <summary>Per-carrier rows with ruled presence / label copy.</summary>
        public IReadOnlyList<CarrierResolutionRowModel> Carriers { get; }

        // ── Record-gap diagnostics publication (read-side) ────────────────

        /// <summary>Distinct ITM device id; null when the record did not stamp one.</summary>
        public byte? ItmDeviceId { get; }

        /// <summary>Explicit wheel-screen col01 hold (not inferred from destination).</summary>
        public bool SurfaceHeld { get; }

        /// <summary>Explicit wheel-screen release edge this tick.</summary>
        public bool ReleaseEdge { get; }

        /// <summary>Dismissal latch carrier ids (ordered ordinal); empty when none.</summary>
        public IReadOnlyList<string> DismissedCarrierIds { get; }

        /// <summary>True when the record stamped a capability-envelope summary.</summary>
        public bool HasCapabilityEnvelope { get; }

        /// <summary>Capability-envelope summary; null when not stamped.</summary>
        public CapabilityEnvelopeSummary CapabilityEnvelope { get; }

        /// <summary>
        /// Full per-carrier evaluator snapshots beyond RemainingMs (ordered by carrier id).
        /// </summary>
        public IReadOnlyList<CarrierSnapshotRowModel> CarrierSnapshots { get; }

        // ── O12 published engine values ──────────────────────────────────

        /// <summary>
        /// O12 (a): in-game vs idle. Anchored to DeviceDisplayRuntime tick
        /// (<c>data.GameRunning &amp;&amp; data.NewData != null</c> →
        /// <c>DisplayCompositionV2TickInput.InGame</c>).
        /// </summary>
        public bool InGame { get; }

        /// <summary>
        /// O12 (b): connection state. Anchored to envelope presence — the runtime
        /// nulls <see cref="Runtime.DisplayPanelSnapshot"/> on disconnect; explicit
        /// here so Overview does not null-test the envelope.
        /// </summary>
        public bool IsConnected { get; }

        /// <summary>
        /// O12 (c): per-surface winners from
        /// <see cref="ComposedResolutionRecord.SurfaceWinners"/> (previously dropped).
        /// </summary>
        public IReadOnlyList<SurfaceWinnerModel> SurfaceWinners { get; }

        /// <summary>
        /// O12 (d): home-seat aggregate n-of-m from
        /// <see cref="SeatArbiterTickResult.Aggregates"/>.
        /// </summary>
        public IReadOnlyList<AggregateMembershipModel> Aggregates { get; }

        /// <summary>
        /// O12 (d): manual-row bookkeeping from
        /// <see cref="SeatArbiterTickResult.Manual"/>. Null when no seat result.
        /// </summary>
        public ManualRowStateModel Manual { get; }

        /// <summary>
        /// Translate a composed-resolution record into ruled copy. Null → <see cref="Empty"/>
        /// (disconnected). Optional O12 fields default to offline / empty.
        /// </summary>
        public static DisplayResolutionSnapshotModel From(ComposedResolutionRecord record)
            => From(record, inGame: false, isConnected: record != null, aggregates: null, manual: null);

        /// <summary>
        /// Full O12 projection: record + session/connection + seat diagnostics.
        /// </summary>
        public static DisplayResolutionSnapshotModel From(
            ComposedResolutionRecord record,
            bool inGame,
            bool isConnected,
            IReadOnlyList<AggregateMembership> aggregates,
            ManualRowState manual)
        {
            if (record == null)
            {
                // Connected with no record yet is still connected; null record from a
                // disconnected host is isConnected=false.
                if (!isConnected)
                    return Empty;
                return new DisplayResolutionSnapshotModel(
                    tickMs: 0,
                    deviceKey: string.Empty,
                    hasDeviceBlock: false,
                    pageKnowledge: CurrentPageKnowledge.Unknown,
                    revertedThisTick: false,
                    adoptWarnedThisTick: false,
                    carriers: NoRows,
                    surfaceWinners: NoWinners,
                    inGame: inGame,
                    isConnected: true,
                    aggregates: ProjectAggregates(aggregates),
                    manual: ProjectManual(manual),
                    itmDeviceId: null,
                    surfaceHeld: false,
                    releaseEdge: false,
                    dismissedCarrierIds: NoDismissedIds,
                    hasCapabilityEnvelope: false,
                    capabilityEnvelope: null,
                    carrierSnapshots: NoSnapshots);
            }

            var statuses = record.CarrierStatuses;
            var rows = new List<CarrierResolutionRowModel>(statuses != null ? statuses.Count : 0);
            if (statuses != null)
            {
                for (int i = 0; i < statuses.Count; i++)
                {
                    var s = statuses[i];
                    rows.Add(new CarrierResolutionRowModel(
                        s.CarrierId,
                        s.SurfaceId,
                        s.DestinationId,
                        PresenceCopy(s.Presence),
                        RowLabelCopies(s.RowLabels),
                        s.RemainingMs));
                }
            }

            var winners = record.SurfaceWinners;
            var winnerModels = new List<SurfaceWinnerModel>(winners != null ? winners.Count : 0);
            if (winners != null)
            {
                for (int i = 0; i < winners.Count; i++)
                {
                    var w = winners[i];
                    winnerModels.Add(new SurfaceWinnerModel(
                        w.SurfaceId, w.WinnerCarrierId, w.DestinationId));
                }
            }

            var snaps = record.CarrierSnapshots;
            var snapModels = new List<CarrierSnapshotRowModel>(snaps != null ? snaps.Count : 0);
            if (snaps != null)
            {
                for (int i = 0; i < snaps.Count; i++)
                {
                    var snap = snaps[i];
                    snapModels.Add(new CarrierSnapshotRowModel(
                        snap.CarrierId,
                        snap.ConditionSatisfied,
                        snap.Active,
                        snap.FreshFire,
                        snap.FiredThisTick,
                        snap.Eligible,
                        snap.ExpiresAtMs,
                        snap.RemainingMs));
                }
            }

            var dismissed = record.DismissedCarrierIds;
            IReadOnlyList<string> dismissedIds = NoDismissedIds;
            if (dismissed != null && dismissed.Count > 0)
            {
                var dlist = new List<string>(dismissed.Count);
                for (int i = 0; i < dismissed.Count; i++)
                {
                    if (dismissed[i] != null)
                        dlist.Add(dismissed[i]);
                }
                if (dlist.Count > 0)
                    dismissedIds = new ReadOnlyCollection<string>(dlist);
            }

            return new DisplayResolutionSnapshotModel(
                record.TickMs,
                record.DeviceKey,
                record.HasDeviceBlock,
                record.PageKnowledge,
                record.RevertedThisTick,
                record.AdoptWarnedThisTick,
                new ReadOnlyCollection<CarrierResolutionRowModel>(rows),
                new ReadOnlyCollection<SurfaceWinnerModel>(winnerModels),
                inGame,
                isConnected,
                ProjectAggregates(aggregates),
                ProjectManual(manual),
                record.ItmDeviceId,
                record.SurfaceHeld,
                record.ReleaseEdge,
                dismissedIds,
                record.HasCapabilityEnvelope,
                record.CapabilityEnvelope,
                snapModels.Count == 0
                    ? NoSnapshots
                    : new ReadOnlyCollection<CarrierSnapshotRowModel>(snapModels));
        }

        private static IReadOnlyList<AggregateMembershipModel> ProjectAggregates(
            IReadOnlyList<AggregateMembership> aggregates)
        {
            if (aggregates == null || aggregates.Count == 0)
                return NoAggregates;
            var list = new List<AggregateMembershipModel>(aggregates.Count);
            for (int i = 0; i < aggregates.Count; i++)
            {
                var a = aggregates[i];
                if (a == null) continue;
                list.Add(new AggregateMembershipModel(
                    a.SeatId,
                    a.DestinationId,
                    a.DerivedCarrierId,
                    a.ActiveCount,
                    a.TotalCount,
                    a.MemberCarrierIds,
                    a.MembershipDegraded));
            }
            return list.Count == 0
                ? NoAggregates
                : new ReadOnlyCollection<AggregateMembershipModel>(list);
        }

        private static ManualRowStateModel ProjectManual(ManualRowState manual)
        {
            if (manual == null)
                return null;
            return new ManualRowStateModel(
                manual.RememberedDestinationId,
                manual.HasRememberedTarget,
                manual.LandingDestinationId,
                manual.OwnsDisplay,
                manual.MsSinceLastPress,
                manual.ReturnedToRest,
                manual.AdoptedUnknownPage);
        }

        /// <summary>Map a D10 presence value to its ruled status string (or empty for non-check states).</summary>
        public static string PresenceCopy(CarrierPresence? presence)
        {
            if (presence == null)
                return string.Empty;

            switch (presence.Value)
            {
                case CarrierPresence.Waiting:
                    return DisplayCopy.Waiting;
                case CarrierPresence.Outranked:
                    return DisplayCopy.Outranked;
                case CarrierPresence.OffScreen:
                    return DisplayCopy.OffScreen;
                case CarrierPresence.OnScreen:
                    return DisplayCopy.OnScreen;
                case CarrierPresence.Dismissed:
                    // Presence-Dismissed is a non-check state; the DISMISSED row label
                    // carries the ruled word when stamped on RowLabels.
                    return string.Empty;
                default:
                    return string.Empty;
            }
        }

        /// <summary>Map a single row-label flag to its ruled (or diagnostics) string; null for None.</summary>
        public static string RowLabelCopy(CarrierRowLabels label)
        {
            switch (label)
            {
                case CarrierRowLabels.None:
                    return null;
                case CarrierRowLabels.Off:
                    return DisplayCopy.Off;
                case CarrierRowLabels.Dismissed:
                    return DisplayCopy.Dismissed;
                case CarrierRowLabels.CantRunHere:
                    return DisplayCopy.CantRunHere;
                case CarrierRowLabels.NoWheel:
                    return DisplayCopy.NoWheel;
                case CarrierRowLabels.Paused:
                    return DisplayCopy.Paused;
                case CarrierRowLabels.KeptAsIs:
                    return DisplayCopy.KeptAsIs;
                case CarrierRowLabels.OutOfSessionScope:
                    return DisplayCopy.OutOfSessionScope;
                case CarrierRowLabels.Untested:
                    return DisplayCopy.Untested;
                default:
                    return null;
            }
        }

        /// <summary>Expand flag bits to ordered ruled label strings (stable flag order).</summary>
        public static IReadOnlyList<string> RowLabelCopies(CarrierRowLabels labels)
        {
            if (labels == CarrierRowLabels.None)
                return NoLabels;

            var list = new List<string>(8);
            AppendIf(labels, CarrierRowLabels.Off, list);
            AppendIf(labels, CarrierRowLabels.NoWheel, list);
            AppendIf(labels, CarrierRowLabels.Paused, list);
            AppendIf(labels, CarrierRowLabels.KeptAsIs, list);
            AppendIf(labels, CarrierRowLabels.CantRunHere, list);
            AppendIf(labels, CarrierRowLabels.Dismissed, list);
            AppendIf(labels, CarrierRowLabels.OutOfSessionScope, list);
            AppendIf(labels, CarrierRowLabels.Untested, list);
            return list.Count == 0
                ? NoLabels
                : new ReadOnlyCollection<string>(list);
        }

        private static void AppendIf(CarrierRowLabels flags, CarrierRowLabels bit, List<string> list)
        {
            if ((flags & bit) == 0)
                return;
            var copy = RowLabelCopy(bit);
            if (copy != null)
                list.Add(copy);
        }
    }

    /// <summary>One carrier row on the resolution snapshot, with ruled copy already applied.</summary>
    public sealed class CarrierResolutionRowModel
    {
        public CarrierResolutionRowModel(
            string carrierId,
            string surfaceId,
            string destinationId,
            string presenceCopy,
            IReadOnlyList<string> rowLabelCopies,
            int? remainingMs)
        {
            CarrierId = carrierId;
            SurfaceId = surfaceId;
            DestinationId = destinationId;
            PresenceCopy = presenceCopy ?? string.Empty;
            RowLabelCopies = rowLabelCopies
                ?? new ReadOnlyCollection<string>(Array.Empty<string>());
            RemainingMs = remainingMs;
        }

        public string CarrierId { get; }
        public string SurfaceId { get; }
        public string DestinationId { get; }

        /// <summary>Ruled status string from <see cref="DisplayCopy"/>, or empty.</summary>
        public string PresenceCopy { get; }

        /// <summary>Ruled row-label strings from <see cref="DisplayCopy"/>.</summary>
        public IReadOnlyList<string> RowLabelCopies { get; }

        public int? RemainingMs { get; }
    }

    /// <summary>
    /// Full per-carrier evaluator snapshot projected for diagnostics (beyond RemainingMs).
    /// </summary>
    public sealed class CarrierSnapshotRowModel
    {
        public CarrierSnapshotRowModel(
            string carrierId,
            bool conditionSatisfied,
            bool active,
            bool freshFire,
            bool firedThisTick,
            bool eligible,
            long expiresAtMs,
            int? remainingMs)
        {
            CarrierId = carrierId ?? string.Empty;
            ConditionSatisfied = conditionSatisfied;
            Active = active;
            FreshFire = freshFire;
            FiredThisTick = firedThisTick;
            Eligible = eligible;
            ExpiresAtMs = expiresAtMs;
            RemainingMs = remainingMs;
        }

        public string CarrierId { get; }
        public bool ConditionSatisfied { get; }
        public bool Active { get; }
        public bool FreshFire { get; }
        public bool FiredThisTick { get; }
        public bool Eligible { get; }
        public long ExpiresAtMs { get; }
        public int? RemainingMs { get; }
    }

    /// <summary>O12 (c): one surface winner projected for UI binding.</summary>
    public sealed class SurfaceWinnerModel
    {
        public SurfaceWinnerModel(string surfaceId, string winnerCarrierId, string destinationId)
        {
            SurfaceId = surfaceId ?? string.Empty;
            WinnerCarrierId = winnerCarrierId;
            DestinationId = destinationId;
        }

        public string SurfaceId { get; }
        public string WinnerCarrierId { get; }
        public string DestinationId { get; }
    }

    /// <summary>O12 (d): aggregate n-of-m for one home seat.</summary>
    public sealed class AggregateMembershipModel
    {
        public AggregateMembershipModel(
            string seatId,
            string destinationId,
            string derivedCarrierId,
            int activeCount,
            int totalCount,
            IReadOnlyList<string> memberCarrierIds,
            bool membershipDegraded)
        {
            SeatId = seatId ?? string.Empty;
            DestinationId = destinationId;
            DerivedCarrierId = derivedCarrierId;
            ActiveCount = activeCount;
            TotalCount = totalCount;
            MemberCarrierIds = memberCarrierIds
                ?? new ReadOnlyCollection<string>(Array.Empty<string>());
            MembershipDegraded = membershipDegraded;
        }

        public string SeatId { get; }
        public string DestinationId { get; }
        public string DerivedCarrierId { get; }
        public int ActiveCount { get; }
        public int TotalCount { get; }
        public IReadOnlyList<string> MemberCarrierIds { get; }
        public bool MembershipDegraded { get; }
    }

    /// <summary>O12 (d): manual-row bookkeeping projected for UI binding.</summary>
    public sealed class ManualRowStateModel
    {
        public ManualRowStateModel(
            string rememberedDestinationId,
            bool hasRememberedTarget,
            string landingDestinationId,
            bool ownsDisplay,
            long? msSinceLastPress,
            bool returnedToRest,
            bool adoptedUnknownPage)
        {
            RememberedDestinationId = rememberedDestinationId;
            HasRememberedTarget = hasRememberedTarget;
            LandingDestinationId = landingDestinationId;
            OwnsDisplay = ownsDisplay;
            MsSinceLastPress = msSinceLastPress;
            ReturnedToRest = returnedToRest;
            AdoptedUnknownPage = adoptedUnknownPage;
        }

        public string RememberedDestinationId { get; }
        public bool HasRememberedTarget { get; }
        public string LandingDestinationId { get; }
        public bool OwnsDisplay { get; }
        public long? MsSinceLastPress { get; }
        public bool ReturnedToRest { get; }
        public bool AdoptedUnknownPage { get; }
    }
}
