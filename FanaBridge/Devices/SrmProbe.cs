using System.Linq;
using FanaBridge.Transport;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Recognizes an SRM conversion kit across its three USB generations. Runs AFTER
    /// <see cref="FanatecBaseProbe"/> (priority 50 vs 10) so that on the ambiguous
    /// <c>0EB7:0005</c> identity — both a real CSL Elite base and the SRM emulation
    /// gen — a real base claims it first via FF 08; only if that declines does this
    /// probe confirm via the SRM exchange. The three gens (different VID:PIDs, the
    /// firmware gate, the passive fallback) are all isolated here + in
    /// <see cref="SrmDriver"/> — the manager never hears the word "gen."
    ///
    /// SKELETON: <see cref="CouldMatch"/> (the cheap VID:PID pre-filter) is real;
    /// <see cref="TryBind"/> binds structurally. The real confirm (the <c>DE FA AD</c>
    /// request-response, the <c>bcdDevice</c> gate, the native passive fallback) lands
    /// with the kit in the SRM phase.
    /// </summary>
    internal sealed class SrmProbe : IDeviceProbe
    {
        public int Priority => 50;

        // (vid, pid) per generation. A new Leo-Bodnar VID is one more row here, nothing
        // else changes.
        private static readonly (int vid, int pid)[] Known =
        {
            (0x1DD2, 0x2011), // native    — Leo-Bodnar lineage VID
            (0x0EB7, 0x0E03), // interim   — Fanatec VID, SRM PID
            (0x0EB7, 0x0005), // emulation — impersonates CSL Elite (AMBIGUOUS with a real base)
        };

        public bool CouldMatch(HidDeviceGroup dev)
            => dev != null && Known.Any(k => k.vid == dev.Vid && k.pid == dev.Pid);

        public IDeviceDriver TryBind(HidDeviceGroup dev, IDeviceTransport io)
        {
            if (dev == null || io == null)
                return null;

            // SKELETON: the real probe does SrmDriver.TryConfirm(io, gen, dev.FirmwareBcd)
            // and returns null when the DE FA AD exchange does not answer (so a genuine
            // 0EB7:0005 base, already handled by FanatecBaseProbe, is never mis-bound).
            // Here it binds structurally to demonstrate the additive shape.
            return new SrmDriver(io, dev.FirmwareBcd);
        }
    }
}
