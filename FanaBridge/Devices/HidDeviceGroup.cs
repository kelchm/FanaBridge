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

        /// <summary>
        /// A stable key for the PHYSICAL device this group represents: VID:PID plus the
        /// USB device-instance segment of the HID path, so two same-PID devices on
        /// different ports get distinct keys. Falls back to VID:PID alone when the
        /// instance can't be parsed (then two same-PID devices collapse — the accepted
        /// "rim on any base" limitation). NOTE: cross-port distinctness for two
        /// identical-PID bases is UNVERIFIED on hardware — the key is logged on connect
        /// for validation and does NOT yet drive interface grouping.
        /// </summary>
        public string DeviceKey { get; }

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
            DeviceKey = DeriveDeviceKey(vid, pid, Interfaces.Select(SafePath));
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
            DeviceKey = DeriveDeviceKey(vid, pid, null);
        }

        /// <summary>
        /// Pure derivation of <see cref="DeviceKey"/> from a device's interface paths.
        /// Picks the first parseable instance segment; VID:PID-only fallback otherwise.
        /// </summary>
        internal static string DeriveDeviceKey(int vid, int pid, IEnumerable<string> interfacePaths)
        {
            string instance = interfacePaths?
                .Select(InstanceSegment)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));

            return instance != null
                ? string.Format("{0:X4}:{1:X4}:{2}", vid, pid, instance)
                : string.Format("{0:X4}:{1:X4}", vid, pid);
        }

        /// <summary>
        /// The middle, device-instance segment of a Windows HID path
        /// (<c>\\?\HID#&lt;hwid+ColXX&gt;#&lt;instance&gt;#{guid}</c>) — the part that
        /// locates the physical device instance, shared by that device's collection
        /// interfaces. Returns null when the path isn't in the expected 4-segment form.
        /// </summary>
        internal static string InstanceSegment(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;
            var parts = devicePath.Split('#');
            // [0]=\\?\HID  [1]=hwid(&ColXX)  [2]=instance  [3]={guid}
            return parts.Length >= 4 ? parts[2].ToLowerInvariant() : null;
        }

        private static string SafePath(HidDevice d)
        {
            try { return d?.DevicePath; }
            catch { return null; }
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
