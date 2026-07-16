using System;
using System.Collections.Generic;
using FanaBridge.Display;
using FanaBridge.Protocol;
using GameReaderCommon;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>One rule's row in the UI snapshot: identity, display label, live status.</summary>
    public struct DisplayRuleRow
    {
        public DisplayRuleRow(string ruleId, string label, RuleStatus status, int? remainingMs)
        {
            RuleId = ruleId;
            Label = label;
            Status = status;
            RemainingMs = remainingMs;
        }

        public string RuleId { get; }

        /// <summary>Display text (<see cref="DisplayRuleFormatter.Label"/>).</summary>
        public string Label { get; }

        public RuleStatus Status { get; }

        /// <summary>Hold countdown at composition time (OnScreen + ForDuration only).</summary>
        public int? RemainingMs { get; }
    }

    /// <summary>
    /// An immutable cross-thread snapshot of the rule stack's live state, published
    /// through a volatile field on the device instance and polled by the (future) UI —
    /// the same hand-off pattern as the ITM status snapshot, kept separate from it.
    /// Recomposed only when something visible changed (activity version or a rule
    /// status), so idle frames publish nothing new.
    /// </summary>
    public sealed class DisplayRuleSnapshot
    {
        internal DisplayRuleSnapshot(string intentDescription,
            IReadOnlyList<DisplayRuleRow> itmRules, IReadOnlyList<DisplayRuleRow> legacyRules,
            IReadOnlyList<DisplayActivityEvent> activity, long activityVersion)
        {
            IntentDescription = intentDescription;
            ItmRules = itmRules;
            LegacyRules = legacyRules;
            Activity = activity;
            ActivityVersion = activityVersion;
        }

        /// <summary>What the ITM surface should be showing, in row language
        /// (page name, or "screen 'X'").</summary>
        public string IntentDescription { get; }

        /// <summary>ITM rules in priority order.</summary>
        public IReadOnlyList<DisplayRuleRow> ItmRules { get; }

        /// <summary>Legacy rules in priority order.</summary>
        public IReadOnlyList<DisplayRuleRow> LegacyRules { get; }

        /// <summary>Recent activity, oldest first (both engines merged by time).</summary>
        public IReadOnlyList<DisplayActivityEvent> Activity { get; }

        /// <summary>Combined engine activity version — a cheap "anything new?" check.</summary>
        public long ActivityVersion { get; }
    }

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
        private readonly DisplayRuleEngine _itmEngine;
        private readonly DisplayRuleEngine _legacyEngine;
        private readonly DisplayPageDirector _director;
        private readonly SimHubPropertySource _properties;
        private readonly DisplayActionHub _actions;
        private readonly Action<string> _log;

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
        private RuleStatus[] _lastItmStatuses;
        private RuleStatus[] _lastLegacyStatuses;

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

            // The device's page set gates rule availability (a Bentley has no Car
            // Settings page) and resolves the base-page fallback below.
            var pages = ItmDeviceCatalog.PagesFor(itmDeviceId);
            var available = new HashSet<ItmPage>();
            foreach (var page in pages)
                available.Add(page.Page);

            // Base page: the config's own when set, else the device's ITM default page
            // setting mapped from wire number to identity — one effective source of
            // truth (the UI phase merges the two settings surfaces). Read at build
            // time: a default-page change alone doesn't rebuild the stack, so it takes
            // effect on the next rebuild (config edit, reconnect, wheel change).
            ItmPage basePage = config.Itm != null && config.Itm.BasePageRaw != null
                ? config.Itm.BasePage
                : WireToPage(pages, defaultWirePage);

            // The base page as this device's wire number. The device instance feeds it
            // to the ITM driver as the effective default page while this stack is live,
            // so the lifecycle (cold bring-up target) and the engine (resting target)
            // agree on ONE base-page authority — otherwise a config base page and the
            // ItmDefaultPage setting would fight at every bring-up and game start.
            // A base page this device doesn't have keeps the caller's default wire.
            BaseWirePage = PageToWire(pages, basePage, defaultWirePage);

            _itmEngine = DisplayRuleEngine.ForItm(config.Itm?.Rules, basePage, available,
                nowMs, _log);
            _legacyEngine = DisplayRuleEngine.ForLegacy(config.Legacy?.Rules,
                config.Legacy?.BaseScreenId, nowMs, _log);
            _director = new DisplayPageDirector(control, itmDeviceId, nowMs, _log);
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
            if (!versionChanged && !itmChanged && !legacyChanged && !intentChanged)
                return null;
            _lastActivityVersion = version;
            _lastIntentDescription = intent;

            return new DisplayRuleSnapshot(
                intent,
                Rows(itm.RuleStates),
                Rows(legacy.RuleStates),
                MergeActivity(),
                version);
        }

        // Compares (and refreshes) the remembered statuses. RemainingMs ticks down
        // every frame and deliberately does NOT trigger recomposition — the snapshot
        // carries the countdown as of its composition.
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

        private void IndexRules(List<DisplayRule> rules)
        {
            if (rules == null)
                return;
            foreach (var rule in rules)
                if (rule?.Id != null)
                    _rulesById[rule.Id] = rule;
        }

        // The identity sitting at a wire page number, for the base-page fallback.
        // An out-of-table default (misconfigured setting) falls back to Lap Info.
        private static ItmPage WireToPage(IReadOnlyList<ItmPageInfo> pages, byte wirePage)
        {
            foreach (var page in pages)
                if (page.Number == wirePage)
                    return page.Page;
            return ItmPage.LapInfo;
        }

        // The wire number a page identity sits at on this device, for the driver-facing
        // base page. A page the device doesn't have keeps the caller's fallback wire.
        private static byte PageToWire(IReadOnlyList<ItmPageInfo> pages, ItmPage page, byte fallback)
        {
            foreach (var info in pages)
                if (info.Page == page)
                    return info.Number;
            return fallback;
        }
    }
}
