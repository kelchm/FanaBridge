using System;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Low-level HID transport abstraction for all protocol encoders
    /// (LEDs, display, tuning, etc.). Implemented by <see cref="FanatecTransport"/>.
    ///
    /// Individual sends are thread-safe — callers do not need to hold any lock
    /// for single-report operations. For multi-report atomic WRITE sequences,
    /// use <see cref="BeginBatch"/> to acquire exclusive access.
    ///
    /// Inbound col03 traffic is demultiplexed by the transport's reader thread
    /// into per-family <see cref="IReportStream"/>s. Reads are lock-free: each
    /// stream has exactly one owning consumer (Identity/Itm/Srm → the wheelbase's
    /// frame-thread drains; Tuning → the tuning controller), so no reader can
    /// steal another family's frames and no read ever holds the write lock.
    /// </summary>
    public interface IDeviceTransport
    {
        /// <summary>Whether the HID streams appear to be open.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Sends a 64-byte report on the LED/config interface (col03).
        /// Thread-safe: acquires the write lock internally.
        /// </summary>
        bool SendCol03(byte[] data);

        /// <summary>col03 FF 08 system reports (identity pushes). Owner: the wheelbase.</summary>
        IReportStream IdentityReports { get; }

        /// <summary>col03 FF 05 ITM subscription/page pushes. Owner: the wheelbase (buffered for the ITM driver).</summary>
        IReportStream ItmReports { get; }

        /// <summary>col03 0xDD SRM DE FA identity replies. Owner: the wheelbase.</summary>
        IReportStream SrmReports { get; }

        /// <summary>col03 FF 03 tuning responses. Owner: the tuning controller.</summary>
        IReportStream TuningReports { get; }

        /// <summary>
        /// Gets the maximum input report length for the col03 interface.
        /// </summary>
        int Col03MaxInputReportLength { get; }

        /// <summary>
        /// Sends an 8-byte report on the display interface (col01).
        /// Thread-safe: acquires the write lock internally.
        /// </summary>
        bool SendCol01(byte[] data);

        /// <summary>
        /// Reads a report from the display/input interface (col01). Returns the number
        /// of bytes read, or -1 on failure/timeout. The <paramref name="timeoutMs"/>
        /// applies to this call only. Used to read the col01 input report that carries
        /// the native rim identity (byte <c>[len-4]</c>) when the col03 FF 08 report is
        /// unavailable.
        /// </summary>
        int ReadCol01(byte[] buffer, int timeoutMs);

        /// <summary>Maximum input report length for the col01 interface.</summary>
        int Col01MaxInputReportLength { get; }

        /// <summary>
        /// Acquires exclusive WRITE access to the transport for multi-report
        /// atomic sequences (e.g. staged LED commit, tuning read-modify-write).
        /// Dispose the returned token to release. Reads never take this lock —
        /// the per-family streams are single-owner by design.
        /// Re-entrant: sends made inside a batch on the same thread re-acquire
        /// the lock recursively, so they never block or deadlock.
        /// </summary>
        IDisposable BeginBatch();
    }
}
