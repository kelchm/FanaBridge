using System;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// The col03 <c>FF 08</c> system-report codec — the wire-level side of identity.
    /// Owns the enable/trigger byte patterns and the report decode, leaving
    /// <see cref="FanatecWheelbase"/> to own identity STATE. Frame routing lives
    /// in <see cref="Col03FrameClassifier"/>: the transport's reader thread feeds
    /// <see cref="IDeviceTransport.IdentityReports"/> with FF 08 frames only, so
    /// the helpers here just drain/elicit that one stream.
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

            /// <summary>
            /// The full report frame this reading was decoded from (a private copy,
            /// safe to retain). Kept for the diagnostics capture so the exact wire
            /// bytes of an unrecognized device can be reported. May be null.
            /// </summary>
            public byte[] Raw;
        }

        private const int ReportLength = 64;
        // Matches the old worst-case connect window (8 attempts × 60 ms while
        // interleaved frames kept arriving). The identity stream is classifier-
        // filtered, so one deadline covers it — and the elicit returns the
        // moment the reply lands, so a fast base still connects in ~one read.
        private const int InitialReadTimeoutMs = 480;
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
            Reading decoded = default;
            byte[] buf = Buffer(io);
            int n = ReportElicit.Elicit(
                io, io.IdentityReports,
                new[] { BuildEnable(), BuildTrigger() },
                (frame, len) => TryDecode(frame, len, out decoded),
                InitialReadTimeoutMs, buf);
            reading = decoded;
            return n > 0;
        }

        /// <summary>
        /// Non-blocking drain of pushed FF 08 system reports; each decoded reading
        /// goes to <paramref name="onReading"/>. Lock-free: the identity stream has
        /// this reader as its single owner. Returns the number of readings delivered.
        /// </summary>
        public int DrainIdentity(IDeviceTransport io, Action<Reading> onReading)
        {
            int count = 0;
            var stream = io.IdentityReports;
            byte[] buf = Buffer(io);
            for (int i = 0; i < DrainMaxReports; i++)
            {
                int n = stream.TryRead(buf, DrainTimeoutMs);
                if (n <= 0) break;

                if (TryDecode(buf, n, out var reading))
                {
                    onReading(reading);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Decodes a frame as an FF 08 system report (signature scan + length
        /// check via <see cref="Col03FrameClassifier"/>). False for anything else.
        /// </summary>
        public static bool TryDecode(byte[] buf, int len, out Reading reading)
        {
            int sig = Col03FrameClassifier.FindIdentitySignature(buf, len);
            if (sig < 0)
            {
                reading = default;
                return false;
            }
            reading = Decode(buf, sig, len);
            return true;
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

        // Decode the three identity bytes and retain a private copy of the full
        // frame (len bytes) for diagnostics. Decode runs only for a matched FF 08
        // report; the base pushes only on attachment change, so steady-state idle
        // drains match nothing and allocate nothing.
        private static Reading Decode(byte[] buf, int sig, int len)
        {
            var raw = new byte[len];
            Array.Copy(buf, raw, len);
            return new Reading
            {
                BaseType = buf[sig + FanatecIdentity.OffBaseType],
                Wire     = buf[sig + FanatecIdentity.OffWireCode],
                ModRaw   = buf[sig + FanatecIdentity.OffModule],
                Raw      = raw,
            };
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
