using System;

namespace FanaBridge.Diagnostics
{
    /// <summary>
    /// Detects whether the Fanatec app/service is running alongside FanaBridge. Both drive
    /// the same ITM display over the same HID channel with no arbitration in the protocol
    /// (last writer wins), and there is no wire-level way to detect a co-driver — no state
    /// query exists — so an OS-level process check is the only detection there is. The
    /// vendor service is known to gate, reset, and re-page the display on its own at
    /// startup and around wheel changes; a user running both should see a warning rather
    /// than chase phantom flicker/lost-page-change bugs.
    ///
    /// Checks are TTL-cached (process enumeration is not free, callers poll per frame or
    /// per UI tick), and detection edges are logged once each way.
    /// </summary>
    public class FanatecSoftwareMonitor
    {
        // The processes that drive ITM: the vendor's device service and its UI app.
        // Deliberately NOT the driver-package PnP service (FWPnpService) — it is
        // auto-started on every boot, never emits ITM traffic, and coexists fine.
        private static readonly string[] CoDriverProcesses = { "FanatecService", "Fanatec" };

        /// <summary>How long a check result is reused before re-probing the process list.</summary>
        public int CacheTtlMs { get; set; } = 10_000;

        // Probe seam: given the candidate process names, returns the first one running or
        // null. The default takes ONE process-table snapshot and matches all names against
        // it — per-name Process.GetProcessesByName calls each snapshot the whole table.
        private readonly Func<string[], string> _probe;
        private readonly Func<long> _now;
        private readonly Action<string> _log;

        // Far enough in the past that the first check always probes, without the
        // now - long.MinValue subtraction overflow.
        private long _lastCheckMs = -1_000_000_000;
        private string _running;   // null = none detected

        public FanatecSoftwareMonitor(Func<string[], string> probe = null,
            Func<long> nowMs = null, Action<string> log = null)
        {
            _probe = probe ?? DefaultProbe;
            _now = nowMs ?? DefaultClock();
            _log = log ?? (_ => { });
        }

        private static string DefaultProbe(string[] names)
        {
            System.Diagnostics.Process[] procs;
            try
            {
                procs = System.Diagnostics.Process.GetProcesses();
            }
            catch
            {
                return null;   // enumeration can fail under restricted accounts — stay quiet
            }

            string found = null;
            foreach (var p in procs)
            {
                if (found == null)
                {
                    string pn;
                    try { pn = p.ProcessName; } catch { pn = null; }
                    foreach (var n in names)
                    {
                        if (string.Equals(pn, n, StringComparison.OrdinalIgnoreCase))
                        {
                            found = n;
                            break;
                        }
                    }
                }
                p.Dispose();
            }
            return found;
        }

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        /// <summary>
        /// The co-driving vendor process detected on the last check (e.g. "FanatecService"),
        /// or null when none is running. Refreshes at most every <see cref="CacheTtlMs"/>.
        /// </summary>
        public string DetectedProcess
        {
            get
            {
                long now = _now();
                if (now - _lastCheckMs >= CacheTtlMs)
                {
                    _lastCheckMs = now;
                    string found = _probe(CoDriverProcesses);
                    if (found != null && _running == null)
                        _log("ITM: Fanatec software detected running (" + found + ") — it may drive the display" +
                             " concurrently; page changes and values can conflict");
                    else if (found == null && _running != null)
                        _log("ITM: Fanatec software no longer running — display is single-driver again");
                    _running = found;
                }
                return _running;
            }
        }

        /// <summary>A user-facing warning line, or null when no co-driver is detected.</summary>
        public string Warning
        {
            get
            {
                var p = DetectedProcess;
                return p == null
                    ? null
                    : "Fanatec software is running (" + p + "). Both programs drive the same display — " +
                      "if its ITM/dashboard features are active, expect flicker or lost page changes.";
            }
        }
    }
}
