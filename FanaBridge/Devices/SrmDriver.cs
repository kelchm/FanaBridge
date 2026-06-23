using System;
using FanaBridge.Transport;

namespace FanaBridge.Devices
{
    /// <summary>
    /// SKELETON driver for an SRM (Sim Racing Machines) conversion kit — a converter
    /// that presents a non-Fanatec wheel/base to the PC. It stands as the worked
    /// example that a new device class drops into the probe/driver model additively;
    /// the real wire I/O is not implemented here (it needs the kit + the RE'd
    /// protocol, and lands in the SRM phase).
    ///
    /// What the real driver does (documented so the shape is concrete):
    /// <list type="bullet">
    /// <item>Identity is a request-response <c>DE FA AD → 0xDD</c> exchange (NOT the
    /// FF 08 push a Fanatec base uses) — so <see cref="Service"/> here is a poll, a
    /// different body from <c>FanatecBaseDriver</c>'s drain.</item>
    /// <item>Three USB generations, all isolated behind one probe + driver: native
    /// (Leo-Bodnar lineage VID 0x1DD2), interim (Fanatec VID, SRM PID 0x0E03), and the
    /// emulation gen (impersonates a CSL Elite as <c>0EB7:0005</c>). A <c>bcdDevice</c>
    /// firmware gate selects the exchange; the native gen has a passive byte-62
    /// fallback when it does not answer the request.</item>
    /// <item>The output recovers the REAL rim identity, so it fills the same
    /// <see cref="DeviceSnapshot"/> shape (a wheel attachment with a known wire code)
    /// and existing wheel profiles match with no SRM-specific profiles.</item>
    /// </list>
    /// Because it produces a <see cref="DeviceSnapshot"/> like any other driver, the
    /// <see cref="DeviceManager"/>, peripheral view, and SimHub adapters need no change
    /// to support it — which is the whole point of the model.
    /// </summary>
    internal sealed class SrmDriver : IDeviceDriver
    {
        private readonly IDeviceTransport _io;
        private readonly ushort _firmwareBcd;
        private DeviceSnapshot _snapshot = new DeviceSnapshot { Stable = true };

        public SrmDriver(IDeviceTransport io, ushort firmwareBcd = 0)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _firmwareBcd = firmwareBcd;
        }

        /// <summary>A wheel-direct converter, not a wheelbase.</summary>
        public DeviceClass Class => DeviceClass.WheelDirect;

        public bool IsConnected => _io.IsConnected;

        public DeviceSnapshot Snapshot => _snapshot;

        /// <summary>
        /// SKELETON: the real body polls the <c>DE FA AD → 0xDD</c> exchange (rate-limited),
        /// or falls back to the native gen's passive byte-62 report, decodes the recovered
        /// rim, and commits a <see cref="DeviceSnapshot"/> with a Wheel attachment. Returns
        /// false here (no live identity yet).
        /// </summary>
        public bool Service() => false;

        public void Dispose() { }
    }
}
