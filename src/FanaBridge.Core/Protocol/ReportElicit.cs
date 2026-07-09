using System;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// The one request→response primitive over a per-family report stream:
    /// flush stale frames, send the request(s) under a write batch, then await
    /// the first frame the matcher accepts. Because the caller elicits on the
    /// stream it OWNS, nothing competes for the response and the flush cannot
    /// discard another consumer's frames — the failure modes of the old shared
    /// col03 read loop.
    /// </summary>
    internal static class ReportElicit
    {
        /// <summary>
        /// Runs one elicit. Returns the matched frame's length (copied into
        /// <paramref name="response"/>), or -1 when no accepted frame arrived
        /// within <paramref name="timeoutMs"/>. Non-matching frames on the same
        /// stream (e.g. a stale response racing the flush) are skipped.
        /// </summary>
        /// <param name="io">Transport, for the sends and the write batch.</param>
        /// <param name="stream">The caller-owned family stream carrying the response.</param>
        /// <param name="requests">Frames to send, in order, before awaiting.</param>
        /// <param name="match">Accepts (frame, length); return true to take the frame.</param>
        /// <param name="timeoutMs">Overall deadline covering all awaited reads.</param>
        /// <param name="response">Destination buffer for the matched frame.</param>
        /// <param name="staleGraceMs">
        /// When &gt; 0, before sending, keep reading (and discarding) stale frames
        /// until the stream stays quiet for this long — absorbing a late response
        /// still in flight from a previous timed-out elicit, which an instant
        /// flush would miss and which would then be matched as THIS request's
        /// reply. Use for request/response protocols where replies carry no
        /// correlation id (tuning); 0 for push streams where a stale frame is
        /// just old state (identity).
        /// </param>
        public static int Elicit(IDeviceTransport io, IReportStream stream, byte[][] requests,
            Func<byte[], int, bool> match, int timeoutMs, byte[] response, int staleGraceMs = 0)
        {
            if (io == null || stream == null || response == null) return -1;

            // The batch spans send→await so another writer can't interleave a
            // frame between the request and the device's reply (reads themselves
            // are lock-free; this serializes the WRITE sequence only).
            using (io.BeginBatch())
            {
                if (staleGraceMs > 0)
                    while (stream.TryRead(response, staleGraceMs) >= 0) { }
                stream.Flush();

                for (int i = 0; i < requests.Length; i++)
                    if (!io.SendCol03(requests[i]))
                        return -1;

                int start = Environment.TickCount;
                int remaining = timeoutMs;
                while (remaining > 0)
                {
                    int n = stream.TryRead(response, remaining);
                    if (n <= 0) return -1; // timed out or stream closed
                    if (match(response, n)) return n;

                    // Wrong frame (stale/other response on our own family) — keep
                    // waiting out the same deadline. TickCount subtraction is
                    // wrap-safe.
                    remaining = timeoutMs - (Environment.TickCount - start);
                }
            }
            return -1;
        }
    }
}
