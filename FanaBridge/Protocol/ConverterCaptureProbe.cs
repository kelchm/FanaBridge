using System;
using System.Collections.Generic;
using System.Diagnostics;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// One-shot, read-only identity capture run at diagnostics time so a SINGLE detection report from
    /// an SRM Conversion Kit user contains everything we need to build/validate driverless identity —
    /// no external USB capture, no Fanatec software.
    ///
    /// It replicates the ENGAGE handshake for the wired SRM classes (PID 0x0005 / 0x0020): the col03
    /// <c>FF 08</c> enable+trigger, then the col01 SubId triggers. Each SubId is a timed ON/OFF pulse
    /// with a ~100 ms gap, read between pulses, so a converter's firmware has time to volunteer its
    /// module/rim records.
    ///
    /// It records, from a single run:
    ///   1. the engage result (which sends the transport accepted),
    ///   2. every DISTINCT col01 input record (the rim byte, and each EXT_INFO sub-record),
    ///   3. the col03 <c>FF 08</c> system report, if the device answers one,
    ///   4. the SRM <c>DE FA AD</c> → <c>0xDD</c> identity reply, if it is a Conversion Kit.
    ///
    /// Nothing here writes tuning/config: the engage is the identity handshake, and <c>DE FA AD</c> is
    /// the SRM config app's "get wheel" query. Genuine (non-SRM) devices answer no <c>0xDD</c>.
    /// </summary>
    public sealed class ConverterCaptureProbe
    {
        private const int MinCol01Len = 33;         // identity reports are report-id 1, len in {33,34}
        private const int PulseWindowMs = 110;      // per-SubId col01 read window (~ native Sleep(100) pulse gap)
        private const int FinalDrainMs = 160;       // trailing drain for anything still streaming
        private const int Col01ReadTimeoutMs = 20;
        private const int Col03MaxReads = 12;
        private const int Col03ReadTimeoutMs = 40;

        /// <summary>The captured surfaces. Any field may be null when that surface stayed silent.</summary>
        public sealed class Result
        {
            public string Engage;
            public readonly List<string> Col01Records = new List<string>();
            public int Col01FramesRead;
            public string Ff08Raw;
            public string Ff08Line;
            public string DeFaRaw;
            public string DeFaLine;
        }

        public Result Run(IDeviceTransport io)
        {
            var r = new Result();
            if (io == null || !io.IsConnected)
            {
                r.Engage = "(not connected)";
                return r;
            }

            var seen = new HashSet<string>();
            var engage = new List<string>();
            var sw = new Stopwatch();

            // Hold the transport for the whole capture so the runtime's frames don't interleave ours.
            using (io.BeginBatch())
            {
                // col03 FF 08 enable + trigger.
                engage.Add("FF08 enable:" + Ok(() => io.SendCol03(Ff08(0x01, 0xFF))));
                engage.Add("FF08 trigger:" + Ok(() => io.SendCol03(Ff08(0x02, 0x00))));

                // col01 SubId pulses. Each ON is followed by a read window (the native ON/Sleep(100)/OFF
                // pulse) so the device has time to respond and we capture the response in-line.
                Pulse(io, 0x01, "SubId=1 module", engage, seen, r, sw);
                Pulse(io, 0x00, "SubId=0 input", engage, seen, r, sw);
                Pulse(io, 0x04, "SubId=4", engage, seen, r, sw);
                Pulse(io, 0x00, "SubId=0 deassert", engage, seen, r, sw);

                // Trailing drain for anything still streaming after the pulses.
                DrainCol01(io, FinalDrainMs, seen, r, sw);
                r.Engage = "engage[" + string.Join("  ", engage) + "]";

                CaptureFf08(io, r);
                CaptureDeFa(io, r);
            }
            return r;
        }

        private static void Pulse(IDeviceTransport io, byte subId, string label,
            List<string> engage, HashSet<string> seen, Result r, Stopwatch sw)
        {
            engage.Add(label + ":" + Ok(() => io.SendCol01(WheelEngage.SubId(subId))));
            DrainCol01(io, PulseWindowMs, seen, r, sw);
        }

        // Read col01 for ~windowMs, recording each DISTINCT tail record (deduped by decoded meaning,
        // not raw bytes — the axis fields change every frame). A representative frame's hex is kept
        // with each record so the exact wire bytes are reportable.
        private static void DrainCol01(IDeviceTransport io, int windowMs, HashSet<string> seen, Result r, Stopwatch sw)
        {
            int len = io.Col01MaxInputReportLength;
            if (len < MinCol01Len) len = 34;
            var buf = new byte[len];

            sw.Restart();
            while (sw.ElapsedMilliseconds < windowMs)
            {
                int n = io.ReadCol01(buf, Col01ReadTimeoutMs);
                if (n <= 0) continue;
                r.Col01FramesRead++;
                if (n < MinCol01Len || buf[0] != 0x01) continue; // report-id 1 = the identity report

                string decode = DescribeCol01Tail(buf, n);
                if (decode != null && seen.Add(decode))
                    r.Col01Records.Add(Hex(buf, n) + "  -> " + decode);
            }
        }

        // Classify a col01 report by its identity tail. [len-4] is the rim/EXT_INFO byte; under
        // EXT_INFO the 4-byte tail is [len-4]=FF | [len-3]=type | [len-2]=b1 | [len-1]=b2.
        internal static string DescribeCol01Tail(byte[] buf, int n)
        {
            if (buf == null || n < MinCol01Len) return null;
            byte wire = buf[n - 4];

            if (wire == 0xFF)
            {
                byte type = buf[n - 3], b1 = buf[n - 2], b2 = buf[n - 1];
                // Show the RAW record only — do not interpret the type-1 module byte. col01 is coarse
                // here (a PBMR and a PBME seemingly both emit b1=0x15), so a "PBME"/"PBMR" label would be
                // misleading; the module must come from FF 08 / DE FA instead.
                string note =
                    type == 0x01 ? "  (button-module record — raw; col01 cannot distinguish PBME/PBMR)" :
                    type == 0x02 ? "  (accessories)" : "";
                return string.Format("EXT_INFO type={0} b1=0x{1:X2} b2=0x{2:X2}{3}", type, b1, b2, note);
            }
            if (wire == 0x00) return "rim: (nothing attached)";
            return string.Format("rim 0x{0:X2} {1}", wire, FanatecIdentity.DecodeCode(wire) ?? "unrecognized");
        }

        // Read col03 for an FF 08 system report (the engage already enabled+triggered it).
        private static void CaptureFf08(IDeviceTransport io, Result r)
        {
            int len = io.Col03MaxInputReportLength;
            if (len < 64) len = 64;
            var buf = new byte[len];

            for (int i = 0; i < Col03MaxReads; i++)
            {
                int n = io.ReadCol03(buf, Col03ReadTimeoutMs);
                if (n <= 0) break;

                int sig = FindPair(buf, n, 0xFF, 0x08);
                if (sig < 0 || n < sig + 0x20) continue;

                r.Ff08Raw = Hex(buf, n);
                byte baseType = buf[sig + 0x02], wire = buf[sig + 0x18], mod = buf[sig + 0x1F];
                r.Ff08Line = string.Format(
                    "base [0x02]=0x{0:X2} {1}   rim [0x18]=0x{2:X2} {3}   module [0x1F]=0x{4:X2} {5}",
                    baseType, FanatecIdentity.DecodeBaseCode(baseType) ?? "unrecognized",
                    wire, FanatecIdentity.DecodeCode(wire) ?? "unrecognized",
                    mod, FanatecIdentity.DecodeModule(mod) ?? "none");
                return;
            }
        }

        // Query the SRM Conversion Kit's DE FA AD -> 0xDD channel on col03. Tries both report-ids
        // (0xFF and 0x00); a genuine base answers neither. OUT data = 00 DE FA AD;
        // IN = DD [kitMaj] [kitMin] [wheelId] [wheelFw] [module].
        private static void CaptureDeFa(IDeviceTransport io, Result r)
        {
            foreach (byte reportId in new byte[] { 0xFF, 0x00 })
            {
                var outRep = new byte[64];
                outRep[0] = reportId;                       // report-id
                outRep[1] = 0x00;                           // data[0] = channel/sub byte
                outRep[2] = 0xDE; outRep[3] = 0xFA; outRep[4] = 0xAD; // magic + cmd AD (get-wheel)
                try { if (!io.SendCol03(outRep)) continue; }
                catch { continue; }

                int len = io.Col03MaxInputReportLength;
                if (len < 64) len = 64;
                var buf = new byte[len];

                for (int i = 0; i < Col03MaxReads; i++)
                {
                    int n = io.ReadCol03(buf, Col03ReadTimeoutMs);
                    if (n <= 0) break;

                    int sig = FindByte(buf, n, 0xDD, 3); // scan first 3 bytes for the 0xDD signature
                    if (sig < 0 || n < sig + 6) continue;

                    r.DeFaRaw = Hex(buf, n);
                    r.DeFaLine = DescribeDeFa(buf, sig, reportId);
                    return;
                }
            }
            r.DeFaLine = "(no 0xDD reply — not an SRM Conversion Kit, or a gen that doesn't answer DE FA AD)";
        }

        // Decode a 0xDD identity reply: DD [kitMaj] [kitMin(BCD)] [wheelId] [wheelFw] [module 1=PBME,2=PBMR].
        internal static string DescribeDeFa(byte[] buf, int sig, byte reportId)
        {
            byte kitMaj = buf[sig + 1], kitMin = buf[sig + 2], wheelId = buf[sig + 3], wheelFw = buf[sig + 4], module = buf[sig + 5];
            string mod = module == 1 ? "PBME" : module == 2 ? "PBMR" : module == 0 ? "none" : "0x" + module.ToString("X2");
            return string.Format(
                "SRM Conversion Kit (report-id 0x{0:X2}): kit fw {1}.{2:X2}   wheelId=0x{3:X2}   wheel fw={4}   module={5}{6}",
                reportId, kitMaj, kitMin, wheelId, wheelFw, mod,
                wheelId == 0 ? "   (no rim attached)" : "");
        }

        private static string Ok(Func<bool> send)
        {
            try { return send() ? "ok" : "FAIL"; }
            catch { return "ERR"; }
        }

        // FF 08 <b2> <b3> control report (64-byte): FF 08 01 FF = enable, FF 08 02 00 = trigger.
        private static byte[] Ff08(byte b2, byte b3)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08; b[2] = b2; b[3] = b3;
            return b;
        }

        // FF 08-style signature scan (tolerates a leading report-id byte).
        private static int FindPair(byte[] buf, int n, byte a, byte b)
        {
            for (int i = 0; i <= 2 && i + 1 < n; i++)
                if (buf[i] == a && buf[i + 1] == b) return i;
            return -1;
        }

        private static int FindByte(byte[] buf, int n, byte val, int within)
        {
            for (int i = 0; i < within && i < n; i++)
                if (buf[i] == val) return i;
            return -1;
        }

        private static string Hex(byte[] buf, int n)
            => BitConverter.ToString(buf, 0, n).Replace('-', ' ');
    }
}
