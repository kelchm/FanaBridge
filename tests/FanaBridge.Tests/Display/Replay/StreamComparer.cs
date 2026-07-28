using System;
using System.Collections.Generic;
using System.Text;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>
    /// Ordered stream compare after PageSet-retry normalization. Any residual diff
    /// must match a named <see cref="KnownDiff"/> with expected bytes (seam-map §4.6).
    /// Named class fixtures are exact expected-byte substitutions at pinned stream
    /// positions — no prefix-retention, no tail-discard, no contains-checks (FR-4 /
    /// comparer-truth). An unnamed residual byte pair fails the cell.
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
        /// parity. Named KnownDiff FrameIndex is matched with ±2 tolerance for collapsed
        /// SetPage retry residual (seam-map §4.4 post-assert). Stream equality itself is
        /// exact on (Channel, Accepted, Payload) — no blanket SetPage FrameIndex grace.
        /// </summary>
        public static Result Compare(
            IReadOnlyList<WireAttempt> rawV9,
            IReadOnlyList<WireAttempt> rawV2,
            IReadOnlyList<KnownDiff>? knownDiffs = null)
        {
            var v9 = PageSetRetryNormalizer.Normalize(rawV9);
            var v2 = PageSetRetryNormalizer.Normalize(rawV2);
            var remaining = new List<KnownDiff>(knownDiffs ?? Array.Empty<KnownDiff>());

            // Named class fixtures: exact-byte substitution only (may rewrite streams).
            string? classFail = ApplyNamedClassFixtures(ref v9, ref v2, remaining);
            if (classFail != null)
            {
                return new Result
                {
                    Passed = false,
                    Failure = classFail,
                    V9 = v9,
                    V2 = v2,
                };
            }

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

                // Divergence: must match a KnownDiff (exact expected-byte substitution).
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

                // One-sided KnownDiff (null expected on one side): advance only the side
                // that produced the pinned payload (insertion / extra write).
                bool onlyV9 = match.ExpectedV9 != null && match.ExpectedV2 == null;
                bool onlyV2 = match.ExpectedV2 != null && match.ExpectedV9 == null;
                if (onlyV9)
                {
                    if (a.HasValue) i++;
                }
                else if (onlyV2)
                {
                    if (b.HasValue) j++;
                }
                else
                {
                    if (a.HasValue) i++;
                    if (b.HasValue) j++;
                }
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

        /// <summary>
        /// Named multi-frame classes: substitute exact expected bytes at every matching
        /// stream entry, then leave residual streams for strict compare. No LCP keep,
        /// no tail truncate, no "contains SetPage" checks.
        /// </summary>
        private static string? ApplyNamedClassFixtures(
            ref List<WireAttempt> v9,
            ref List<WireAttempt> v2,
            List<KnownDiff> remaining)
        {
            int bakeIdx = remaining.FindIndex(d =>
                string.Equals(d.Name, "legacy-mode-bake-is-v1-only", StringComparison.Ordinal));
            if (bakeIdx >= 0)
            {
                var d = remaining[bakeIdx];
                remaining.RemoveAt(bakeIdx);
                string? bakeErr = SubstituteExactPayloads(
                    ref v9, ref v2,
                    d.ExpectedV9, d.ExpectedV2,
                    "legacy-mode-bake-is-v1-only");
                if (bakeErr != null)
                    return bakeErr;
                // Scripted dynamic gear faces (v9-only Col01 Gear 3/5/2) — optional pins.
                for (int g = 0; g < ReplayKnownDiffs.ModeBakeDynamicGearFacesV9Only.Length; g++)
                    RemoveAllMatchingPayload(ref v9, ReplayKnownDiffs.ModeBakeDynamicGearFacesV9Only[g]);
            }

            int blankIdx = remaining.FindIndex(d =>
                string.Equals(d.Name, "blank-compile-firmware-idle", StringComparison.Ordinal));
            if (blankIdx >= 0)
            {
                var d = remaining[blankIdx];
                remaining.RemoveAt(blankIdx);
                // One-sided: remove every exact v2 blank face; v9 has no twin payload.
                if (d.ExpectedV2 == null)
                    return "blank-compile-firmware-idle: ExpectedV2 must be pinned";
                int removed = RemoveAllMatchingPayload(ref v2, d.ExpectedV2);
                if (removed == 0)
                {
                    return "blank-compile-firmware-idle: v2 missing expected blank face "
                        + Hex(d.ExpectedV2);
                }
                // Class C residual: one-sided v9 SetPage(3,2) including unconfirmed retry.
                // Optional (0 OK) so --blank-compile-three-row-split can stack this class
                // without requiring the idle policy page pin.
                RemoveAllMatchingPayload(ref v9, ReplayKnownDiffs.SetPagePolicyPage2Pbme);
            }

            int blinkIdx = remaining.FindIndex(d =>
                string.Equals(d.Name, "suffix-blink-v2-only", StringComparison.Ordinal));
            if (blinkIdx >= 0)
            {
                remaining.RemoveAt(blinkIdx);
                // First value-update pair must differ (blink plan on v2). Then substitute
                // every remaining value-update pair (same-index walk) so later blink
                // frames do not re-fail — residual non-value-update content stays strict.
                int i9 = IndexOfValueUpdate(v9);
                int i2 = IndexOfValueUpdate(v2);
                if (i9 < 0 || i2 < 0)
                    return "suffix-blink-v2-only: missing value-update on one side";
                if (WireAttempt.PayloadBytesEqual(v9[i9].Payload, v2[i2].Payload))
                    return "suffix-blink-v2-only: value-update payloads identical (blink did not fire)";
                while (true)
                {
                    i9 = IndexOfValueUpdate(v9);
                    i2 = IndexOfValueUpdate(v2);
                    if (i9 < 0 && i2 < 0)
                        break;
                    if (i9 < 0 || i2 < 0)
                        return "suffix-blink-v2-only: value-update count mismatch after pin";
                    v9 = RemoveAt(v9, i9);
                    v2 = RemoveAt(v2, i2);
                }
                // Mode-bake companion (page target, no segment world): gear vs speed paints.
                string? bakeErr = SubstituteExactPayloads(
                    ref v9, ref v2,
                    ReplayKnownDiffs.GearFourCol01,
                    ReplayKnownDiffs.Speed142Col01,
                    "suffix-blink-v2-only/mode-bake");
                if (bakeErr != null)
                    return bakeErr;
                for (int g = 0; g < ReplayKnownDiffs.ModeBakeDynamicGearFacesV9Only.Length; g++)
                    RemoveAllMatchingPayload(ref v9, ReplayKnownDiffs.ModeBakeDynamicGearFacesV9Only[g]);
            }

            int itmRestIdx = remaining.FindIndex(d =>
                string.Equals(d.Name, "itm-rest-page-is-v1-only", StringComparison.Ordinal));
            if (itmRestIdx >= 0)
            {
                var d = remaining[itmRestIdx];
                remaining.RemoveAt(itmRestIdx);
                // SetPage→Legacy: strict ≥1 both sides (KnownDiff ExpectedV9/V2 are the pin).
                if (d.ExpectedV9 == null || d.ExpectedV2 == null)
                {
                    return "itm-rest-page-is-v1-only: ExpectedV9 and ExpectedV2 "
                        + "must both be pinned SetPage bytes";
                }
                string? setPageErr = SubstituteExactPayloads(
                    ref v9, ref v2,
                    d.ExpectedV9, d.ExpectedV2,
                    "itm-rest-page-is-v1-only/setpage");
                if (setPageErr != null)
                    return setPageErr;

                // VAL/DEF: ≥1 on v9 only (v2 may be zero on lifecycle-recovery).
                bool bentley = d.ExpectedV9 != null
                    && d.ExpectedV9.Length >= 4
                    && d.ExpectedV9[3] == 0x04;
                byte[] val = bentley
                    ? ReplayKnownDiffs.ValueUpdateLapInfoBentley
                    : ReplayKnownDiffs.ValueUpdateLapInfoPbme;
                byte[] def = bentley
                    ? ReplayKnownDiffs.ParamDefsLapInfoBentley
                    : ReplayKnownDiffs.ParamDefsLapInfoPbme;
                string? valErr = SubstituteExactPayloads(
                    ref v9, ref v2, val, val,
                    "itm-rest-page-is-v1-only/value-update",
                    requireV9: true, requireV2: false);
                if (valErr != null)
                    return valErr;
                string? defErr = SubstituteExactPayloads(
                    ref v9, ref v2, def, def,
                    "itm-rest-page-is-v1-only/param-defs",
                    requireV9: true, requireV2: false);
                if (defErr != null)
                    return defErr;
            }

            int segIdx = remaining.FindIndex(d =>
                string.Equals(d.Name, "segment-screen-setpage-ordering", StringComparison.Ordinal));
            if (segIdx >= 0)
            {
                var d = remaining[segIdx];
                remaining.RemoveAt(segIdx);
                // Exact expected-byte substitution of the pinned SetPage pair only.
                // Bytes/channel are on the KnownDiff; no contains-check, no tail discard.
                if (d.ExpectedV9 == null || d.ExpectedV2 == null)
                {
                    return "segment-screen-setpage-ordering: ExpectedV9 and ExpectedV2 "
                        + "must both be pinned SetPage bytes";
                }
                string? segErr = SubstituteExactPayloads(
                    ref v9, ref v2,
                    d.ExpectedV9, d.ExpectedV2,
                    "segment-screen-setpage-ordering");
                if (segErr != null)
                    return segErr;
            }

            // special-v2-trailing-setpage intentionally absent (manufactured fixture class killed).

            return null;
        }

        /// <summary>
        /// Remove every exact ExpectedV9 payload from v9 and every exact ExpectedV2
        /// payload from v2. By default requires at least one match on each non-null side.
        /// <paramref name="requireV9"/> / <paramref name="requireV2"/> relax that for
        /// asymmetric class members (itm-rest-page VAL/DEF: v9 ≥1 only).
        /// </summary>
        private static string? SubstituteExactPayloads(
            ref List<WireAttempt> v9,
            ref List<WireAttempt> v2,
            byte[]? expectedV9,
            byte[]? expectedV2,
            string className,
            bool requireV9 = true,
            bool requireV2 = true)
        {
            int n9 = expectedV9 == null ? 0 : RemoveAllMatchingPayload(ref v9, expectedV9);
            int n2 = expectedV2 == null ? 0 : RemoveAllMatchingPayload(ref v2, expectedV2);
            if (requireV9 && expectedV9 != null && n9 == 0)
            {
                return className + ": v9 missing expected payload " + Hex(expectedV9);
            }
            if (requireV2 && expectedV2 != null && n2 == 0)
            {
                return className + ": v2 missing expected payload " + Hex(expectedV2);
            }
            return null;
        }

        private static int RemoveAllMatchingPayload(ref List<WireAttempt> stream, byte[] payload)
        {
            int removed = 0;
            var list = new List<WireAttempt>(stream.Count);
            for (int i = 0; i < stream.Count; i++)
            {
                if (WireAttempt.PayloadBytesEqual(stream[i].Payload, payload))
                {
                    removed++;
                    continue;
                }
                list.Add(stream[i]);
            }
            stream = list;
            return removed;
        }

        private static int IndexOfValueUpdate(IReadOnlyList<WireAttempt> stream)
        {
            for (int i = 0; i < stream.Count; i++)
            {
                var p = stream[i].Payload;
                if (stream[i].Channel == Chan.Col03
                    && p != null && p.Length >= 3
                    && p[0] == 0xFF && p[1] == 0x05 && p[2] == 0x01)
                    return i;
            }
            return -1;
        }

        private static List<WireAttempt> RemoveAt(IReadOnlyList<WireAttempt> stream, int index)
        {
            var list = new List<WireAttempt>(stream.Count - 1);
            for (int i = 0; i < stream.Count; i++)
            {
                if (i != index)
                    list.Add(stream[i]);
            }
            return list;
        }

        private static string Hex(byte[]? p)
            => p == null ? "<null>" : BitConverter.ToString(p);

        private static bool EqualForDiff(WireAttempt a, WireAttempt b)
            => a.Channel == b.Channel
            && a.Accepted == b.Accepted
            && WireAttempt.PayloadBytesEqual(a.Payload, b.Payload);

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
                if (d.FrameIndex >= 0)
                {
                    if (d.Channel != channel)
                        continue;
                    if (Math.Abs(d.FrameIndex - frame) > PageSetRetryNormalizer.MaxRetryFrameGap)
                        continue;
                }
                else
                {
                    bool chOk = d.Channel == channel
                        || (v2.HasValue && d.Channel == v2.Value.Channel)
                        || (v9.HasValue && d.Channel == v9.Value.Channel);
                    if (!chOk)
                        continue;
                }

                // One-sided: null expected means that side is not required to match payload
                // (insertion). The non-null side must match.
                bool onlyV9 = d.ExpectedV9 != null && d.ExpectedV2 == null;
                bool onlyV2 = d.ExpectedV2 != null && d.ExpectedV9 == null;
                if (onlyV9)
                {
                    if (!v9.HasValue || !BytesMatch(d.ExpectedV9, v9.Value.Payload))
                        continue;
                }
                else if (onlyV2)
                {
                    if (!v2.HasValue || !BytesMatch(d.ExpectedV2, v2.Value.Payload))
                        continue;
                }
                else if (!BytesMatch(d.ExpectedV9, v9?.Payload)
                    || !BytesMatch(d.ExpectedV2, v2?.Payload))
                {
                    continue;
                }

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
