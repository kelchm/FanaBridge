using System;
using System.Linq;
using System.Text;
using FanaBridge.Transport;
using HidSharp;

namespace FanaBridge.Diagnostics
{
    /// <summary>
    /// Builds a human-readable, GitHub-ready snapshot of device detection — the
    /// in-app equivalent of the Fanatec-RE Col03IdentityProbe, captured from the
    /// live transport FanaBridge already holds open (so there is no need to close
    /// SimHub or run an external tool).
    ///
    /// Mostly read-only: it re-enumerates the HID bus, decodes the last FF 08 system report
    /// FanaBridge already drained (identity + firmware versions), flags the col03 control
    /// interface the same way the transport selects it (the &amp;col03 path token, else a
    /// 64-byte OUTPUT report), and takes a col01 input snapshot. It also runs one ACTIVE
    /// "converter identity probe" — the engage handshake plus the SRM <c>DE FA AD</c> query —
    /// then dumps what the device volunteers on both surfaces and the SRM <c>0xDD</c> channel.
    /// The only writes are identity handshakes, never tuning/config.
    ///
    /// The output is a fenced Markdown block ready to paste into a GitHub issue,
    /// emitting the same wire bytes (raw FF 08 hex + the 0x02/0x18/0x1F key bytes)
    /// as a raw USB capture, so an unrecognized wheel/hub/module is byte-comparable
    /// and directly reportable.
    /// </summary>
    public static class DiagnosticsReport
    {
        public static string Build(
            FanatecWheelbase wheelbase, bool connected, string statusDetail, string buildInfo,
            string controlMapperSection = null, string itmSection = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("### FanaBridge detection report");
            sb.AppendLine();
            sb.AppendLine("> Describe what's physically attached: wheelbase **or SRM converter** (model + firmware),");
            sb.AppendLine("> the wheel/hub (+ button module), and — for a converter — what the SRM software shows.");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(string.Format("{0,-13}{1}", "FanaBridge:", string.IsNullOrEmpty(buildInfo) ? "unknown" : buildInfo));
            sb.AppendLine(string.Format("{0,-13}{1}", "OS:", SafeOsVersion()));
            sb.AppendLine(string.Format("{0,-13}{1}", "Captured:", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"));
            sb.AppendLine();

            // Hardware detection: raw bus inventory -> decoded identity -> raw appendix.
            // Nothing sits above its source (identity is decoded from a col03 interface
            // listed above it; the verbose descriptor dump trails last).
            AppendInterfaceInventory(sb);
            sb.AppendLine();
            AppendSystemReport(sb, wheelbase, connected, statusDetail);
            sb.AppendLine();
            AppendConverterProbe(sb, wheelbase, connected);
            sb.AppendLine();
            AppendInputProbe(sb);
            sb.AppendLine();
            AppendReportDescriptors(sb);

            // Control Mapper (feature-specific), kept together as a trailer so the
            // hardware detection above stands on its own: what the app enumerates,
            // then what FanaBridge feeds it. Folded INSIDE the fence (the section is
            // passed in by the caller because it lives in the Control Mapper bridge).
            sb.AppendLine();
            AppendDirectInputControllers(sb);
            if (!string.IsNullOrEmpty(controlMapperSection))
            {
                sb.AppendLine();
                sb.AppendLine(controlMapperSection.TrimEnd());
            }
            if (!string.IsNullOrEmpty(itmSection))
            {
                sb.AppendLine();
                sb.AppendLine(itmSection.TrimEnd());
            }

            sb.AppendLine("```");
            return sb.ToString();
        }

        // ── Converter identity probe (active engage) ─────────────────────
        // The passive FF 08 section above is the device's resting state. This actively runs the engage
        // handshake and dumps what the device volunteers on both surfaces (col01 records + the col03
        // FF 08 report) plus the SRM DE FA AD -> 0xDD channel, so a single report from an SRM Conversion
        // Kit user carries everything needed to identify it. Read-only; a genuine base answers no 0xDD.
        private static void AppendConverterProbe(StringBuilder sb, FanatecWheelbase wb, bool connected)
        {
            sb.AppendLine("Converter identity probe (active engage — read-only):");
            if (!connected || wb?.Transport == null || !wb.Transport.IsConnected)
            {
                sb.AppendLine("  (not connected — connect the device and re-capture)");
                return;
            }

            ConverterCaptureProbe.Result r;
            try { r = new ConverterCaptureProbe().Run(wb.Transport); }
            catch (Exception ex) { sb.AppendLine("  (probe failed: " + ex.Message + ")"); return; }

            sb.AppendLine(Kv("Engage", r.Engage ?? "(none)"));

            sb.AppendLine(Kv("col01 records", r.Col01Records.Count + " distinct in " + r.Col01FramesRead + " frame(s):"));
            if (r.Col01Records.Count == 0)
                sb.AppendLine("      (no col01 input reports arrived)");
            else
                foreach (var rec in r.Col01Records)
                    sb.AppendLine("      " + rec);

            sb.AppendLine(Kv("col03 FF 08", r.Ff08Line ?? "(no FF 08 report — SRM converter or under-engaged)"));
            if (r.Ff08Raw != null)
                sb.AppendLine("      raw: " + r.Ff08Raw);

            sb.AppendLine(Kv("SRM DE FA AD", r.DeFaLine ?? "(no reply)"));
            if (r.DeFaRaw != null)
                sb.AppendLine("      raw: " + r.DeFaRaw);
        }

        // ── HID interface inventory ──────────────────────────────────────
        // Gathered fresh at capture time and independent of whether FanaBridge
        // connected, so the worst regression — a base the old path saw but the
        // new col03 logic does not — still produces useful evidence.
        private static void AppendInterfaceInventory(StringBuilder sb)
        {
            sb.AppendLine("HID interfaces");

            HidDevice[] devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices()
                    .Where(d => IsRelevantVid(d.VendorID))
                    .ToArray();
            }
            catch (Exception ex)
            {
                sb.AppendLine("  (enumeration failed: " + ex.Message + ")");
                return;
            }

            if (devices.Length == 0)
            {
                sb.AppendLine("  (none — device off/unplugged, or claimed by another process)");
                return;
            }

            // One entry per physical device (VID+PID shown on the entry line, so an SRM
            // converter is visible whether it enumerates under the Fanatec VID or its own).
            // Shared name/rel/mfr/serial print once; each collection is a compact line;
            // long paths go in their own block.
            foreach (var grp in devices.GroupBy(d => new { d.VendorID, d.ProductID })
                                       .OrderBy(g => g.Key.VendorID).ThenBy(g => g.Key.ProductID))
            {
                var members = grp.OrderBy(ColTag, StringComparer.OrdinalIgnoreCase).ToList();
                var first = members[0];
                sb.AppendLine(string.Format("  VID 0x{0:X4}  PID 0x{1:X4}  \"{2}\"  rel=0x{3:X4}  mfr=\"{4}\"  serial={5}",
                    grp.Key.VendorID, grp.Key.ProductID, SafeName(first),
                    SafeReleaseBcd(first), SafeManufacturer(first), SafeSerial(first)));

                foreach (var d in members)
                {
                    string usage = TopLevelUsage(SafeRawDescriptor(d));
                    sb.AppendLine(string.Format("    {0,-6} out={1,-4}in={2,-4}feat={3,-4}{4}",
                        ColTag(d), SafeMaxOutput(d), SafeMaxInput(d), SafeMaxFeature(d),
                        usage != null ? "usage=" + usage : ""));
                }

                // col03 = the &col03 node or a 64-byte OUTPUT report; a 64-byte INPUT
                // alone is not col03 (a PS4-mode gamepad col01 has in=64).
                bool col03 = members.Any(d => SafePath(d).IndexOf("col03", StringComparison.OrdinalIgnoreCase) >= 0)
                    || members.Any(d => Is64(SafeMaxOutput(d)));
                bool col02 = members.Any(d => SafePath(d).IndexOf("col02", StringComparison.OrdinalIgnoreCase) >= 0);
                sb.AppendLine("    " + string.Format("{0,-13}{1}", "col03 (PC):",
                    col03 ? "present" : col02 ? "absent (col02 only)" : "absent"));

                sb.AppendLine("    paths:");
                foreach (var d in members)
                    sb.AppendLine(string.Format("      {0,-6} {1}", ColTag(d), SafePath(d)));
            }
        }

        // The HID collection tag from the device path (col01..col05), or "(?)" when the
        // device exposes a single top-level collection (no &colNN in the path).
        private static string ColTag(HidDevice d)
        {
            string path = SafePath(d);
            foreach (var c in new[] { "col01", "col02", "col03", "col04", "col05" })
                if (path.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) return c;
            return "(?)";
        }

        private const int SRM_VENDOR_ID = 0x35F9;   // SRM Conversion Kit management VID

        // VIDs we enumerate: the Fanatec VID plus the SRM Conversion Kit VID. An SRM
        // converter can appear under either, so we list both and show the VID per entry
        // rather than (mis)labelling a VID as "SRM".
        private static bool IsRelevantVid(int vid)
            => vid == FanatecWheelbase.FANATEC_VENDOR_ID || vid == SRM_VENDOR_ID;

        // ── DirectInput game controllers ─────────────────────────────────
        // What SimHub's Control Mapper actually sees: the SAME DirectInput query
        // it runs (GameControl / AttachedOnly). A Fanatec base exposes more than
        // one HID collection that declares a game-controller usage, so it can show
        // up as TWO entries here (and in Control Mapper's "Add source controller"
        // picker) under the same name — but only one is actually fed by the firmware;
        // the other is "real on paper, dead on the wire" (declares controls but never
        // transmits, so its Windows Test panel is blank). IMPORTANT: that difference
        // is NOT visible at the enumeration layer — Capabilities AND GetObjects both
        // report full controls for the inert collection too (measured on a DD+:
        // col01 objects=118, col02=80). It only appears when the device is acquired
        // and read. We report both counts purely as evidence; do not rely on them to
        // pick the live one. Read-only: enumerates capabilities/objects; never
        // acquires or writes.
        private static void AppendDirectInputControllers(StringBuilder sb)
        {
            sb.AppendLine("DirectInput game controllers (Control Mapper sees these)");

            SharpDX.DirectInput.DirectInput di = null;
            try
            {
                di = new SharpDX.DirectInput.DirectInput();
                var all = di.GetDevices(
                    SharpDX.DirectInput.DeviceClass.GameControl,
                    SharpDX.DirectInput.DeviceEnumerationFlags.AttachedOnly);

                int fanatec = 0;
                foreach (var inst in all)
                {
                    SharpDX.DirectInput.Joystick js = null;
                    try
                    {
                        js = new SharpDX.DirectInput.Joystick(di, inst.InstanceGuid);

                        int vid = 0, pid = 0;
                        string ifacePath = "";
                        try { vid = js.Properties.VendorId; } catch { }
                        try { pid = js.Properties.ProductId; } catch { }
                        try { ifacePath = js.Properties.InterfacePath ?? ""; } catch { }

                        if (vid != FanatecWheelbase.FANATEC_VENDOR_ID) continue;
                        fanatec++;

                        int buttons = 0, axes = 0, povs = 0, objectCount = -1;
                        try { buttons = js.Capabilities.ButtonCount; } catch { }
                        try { axes = js.Capabilities.AxeCount; } catch { }
                        try { povs = js.Capabilities.PovCount; } catch { }
                        // GetObjects() is the actual enumerable control list. NOTE (measured
                        // on a ClubSport DD+): it does NOT distinguish the live collection from
                        // the inert one — both report full controls (col01 objects=118,
                        // col02=80) even though only col01 is ever fed and the Windows Test
                        // page renders col02 blank. So neither the Capabilities header NOR
                        // GetObjects exposes the live/inert difference; that only shows when the
                        // device is actually acquired and read. We report both counts as
                        // evidence, but treat neither as a reliable "inert" signal.
                        try { objectCount = js.GetObjects().Count; } catch { }

                        sb.AppendLine(string.Format("  PID 0x{0:X4}  caps(btn={1} axis={2} pov={3})  objects={4}{5}",
                            pid, buttons, axes, povs,
                            objectCount < 0 ? "?" : objectCount.ToString(),
                            objectCount == 0 ? "  <- inert: no enumerable controls (blank Test panel)" : ""));
                        sb.AppendLine(string.Format("    name: \"{0}\"", SafeInstanceName(inst)));
                        sb.AppendLine(string.Format("    guid: {0}", inst.InstanceGuid));
                        sb.AppendLine(string.Format("    path: {0}", string.IsNullOrEmpty(ifacePath) ? "(unavailable)" : ifacePath));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("  (device read failed: " + ex.Message + ")");
                    }
                    finally
                    {
                        try { js?.Dispose(); } catch { }
                    }
                }

                if (fanatec == 0)
                    sb.AppendLine("  (no Fanatec game controller enumerated)");
                sb.AppendLine(string.Format("  total game controllers seen: {0}", all.Count));
            }
            catch (Exception ex)
            {
                sb.AppendLine("  (DirectInput enumeration failed: " + ex.Message + ")");
            }
            finally
            {
                try { di?.Dispose(); } catch { }
            }
        }

        private static string SafeInstanceName(SharpDX.DirectInput.DeviceInstance inst)
        {
            try { return inst.InstanceName; } catch { return "?"; }
        }

        // ── Input report snapshot (read-only) ────────────────────────────
        // One bounded read per input-bearing collection (col01/col02/col03 — any with an
        // input report >= 33 bytes; each line is tagged with its collection). HID input is
        // change-driven, so a stationary wheel yields no report; the FF 08 report above is
        // the authoritative identity. Safe alongside a live connection (Windows delivers
        // HID input per-handle, so this can't steal the transport's frames).
        private static void AppendInputProbe(StringBuilder sb)
        {
            sb.AppendLine("Input report snapshot");

            HidDevice[] devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices()
                    .Where(d => IsRelevantVid(d.VendorID))
                    .ToArray();
            }
            catch (Exception ex)
            {
                sb.AppendLine("  (enumeration failed: " + ex.Message + ")");
                return;
            }

            var targets = devices.Where(d => SafeMaxInput(d) >= 33)
                .OrderBy(ColTag, StringComparer.OrdinalIgnoreCase).ToArray();
            if (targets.Length == 0)
            {
                sb.AppendLine("  (no interface with an input report >= 33 bytes)");
                return;
            }

            var idle = new System.Collections.Generic.List<string>();
            foreach (var d in targets)
            {
                string data = ReadInput(d);
                if (data != null)
                    sb.AppendLine(string.Format("  {0,-6} PID 0x{1:X4} in={2}: {3}",
                        ColTag(d), d.ProductID, SafeMaxInput(d), data));
                else
                    idle.Add(ColTag(d));
            }
            if (idle.Count > 0)
                sb.AppendLine("  " + string.Join("/", idle) + ": no report in 300 ms");
        }

