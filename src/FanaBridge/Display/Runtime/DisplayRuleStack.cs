using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using GameReaderCommon;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using SimHub.Plugins;

namespace FanaBridge.Display.Runtime
{
    /// <summary>
    /// The per-device display-customization runtime: both rule engines (ITM + legacy),
    /// the page director, the property source, and the action hub, composed for exactly
    /// one (config, ITM driver) pair. The device instance builds a stack lazily on the
    /// frame path only when the config actually customizes something
    /// (<see cref="DisplayCustomizationConfig.IsEmpty"/> is the parity gate: an empty
    /// config constructs none of this) and replaces it whole on any identity change —
    /// config swap, driver rebuild (generation rebind, display-id change), wheel change,
    /// disconnect. Engines are per-config by design, so a rebuild is the state reset.
    ///
    /// Tick order (inside DataUpdate, after the driver's own
    /// <see cref="ItmDisplayDriver.Update"/> has run the lifecycle's Tick): property
    /// source BeginFrame → action drain → engine Tick → director Tick. The director's
    /// manual-navigation result feeds the ENGINE'S NEXT tick — one frame of latency
    /// (~16 ms), harmless because the lifecycle already adopted the page.
    ///
    /// P2 scope note: the legacy engine runs and its intents are logged, but nothing is
    /// written to the 7-segment surface yet (the col01 text path is a later phase); an
    /// ITM-rule legacy-screen target gets the display onto the legacy page (director)
    /// with the screen id logged the same way.
    /// </summary>
    public class DisplayRuleStack
    {
        /// <summary>Countdown recompose floor: while the on-screen winner carries a timed
        /// hold, the snapshot refreshes this often so a UI countdown can tick — the only
        /// visible change with no status/activity/intent edge. Bounded churn: outside a
        /// timed hold the change gates alone decide.</summary>
        internal const int CountdownRecomposeMs = 250;

        private readonly DisplayRuleEngine _itmEngine;
        private readonly DisplayRuleEngine _legacyEngine;
        private readonly DisplayPageDirector _director;
        private readonly SimHubPropertySource _properties;
        private readonly DisplayActionHub _actions;
        private readonly Action<string> _log;

        // The stack's clock, shared with both engines and the director (one coherent
        // timeline: event AtMs, the snapshot's ComposedAtMs, and the countdown
        // recompose floor all read the same milliseconds).
        private readonly Func<long> _now;

        // Rule lookup for snapshot labels (ids are unique across both sets — validator).
        private readonly Dictionary<string, DisplayRule> _rulesById =
            new Dictionary<string, DisplayRule>(StringComparer.Ordinal);

        // Manual navigation detected by the director last tick, consumed by the ITM
        // engine this tick (the documented one-frame latency).
        private ManualNavigation? _pendingManual;

        private readonly List<string> _actionBuf = new List<string>();

        // Change detection for logging (P2: legacy intents are log-only) and for
        // snapshot recomposition.
        private string _lastLegacyLogged;
        private string _lastLegacyScreenLogged;
        private long _lastActivityVersion = -1;
        private string _lastIntentDescription;
        private readonly string _basePageName;
        private RuleStatus[] _lastItmStatuses;
        private RuleStatus[] _lastLegacyStatuses;
        private long _lastComposedAt = long.MinValue / 2;

        /// <summary>Production wiring: the director talks to the driver's lifecycle
        /// through <see cref="ItmLifecyclePageControl"/>.</summary>
        public DisplayRuleStack(DisplayCustomizationConfig config, ItmDisplayDriver driver,
            byte itmDeviceId, byte defaultWirePage, Action<string> log = null)
            : this(config, new ItmLifecyclePageControl(driver.Lifecycle), itmDeviceId,
                defaultWirePage, log, nowMs: null)
        {
            Driver = driver;
        }

        /// <summary>Test wiring: a fake <see cref="IItmPageControl"/> and injected clock.</summary>
        internal DisplayRuleStack(DisplayCustomizationConfig config, IItmPageControl control,
            byte itmDeviceId, byte defaultWirePage, Action<string> log, Func<long> nowMs)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? (_ => { });
            // Resolve the clock HERE (not in each engine) so both engines, the director,
            // and the stack's own composition share one timeline — MergeActivity and the
            // snapshot's ComposedAtMs rely on that.
            _now = nowMs ?? DefaultClock();

