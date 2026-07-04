using System;
using System.Collections.Generic;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// One-shot, read-only identity capture run at diagnostics time so a SINGLE detection report from
    /// an SRM Conversion Kit user contains everything we need to build/validate driverless identity —
    /// no external USB capture, no Fanatec software.
    ///
    /// It replicates the kernel filter's ENGAGE handshake, then records what the device emits on BOTH
    /// surfaces plus the SRM config channel:
    ///   1. the engage result (which of the 5 sends the transport accepted),
    ///   2. every distinct col01 input record (the rim byte, and each EXT_INFO sub-record),
    ///   3. the col03 <c>FF 08</c> system report, if the device answers one,
    ///   4. the SRM <c>DE FA AD</c> → <c>0xDD</c> identity reply, if it is a Conversion Kit.
    ///
    /// Nothing here writes tuning/config: the engage is the identity handshake, and <c>DE FA AD</c> is
    /// the SRM config app's "get wheel" query. See the Fanatec-RE converter-support + command-protocol
    /// docs. Genuine (non-SRM) devices simply return no <c>0xDD</c> reply — the query is harmless.
    /// </summary>
    public sealed class ConverterCaptureProbe
    {
        private const int MinCol01Len = 33;         // identity reports are report-id 1, len in {33,34}
        private const int Col01MaxFrames = 40;      // enough to surface every EXT_INFO record variant
        private const int Col01ReadTimeoutMs = 25;
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

        private readonly WheelEngage _engage = new WheelEngage();

        public Result Run(IDeviceTransport io)
        {
            var r = new Result();
            if (io == null || !io.IsConnected)
            {
                r.Engage = "(not connected)";
                return r;
            }

            // Hold the transport for the whole capture so the runtime's frames don't interleave ours.
            using (io.BeginBatch())
            {
                try { r.Engage = FormatEngage(_engage.Engage(io)); }
                catch (Exception ex) { r.Engage = "engage FAILED: " + ex.Message; }

                CaptureCol01(io, r);
                CaptureFf08(io, r);
                CaptureDeFa(io, r);
            }
            return r;
        }

        // Read a burst of col01 frames and record each DISTINCT tail record (deduped by decoded
        // meaning, not raw bytes — the axis fields change every frame). A representative frame's hex
        // is kept with each record so the exact wire bytes are reportable.
        private static void CaptureCol01(IDeviceTransport io, Result r)
        {
            int len = io.Col01MaxInputReportLength;
            if (len < MinCol01Len) len = 34;
            var buf = new byte[len];
            var seen = new HashSet<string>();

            for (int i = 0; i < Col01MaxFrames; i++)
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
                string note =
                    type == 0x01 ? "  (button module: " + Col01ModuleName(b1) + ")" :
                    type == 0x02 ? "  (accessories)" : "";
                return string.Format("EXT_INFO type={0} b1=0x{1:X2} b2=0x{2:X2}{3}", type, b1, b2, note);
            }
            if (wire == 0x00) return "rim: (nothing attached)";
            return string.Format("rim 0x{0:X2} {1}", wire, FanatecIdentity.DecodeCode(wire) ?? "unrecognized");
        }

        // col01 type-1 DeviceModule byte = FF 08 0x1F raw + 0x14 (FWFUUtilDeviceModulePresenceGet):
        // 0x15 -> PBME, 0x16 -> PBMR. NOTE: observed COARSE on genuine hardware — both PBMR and PBME
        // emit 0x15; only FF 08 0x1F carries the fine split. Report the raw b1 regardless.
        internal static string Col01ModuleName(byte b1)
        {
            if (b1 == 0x00) return "none";
            if (b1 < 0x15) return "?";
            return FanatecIdentity.DecodeModule((byte)(b1 - 0x14)) ?? "unrecognized";
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
        // (0xFF emulation gen, 0x00 native/interim); a genuine base answers neither. See converter-
        // support §3: OUT data = 00 DE FA AD; IN = DD [kitMaj] [kitMin] [wheelId] [wheelFw] [module].
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

        private static string FormatEngage(WheelEngage.Step[] steps)
        {
            var parts = new List<string>(steps.Length);
            foreach (var s in steps) parts.Add(s.Label + ":" + (s.Sent ? "ok" : "FAIL"));
            return "engage[" + string.Join("  ", parts) + "]";
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
