using System;
using System.Linq;
using System.Text;
using FanaBridge.Transport;
using HidSharp;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Builds a human-readable, GitHub-ready snapshot of device detection — the
    /// in-app equivalent of the Fanatec-RE Col03IdentityProbe, captured from the
    /// live transport FanaBridge already holds open (so there is no need to close
    /// SimHub or run an external tool).
    ///
    /// Strictly read-only: it re-enumerates the HID bus and formats the last FF 08
    /// system report FanaBridge already drained. It sends nothing to the device.
    ///
    /// The output is a fenced Markdown block ready to paste into a GitHub issue,
    /// emitting the same wire bytes (raw FF 08 hex + the 0x02/0x18/0x1F key bytes)
    /// as every existing RE capture, so an unrecognized wheel/hub/module is
    /// byte-comparable and directly reportable.
    /// </summary>
    public static class DiagnosticsReport
    {
        public static string Build(
            FanatecWheelbase wheelbase, bool connected, string statusDetail, string buildInfo)
        {
            var sb = new StringBuilder();

            sb.AppendLine("### FanaBridge detection report");
            sb.AppendLine();
            sb.AppendLine("> **Please describe what is physically attached** (wheelbase model, "
                + "wheel/hub, button module) so it can be matched to the bytes below.");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("FanaBridge : " + (string.IsNullOrEmpty(buildInfo) ? "unknown" : buildInfo));
            sb.AppendLine("OS         : " + SafeOsVersion());
            sb.AppendLine("Captured   : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            sb.AppendLine();

            AppendInterfaceInventory(sb);
            sb.AppendLine();
            AppendIdentity(sb, wheelbase, connected, statusDetail);
            sb.AppendLine();
            AppendRawReport(sb, wheelbase);

            sb.AppendLine("```");
            return sb.ToString();
        }

        // ── HID interface inventory ──────────────────────────────────────
        // Gathered fresh at capture time and independent of whether FanaBridge
        // connected, so the worst regression — a base the old path saw but the
        // new col03 logic does not — still produces useful evidence.
        private static void AppendInterfaceInventory(StringBuilder sb)
        {
            sb.AppendLine("Fanatec HID interfaces (VID 0x0EB7):");

            HidDevice[] devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FanatecWheelbase.FANATEC_VENDOR_ID)
                    .OrderBy(d => d.ProductID)
                    .ToArray();
            }
            catch (Exception ex)
            {
                sb.AppendLine("  (enumeration failed: " + ex.Message + ")");
                return;
            }

            if (devices.Length == 0)
            {
                sb.AppendLine("  (none — base off/unplugged, or claimed by another process)");
                return;
            }

            foreach (var d in devices)
                sb.AppendLine("  " + DescribeDevice(d));
        }

        private static string DescribeDevice(HidDevice d)
        {
            string col = "?";
            string path = SafePath(d);
            foreach (var c in new[] { "col01", "col02", "col03", "col04", "col05" })
                if (path.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) col = c;

            return string.Format("PID 0x{0:X4}  {1,-6} out={2,3} in={3,3}  \"{4}\"",
                d.ProductID, col, SafeMaxOutput(d), SafeMaxInput(d), SafeName(d));
        }

        // ── Decoded identity ─────────────────────────────────────────────
        private static void AppendIdentity(
            StringBuilder sb, FanatecWheelbase wb, bool connected, string statusDetail)
        {
            sb.AppendLine("Detection:");
            sb.AppendLine("  Connected  : " + connected);
            if (!string.IsNullOrEmpty(statusDetail))
                sb.AppendLine("  Detail     : " + statusDetail);

            if (!connected || wb == null)
                return;

            sb.AppendLine(string.Format("  Device     : \"{0}\"  (PID 0x{1:X4})",
                wb.ProductName ?? "?", wb.ConnectedProductId));
            sb.AppendLine(string.Format("  Wheelbase  : {0}  [0x02]=0x{1:X2}",
                wb.BaseCode ?? "Unknown", wb.BaseType));

            if (!wb.WheelDetected)
            {
                sb.AppendLine("  Attachment : (nothing attached)");
            }
            else
            {
                string wheel =
                    wb.WheelCode != null     ? wb.WheelCode :
                    wb.WheelWireCode == 0xFF ? "EXT_INFO (please report)" :
                                               "Unknown (please report)";
                sb.AppendLine(string.Format("  Attachment : {0}{1}  [0x18]=0x{2:X2}",
                    wheel, wb.IsHub ? " [hub]" : "", wb.WheelWireCode));

                string module =
                    wb.ModuleCode != null                  ? wb.ModuleCode :
                    wb.IsHub && wb.ModuleWireCode != 0      ? "Unknown (please report)" :
                                                              "none";
                sb.AppendLine(string.Format("  Module     : {0}  [0x1F]=0x{1:X2}",
                    module, wb.ModuleWireCode));
            }

            sb.AppendLine("  Identity   : " + (wb.IdentityStable ? "stable" : "settling")
                + "  DisplayName=\"" + wb.DisplayName + "\"");
            sb.AppendLine("  Identifier : " + BuildIdentifier(wb));
        }

        // The FanaBridge identifier — the single line that matters for mapping,
        // mirroring Col03IdentityProbe (e.g. "PHUB_PBME").
        private static string BuildIdentifier(FanatecWheelbase wb)
        {
            if (!wb.WheelDetected)
                return "(no wheel)";

            string wheel =
                wb.WheelCode != null     ? wb.WheelCode :
                wb.WheelWireCode == 0xFF ? "EXT_INFO(0xFF)" :
                                           string.Format("UNKNOWN(0x{0:X2})", wb.WheelWireCode);

            return (wb.IsHub && wb.ModuleCode != null) ? wheel + "_" + wb.ModuleCode : wheel;
        }

        // ── Raw FF 08 frame ──────────────────────────────────────────────
        private static void AppendRawReport(StringBuilder sb, FanatecWheelbase wb)
        {
            sb.AppendLine("Raw FF 08 system report:");

            byte[] raw = wb?.LastRawReport;
            if (raw == null || raw.Length == 0)
            {
                sb.AppendLine("  (none captured — not connected, no wheel attached, or no FF 08");
                sb.AppendLine("   frame received. If a wheel IS attached, a full USB capture may be");
                sb.AppendLine("   needed — see the Fanatec-RE capture-fanatec-usb.ps1 workflow.)");
                return;
            }

            sb.AppendLine("  " + BytesToHex(raw));
        }

        // ── Helpers (mirror Col03IdentityProbe's defensive HID accessors) ─
        private static string BytesToHex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
        private static int SafeMaxOutput(HidDevice d) { try { return d.GetMaxOutputReportLength(); } catch { return 0; } }
        private static int SafeMaxInput(HidDevice d) { try { return d.GetMaxInputReportLength(); } catch { return 0; } }
        private static string SafeName(HidDevice d) { try { return d.GetProductName(); } catch { return "?"; } }
        private static string SafePath(HidDevice d) { try { return d.DevicePath ?? ""; } catch { return ""; } }
        private static string SafeOsVersion() { try { return Environment.OSVersion.ToString(); } catch { return "?"; } }
    }
}
