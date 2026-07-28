using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Pure merge of E4 + E5 + E6 composed-resolution slices into THE one-tick
    /// <see cref="ComposedResolutionRecord"/> (contract §6.1). Dormant until E8 wires
    /// composition — nothing live calls this yet.
    /// </summary>
    public static class ComposedResolutionMerger
    {
        /// <summary>
        /// Thrown when a presence or ownership conflict is hard-failed (contract §6.1).
        /// </summary>
        public sealed class MergeConflictException : InvalidOperationException
        {
            public MergeConflictException(string message) : base(message) { }
        }

        /// <summary>
        /// Merge partial records for one tick. Null slices are skipped.
        /// Presence conflicts (both non-null and disagree) invoke
        /// <paramref name="onPresenceConflict"/> then throw
        /// <see cref="MergeConflictException"/> (fail-fast). DestinationId is resolved
        /// by surface-prefix ownership (page:/field: → frame; display → seat;
        /// wheelScreen → wheel). Duplicate SurfaceWinners assert like presence.
        /// TickMs/DeviceKey prefer E4 when present.
        /// </summary>
        public static ComposedResolutionRecord Merge(
            ComposedResolutionRecord seat,
            ComposedResolutionRecord frame,
            ComposedResolutionRecord wheelScreen,
            Action<string> onPresenceConflict = null)
        {
            long tickMs = 0;
            string deviceKey = "";
            bool haveMeta = false;

            // Device-level block: prefer seat (director feeds next-tick via E4), then others.
            CurrentPageKnowledge pageKnowledge = CurrentPageKnowledge.Unknown;
            bool havePageKnowledge = false;
            bool reverted = false;
            bool adoptWarned = false;

            void TakeMeta(ComposedResolutionRecord r, bool prefer)
            {
                if (r == null)
                    return;
                if (!haveMeta || prefer)
                {
                    tickMs = r.TickMs;
                    deviceKey = r.DeviceKey ?? "";
                    haveMeta = true;
                }
                else if (r.TickMs != tickMs
                    || !string.Equals(r.DeviceKey ?? "", deviceKey, StringComparison.Ordinal))
                {
                    string msg = "composed-resolution merge: TickMs/DeviceKey mismatch across emitters";
                    onPresenceConflict?.Invoke(msg);
                    throw new MergeConflictException(msg);
                }

                if (r.HasDeviceBlock)
                {
                    if (!havePageKnowledge || prefer)
                    {
                        pageKnowledge = r.PageKnowledge;
                        havePageKnowledge = true;
                    }
                    if (r.RevertedThisTick)
                        reverted = true;
                    if (r.AdoptWarnedThisTick)
                        adoptWarned = true;
                }
            }

            // Prefer E4 meta when present (contract §6.1).
            TakeMeta(seat, prefer: true);
            TakeMeta(frame, prefer: false);
            TakeMeta(wheelScreen, prefer: false);

            var winners = new Dictionary<string, SurfaceWinner>(StringComparer.Ordinal);
            // Track which emitter owned each surface winner for duplicate detection.
            var winnerOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            var statuses = new Dictionary<RowKey, CarrierResolutionStatus>(RowKeyComparer.Instance);
            // Track DestinationId source ownership per row for conflict detection.
            var destOwner = new Dictionary<RowKey, string>(RowKeyComparer.Instance);
            var snapshots = new Dictionary<string, CarrierTickSnapshot>(StringComparer.Ordinal);

            void Fail(string message)
            {
                onPresenceConflict?.Invoke(message);
                throw new MergeConflictException(message);
            }

            void Ingest(ComposedResolutionRecord r, string emitter)
            {
                if (r == null)
                    return;

                if (r.SurfaceWinners != null)
                {
                    foreach (var w in r.SurfaceWinners)
                    {
                        if (w.SurfaceId == null)
                            continue;
                        if (winners.TryGetValue(w.SurfaceId, out var existing))
                        {
                            // Duplicate SurfaceWinner for same surface from different emitters
                            // is a wiring bug (contract §6.1: one owning emitter per surface).
                            if (!string.Equals(winnerOwner[w.SurfaceId], emitter, StringComparison.Ordinal)
                                && !SurfaceWinnerEqual(existing, w))
                            {
                                Fail("composed-resolution merge: duplicate SurfaceWinner for "
                                    + w.SurfaceId + " from " + winnerOwner[w.SurfaceId]
                                    + " and " + emitter);
                            }
                            continue;
                        }
                        // Only the owning emitter may register a surface (by prefix).
                        string owner = OwnerForSurface(w.SurfaceId);
                        if (owner != null && !string.Equals(owner, emitter, StringComparison.Ordinal))
                        {
                            // Non-owner winner is ignored when no owner entry yet; if values
                            // would later conflict the owner wins. Soft skip for foreign noise.
                            continue;
                        }
                        winners[w.SurfaceId] = w;
                        winnerOwner[w.SurfaceId] = emitter;
                    }
                }

                if (r.CarrierStatuses != null)
                {
                    foreach (var s in r.CarrierStatuses)
                    {
                        if (s.CarrierId == null || s.SurfaceId == null)
                            continue;
                        var key = new RowKey(s.CarrierId, s.SurfaceId);
                        if (!statuses.TryGetValue(key, out var existing))
                        {
                            statuses[key] = s;
                            if (s.DestinationId != null)
                                destOwner[key] = emitter;
                            continue;
                        }

                        // RowLabels: union of flags.
                        var labels = existing.RowLabels | s.RowLabels;

                        // Presence: at most one non-null; conflict = fail-fast.
                        CarrierPresence? presence = existing.Presence;
                        if (presence == null)
                            presence = s.Presence;
                        else if (s.Presence != null && s.Presence != presence)
                        {
                            Fail("composed-resolution merge: presence conflict for "
                                + s.CarrierId + "@" + s.SurfaceId
                                + " (" + presence + " vs " + s.Presence + ")");
                        }

                        // DestinationId: resolve by surface-prefix ownership.
                        string dest = ResolveDestinationId(
                            s.SurfaceId, existing.DestinationId, s.DestinationId,
                            destOwner.TryGetValue(key, out var priorOwner) ? priorOwner : null,
                            emitter, onPresenceConflict, Fail);
                        if (s.DestinationId != null
                            && string.Equals(OwnerForSurface(s.SurfaceId), emitter, StringComparison.Ordinal))
                            destOwner[key] = emitter;

                        // RemainingMs: prefer non-null.
                        int? remaining = existing.RemainingMs ?? s.RemainingMs;

                        statuses[key] = new CarrierResolutionStatus(
                            s.CarrierId, s.SurfaceId, dest, presence, remaining, labels);
                    }
                }

                if (r.CarrierSnapshots != null)
                {
                    foreach (var snap in r.CarrierSnapshots)
                    {
                        if (snap.CarrierId == null)
                            continue;
                        // Union by CarrierId (not concat).
                        snapshots[snap.CarrierId] = snap;
                    }
                }
            }

            // Order: evaluator → E4 → E6 → E5 → writes (contract §6.2 within-tick order).
            // Merge ingest: seat first, then wheel, then frame — ownership resolved by prefix.
            Ingest(seat, "seat");
            Ingest(wheelScreen, "wheelScreen");
            Ingest(frame, "frame");

            // Deterministic ordering for the golden.
            var winnerList = new List<SurfaceWinner>(winners.Values);
            winnerList.Sort((a, b) => string.CompareOrdinal(a.SurfaceId, b.SurfaceId));

            var statusList = new List<CarrierResolutionStatus>(statuses.Values);
            statusList.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.SurfaceId, b.SurfaceId);
                return c != 0 ? c : string.CompareOrdinal(a.CarrierId, b.CarrierId);
            });

            var snapList = new List<CarrierTickSnapshot>(snapshots.Values);
            snapList.Sort((a, b) => string.CompareOrdinal(a.CarrierId, b.CarrierId));

            return new ComposedResolutionRecord(
                tickMs, deviceKey, winnerList, statusList, snapList,
                pageKnowledge, reverted, adoptWarned);
        }

        /// <summary>
        /// Surface ownership by prefix (contract §6.1): page:/field: → frame;
        /// display → seat; wheelScreen → wheelScreen.
        /// </summary>
        public static string OwnerForSurface(string surfaceId)
        {
            if (surfaceId == null)
                return null;
            if (surfaceId.StartsWith("page:", StringComparison.Ordinal)
                || surfaceId.StartsWith("field:", StringComparison.Ordinal))
                return "frame";
            if (string.Equals(surfaceId, DestinationIds.WheelScreenSurfaceId, StringComparison.Ordinal)
                || string.Equals(surfaceId, "wheelScreen", StringComparison.Ordinal))
                return "wheelScreen";
            if (string.Equals(surfaceId, "display", StringComparison.Ordinal))
                return "seat";
            return null;
        }

        private static string ResolveDestinationId(
            string surfaceId,
            string existingDest,
            string incomingDest,
            string existingEmitter,
            string incomingEmitter,
            Action<string> onConflict,
            Action<string> fail)
        {
            string owner = OwnerForSurface(surfaceId);

            // Prefer the owner's non-null value.
            if (owner != null)
            {
                if (string.Equals(incomingEmitter, owner, StringComparison.Ordinal)
                    && incomingDest != null)
                {
                    // Owner supplies — if a non-owner already stamped a different value, conflict.
                    if (existingDest != null
                        && !string.Equals(existingDest, incomingDest, StringComparison.Ordinal)
                        && existingEmitter != null
                        && !string.Equals(existingEmitter, owner, StringComparison.Ordinal))
                    {
                        string msg = "composed-resolution merge: DestinationId ownership conflict for "
                            + surfaceId + " (owner " + owner + " has '" + incomingDest
                            + "', non-owner " + existingEmitter + " had '" + existingDest + "')";
                        onConflict?.Invoke(msg);
                        // Owner wins; diagnostic already fired. Keep owner value.
                    }
                    return incomingDest;
                }

                if (string.Equals(existingEmitter, owner, StringComparison.Ordinal)
                    && existingDest != null)
                {
                    // Existing is owner; incoming non-owner with different non-null → conflict.
                    if (incomingDest != null
                        && !string.Equals(existingDest, incomingDest, StringComparison.Ordinal)
                        && !string.Equals(incomingEmitter, owner, StringComparison.Ordinal))
                    {
                        string msg = "composed-resolution merge: DestinationId ownership conflict for "
                            + surfaceId + " (owner " + owner + " has '" + existingDest
                            + "', non-owner " + incomingEmitter + " supplied '" + incomingDest + "')";
                        onConflict?.Invoke(msg);
                    }
                    return existingDest;
                }
            }

            // No owner rule or neither is owner: prefer non-null first.
            return existingDest ?? incomingDest;
        }

        private static bool SurfaceWinnerEqual(SurfaceWinner a, SurfaceWinner b)
            => string.Equals(a.WinnerCarrierId, b.WinnerCarrierId, StringComparison.Ordinal)
               && string.Equals(a.DestinationId, b.DestinationId, StringComparison.Ordinal);

        private readonly struct RowKey
        {
            public RowKey(string carrierId, string surfaceId)
            {
                CarrierId = carrierId;
                SurfaceId = surfaceId;
            }

            public string CarrierId { get; }
            public string SurfaceId { get; }
        }

        private sealed class RowKeyComparer : IEqualityComparer<RowKey>
        {
            public static readonly RowKeyComparer Instance = new RowKeyComparer();

            public bool Equals(RowKey x, RowKey y)
                => string.Equals(x.CarrierId, y.CarrierId, StringComparison.Ordinal)
                   && string.Equals(x.SurfaceId, y.SurfaceId, StringComparison.Ordinal);

            public int GetHashCode(RowKey obj)
            {
                unchecked
                {
                    int h = obj.CarrierId != null
                        ? StringComparer.Ordinal.GetHashCode(obj.CarrierId) : 0;
                    h = (h * 397) ^ (obj.SurfaceId != null
                        ? StringComparer.Ordinal.GetHashCode(obj.SurfaceId) : 0);
                    return h;
                }
            }
        }
    }
}
