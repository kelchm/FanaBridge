using System;

namespace FanaBridge.Core
{
    /// <summary>
    /// Low-level HID transport abstraction for all protocol encoders
    /// (LEDs, display, tuning, etc.). Implemented by <see cref="FanatecDevice"/>.
    ///
    /// Individual sends are thread-safe — callers do not need to hold any lock
    /// for single-report operations. For multi-report atomic sequences, use
    /// <see cref="BeginBatch"/> to acquire exclusive access.
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

        /// <summary>
        /// Reads a report from the LED/config interface (col03).
        /// Returns the number of bytes read, or -1 on failure/timeout.
        /// The <paramref name="timeoutMs"/> applies to this call only and
        /// does not affect other callers.
        /// </summary>
        int ReadCol03(byte[] buffer, int timeoutMs);

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
        /// Acquires exclusive access to the transport for multi-report
        /// atomic sequences (e.g. staged LED commit, tuning read-modify-write).
        /// Dispose the returned token to release.
        /// Re-entrant: sends made inside a batch skip re-acquiring the lock.
        /// </summary>
        IDisposable BeginBatch();
    }
}
