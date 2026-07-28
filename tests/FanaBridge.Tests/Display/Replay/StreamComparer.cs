using System;
using System.Collections.Generic;
using System.Text;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>
    /// Ordered stream compare after PageSet-retry normalization. Any residual diff
    /// must match a named <see cref="KnownDiff"/> with expected bytes (seam-map §4.6).
    /// </summary>
    internal static class StreamComparer
    {
        public sealed class Result
        {
            public bool Passed { get; set; }
            public string? Failure { get; set; }
            public IReadOnlyList<WireAttempt> V9 { get; set; } = Array.Empty<WireAttempt>();
            public IReadOnlyList<WireAttempt> V2 { get; set; } = Array.Empty<WireAttempt>();
        }

        /// <summary>
        /// Compare streams. <paramref name="knownDiffs"/> may be null/empty for strict
        /// parity. FrameIndex on KnownDiff is matched with ±2 tolerance for collapsed
        /// SetPage runs (seam-map §4.4 post-assert); payload/channel/Accepted compare
        /// exactly for non-diff positions.
        /// </summary>
        public static Result Compare(
            IReadOnlyList<WireAttempt> rawV9,
            IReadOnlyList<WireAttempt> rawV2,
            IReadOnlyList<KnownDiff>? knownDiffs = null)
        {
            var v9 = PageSetRetryNormalizer.Normalize(rawV9);
            var v2 = PageSetRetryNormalizer.Normalize(rawV2);
            var remaining = new List<KnownDiff>(knownDiffs ?? Array.Empty<KnownDiff>());

            int i = 0, j = 0;
            while (i < v9.Count || j < v2.Count)
            {
                WireAttempt? a = i < v9.Count ? v9[i] : (WireAttempt?)null;
                WireAttempt? b = j < v2.Count ? v2[j] : (WireAttempt?)null;

                if (a.HasValue && b.HasValue
                    && EqualForDiff(a.Value, b.Value))
                {
                    i++;
                    j++;
                    continue;
                }

                // Divergence: must match a KnownDiff.
                int frame = a?.FrameIndex ?? b?.FrameIndex ?? -1;
                Chan channel = a?.Channel ?? b?.Channel ?? Chan.Col03;
                var match = FindAndConsume(remaining, frame, channel, a, b);
                if (match == null)
                {
                    return new Result
                    {
                        Passed = false,
                        Failure = FormatMismatch(v9, v2, i, j, remaining),
                        V9 = v9,
                        V2 = v2,
                    };
                }

                if (a.HasValue) i++;
                if (b.HasValue) j++;
            }

            if (remaining.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("Unconsumed KnownDiff(s): ");
                foreach (var d in remaining)
                    sb.Append(d.Name).Append(' ');
                return new Result
                {
                    Passed = false,
                    Failure = sb.ToString().TrimEnd(),
                    V9 = v9,
                    V2 = v2,
                };
            }

            return new Result { Passed = true, V9 = v9, V2 = v2 };
        }

        private static bool EqualForDiff(WireAttempt a, WireAttempt b)
            => a.Channel == b.Channel
            && a.Accepted == b.Accepted
            && WireAttempt.PayloadBytesEqual(a.Payload, b.Payload)
            // FrameIndex within ±2 for SetPage-retry residual timing (seam-map §4.4).
            && (a.FrameIndex == b.FrameIndex
                || (PageSetRetryNormalizer.IsSetPage(a.Payload)
                    && Math.Abs(a.FrameIndex - b.FrameIndex) <= 2));

        private static KnownDiff? FindAndConsume(
            List<KnownDiff> remaining,
            int frame,
            Chan channel,
            WireAttempt? v9,
            WireAttempt? v2)
        {
            for (int k = 0; k < remaining.Count; k++)
            {
                var d = remaining[k];
                if (d.Channel != channel)
                    continue;
                if (Math.Abs(d.FrameIndex - frame) > 2)
                    continue;

                if (!BytesMatch(d.ExpectedV9, v9?.Payload)
                    || !BytesMatch(d.ExpectedV2, v2?.Payload))
                    continue;

                remaining.RemoveAt(k);
                return d;
            }
            return null;
        }

        private static bool BytesMatch(byte[]? expected, byte[]? actual)
        {
            if (expected == null && actual == null)
                return true;
            if (expected == null || actual == null)
                return false;
            return WireAttempt.PayloadBytesEqual(expected, actual);
        }

        private static string FormatMismatch(
            IReadOnlyList<WireAttempt> v9,
            IReadOnlyList<WireAttempt> v2,
            int i,
            int j,
            List<KnownDiff> remaining)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Stream mismatch at v9[" + i + "] vs v2[" + j + "].");
            if (i < v9.Count)
            {
                var a = v9[i];
                sb.AppendLine("  v9: f=" + a.FrameIndex + " seq=" + a.SeqInFrame
                    + " " + a.Channel + " acc=" + a.Accepted + " " + a.ToHex());
            }
            else
                sb.AppendLine("  v9: <end>");
            if (j < v2.Count)
            {
                var b = v2[j];
                sb.AppendLine("  v2: f=" + b.FrameIndex + " seq=" + b.SeqInFrame
                    + " " + b.Channel + " acc=" + b.Accepted + " " + b.ToHex());
            }
            else
                sb.AppendLine("  v2: <end>");

            sb.AppendLine("--- v9 stream (" + v9.Count + ") ---");
            Dump(sb, v9);
            sb.AppendLine("--- v2 stream (" + v2.Count + ") ---");
            Dump(sb, v2);
            if (remaining.Count > 0)
            {
                sb.Append("Remaining KnownDiffs: ");
                foreach (var d in remaining)
                    sb.Append(d.Name).Append(' ');
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static void Dump(StringBuilder sb, IReadOnlyList<WireAttempt> stream)
        {
            for (int n = 0; n < stream.Count; n++)
            {
                var a = stream[n];
                sb.AppendLine(string.Format(
                    "  [{0,3}] f={1,3} s={2,2} {3} acc={4} {5}",
                    n, a.FrameIndex, a.SeqInFrame, a.Channel, a.Accepted, a.ToHex()));
            }
        }
    }
}
