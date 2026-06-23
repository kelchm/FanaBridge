using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace FanaBridge.Devices
{
    /// <summary>
    /// A physical device: the set of HID interfaces (col01..col04) that share a
    /// VID:PID (and, later, container/serial). Probes pre-filter on this before any
    /// I/O. <see cref="FirmwareBcd"/> carries <c>bcdDevice</c> for firmware-gated
    /// quirks (e.g. the SRM converter generations) — unused today.
    /// </summary>
    public sealed class HidDeviceGroup
    {
        public int Vid { get; }
        public int Pid { get; }

        /// <summary>USB <c>bcdDevice</c> (firmware/release number), or 0 if unknown.</summary>
        public ushort FirmwareBcd { get; }

        /// <summary>Product name from the HID descriptor, or null.</summary>
        public string ProductName { get; }

        /// <summary>The HID interfaces that make up this device.</summary>
        public IReadOnlyList<HidDevice> Interfaces { get; }

        /// <summary>
        /// Whether this device exposes a col03 (64-byte) control collection — by its
        /// <c>&amp;col03</c> path token, else a 64/65-byte OUTPUT report. This is the
        /// cheap pre-filter a base probe matches on.
        /// </summary>
        public bool HasCol03 { get; }

        public HidDeviceGroup(
            int vid, int pid, IReadOnlyList<HidDevice> interfaces,
            string productName = null, ushort firmwareBcd = 0)
        {
            Vid = vid;
            Pid = pid;
            Interfaces = interfaces ?? Array.Empty<HidDevice>();
            ProductName = productName;
            FirmwareBcd = firmwareBcd;
            HasCol03 = Interfaces.Any(IsCol03Interface);
        }

        /// <summary>
        /// Builds a group from an already-known col03 status, skipping the interface
        /// scan. Used where the caller has already determined col03 (and by unit tests
        /// that exercise probe routing without real HID interfaces).
        /// </summary>
        internal HidDeviceGroup(int vid, int pid, bool hasCol03, ushort firmwareBcd = 0, string productName = null)
        {
            Vid = vid;
            Pid = pid;
            Interfaces = Array.Empty<HidDevice>();
            ProductName = productName;
            FirmwareBcd = firmwareBcd;
            HasCol03 = hasCol03;
        }

        private static bool IsCol03Interface(HidDevice d)
        {
            try
            {
                if (d.DevicePath != null && d.DevicePath.Contains("col03"))
                    return true;
                int len = d.GetMaxOutputReportLength();
                return len == 64 || len == 65;
            }
            catch
            {
                return false;
            }
        }
    }
}