            // The device's page set gates rule availability (a Bentley has no Car
            // Settings page) and resolves the base page — one table, both directions.
            var table = ItmPageTable.ForDevice(itmDeviceId);
            var available = new HashSet<ItmPage>();
            foreach (var page in table.Pages)
                available.Add(page.Page);

            // The config's own base page when set, else null (the effective base falls to
            // the device's default wire below). Read at build time: a default-page change
            // alone doesn't rebuild the stack, so it takes effect on the next rebuild
            // (config edit, reconnect, wheel change).
            ItmPage? configuredBase = config.Itm != null && config.Itm.BasePageRaw != null
                ? config.Itm.BasePage
                : (ItmPage?)null;
            // Latch the two resolution inputs so a later cross-device rebuild can
            // re-resolve the base against the NEW device's table (a wire page number is
            // valid only with the device id/table that produced it). Values are stored as
            // computed here — never mutated.
            ConfiguredBase = configuredBase;
            DefaultWirePage = defaultWirePage;

            // The effective base — the wire the display actually rests on, that wire's
            // identity, and its name — through the ONE table: the config's base when this
            // device offers it, else the default wire's identity. The device instance feeds
            // BaseWirePage to the ITM driver as the effective default page while this stack
            // is live, so the lifecycle (cold bring-up target) and the engine (resting
            // target) agree on ONE base-page authority. The snapshot's "Always →" name
            // follows the same resolution, so the UI can't claim a pinned page this device
            // doesn't have, or a default-page setting this stack hasn't latched.
            var baseResolution = table.ResolveBase(configuredBase, defaultWirePage);
            BaseWirePage = baseResolution.Wire;
            _basePageName = baseResolution.Name;

            // A configured base this device lacks (a Bentley pinned to Car Settings) is a
            // real misconfiguration: the config document keeps the user's value untouched,
            // but this stack rests on the fallback resolved above. Say so once so the pinned
            // page's absence is visible in the log.
            if (configuredBase.HasValue && !table.Offers(configuredBase.Value))
                _log("DisplayRules: configured base page "
                    + ItmTelemetry.NameOf(configuredBase.Value)
                    + " is not available on this display — resting on " + _basePageName);

            // The engine rests on the EFFECTIVE base IDENTITY — the one sitting at
            // BaseWirePage — not the raw configured page. Resting on a page this device
            // lacks would strand the display: the director cannot resolve it to a wire, so
            // once a rule expired nothing would return the display to the base. Passing the
            // resolved identity makes the engine's rest-intent, BaseWirePage, and
            // BasePageName all name the ONE page the director can actually request.
            _itmEngine = DisplayRuleEngine.ForItm(config.Itm?.Rules, baseResolution.Identity,
                available, _now, _log);
            _legacyEngine = DisplayRuleEngine.ForLegacy(config.Legacy?.Rules,
                config.Legacy?.BaseScreenId, _now, _log);
            _director = new DisplayPageDirector(control, itmDeviceId, _now, _log);
            _properties = new SimHubPropertySource(_log);
            _actions = new DisplayActionHub(config, _log);

            IndexRules(config.Itm?.Rules);
            IndexRules(config.Legacy?.Rules);
        }

        /// <summary>The config this stack was built from (reference identity — a swap
        /// publishes a new instance, which is the rebuild signal).</summary>
        public DisplayCustomizationConfig Config { get; }

        /// <summary>The ITM driver this stack was built against (reference identity —
        /// a driver rebuild invalidates the stack). Null when test-wired.</summary>
        internal ItmDisplayDriver Driver { get; }

        /// <summary>The engine's base page as this device's wire number — the effective
        /// default page while this stack owns page policy (see the ctor note).</summary>
        internal byte BaseWirePage { get; }

        /// <summary>The configured base page identity this stack latched at build time (null
        /// when the config pins none). A wire number is device-specific, so a cross-device
        /// driver rebuild re-resolves this identity against the NEW device's table rather
        /// than carrying <see cref="BaseWirePage"/> — which is valid only on the old table.</summary>
        internal ItmPage? ConfiguredBase { get; }

