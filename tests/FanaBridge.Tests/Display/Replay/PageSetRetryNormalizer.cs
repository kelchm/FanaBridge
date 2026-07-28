using System;
using System.Collections.Generic;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>
    /// The sole sanctioned stream normalization (seam-map §4.4): collapse true PageSet
    /// retry jitter — consecutive same-channel byte-identical SetPage reports within the
    /// documented retry window where every attempt except possibly the last is declined —
    /// into a single entry retaining the run's last attempt and first FrameIndex.
    /// Does not merge identical SetPages across arbitrary silent frames.
    /// </summary>
    internal static class PageSetRetryNormalizer
    {
        /// <summary>
        /// Max frame gap between consecutive declined retries of the same SetPage
        /// (ItmLifecycleController.PageSetSpacingMs ≈ 100 ms ≈ a few 16 ms frames;
        /// seam-map §4.4 residual ±2 after collapse).
        /// </summary>
        public const int MaxRetryFrameGap = 2;

        /// <summary>ITM SetPage report: FF 05 04 &lt;deviceId&gt; &lt;page&gt; …</summary>
        public static bool IsSetPage(byte[] payload)
            => payload != null
            && payload.Length >= 5
            && payload[0] == 0xFF
            && payload[1] == 0x05
            && payload[2] == 0x04;

        public static List<WireAttempt> Normalize(IReadOnlyList<WireAttempt> raw)
        {
            var result = new List<WireAttempt>(raw.Count);
            int i = 0;
            while (i < raw.Count)
            {
                var a = raw[i];
                if (!IsSetPage(a.Payload))
                {
                    result.Add(a);
                    i++;
                    continue;
                }

                // True retry run: consecutive stream entries, same channel/payload,
                // each prior attempt declined, and FrameIndex within MaxRetryFrameGap.
                int runStart = i;
                int j = i + 1;
                while (j < raw.Count
                    && raw[j].Channel == a.Channel
                    && IsSetPage(raw[j].Payload)
                    && WireAttempt.PayloadBytesEqual(raw[j].Payload, a.Payload)
                    && Math.Abs(raw[j].FrameIndex - raw[j - 1].FrameIndex) <= MaxRetryFrameGap)
                {
                    j++;
                }

                // Collapse only when every attempt except possibly the last is declined.
                bool collapsible = j - runStart > 1;
                if (collapsible)
                {
                    for (int k = runStart; k < j - 1; k++)
                    {
                        if (raw[k].Accepted)
                        {
                            collapsible = false;
                            break;
                        }
                    }
                }

                if (collapsible)
                {
                    var last = raw[j - 1];
                    var first = raw[runStart];
                    result.Add(new WireAttempt(
                        first.FrameIndex,
                        first.SeqInFrame,
                        first.TickMs,
                        last.Channel,
                        last.Payload,
                        last.Accepted));
                    i = j;
                }
                else
                {
                    // Emit the run as-is (single attempt, or non-collapsible).
                    for (int k = runStart; k < j; k++)
                        result.Add(raw[k]);
                    i = j;
                }
            }
            return result;
        }
    }
}
