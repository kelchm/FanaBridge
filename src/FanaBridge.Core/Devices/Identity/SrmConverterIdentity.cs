using System;
using FanaBridge.Transport;

namespace FanaBridge.Devices.Identity
{
    /// <summary>
    /// SRM Conversion Kit identity, recovered from the kit's private <c>DE FA AD</c> → <c>0xDD</c>
    /// channel on col03. A converter emulates a Fanatec base but does not reliably emit <c>FF 08</c>,
    /// and its wheel is hard-wired, so identity is a one-shot resolved at connect and held until the
    /// unit is unplugged. Read/identify-only. The caller reaches this only when <c>FF 08</c> is silent;
    /// a genuine base answers <c>FF 08</c> and never <c>0xDD</c>, so normal hardware is unaffected.
    /// </summary>
    internal sealed class SrmConverterIdentity
    {
        /// <summary>A decoded <c>0xDD</c> identity reply.</summary>
        internal struct Result
        {
            public byte WheelId;       // == the Fanatec wire byte (except 0x17)
            public string WheelCode;   // FanaBridge/SWTYPE code ("CSSWFORMV2", "PHUB", …), or null if unknown id
            public byte ModuleRaw;     // 0/1/2 — the SAME encoding as FF 08 byte 0x1F
            public string ModuleCode;  // "PBME"/"PBMR"/null
            public string KitFirmware; // e.g. "6.12"
            public byte[] Raw;         // the 0xDD reply frame, for diagnostics
        }

        private const int Col03Len = 64;

        /// <summary>
        /// Sends the <c>DE FA AD</c> query on col03 (report-id 0xFF, then 0x00). Send-only: the reply
        /// arrives on the transport's SRM stream (<see cref="IDeviceTransport.SrmReports"/>), so it can
        /// never consume a genuine FF 08 frame. Harmless to a genuine base (which does not answer).
        /// </summary>
        public void SendQuery(IDeviceTransport io)
        {
            if (io == null) return;
            foreach (byte reportId in new byte[] { 0xFF, 0x00 })
            {
                var outRep = new byte[Col03Len];
                outRep[0] = reportId;                        // report-id
                outRep[1] = 0x00;                            // data[0] = channel/sub byte
                outRep[2] = 0xDE; outRep[3] = 0xFA; outRep[4] = 0xAD; // magic + cmd AD (get-wheel)
                try { io.SendCol03(outRep); } catch { /* transient */ }
            }
        }

        /// <summary>
        /// Decode a col03 frame if it is a <c>0xDD</c> reply. The signature rule lives in
        /// <see cref="Col03FrameClassifier.IsSrm"/> (offset 0 or 1 only, so an FF 08 / FF 05
        /// frame can never be mistaken for a converter reply).
        /// </summary>
        public static bool TryDecodeFrame(byte[] buf, int n, out Result result)
        {
            result = default;
            if (buf == null) return false;
            if (!Col03FrameClassifier.IsSrm(buf, n, out int sig)) return false;
            result = Decode(buf, sig, n);
            return true;
        }

        // 0xDD reply: DD [kitMaj] [kitMin BCD] [wheelId] [wheelFw] [module 1=PBME,2=PBMR].
        internal static Result Decode(byte[] buf, int sig, int n)
        {
            byte wheelId = buf[sig + 3], module = buf[sig + 5];
            var raw = new byte[n];
            Array.Copy(buf, raw, n);
            return new Result
            {
                WheelId = wheelId,
                WheelCode = DecodeSrmWheel(wheelId),
                ModuleRaw = module,
                ModuleCode = FanatecIdentity.DecodeModule(module), // 1=PBME, 2=PBMR (1:1 with FF 08 0x1F)
                KitFirmware = buf[sig + 1] + "." + buf[sig + 2].ToString("X2"),
                Raw = raw,
            };
        }

        /// <summary>
        /// SRM wheelId → FanaBridge code, or null (0 = no rim; unmapped = unknown id). The SRM id is
        /// the Fanatec wire byte, so this delegates to the shared wire table (wheels and hubs) — the
        /// one exception is 0x17 (SRM = CSL WRC V2, where the Fanatec wire 0x17 = PSWBMW).
        /// TODO: validate the SRM 0x17 = CSL WRC V2 claim; it is documented but untested.
        /// </summary>
        internal static string DecodeSrmWheel(byte wheelId)
            => wheelId == 0x00 ? null
             : wheelId == 0x17 ? "CSLESWWRC"
             : FanatecIdentity.DecodeCode(wheelId);
    }
}
