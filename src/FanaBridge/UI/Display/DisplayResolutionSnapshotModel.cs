using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FanaBridge.Display.Rules;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Read-side view-model seam over <see cref="ComposedResolutionRecord"/> for future
    /// v2 views and the diagnostics panel. Pure model — no WPF. Presence and row-label
    /// enums map to ruled copy via <see cref="DisplayCopy"/>; the device block passes
    /// through unchanged. A null record yields <see cref="Empty"/>.
    /// </summary>
    public sealed class DisplayResolutionSnapshotModel
    {
        private static readonly IReadOnlyList<CarrierResolutionRowModel> NoRows =
            new ReadOnlyCollection<CarrierResolutionRowModel>(Array.Empty<CarrierResolutionRowModel>());

        private static readonly IReadOnlyList<string> NoLabels =
            new ReadOnlyCollection<string>(Array.Empty<string>());

        /// <summary>Empty model for a null / missing record.</summary>
        public static DisplayResolutionSnapshotModel Empty { get; } =
            new DisplayResolutionSnapshotModel(
                tickMs: 0,
                deviceKey: string.Empty,
                hasDeviceBlock: false,
                pageKnowledge: CurrentPageKnowledge.Unknown,
                revertedThisTick: false,
                adoptWarnedThisTick: false,
                carriers: NoRows);

        private DisplayResolutionSnapshotModel(
            long tickMs,
            string deviceKey,
            bool hasDeviceBlock,
            CurrentPageKnowledge pageKnowledge,
            bool revertedThisTick,
            bool adoptWarnedThisTick,
            IReadOnlyList<CarrierResolutionRowModel> carriers)
        {
            TickMs = tickMs;
            DeviceKey = deviceKey ?? string.Empty;
            HasDeviceBlock = hasDeviceBlock;
            PageKnowledge = pageKnowledge;
            RevertedThisTick = revertedThisTick;
            AdoptWarnedThisTick = adoptWarnedThisTick;
            Carriers = carriers ?? NoRows;
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

        /// <summary>
        /// Translate a composed-resolution record into ruled copy. Null → <see cref="Empty"/>.
        /// </summary>
        public static DisplayResolutionSnapshotModel From(ComposedResolutionRecord record)
        {
            if (record == null)
                return Empty;

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

            return new DisplayResolutionSnapshotModel(
                record.TickMs,
                record.DeviceKey,
                record.HasDeviceBlock,
                record.PageKnowledge,
                record.RevertedThisTick,
                record.AdoptWarnedThisTick,
                new ReadOnlyCollection<CarrierResolutionRowModel>(rows));
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
}
