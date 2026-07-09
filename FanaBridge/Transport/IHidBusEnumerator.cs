using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace FanaBridge.Transport
{
    /// <summary>
    /// A lightweight snapshot of one HID device on the bus, with the descriptor
    /// values device selection needs. Descriptor queries can throw on busy
    /// handles; a failed query is represented as -1 / null rather than an
    /// exception, so selection logic stays branch-testable.
    /// </summary>
    public sealed class HidDeviceInfo
    {
        public HidDeviceInfo(int productId, int maxOutputReportLength, int maxInputReportLength, string productName)
        {
            ProductId = productId;
            MaxOutputReportLength = maxOutputReportLength;
            MaxInputReportLength = maxInputReportLength;
            ProductName = productName;
        }

        public int ProductId { get; }

        /// <summary>Max output report length, or -1 when the descriptor query failed.</summary>
        public int MaxOutputReportLength { get; }

        /// <summary>Max input report length, or -1 when the descriptor query failed.</summary>
        public int MaxInputReportLength { get; }

        /// <summary>Product name from the HID descriptor, or null when unavailable.</summary>
        public string ProductName { get; }
    }

    /// <summary>
    /// Seam over HID bus enumeration (<c>DeviceList.Local</c> is a process-global
    /// static that cannot be faked). <see cref="FanatecWheelbase"/> consumes this
    /// for discovery so base-PID selection — which has historically bitten on
    /// interface-shape edge cases — is table-testable.
    /// </summary>
    public interface IHidBusEnumerator
    {
        /// <summary>Snapshots the HID devices for one vendor id.</summary>
        IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId);
    }

    /// <summary>Production enumerator over HidSharp's <c>DeviceList.Local</c>.</summary>
    public sealed class HidSharpBusEnumerator : IHidBusEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId)
        {
            return DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == vendorId)
                .Select(Describe)
                .ToList();
        }

        private static HidDeviceInfo Describe(HidDevice d)
        {
            // Each descriptor query is guarded individually — a busy handle can
            // fail one query while the others still answer.
            int maxOut, maxIn;
            string name;
            try { maxOut = d.GetMaxOutputReportLength(); } catch { maxOut = -1; }
            try { maxIn = d.GetMaxInputReportLength(); } catch { maxIn = -1; }
            try { name = d.GetProductName(); } catch { name = null; }
            return new HidDeviceInfo(d.ProductID, maxOut, maxIn, name);
        }
    }
}