        // Returns the raw input bytes + key offsets, or null if no report arrived.
        private static string ReadInput(HidDevice d)
        {
            int len = SafeMaxInput(d);
            if (len <= 0) len = 64;

            HidStream stream = null;
            try
            {
                if (!d.TryOpen(out stream) || stream == null) return null;
                stream.ReadTimeout = 300;
                var buf = new byte[len];
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return null;

                string hex = BytesToHex(buf.Take(n).ToArray());
                return (n >= 34)
                    ? string.Format("{0}   [30]=0x{1:X2} [31]=0x{2:X2} [32..33]=0x{3:X2} 0x{4:X2}",
                        hex, buf[30], buf[31], buf[32], buf[33])
                    : hex;
            }
            catch { return null; }
            finally
            {
                try { stream?.Close(); } catch { }
                try { stream?.Dispose(); } catch { }
            }
        }

        // ── Raw HID report descriptors ───────────────────────────────────
        // The device's own report descriptor: every report ID, size, and collection it
        // exposes — ground truth for the col03 / report-id-0xFF question, available
        // without the wheel moving, so one capture answers it without a re-run. Verbose,
        // so it sits last in the hardware-detection block.
        private static void AppendReportDescriptors(StringBuilder sb)
        {
            sb.AppendLine("HID report descriptors (raw)");

            HidDevice[] devices;
            try
            {
                devices = DeviceList.Local.GetHidDevices()
                    .Where(d => IsRelevantVid(d.VendorID))
                    .ToArray();
            }
            catch (Exception ex)
            {
                sb.AppendLine("  (enumeration failed: " + ex.Message + ")");
                return;
            }
            if (devices.Length == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            foreach (var grp in devices.GroupBy(d => new { d.VendorID, d.ProductID })
                                       .OrderBy(g => g.Key.VendorID).ThenBy(g => g.Key.ProductID))
                foreach (var d in grp.OrderBy(ColTag, StringComparer.OrdinalIgnoreCase))
                {
                    byte[] rd = SafeRawDescriptor(d);
                    sb.AppendLine(string.Format("  VID 0x{0:X4} PID 0x{1:X4} {2,-7}{3}",
                        d.VendorID, d.ProductID, ColTag(d) + ":", rd == null || rd.Length == 0 ? "(unavailable)" : BytesToHex(rd)));
                }
        }

        // ── FF 08 system report (identity + firmware, one payload) ───────
        // The base/wheel/module codes, their wire bytes, and the firmware versions are
        // ALL decoded from the single col03 FF 08 frame, so they are presented together:
        // one component per line carrying its code, identity byte, and firmware.
        private static void AppendSystemReport(
            StringBuilder sb, FanatecWheelbase wb, bool connected, string statusDetail)
        {
            sb.AppendLine("FF 08 system report (col03 — identity + firmware):");
            sb.AppendLine(Kv("Connected", connected.ToString()));
            if (!string.IsNullOrEmpty(statusDetail))
                sb.AppendLine(Kv("Detail", statusDetail));

            byte[] raw = wb?.LastRawReport;
            if (!connected || wb == null || raw == null || raw.Length == 0)
            {
                // A committed SRM converter identity has no FF 08 frame (it came from the DE FA
                // channel) — surface it so the report doesn't read as "unidentified".
                if (wb != null && wb.IsSrmConverter)
                {
                    sb.AppendLine(Kv("Identity source", "SRM Conversion Kit (DE FA channel — no FF 08)"));
                    sb.AppendLine(Kv("Steering wheel", wb.WheelDetected
                        ? (wb.WheelCode ?? string.Format("Unknown (id 0x{0:X2})", wb.WheelWireCode))
                            + (wb.IsHub ? " [hub]" : "")
                        : "(nothing attached)"));
                    if (wb.IsHub)
                        sb.AppendLine(Kv("Button module", (wb.ModuleCode
                            ?? (wb.ModuleWireCode != 0 ? string.Format("Unknown (0x{0:X2})", wb.ModuleWireCode) : "(none)"))
                            + (wb.ModuleWireCode != 0 ? "   [converter-module decode UNVALIDATED — please report]" : "")));
                    sb.AppendLine(Kv("Kit firmware", wb.SrmKitFirmware ?? "?"));
                    return;
                }

                sb.AppendLine("  (no FF 08 captured — not connected, no col03 interface, or no wheel");
                sb.AppendLine("   attached. If a wheel IS attached, a full USB capture may be needed —");
                sb.AppendLine("   see the Fanatec-RE capture-fanatec-usb.ps1 workflow.)");
                return;
            }

            var fw = DecodeFirmware(raw);
            if (fw != null)
                sb.AppendLine(Kv("SystemConfig", string.Format("0x{0:X4} ({1})",
                    fw.SystemConfig, fw.Extended ? "extended" : "legacy")));
            sb.AppendLine(Kv("Raw", BytesToHex(raw)));

            sb.AppendLine(Row("Wheelbase", wb.BaseCode ?? "Unknown", 0x02, wb.BaseType, fw?.Wheelbase));

            if (!wb.WheelDetected)
            {
                sb.AppendLine(Kv("Steering wheel", "(nothing attached)"));
            }
            else
            {
                string wheel =
                    wb.WheelCode != null     ? wb.WheelCode :
                    wb.WheelWireCode == 0xFF ? "EXT_INFO" :
                                               "Unknown";
                if (wb.IsHub) wheel += " [hub]";
                sb.AppendLine(Row("Steering wheel", wheel, 0x18, wb.WheelWireCode, fw?.SteeringWheel));

                if (wb.IsHub && (wb.ModuleCode != null || wb.ModuleWireCode != 0))
                    sb.AppendLine(Row("Button module", wb.ModuleCode ?? "Unknown", 0x1F, wb.ModuleWireCode, fw?.ButtonModule));
            }

            sb.AppendLine(Kv("Identifier", BuildIdentifier(wb) + (wb.IdentityStable ? "" : "  (settling)")));
            if (!string.IsNullOrEmpty(wb.DisplayName))
                sb.AppendLine(new string(' ', 18) + "(\"" + wb.DisplayName + "\")");
        }

        // "  Label:         value" — colon attached to the label, values aligned.
        private static string Kv(string label, string value)
        {
            return string.Format("  {0,-16}{1}", label + ":", value);
        }

        // A component row: code (left-aligned), its FF 08 identity byte, and firmware.
        // Shares the Kv value column so codes line up under scalar values.
        private static string Row(string label, string code, int offset, int wireByte, string fw)
        {
            string fwPart = string.IsNullOrEmpty(fw) ? "" : "  fw " + fw;
            return string.Format("  {0,-16}{1,-11} [0x{2:X2}]=0x{3:X2}{4}",
                label + ":", code, offset, wireByte, fwPart);
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

        // ── Firmware versions (decoded from the FF 08 system report) ──────
        // Folded into AppendSystemReport's component rows (wheelbase/steering-wheel/
        // button-module). Motor and Wireless QR are decoded below but NOT displayed: the
        // Fanatec updater doesn't surface Motor, and the FF 08 does not reliably carry the
        // Wireless QR version (the updater reads it via a separate protocol query), so it
        // usually reads 0.
        internal sealed class FirmwareVersions
        {
            public int SystemConfig;
            public bool Extended;
            public string Wheelbase, Motor, WirelessQr, SteeringWheel, ButtonModule;
        }

        // Decode the FF 08 firmware-version fields. Offsets are from the Fanatec driver
        // (FWFUProtocolHidHandleSystemReport); the offset->component mapping follows the
        // SDK struct order (FS_DEVICE_INFO.FirmwareVersion=base, then Motor/WQR/Steering-
        // Wheel/ButtonModule) and is cross-checked against the Fanatec FW updater:
        //   base [5..8]=Wheelbase, [0x0C..0x0F]=Motor, [0x13..0x16]=Wireless QR,
        //   [0x1A..0x1D]=Steering wheel/Hub, [0x21..0x24]=Button module.
        // Each component is a single byte in the FF 08 (the updater can show wider values
        // from its own query). Returns null when there is no usable FF 08 report.
        internal static FirmwareVersions DecodeFirmware(byte[] r)
        {
            if (r == null || r.Length < 0x25) return null;
            int systemConfig = (r[3] << 8) | r[2];
            bool extended = systemConfig >= 6;
            return new FirmwareVersions
            {
                SystemConfig = systemConfig,
                Extended = extended,
                Wheelbase = FwBase(r, extended),            // [5..8]  (16-bit LE in legacy)
                Motor = FwField(r, 0x0C, extended),         // [0x0C..0x0F]
                WirelessQr = FwField(r, 0x13, extended),    // [0x13..0x16]
                SteeringWheel = FwField(r, 0x1A, extended), // [0x1A..0x1D]
                ButtonModule = FwField(r, 0x21, extended),  // [0x21..0x24]
            };
        }

        // Extended: 4-byte block "a.b.c.d" at off. Legacy: a single byte at off.
        private static string FwField(byte[] r, int off, bool extended)
        {
            if (extended)
                return (off + 3 < r.Length) ? Fw4(r[off], r[off + 1], r[off + 2], r[off + 3]) : "?";
            return off < r.Length ? r[off].ToString() : "?";
        }

        // The base/wheelbase field: extended = 4-byte block at [5..8]; legacy = 16-bit LE (r[5],r[6]).
        private static string FwBase(byte[] r, bool extended)
        {
            if (extended)
                return r.Length > 8 ? Fw4(r[5], r[6], r[7], r[8]) : "?";
            return r.Length > 6 ? ((r[6] << 8) | r[5]).ToString() : "?";
        }

        // Format a 4-component version, trimming trailing-zero components the way the
        // Fanatec updater does ("6.0.0.0" -> "6", "2.12.0.1" -> "2.12.0.1", all-zero -> "0").
        private static string Fw4(int a, int b, int c, int d)
        {
            var parts = new[] { a, b, c, d };
            int last = 0;
            for (int i = 3; i > 0; i--) { if (parts[i] != 0) { last = i; break; } }
            return string.Join(".", parts.Take(last + 1));
        }

        // ── Helpers (mirror Col03IdentityProbe's defensive HID accessors) ─
        private static bool Is64(int len) => len == 64 || len == 65;
        private static string BytesToHex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
        private static byte[] SafeRawDescriptor(HidDevice d) { try { return d.GetRawReportDescriptor(); } catch { return null; } }

        // Top-level Usage Page + Usage from a raw HID report descriptor — the global
        // Usage Page and the first local Usage seen before the first Main Collection.
        // Returns "PPPP/UUUU" (hex) or null. Internal for unit testing.
        internal static string TopLevelUsage(byte[] rd)
        {
            if (rd == null || rd.Length == 0) return null;
            int usagePage = -1, usage = -1, i = 0;
            while (i < rd.Length)
            {
                byte b = rd[i++];
                if (b == 0xFE) { if (i < rd.Length) i += 2 + rd[i]; continue; }   // long item: skip
                int size = b & 0x03; if (size == 3) size = 4;
                int type = (b >> 2) & 0x03, tag = (b >> 4) & 0x0F;
                long data = 0;
                for (int k = 0; k < size && i + k < rd.Length; k++) data |= (long)rd[i + k] << (8 * k);
                i += size;
                if (type == 1 && tag == 0x0) usagePage = (int)data;                        // Global: Usage Page
                else if (type == 2 && tag == 0x0 && usage < 0) usage = (int)(data & 0xFFFF); // Local: Usage
                else if (type == 0 && tag == 0xA) break;                                    // Main: Collection
            }
            if (usagePage < 0 && usage < 0) return null;
            return string.Format("{0:X4}/{1:X4}", usagePage < 0 ? 0 : usagePage, usage < 0 ? 0 : usage);
        }
        private static int SafeMaxOutput(HidDevice d) { try { return d.GetMaxOutputReportLength(); } catch { return 0; } }
        private static int SafeMaxInput(HidDevice d) { try { return d.GetMaxInputReportLength(); } catch { return 0; } }
        private static int SafeMaxFeature(HidDevice d) { try { return d.GetMaxFeatureReportLength(); } catch { return 0; } }
        private static int SafeReleaseBcd(HidDevice d) { try { return d.ReleaseNumberBcd; } catch { return 0; } }
        // Fanatec wheelbases set a manufacturer string but usually expose no serial
        // (iSerialNumber = 0), so GetSerialNumber throws. Treat throw and empty alike as
        // "(none)" rather than a misleading "?" that reads like an error.
        private static string SafeManufacturer(HidDevice d) { try { var s = d.GetManufacturer(); return string.IsNullOrEmpty(s) ? "(none)" : s; } catch { return "(none)"; } }
        private static string SafeSerial(HidDevice d) { try { var s = d.GetSerialNumber(); return string.IsNullOrEmpty(s) ? "(none)" : s; } catch { return "(none)"; } }
        private static string SafeName(HidDevice d) { try { return d.GetProductName(); } catch { return "?"; } }
        private static string SafePath(HidDevice d) { try { return d.DevicePath ?? ""; } catch { return ""; } }
        private static string SafeOsVersion() { try { return Environment.OSVersion.ToString(); } catch { return "?"; } }
    }
}
