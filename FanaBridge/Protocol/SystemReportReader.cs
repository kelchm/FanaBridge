using System;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Reads the col03 <c>FF 08</c> system report — the wire-level side of identity.
    /// Owns the enable/trigger byte patterns, the report offsets, and the signature
    /// scan, leaving <see cref="FanatecWheelbase"/> to own identity STATE.
    ///
    /// After a single <see cref="Enable"/> the base PUSHES this report on every
    /// attachment change and is otherwise silent, so steady-state reads are a
    /// non-blocking drain — no triggering.
    /// </summary>
    internal sealed class SystemReportReader
    {
        /// <summary>The raw bytes an identity is built from (one system report).</summary>
        public struct Reading
        {
            public byte BaseType;
            public byte Wire;
            public byte ModRaw;
        }

        private const int ReportLength = 64;
        private const int InitialReadTimeoutMs = 60;
        private const int InitialMaxReadAttempts = 8; // axis reports interleave; skip past them
        private const int DrainTimeoutMs = 0;         // non-blocking
        private const int DrainMaxReports = 16;       // bound per drain

        private byte[] _readBuf;

        /// <summary>Enable the firmware's push-on-change for the system report.</summary>
        public void Enable(IDeviceTransport io)
        {
            io.SendCol03(BuildEnable());
        }

        /// <summary>
        /// Enable + trigger + read once, returning the current identity. The base
        /// only pushes on change, so this seeds the initial state on connect.
        /// </summary>
        public bool ReadInitial(IDeviceTransport io, out Reading reading)
        {
            reading = default;

            // Hold the transport for the enable→trigger→read sequence so an
            // interleaved LED write can't land between trigger and read.
            using (io.BeginBatch())
            {
                io.SendCol03(BuildEnable());
                io.SendCol03(BuildTrigger());

                byte[] buf = Buffer(io);
                for (int attempt = 0; attempt < InitialMaxReadAttempts; attempt++)
                {
                    int n = io.ReadCol03(buf, InitialReadTimeoutMs);
                    if (n <= 0) break;

                    int sig = FindSignature(buf, n);
                    if (sig < 0) continue; // axis/other report — read again

                    reading = Decode(buf, sig);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Non-blocking drain of pushed reports; every FF 08 report found is passed
        /// to <paramref name="onReading"/>. Returns the number delivered.
        /// </summary>
        public int DrainPushes(IDeviceTransport io, Action<Reading> onReading)
        {
            int count = 0;
            using (io.BeginBatch())
            {
                byte[] buf = Buffer(io);
                for (int i = 0; i < DrainMaxReports; i++)
                {
                    int n = io.ReadCol03(buf, DrainTimeoutMs);
                    if (n <= 0) break;

                    int sig = FindSignature(buf, n);
                    if (sig < 0) continue; // axis/other report — not FF 08

                    onReading(Decode(buf, sig));
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Times a few empty (idle) drains to confirm <c>ReadCol03(buf, 0)</c> is
        /// non-blocking — the per-frame drain depends on it; a blocking read would
        /// stall the frame thread. Returns the slowest sample in milliseconds.
        /// Call once after the initial read (when the input is quiet).
        /// </summary>
        public double ProbeDrainLatencyMs(IDeviceTransport io, int samples)
        {
            double worst = 0;
            var sw = new System.Diagnostics.Stopwatch();
            using (io.BeginBatch())
            {
                byte[] buf = Buffer(io);
                for (int i = 0; i < samples; i++)
                {
                    sw.Restart();
                    io.ReadCol03(buf, DrainTimeoutMs); // expected empty → must return immediately
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (ms > worst) worst = ms;
                }
            }
            return worst;
        }

        // Reusable read buffer, sized to the col03 input report length. Safe to
        // reuse because each match is decoded into a Reading before the next read.
        private byte[] Buffer(IDeviceTransport io)
        {
            int len = io.Col03MaxInputReportLength;
            if (len < ReportLength) len = ReportLength;
            if (_readBuf == null || _readBuf.Length < len)
                _readBuf = new byte[len];
            return _readBuf;
        }

        private static Reading Decode(byte[] buf, int sig) => new Reading
        {
            BaseType = buf[sig + FanatecIdentity.OffBaseType],
            Wire     = buf[sig + FanatecIdentity.OffWireCode],
            ModRaw   = buf[sig + FanatecIdentity.OffModule],
        };

        // Locate the "FF 08" signature; tolerates a leading report-ID byte by
        // scanning the first few positions.
        private static int FindSignature(byte[] buf, int len)
        {
            int limit = len - (FanatecIdentity.OffModule + 1);
            for (int i = 0; i <= limit && i <= 2; i++)
                if (buf[i] == 0xFF && buf[i + 1] == 0x08)
                    return i;
            return -1;
        }

        private static byte[] BuildEnable()
        {
            var b = new byte[ReportLength];
            b[0] = 0xFF; b[1] = 0x08; b[2] = 0x01; b[3] = 0xFF;
            return b;
        }

        private static byte[] BuildTrigger()
        {
            var b = new byte[ReportLength];
            b[0] = 0xFF; b[1] = 0x08; b[2] = 0x02;
            return b;
        }
    }
}
