using System.Collections.Generic;
using System.Linq;
using FanaBridge.Transport;

namespace FanaBridge.Devices
{
    /// <summary>
    /// The priority-ordered binding loop: try probes in ascending <see cref="IDeviceProbe.Priority"/>,
    /// skip those whose cheap <see cref="IDeviceProbe.CouldMatch"/> fails, and return the
    /// first non-null <see cref="IDeviceProbe.TryBind"/> result. First match wins, which is
    /// how ambiguous identities resolve by order rather than nested conditionals — e.g.
    /// <c>0EB7:0005</c> is both a real CSL Elite base and the SRM emulation generation:
    /// <c>FanatecBaseProbe</c> (priority 10) runs first and confirms via FF 08; if it
    /// declines, <c>SrmProbe</c> (priority 50) confirms via its own exchange.
    ///
    /// This is the binding step the <see cref="DeviceManager"/> will own once it groups and
    /// binds a device collection. It is pulled out here, pure and unit-tested, so that step
    /// is a wiring change rather than new logic.
    /// </summary>
    internal static class ProbeBinder
    {
        public static IDeviceDriver Bind(
            HidDeviceGroup group, IDeviceTransport io, IReadOnlyList<IDeviceProbe> probes)
        {
            if (group == null || probes == null)
                return null;

            foreach (var probe in probes.OrderBy(p => p.Priority))
            {
                if (!probe.CouldMatch(group))
                    continue;

                var driver = probe.TryBind(group, io);
                if (driver != null)
                    return driver;   // first match by priority wins
            }

            return null;
        }
    }
}
