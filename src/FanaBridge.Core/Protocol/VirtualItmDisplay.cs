using System;
using System.Collections.Generic;
using System.Text;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// A virtual ITM display panel — the wire-driven digital twin. It consumes exactly
    /// what a real panel consumes: the decoded col03 OUT frames that were actually sent
    /// (via <see cref="ICol03SendObserver"/> behind the outbound tap) plus the firmware's
    /// subscription pushes (col03-IN), maintains the same state a hardware panel holds,
    /// and publishes an immutable <see cref="DisplayValuesSnapshot"/> of the screen.
    /// Because it renders the wire rather than the host's intent, it can disagree with
    /// the host — which is the point: a twin that follows the bytes surfaces the bug
    /// classes that live between intent and device interpretation.
    ///
    /// <b>Device state modeled</b> (semantics per docs/reference/protocol.md, §0x05 —
    /// ITM Display):
    /// <list type="bullet">
    /// <item><b>Gate</b> (§0x02 — ITM Mode): off drops the panel to the true legacy
    /// 7-segment view (blank here — that content is written over col01, outside this
    /// model). The handle table is dropped — a gate cycle resets the firmware's table
    /// (§Firmware Subscription Pushes), and the landing page's push, if any, rebuilds
    /// it. A PageSet while gated off is recorded and applied on the next gate-on; a
    /// bare gate-on with none recorded lands on the legacy ITM page (both
    /// hardware-confirmed, §ITM Mode). The firmware retains every field value across
    /// the off→on cycle (hardware-verified, §DisplayReset); the twin reproduces that
    /// retention only when the gate-on lands back on the page it left (same-page
    /// ChangePage no-ops, so the painted glyphs stay). When the cycle lands on a
    /// DIFFERENT page — the legacy page after a bare cycle, or a cross-page PageSet
    /// recorded while off — the twin clears to placeholders under its MODELED
    /// page-change rule (below), where the firmware's stale-value cache is
    /// under-specified; a twin/hardware disagreement there is a discovery signal,
    /// not a fidelity claim.</item>
    /// <item><b>Session enable</b> (§0x02 — ITM Enable): recorded only. It has never
    /// been observed to have an effect (§Control Model), so no modeled behavior
    /// depends on it.</item>
    /// <item><b>Page</b> (§0x04 — PageSet): a host PageSet is authoritative for what
    /// the host selected. Wheel-button navigation is invisible on the OUT wire — it
    /// surfaces only as a subscription push, so page identity is then inferred by
    /// matching the accumulated parameter set against <see cref="ItmDeviceCatalog"/>
    /// (set matching, never handles — handle allocation varies by setup, §Firmware
    /// Subscription Pushes). Where the set identifies no page, the twin exposes
    /// Unknown honestly (wire page 0, no identity) rather than guessing.</item>
    /// <item><b>Handle table</b>: adopted exactly as pushed, entry by entry, including
    /// unsubscribes, accumulated across fragmented reports (one setup pushes one entry
    /// per report — a single report is never the complete set, §Firmware Subscription
    /// Pushes).</item>
    /// <item><b>Field values</b>: a ValueUpdate paints a field only when its handle is
    /// currently subscribed to that parameter — the firmware ignores values at guessed
    /// or stale handles (§Control Model), and so does the twin. An unsubscribe alone is
    /// table bookkeeping, not a visual event: the screen keeps showing its content
    /// until the page changes or a reset clears it. (The paramId cross-check on a
    /// subscribed handle is MODELED — the wire entry carries the paramId redundantly,
    /// and firmware behavior on a handle/paramId mismatch is unverified; the strict
    /// check makes a host writing through a stale table visible, which is the twin's
    /// job.)</item>
    /// <item><b>DisplayReset</b> (§0x05 — DisplayReset): clears every painted field
    /// value to its placeholder on every page; gate, session, page, and subscriptions
    /// stay untouched, and values arriving afterwards repopulate per handle
    /// (hardware-verified).</item>
    /// <item><b>Suffixes</b> (§0x03 — ParamDefs): per-slot suffix text, keyed by the
    /// slot's handle (<c>slotId &amp; 0x7F</c>), rendered next to that handle's painted
    /// value.</item>
    /// </list>
    ///
    /// <b>Modeled (not capture-verified) behavior</b>, marked per the renderer's own
    /// verified-vs-modeled discipline: on a page change the twin clears painted values
    /// to placeholders, while the firmware's stale-value cache may re-show a previous
    /// visit's values (under-specified — the honest placeholder is chosen over a guess);
    /// suffix decorations are kept across a DisplayReset (the reset spec enumerates
    /// field values only) and across a gate cycle (retained with the painted values
    /// they decorate), and adopted regardless of subscription state; a value encoded
    /// against the wrong declared wire type renders literally here, where real firmware
    /// may render nothing.
    ///
    /// <b>State the wire does not carry</b>: the host lifecycle state stamped on
    /// snapshots (the UI's status caption) is elicited from the caller via
    /// <see cref="Tick"/> — it is host-side annotation, not device state. A pre-existing
    /// firmware page at attach is not detectable on the wire; the twin starts at
    /// Unknown/placeholders until the first observed frames ground it — it is
    /// constructed with the tap, at session start, and re-grounded by the reset/gate/
    /// PageSet frames it observes and by <see cref="OnColdStart"/> (wheel change,
    /// disconnect — the same cold edges the lifecycle gets).
    ///
    /// One instance models ONE display device (the constructor's <c>deviceId</c>); frames and
    /// entries addressed to other display devices are ignored, exactly as an unattached
    /// panel ignores them. Additional displays are additional instances behind the same
    /// tap.
    ///
    /// Purity: the twin holds no transport reference and exposes no frame-emitting API —
    /// it is one-directional by construction. Never-stuck: unknown or malformed frames
    /// decode to an ignored token (<see cref="ItmFrameDecoder"/>); nothing here throws
    /// on any input. All inputs (<see cref="OnCol03Sent"/>, <see cref="OnSubscriptionReport"/>,
    /// <see cref="Tick"/>, <see cref="OnColdStart"/>) must arrive on one producer thread
    /// (the SimHub DataUpdate thread — the same thread that sends the frames and drains
    /// the pushes); <see cref="Snapshot"/> is volatile and safe to read from any thread.
    /// </summary>
    public sealed class VirtualItmDisplay : ICol03SendObserver
    {
        private readonly byte _deviceId;
        private readonly Func<long> _now;

        /// <summary>Minimum spacing between snapshot compositions. Change-gated on top
        /// of this floor: nothing recomposes while nothing on the screen changed.</summary>
        public int SnapshotIntervalMs { get; set; } = 250;

        /// <summary>
        /// How long an accumulated parameter set that identifies no page may stand
        /// before the twin judges it — the same window discipline as the lifecycle's
        /// unsubscribe grace. Fragmented pushes complete within tens of milliseconds;
        /// a set still unresolved after this window is judged as it stands: empty →
        /// the legacy ITM page (an unsubscribe-all with nothing following,
        /// protocol.md §ITM Mode), anything else → honest Unknown.
        /// </summary>
        public int PageIdentityGraceMs { get; set; } = 100;

        // ── Device state ─────────────────────────────────────────────────
        // Gate: null until first observed gate frame — the persisted hardware setting
        // is not knowable from the wire (§ITM Mode, setting vs session).
        private bool? _gateOn;
        private bool _sessionEnableSeen;
        private byte _wirePage;      // 0 = unknown
        private byte _pendingPage;   // PageSet recorded while gated off (0 = none)

        // Handle table, from pushes only (host handle → subscription).
        private readonly SortedDictionary<byte, ItmSubscription> _subs =
            new SortedDictionary<byte, ItmSubscription>();

        // A field value as painted on the screen. DataType is latched from the
        // subscription in effect when the value landed — the declared wire form the
        // host encoded against (the live table may rebind or drop the handle while
        // the painted glyphs stay on screen).
        private struct PaintedValue
        {
            public ushort ParamId;
            public byte Size;
            public uint Raw;
            public byte DataType;
        }

        private readonly Dictionary<byte, PaintedValue> _painted = new Dictionary<byte, PaintedValue>();
        private readonly Dictionary<byte, string> _suffixes = new Dictionary<byte, string>();

        // When the accumulated param set stopped identifying the current page (0 = it does).
        private long _unresolvedSinceMs;

        // Host-side annotation for the UI caption; elicited via Tick, never wire-derived.
        private ItmLifecycleState _hostState = ItmLifecycleState.Idle;

        // ── Snapshot publication ─────────────────────────────────────────
        private volatile DisplayValuesSnapshot _snapshot;
        private bool _dirty = true;   // first event composes the first snapshot
        private long _composedAtMs = long.MinValue / 2;

        private readonly HashSet<ushort> _setScratch = new HashSet<ushort>();

        public VirtualItmDisplay(byte deviceId = ItmEncoder.DefaultDeviceId, Func<long> nowMs = null)
        {
            _deviceId = deviceId;
            _now = nowMs ?? DefaultClock();
        }

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        // ── Observable state ─────────────────────────────────────────────

        /// <summary>The latest screen snapshot, or null before the first observed
        /// input (and after <see cref="OnColdStart"/>). Volatile — any thread.</summary>
        public DisplayValuesSnapshot Snapshot => _snapshot;

        /// <summary>The ITM Mode gate as observed on the wire; null until a gate frame
        /// has been seen (the persisted setting is not knowable from the wire).</summary>
        public bool? GateOn => _gateOn;

        /// <summary>The wire page the panel is on (0 = unknown/unidentified).</summary>
        public byte WirePage => _wirePage;

        /// <summary>Number of handles currently subscribed (from pushes).</summary>
        public int SubscriptionCount => _subs.Count;

        /// <summary>Whether a session enable (<c>FF 02 02</c>) has been observed this
        /// session. Recorded only — no modeled behavior depends on it (§Control Model:
        /// the enable has never been observed to have an effect).</summary>
        public bool SessionEnableObserved => _sessionEnableSeen;

        // ── Inputs (producer thread only) ────────────────────────────────

        /// <summary>
        /// One accepted col03 OUT report from the outbound tap (a private copy — see
        /// <see cref="ICol03SendObserver"/>). Decoded and applied to the panel state;
        /// unknown or malformed frames are ignored, never thrown on.
        /// </summary>
        public void OnCol03Sent(byte[] frame)
        {
            long now = _now();
            ResolveIdentityGrace(now);

            var f = ItmFrameDecoder.Decode(frame);
            switch (f.Type)
            {
                case ItmFrameType.SessionEnable:
                    _sessionEnableSeen = true;   // recorded only; no visual effect
                    break;

                case ItmFrameType.Gate:
                    ApplyGate(f.GateOn);
                    break;

                case ItmFrameType.DisplayReset:
                    // Clears every painted field on every page; gate, session, page and
                    // subscriptions untouched (§DisplayReset, hardware-verified).
                    if (_painted.Count > 0)
                    {
                        _painted.Clear();
                        _dirty = true;
                    }
                    break;

                case ItmFrameType.PageSet:
                    if (f.DeviceId != _deviceId)
                        break;   // another display device — not this panel
                    if (_gateOn == false)
                        _pendingPage = f.Page;   // recorded while off; applied at gate-on (§ITM Mode)
                    else
                        ChangePage(f.Page);      // host-authoritative selection
                    break;

                case ItmFrameType.ValueUpdate:
                    for (int i = 0; i < f.Values.Count; i++)
                        if (f.Values[i].DeviceId == _deviceId)
                            ApplyValue(f.Values[i]);
                    break;

                case ItmFrameType.ParamDefs:
                    for (int i = 0; i < f.ParamDefs.Count; i++)
                        if (f.ParamDefs[i].DeviceId == _deviceId)
                            ApplySuffix(f.ParamDefs[i]);
                    break;

                    // Unknown: ignored — never-stuck.
            }

            // Deliberately no compose here. This runs on the outbound tap path —
            // synchronously on the sending thread, and for the encoder's BATCHED sends
            // (SendValues / SetParamDefs wrap their report loop in one BeginBatch) still
            // inside the transport's held col03 write lock. Applying frame state is cheap;
            // composing a snapshot (per-slot DisplayValueSlot/DisplayValueField[] arrays
            // plus per-field ItmValueRenderer calls) is not, and must never extend the
            // wire path's lock-hold (research R7 — rendering cost never back-pressures the
            // wire). The owner calls Tick every frame AFTER the driver's sends complete,
            // off the batch and its lock, and that is where the deferred compose runs.
        }

        /// <summary>
        /// One firmware subscription report (col03-IN <c>FF 05 01</c>) — the same raw
        /// report the driver consumes. Entries for this display are adopted into the
        /// handle table exactly as pushed (subscribe / unsubscribe), and page identity
        /// is re-evaluated from the accumulated parameter set.
        /// </summary>
        public void OnSubscriptionReport(byte[] report)
        {
            long now = _now();
            ResolveIdentityGrace(now);

            var entries = ItmTelemetry.ParseSubscriptionReport(report, report?.Length ?? 0, _deviceId);
            if (entries.Count > 0)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var s = entries[i];
                    if (s.IsUnsubscribe)
                        _subs.Remove(s.Handle);   // painted glyphs stay — not a visual event
                    else
                        _subs[s.Handle] = s;
                }
                EvaluatePageIdentity(now);
            }

            MaybeCompose(now);
        }

        /// <summary>
        /// Per-frame poke from the owner: stamps the host lifecycle state onto future
        /// snapshots (caller-elicited annotation — the wire does not carry it), expires
        /// the page-identity grace window, and flushes any change held by the snapshot
        /// throttle. Producer thread only.
        /// </summary>
        public void Tick(ItmLifecycleState hostState)
        {
            long now = _now();
            if (hostState != _hostState)
            {
                _hostState = hostState;
                _dirty = true;
            }
            ResolveIdentityGrace(now);
            MaybeCompose(now);
        }

        /// <summary>
        /// Cold re-grounding: the panel behind the wire is known to be cold (wheel/hub/
        /// module change, disconnect, session stop — the same edges that cold-start the
        /// lifecycle). Everything observed so far described a device that no longer
        /// exists: all state is dropped, the published snapshot is cleared (a stale
        /// screen must never outlive the session it described), and the next observed
        /// frames re-ground the twin from the fresh bring-up.
        /// </summary>
        public void OnColdStart()
        {
            _gateOn = null;
            _sessionEnableSeen = false;
            _wirePage = 0;
            _pendingPage = 0;
            _subs.Clear();
            _painted.Clear();
            _suffixes.Clear();
            _unresolvedSinceMs = 0;
            _hostState = ItmLifecycleState.Idle;
            _snapshot = null;
            _dirty = true;
            _composedAtMs = long.MinValue / 2;   // the next event composes immediately
        }

        // ── Frame application ────────────────────────────────────────────

        private void ApplyGate(bool on)
        {
            if (on)
            {
                if (_gateOn == false)
                {
                    // Off → on: land on the page recorded while off, or the legacy ITM
                    // page if none (§ITM Mode, hardware-confirmed). Landing where the
                    // panel already was keeps its painted values — the firmware retains
                    // them across a gate cycle (§DisplayReset, hardware-verified) —
                    // because ChangePage no-ops on the same page.
                    byte target = _pendingPage != 0 ? _pendingPage : LegacyPageNumber();
                    _pendingPage = 0;
                    _gateOn = true;
                    _dirty = true;   // blank → visible
                    ChangePage(target);
                }
                else if (_gateOn == null)
                {
                    // First observed gate frame of the session (bring-up). The pre-gate
                    // page is unknowable (§ITM Mode: an unsubscribe-all does not identify
                    // the prior state) — leave the page alone; the PageSet that follows
                    // grounds it.
                    _gateOn = true;
                    _dirty = true;
                }
                // Already on: redundant re-assert, no state change.
            }
            else if (_gateOn != false)
            {
                // On (or unknown) → off: the panel drops to the true legacy 7-segment
                // view. Painted values and the page are retained for re-enable
                // (§DisplayReset, hardware-verified), but the handle table is dropped:
                // a gate cycle resets the firmware's table (§Firmware Subscription
                // Pushes), so values sent at pre-cycle handles are ignored — by the
                // firmware and by the twin — until a fresh push re-subscribes them.
                // A pending identity judgment describes a table that no longer exists.
                _gateOn = false;
                _pendingPage = 0;   // nothing recorded yet during this off period
                _subs.Clear();
                _unresolvedSinceMs = 0;
                _dirty = true;
            }
        }

        // Moves the panel to a wire page (0 = unknown). Painted values and suffixes are
        // cleared to placeholders on a genuine change — MODELED, not capture-verified:
        // the firmware's stale-value cache may re-show a previous visit's values on a
        // revisit, but that behavior is under-specified, and the honest placeholder is
        // chosen over a guess (twin/hardware discrepancies here are discovery signals).
        private void ChangePage(byte page)
        {
            if (page == _wirePage)
                return;
            _wirePage = page;
            _painted.Clear();
            _suffixes.Clear();
            _dirty = true;
        }

        private void ApplyValue(ItmValueEntry entry)
        {
            // The firmware ignores values at handles it has not subscribed for that
            // parameter (§Control Model: values at guessed handles are ignored) — the
            // twin does the same, which is exactly how it catches a host writing
            // through a stale handle table. The declared wire type is latched with the
            // painted value: it is the form the host encoded against, and the live
            // table may drop or rebind the handle while the glyphs stay on screen.
            ItmSubscription sub;
            if (!_subs.TryGetValue(entry.Handle, out sub) || sub.ParamId != entry.ParamId)
                return;

            var p = new PaintedValue
            {
                ParamId = entry.ParamId,
                Size = entry.Size,
                Raw = entry.Raw,
                DataType = sub.DataType,
            };

            PaintedValue old;
            if (_painted.TryGetValue(entry.Handle, out old)
                && old.ParamId == p.ParamId && old.Size == p.Size
                && old.Raw == p.Raw && old.DataType == p.DataType)
                return;   // unchanged re-assert — nothing on screen moved

            _painted[entry.Handle] = p;
            _dirty = true;
        }

        private void ApplySuffix(ItmParamDefEntry entry)
        {
            // Adopted regardless of subscription state (whether firmware ignores defs
            // for unsubscribed slots is unverified); a suffix only renders next to a
            // painted value at the same handle, so a stray def is invisible until a
            // subscribed value lands there.
            string text = entry.Suffix.Length == 0 ? "" : Encoding.ASCII.GetString(entry.Suffix);
            string old;
            if (_suffixes.TryGetValue(entry.Handle, out old) && old == text)
                return;
            _suffixes[entry.Handle] = text;
            _dirty = true;
        }

        // ── Page identity (set matching, mirroring the lifecycle's rules) ─

        // Re-judges identity after every push: the accumulated parameter set either
        // confirms the current page, positively identifies another catalog page (a
        // wheel-button change — the only way navigation surfaces on the wire), or
        // identifies nothing yet and opens the grace window. Matching is on the
        // parameter SET, never handles (allocation varies by setup, §Firmware
        // Subscription Pushes), and only an exact match moves the page.
        private void EvaluatePageIdentity(long now)
        {
            var set = SubscribedParamSet();

            if (SetMatchesPage(set, _wirePage))
            {
                _unresolvedSinceMs = 0;
                return;
            }

            byte matched = PageForParamSet(set);
            if (matched != 0)
            {
                _unresolvedSinceMs = 0;
                ChangePage(matched);
                return;
            }

            // Ambiguous: empty (front half of a change in flight, or the legacy page)
            // or a partial/uncataloged set. Fragments complete within tens of ms —
            // keep the current page through the grace window rather than flapping to
            // Unknown on every fragment; the window's expiry judges what proved stable.
            if (_unresolvedSinceMs == 0)
                _unresolvedSinceMs = now;
        }

        // Judges an accumulated set that stayed unresolved past the grace window.
        private void ResolveIdentityGrace(long now)
        {
            if (_unresolvedSinceMs == 0 || now - _unresolvedSinceMs < PageIdentityGraceMs)
                return;
            _unresolvedSinceMs = 0;

            var set = SubscribedParamSet();
            if (SetMatchesPage(set, _wirePage))
                return;

            byte matched = PageForParamSet(set);
            if (matched != 0)
            {
                ChangePage(matched);
            }
            else if (set.Count == 0)
            {
                // An unsubscribe-all with nothing following: the panel moved to the
                // legacy ITM page, which carries no telemetry parameters (§ITM Mode).
                // The wire cannot distinguish this from a gated-off panel — but the
                // gate is host-originated, and the twin tracks its own gate frames, so
                // that half of the ambiguity is covered from the OUT side.
                ChangePage(LegacyPageNumber());
            }
            else
            {
                // A stable set matching no catalog page — the firmware knows pages we
                // don't. Honest Unknown (the UI renders it as an unrecognized page).
                ChangePage(0);
            }
        }

        private HashSet<ushort> SubscribedParamSet()
        {
            _setScratch.Clear();
            foreach (var kv in _subs)
                _setScratch.Add(kv.Value.ParamId);
            return _setScratch;
        }

        // Whether the set is exactly the page's parameter list. The legacy page carries
        // no parameters, so it matches exactly the empty set; page 0 / an uncataloged
        // wire page matches nothing (it has no known parameter list to compare).
        private bool SetMatchesPage(HashSet<ushort> set, byte page)
        {
            var info = PageInfoFor(page);
            if (info == null)
                return false;
            if (info.Params.Count != set.Count)
                return false;
            for (int i = 0; i < info.Params.Count; i++)
                if (!set.Contains(info.Params[i]))
                    return false;
            return true;
        }

        // The wire page whose parameter set exactly matches, or 0 if none does — the
        // same rule the lifecycle applies (empty pages excluded: the empty set is
        // judged by the grace path, not matched positively).
        private byte PageForParamSet(HashSet<ushort> set)
        {
            var pages = ItmDeviceCatalog.PagesFor(_deviceId);
            for (int p = 0; p < pages.Count; p++)
            {
                var info = pages[p];
                if (info.Params.Count == 0 || info.Params.Count != set.Count)
                    continue;
                bool all = true;
                for (int i = 0; i < info.Params.Count; i++)
                {
                    if (!set.Contains(info.Params[i]))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                    return info.Number;
            }
            return 0;
        }

        private ItmPageInfo PageInfoFor(byte page)
        {
            if (page != 0)
            {
                var pages = ItmDeviceCatalog.PagesFor(_deviceId);
                for (int i = 0; i < pages.Count; i++)
                    if (pages[i].Number == page)
                        return pages[i];
            }
            return null;
        }

        private byte LegacyPageNumber()
        {
            var pages = ItmDeviceCatalog.PagesFor(_deviceId);
            for (int i = 0; i < pages.Count; i++)
                if (pages[i].IsLegacy)
                    return pages[i].Number;
            return 0;
        }

        // ── Snapshot composition ─────────────────────────────────────────

        // Change-gated, throttled: only when something on the screen (or the stamped
        // host state) edged, and no more often than SnapshotIntervalMs. A change landing
        // inside the window stays dirty and composes on a later event/Tick.
        //
        // One exception bypasses the throttle floor: a host-state transition (the status
        // caption edged). It is elicited by Tick, which the owner runs AFTER the frame's
        // subscription pushes and OUT frames have already been observed — so if an earlier
        // compose this same frame (a subscription push that found the throttle window open)
        // already published with the pre-transition caption, the throttle would otherwise
        // mask the Tick recompose for up to a full window. A caption transition is rare
        // (not the per-frame value churn the throttle exists to pace), so publishing it at
        // once keeps the twin honest without defeating the throttle.
        private void MaybeCompose(long now)
        {
            if (!_dirty)
                return;
            bool captionEdge = _snapshot == null || _snapshot.State != _hostState;
            if (!captionEdge && now - _composedAtMs < SnapshotIntervalMs)
                return;   // held until the throttle window passes (still dirty)
            _dirty = false;
            _composedAtMs = now;
            _snapshot = Compose(now);
        }

        private DisplayValuesSnapshot Compose(long now)
        {
            // Gated off: the true legacy 7-segment view — no ITM page, no fields (that
            // content arrives over col01, outside this model). Internal state is
            // retained for the next gate-on; the snapshot just shows nothing.
            if (_gateOn == false)
                return new DisplayValuesSnapshot(null, 0, null, _hostState, false,
                    null, null, null, null, null, null, now, DateTime.UtcNow);

            var info = PageInfoFor(_wirePage);
            var layout = info != null ? ItmDisplayLayout.For(info.Page) : null;

            DisplayValueSlot lt = null, lb = null, rt = null, rb = null;
            string gear = null, speed = null;
            if (layout != null && layout.HasSlots)
            {
                lt = BuildSlot(layout.LeftTop);
                lb = BuildSlot(layout.LeftBottom);
                rt = BuildSlot(layout.RightTop);
                rb = BuildSlot(layout.RightBottom);
                gear = RenderParam(ItmParam.Gear);
                speed = RenderParam(ItmParam.Speed);
            }

            string pageName = info != null ? info.Name : (_wirePage != 0 ? "Page " + _wirePage : null);
            return new DisplayValuesSnapshot(
                info != null ? info.Page : (ItmPage?)null, _wirePage, pageName,
                _hostState, _painted.Count == 0,
                lt, lb, rt, rb, gear, speed, now, DateTime.UtcNow);
        }

        private DisplayValueSlot BuildSlot(ItmDisplaySlot slot)
        {
            var fields = new DisplayValueField[slot.Fields.Count];
            for (int i = 0; i < fields.Length; i++)
            {
                var f = slot.Fields[i];
                fields[i] = new DisplayValueField(f.ParamId, f.Label, RenderParam(f.ParamId));
            }
            return new DisplayValueSlot(slot.Label, fields);
        }

        // The display string for one parameter: the painted value (fields repopulate
        // per handle, so each field independently shows its value or its placeholder),
        // rendered with the suffix decorating that handle and the wire form latched
        // when the value landed.
        private string RenderParam(ushort paramId)
        {
            foreach (var kv in _painted)
            {
                if (kv.Value.ParamId != paramId)
                    continue;
                string suffix;
                _suffixes.TryGetValue(kv.Key, out suffix);
                return ItmValueRenderer.Render(paramId,
                    new ItmValue(kv.Key, paramId, kv.Value.Size, kv.Value.Raw),
                    suffix, kv.Value.DataType);
            }
            return ItmValueRenderer.Placeholder(paramId);
        }
    }
}