        /// <summary>The default wire page this stack latched at build time (the fallback the
        /// effective base resolves against when the configured base is absent). Paired with
        /// <see cref="ConfiguredBase"/> to re-resolve the base on a new device's table.</summary>
        internal byte DefaultWirePage { get; }

        /// <summary>Test access to the action hub (production handlers reach it via
        /// the registered SimHub actions).</summary>
        internal DisplayActionHub Actions => _actions;

        /// <summary>
        /// Runs one frame: resolves properties, drains action fires, ticks both engines,
        /// and lets the director reconcile the ITM intent with the lifecycle. Call once
        /// per DataUpdate, after the ITM driver's Update (all ITM mutation stays on the
        /// DataUpdate thread). Returns a fresh snapshot when the visible state changed,
        /// else null (the caller keeps publishing the previous one).
        /// </summary>
        public DisplayRuleSnapshot Tick(PluginManager pm, GameData data)
        {
            _properties.BeginFrame(pm, data);
            _actions.EnsureRegistered(pm);
            _actionBuf.Clear();
            _actions.DrainTriggered(_actionBuf);

            // The existing ITM gate: telemetry is live only while a game is feeding
            // fresh data. Idle-eligible rules see InGame=false on connected idle
            // frames (DataUpdate keeps ticking device instances with no game running —
            // the disconnect/suspension guards sit earlier in the device instance).
            bool inGame = data != null && data.GameRunning && data.NewData != null;

            var input = new RuleEngineInput
            {
                InGame = inGame,
                Properties = _properties,
                TriggeredActions = _actionBuf.Count > 0 ? _actionBuf : null,
                Manual = _pendingManual,
            };
            _pendingManual = null;

            var itm = _itmEngine.Tick(input);

            // The legacy surface has no manual navigation (no wheel button walks
            // 7-segment screens).
            input.Manual = null;
            var legacy = _legacyEngine.Tick(input);
            LogLegacyIntentChange(legacy.Intent);

            var directed = _director.Tick(itm.Intent);
            _pendingManual = directed.Manual;
            LogLegacyScreenChange(directed.LegacyScreenId);

            return MaybeCompose(itm, legacy);
        }

        // ── Composition (change-gated) ───────────────────────────────────

        private DisplayRuleSnapshot MaybeCompose(RuleEngineResult itm, RuleEngineResult legacy)
        {
            long version = itm.ActivityVersion + legacy.ActivityVersion;
            // Evaluate every gate (no short-circuit): StatusesChanged also refreshes
            // the remembered statuses, which must happen every tick or a skipped
            // comparison would re-report the same change next frame.
            bool versionChanged = version != _lastActivityVersion;
            bool itmChanged = StatusesChanged(itm.RuleStates, ref _lastItmStatuses);
            bool legacyChanged = StatusesChanged(legacy.RuleStates, ref _lastLegacyStatuses);
            // The described intent is a gate of its own: an Alternate target flips the
            // emitted intent every period with no activity event and no status change,
            // and the published snapshot must follow what the display actually shows.
            string intent = DescribeIntent(itm.Intent);
            bool intentChanged = !string.Equals(intent, _lastIntentDescription, StringComparison.Ordinal);
            long now = _now();
            if (!versionChanged && !itmChanged && !legacyChanged && !intentChanged)
            {
                // Nothing edged — but a timed on-screen hold still counts down, and the
                // snapshot carries RemainingMs as of composition, so a frozen snapshot
                // would freeze a UI countdown. Recompose at most every
                // CountdownRecomposeMs while the current winner carries one (bounded
                // churn: only during a timed hold).
                bool counting = WinnerCountsDown(itm.RuleStates)
                    || WinnerCountsDown(legacy.RuleStates);
                if (!counting || now - _lastComposedAt < CountdownRecomposeMs)
                    return null;
            }
            _lastActivityVersion = version;
            _lastIntentDescription = intent;
            _lastComposedAt = now;

            return new DisplayRuleSnapshot(
                intent,
                _basePageName,
                Rows(itm.RuleStates),
                Rows(legacy.RuleStates),
                MergeActivity(),
                version,
                now,
                DateTime.UtcNow);
        }

