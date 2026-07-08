using System;

namespace FanaBridge.Transport
{
    /// <summary>
    /// One family of inbound HID reports (e.g. col03 FF 08 identity), fed by the
    /// transport's reader thread and consumed by exactly ONE owning component.
    /// Single ownership is the concurrency model: because no two components pop
    /// the same stream, reads need no lock and nothing can be stolen. A component
    /// that needs to observe a family it does not own uses <see cref="Tap"/>.
    /// </summary>
    public interface IReportStream
    {
        /// <summary>
        /// Dequeues the next report into <paramref name="destination"/>, waiting up
        /// to <paramref name="timeoutMs"/> (0 = check and return). Returns the
        /// report length, or -1 on empty timeout / disconnected. Never throws.
        /// </summary>
        int TryRead(byte[] destination, int timeoutMs);

        /// <summary>Drops any pending reports (stale-response clearing before an elicit).</summary>
        void Flush();

        /// <summary>
        /// Registers a non-consuming observer: it receives a private copy of every
        /// report routed to this family until the returned token is disposed. The
        /// owning consumer still receives every report. Callbacks run on the
        /// transport reader thread — observers must be fast and must not call
        /// back into the transport.
        /// </summary>
        IDisposable Tap(Action<byte[]> observer);
    }
}
