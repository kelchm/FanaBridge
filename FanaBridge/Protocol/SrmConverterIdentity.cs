using System;
using System.Collections.Generic;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// SRM Conversion Kit identity, recovered from the kit's private <c>DE FA AD</c> → <c>0xDD</c>
    /// channel on col03. A converter emulates a Fanatec base but does NOT reliably emit <c>FF 08</c>,
    /// and its wheel is hard-wired (there is no wheel-swap), so identity is a ONE-SHOT resolved at
    /// connect and held fixed until the unit is unplugged.
    ///
    /// The caller gates this behind <c>FF 08</c> SILENCE: a genuine base answers <c>FF 08</c> and
    /// never answers <c>0xDD</c>, so it never reaches this path — the converter support cannot affect
    /// normal hardware. Read/identify-only: <c>DE FA AD</c> is the SRM config app's "get wheel" query;
    /// it never writes tuning/flash (never <c>DE FA AC/CE</c>). Validated against real hardware
    /// (simanthrop, kit fw 6.12 / 7.06 → wheelId 0x0A). See fanabridge-converter-support.md §3/§5/§6.
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
        private const int MaxReads = 12;
        private const int ReadTimeoutMs = 40;

        /// <summary>
        /// Sends <c>DE FA AD</c> (report-id 0xFF, then 0x00) and, on a <c>0xDD</c> reply, decodes the
        /// converter identity. Returns false when no <c>0xDD</c> arrives (a genuine base, an 8x kit,
        /// or kit fw &lt; 4.1).
        /// </summary>
        public bool TryProbe(IDeviceTransport io, out Result result)
        {
            result = default;
            if (io == null || !io.IsConnected) return false;

            using (io.BeginBatch())
            {
                foreach (byte reportId in new byte[] { 0xFF, 0x00 })
                {
                    var outRep = new byte[Col03Len];
                    outRep[0] = reportId;                        // report-id
                    outRep[1] = 0x00;                            // data[0] = channel/sub byte
                    outRep[2] = 0xDE; outRep[3] = 0xFA; outRep[4] = 0xAD; // magic + cmd AD (get-wheel)
                    try { if (!io.SendCol03(outRep)) continue; }
                    catch { continue; }

                    int len = io.Col03MaxInputReportLength;
                    if (len < Col03Len) len = Col03Len;
                    var buf = new byte[len];
                    for (int i = 0; i < MaxReads; i++)
                    {
                        int n = io.ReadCol03(buf, ReadTimeoutMs);
                        if (n <= 0) break;

                        int sig = FindByte(buf, n, 0xDD, 3); // scan first 3 bytes for the 0xDD signature
                        if (sig < 0 || n < sig + 6) continue;

                        result = Decode(buf, sig, n);
                        return true;
                    }
                }
            }
            return false;
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

        /// <summary>SRM wheelId → FanaBridge code, or null (0 = no rim attached; unmapped = unknown id).</summary>
        internal static string DecodeSrmWheel(byte wheelId)
            => wheelId == 0 ? null
             : SrmWheelMap.TryGetValue(wheelId, out var code) ? code
             : null;

        // From fanabridge-converter-support.md §6 (SRM firmware #defines). Numerically identical to
        // the Fanatec wire byte for every wheel EXCEPT 0x17 (SRM = CSLESWWRC; Fanatec wire 0x17 =
        // PSWBMW). Rows 0x04/0x05/0x06 are flagged low-confidence pending hardware. Includes hubs
        // (PHUB 0x0C, CSLSWUH 0x11, CSUHV2 0x15, …), so a hub+module through a kit resolves too.
        internal static readonly IReadOnlyDictionary<byte, string> SrmWheelMap = new Dictionary<byte, string>
        {
            { 0x01, "CSSWBMW"     }, { 0x02, "CSSWFORM"    }, { 0x03, "CSSWPORSCHE" }, { 0x04, "CSSWUH"      },
            { 0x05, "CSSWUHX"     }, { 0x06, "CSSWUH"      }, { 0x07, "CSLESWP1X"   }, { 0x08, "CSLESWP1PS4" },
            { 0x09, "CSLESWMCL"   }, { 0x0A, "CSSWFORMV2"  }, { 0x0B, "CSLESWMCLV2" }, { 0x0C, "PHUB"        },
            { 0x0E, "PSWBENT"     }, { 0x0F, "PSWBMW"      }, { 0x10, "GTSWPRO"     }, { 0x11, "CSLSWUH"     },
            { 0x12, "CSLESWWRC"   }, { 0x13, "CSSWBMWV2"   }, { 0x14, "CSSWRS"      }, { 0x15, "CSUHV2"      },
            { 0x16, "CSSWF1ESV2"  }, { 0x17, "CSLESWWRC"   },
        };

        private static int FindByte(byte[] buf, int n, byte val, int within)
        {
            for (int i = 0; i < within && i < n; i++)
                if (buf[i] == val) return i;
            return -1;
        }
    }
}