        // True when the surface's winning rule is holding the screen on a timer — the
        // one state whose visible representation (the countdown) changes with no
        // status or activity edge.
        private static bool WinnerCountsDown(IReadOnlyList<RuleLiveState> states)
        {
            for (int i = 0; i < states.Count; i++)
                if (states[i].Status == RuleStatus.OnScreen && states[i].RemainingMs != null)
                    return true;
            return false;
        }

        // Compares (and refreshes) the remembered statuses. RemainingMs ticking down is
        // NOT a status change — the countdown recompose floor above handles it at a
        // bounded cadence instead of every frame.
        private static bool StatusesChanged(IReadOnlyList<RuleLiveState> states, ref RuleStatus[] last)
        {
            bool changed = last == null || last.Length != states.Count;
            if (changed)
                last = new RuleStatus[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                if (!changed && last[i] != states[i].Status)
                    changed = true;
                last[i] = states[i].Status;
            }
            return changed;
        }

        private DisplayRuleRow[] Rows(IReadOnlyList<RuleLiveState> states)
        {
            var rows = new DisplayRuleRow[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                _rulesById.TryGetValue(state.RuleId ?? "", out var rule);
                rows[i] = new DisplayRuleRow(state.RuleId,
                    rule != null ? DisplayRuleFormatter.Label(rule) : (state.RuleId ?? "?"),
                    state.Status, state.RemainingMs);
            }
            return rows;
        }

        // Both engines share one clock, so a time-merge yields one coherent feed.
        private IReadOnlyList<DisplayActivityEvent> MergeActivity()
        {
            var a = _itmEngine.GetActivityEvents();
            var b = _legacyEngine.GetActivityEvents();
            if (b.Count == 0) return a;
            if (a.Count == 0) return b;
            var merged = new List<DisplayActivityEvent>(a.Count + b.Count);
            int i = 0, j = 0;
            while (i < a.Count || j < b.Count)
            {
                if (j >= b.Count || (i < a.Count && a[i].AtMs <= b[j].AtMs))
                    merged.Add(a[i++]);
                else
                    merged.Add(b[j++]);
            }
            return merged;
        }

        private static string DescribeIntent(RuleIntent intent)
            => intent.Kind == TargetKind.LegacyScreen
                ? "screen '" + (intent.ScreenId ?? "(blank)") + "'"
                : (intent.Page == null
                    // Resting without a page intent: the wheel navigated to a page
                    // outside the catalog and the engine adopted "wherever the wheel is".
                    ? "Current page"
                    : DisplayRuleFormatter.PageName(intent.Page));

        // ── P2 log-only surfaces ─────────────────────────────────────────

        // The legacy ENGINE's intent (what the 7-segment surface should show). The
        // col01 write lands in a later phase; until then the decision is logged on
        // change so field sessions can verify rule behavior.
        private void LogLegacyIntentChange(RuleIntent intent)
        {
            string screenId = intent.Kind == TargetKind.LegacyScreen ? intent.ScreenId : null;
            if (string.Equals(screenId, _lastLegacyLogged, StringComparison.Ordinal))
                return;
            _lastLegacyLogged = screenId;
            if (screenId != null)
                _log("DisplayRules: legacy surface wants screen '" + screenId
                    + "' (text write lands in a later phase)");
        }

        // An ITM rule targeting a legacy screen: the director already routed the
        // display to the legacy page; the screen text itself is the same later phase.
        private void LogLegacyScreenChange(string screenId)
        {
            if (string.Equals(screenId, _lastLegacyScreenLogged, StringComparison.Ordinal))
                return;
            _lastLegacyScreenLogged = screenId;
            if (screenId != null)
                _log("DisplayRules: ITM rule targets legacy screen '" + screenId
                    + "' — legacy page requested (text write lands in a later phase)");
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        private void IndexRules(List<DisplayRule> rules)
        {
            if (rules == null)
                return;
            foreach (var rule in rules)
                if (rule?.Id != null)
                    _rulesById[rule.Id] = rule;
        }
    }
}
