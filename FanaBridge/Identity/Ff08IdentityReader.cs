using FanatecManaged;
using FanaBridge.Transport;

namespace FanaBridge.Identity
{
    /// <summary>
    /// Reads the col03 <c>FF 08</c> system report over the existing HID transport
    /// and decodes full wheel identity — base, rim/hub, and module — with no
    /// Fanatec driver, service, or SimHub.FanatecManaged.dll.
    ///
    /// Sequence (hardware-verified):
    ///   1. enable  : 64-byte col03 OUT report <c>FF 08 01 FF</c>
    ///   2. trigger : 64-byte col03 OUT report <c>FF 08 02</c>
    ///   3. read    : scan col03 IN reports for the <c>FF 08</c> signature
    /// </summary>
    public static class Ff08IdentityReader
    {
        private const int ReportLength = 64;
        private const int ReadTimeoutMs = 60;
        private const int MaxReadAttempts = 8; // axis reports interleave; skip past them

        public struct Identity
        {
            public bool Detected;
            public M_FS_WHEEL_SWTYPE SteeringWheelType;
            public string RimName;
            public bool IsHub;
            public M_FS_WHEEL_SW_MODULETYPE ModuleType;
            public string ModuleName;
            public byte BaseType;
            public string BaseName;
            public byte RimRaw;
        }

        /// <summary>
        /// Triggers and reads the FF 08 report, returning decoded identity.
        /// Returns false if no FF 08 report could be read (transport down,
        /// base doesn't emit the system report, etc.).
        /// </summary>
        public static bool TryRead(IDeviceTransport transport, int productId, out Identity identity)
        {
            identity = default;
            if (transport == null || !transport.IsConnected)
                return false;

            var enable = new byte[ReportLength];
            enable[0] = 0xFF; enable[1] = 0x08; enable[2] = 0x01; enable[3] = 0xFF;

            var trigger = new byte[ReportLength];
            trigger[0] = 0xFF; trigger[1] = 0x08; trigger[2] = 0x02;

            // Hold the transport for the enable→trigger→read sequence so an
            // interleaved LED write can't land between trigger and read.
            using (transport.BeginBatch())
            {
                transport.SendCol03(enable);
                transport.SendCol03(trigger);

                int bufLen = transport.Col03MaxInputReportLength;
                if (bufLen < ReportLength) bufLen = ReportLength;

                for (int attempt = 0; attempt < MaxReadAttempts; attempt++)
                {
                    var buf = new byte[bufLen];
                    int n = transport.ReadCol03(buf, ReadTimeoutMs);
                    if (n <= 0) break;

                    int sig = FindSignature(buf, n);
                    if (sig < 0) continue; // not the FF08 report (axis/other) — read again

                    identity = Decode(buf, sig, productId);
                    return true;
                }
            }

            return false;
        }

        // Locate the "FF 08" system-report signature; tolerates a leading
        // report-ID byte by scanning the first few positions.
        private static int FindSignature(byte[] buf, int len)
        {
            int limit = len - (FanatecIdentity.OffModule + 1);
            for (int i = 0; i <= limit && i <= 2; i++)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0x08)
                    return i;
            }
            return -1;
        }

        private static Identity Decode(byte[] buf, int sig, int productId)
        {
            byte baseType = buf[sig + FanatecIdentity.OffBaseType];
            byte rimRaw   = buf[sig + FanatecIdentity.OffRimType];
            byte modRaw   = buf[sig + FanatecIdentity.OffModule];

            var rim = FanatecIdentity.DecodeRim(rimRaw);
            var mod = FanatecIdentity.DecodeModule(modRaw);

            // A module is only meaningful on a hub; ignore stray bytes on a wheel.
            var moduleType = rim.IsHub ? mod.Type : M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;
            string moduleName = rim.IsHub ? mod.Name : null;

            return new Identity
            {
                Detected = rim.Type != M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED,
                SteeringWheelType = rim.Type,
                RimName = rim.Name,
                IsHub = rim.IsHub,
                ModuleType = moduleType,
                ModuleName = moduleName,
                BaseType = baseType,
                BaseName = FanatecIdentity.DecodeBaseName(productId, baseType),
                RimRaw = rimRaw,
            };
        }
    }
}
