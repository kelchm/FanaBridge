using System;
using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Display.Legacy;
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
    /// Tick order (inside DataUpdate): the runtime scopes the shared
    /// <see cref="SimHubPropertySource"/> with <c>BeginFrame</c> <b>before</b> the
    /// driver's <see cref="ItmDisplayDriver.Update"/> (so field-mapping overrides
    /// resolve on the same frame), then this stack's Tick: action drain → engine
    /// Tick → director Tick. The director's manual-navigation result feeds the
    /// ENGINE'S NEXT tick — one frame of latency (~16 ms), harmless because the
    /// lifecycle already adopted the page.
    ///
    /// Legacy col01: when <see cref="LegacyRuleWrites"/> is on and the config's legacy
    /// world is non-empty, resolved screen text is handed to the injected segment sink
    /// (the device instance's <see cref="LegacyDisplayDriver"/> — the stack never
    /// constructs drivers or encoders). Flag-off restores the exact log-only message.
    /// An ITM-rule legacy-screen target still routes the display onto the legacy page
    /// via the director.
    /// </summary>
    public class DisplayRuleStack
    {
        /// <summary>Countdown recompose floor: while the on-screen winner carries a timed
        /// hold, the snapshot refreshes this often so a UI countdown can tick — the only
        /// visible change with no status/activity/intent edge. Bounded churn: outside a
        /// timed hold the change gates alone decide.</summary>
        internal const int CountdownRecomposeMs = 250;

        /// <summary>
        /// When true (default), a non-empty legacy world resolves intents to segment
        /// frames and feeds them through <see cref="TryWriteLegacySegments"/>. When
        /// false, restores the exact pre-7b log-only behavior (the
        /// "text write lands in a later phase" message). Test-settable.
        /// </summary>
        internal static bool LegacyRuleWrites = true;

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

        // Legacy screen library (id → screen) for rule-path resolution.
        private readonly Dictionary<string, LegacyScreen> _screensById =
            new Dictionary<string, LegacyScreen>(StringComparer.Ordinal);

        // True when this config has any legacy screens or rules — the gate that
        // activates the rule-driven col01 path (empty world = mode-based fallback).
        private readonly bool _hasLegacyWorld;

        // Manual navigation detected by the director last tick, consumed by the ITM
        // engine this tick (the documented one-frame latency).
        private ManualNavigation? _pendingManual;

        private readonly List<string> _actionBuf = new List<string>();

        // Change detection for logging and snapshot recomposition.
        private string _lastLegacyLogged;
        private string _lastLegacyScreenLogged;
        private long _lastActivityVersion = -1;
        private string _lastIntentDescription;
        private readonly string _basePageName;
        private RuleStatus[] _lastItmStatuses;
        private RuleStatus[] _lastLegacyStatuses;
        private long _lastComposedAt = long.MinValue / 2;

        // Last-resolved legacy frame (for change-gated snapshot + write logging).
        private byte _lastSeg0, _lastSeg1, _lastSeg2;
        private bool _hasLastSegs;
        private string _lastLegacyScreenName;
        // This-tick resolution (fed into MaybeCompose).
        private byte _tickSeg0, _tickSeg1, _tickSeg2;
        private bool _tickHasSegs;
        private string _tickLegacyScreenName;

        // GearAndSpeed overlay state (clock-injected; matches formatter contract).
        private int _overlayGear = int.MinValue;
        private long _overlayGearAtMs;

        /// <summary>Production wiring: the director talks to the driver's lifecycle
        /// through <see cref="ItmLifecyclePageControl"/>. The shared
        /// <paramref name="properties"/> is the runtime's SimHubPropertySource (also
        /// fed to the ITM mapper for field overrides); when null a private source is
        /// created (legacy / test convenience).</summary>
        public DisplayRuleStack(DisplayCustomizationConfig config, ItmDisplayDriver driver,
            byte itmDeviceId, byte defaultWirePage, Action<string> log = null,
            SimHubPropertySource properties = null)
            : this(config, new ItmLifecyclePageControl(driver.Lifecycle), itmDeviceId,
                defaultWirePage, log, nowMs: null, rawLookup: null, properties: properties)
        {
            Driver = driver;
        }

        /// <summary>Test wiring: a fake <see cref="IItmPageControl"/>, injected clock, and an
        /// optional raw named-property lookup (so a test can drive — and count — the property
        /// reads the LiveText composition reuses). Production passes the shared
        /// <see cref="SimHubPropertySource"/>; when null a private source is built and named
        /// lookups resolve through the frame's <c>PluginManager</c>.</summary>
        internal DisplayRuleStack(DisplayCustomizationConfig config, IItmPageControl control,
            byte itmDeviceId, byte defaultWirePage, Action<string> log, Func<long> nowMs,
            Func<string, object> rawLookup = null, SimHubPropertySource properties = null)
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
            // Shared with the ITM mapper (field overrides) when the runtime supplies one;
            // otherwise a private source so stack-level tests stay self-contained.
            _properties = properties ?? new SimHubPropertySource(_log, rawLookup);
            _actions = new DisplayActionHub(config, _log);

            IndexRules(config.Itm?.Rules);
            IndexRules(config.Legacy?.Rules);
            IndexScreens(config.Legacy?.Screens);
            _hasLegacyWorld = HasLegacyWorld(config);
        }

        /// <summary>
        /// True when the document's legacy surface has any screens or rules — the
        /// non-empty-world gate for rule-driven col01. BaseScreenId alone does not
        /// count (nothing to resolve without a screen library entry).
        /// </summary>
        internal static bool HasLegacyWorld(DisplayCustomizationConfig config)
        {
            var leg = config?.Legacy;
            if (leg == null)
                return false;
            return (leg.Rules != null && leg.Rules.Count > 0)
                || (leg.Screens != null && leg.Screens.Count > 0);
        }

        /// <summary>
        /// Segment sink for the rule-driven col01 path. Threaded from the device
        /// instance's <see cref="LegacyDisplayDriver.TryShowSegments"/> — the stack
        /// never constructs a driver or encoder. Null = resolve for snapshot only
        /// (no wire writes). Returns false when a send was attempted and declined.
        /// </summary>
        internal Func<byte, byte, byte, bool> TryWriteLegacySegments { get; set; }

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
        /// The shared property source (also fed to the ITM mapper for field overrides).
        /// </summary>
        internal SimHubPropertySource Properties => _properties;

        /// <summary>
        /// Runs one frame: drains action fires, ticks both engines, and lets the director
        /// reconcile the ITM intent with the lifecycle. Call once per DataUpdate, after
        /// the ITM driver's Update (all ITM mutation stays on the DataUpdate thread).
        /// <see cref="SimHubPropertySource.BeginFrame"/> is owned by the runtime and runs
        /// once before the driver Update so field-mapping overrides and rules share the
        /// same framed reads; this Tick re-scopes only when the runtime has not already
        /// (standalone tests call Tick without a prior BeginFrame).
        /// Returns a fresh snapshot when the visible state changed, else null (the caller
        /// keeps publishing the previous one).
        /// </summary>
        public DisplayRuleSnapshot Tick(PluginManager pm, GameData data)
        {
            // Idempotent re-scope: when the runtime already BeginFrame'd for the mapper
            // this is a no-op cost (memo clear + same frame data). Stack-only tests rely
            // on this path for their sole BeginFrame.
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

            // Rule-path col01: flag-on + non-empty world → resolve + sink write
            // (telemetryLive-gated). Flag-off → exact log-only message.
            if (LegacyRuleWrites && _hasLegacyWorld)
                DriveLegacyCol01(legacy.Intent, inGame, data);
            else
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
            // The described intent is a gate of its own: a cycle-family target (Alternate
            // or Cycle) flips the emitted intent every period with no activity event and
            // no status change, and the published snapshot must follow what the display
            // actually shows.
            string intent = DescribeIntent(itm.Intent);
            bool intentChanged = !string.Equals(intent, _lastIntentDescription, StringComparison.Ordinal);
            // Last-resolved legacy segments / screen name — effect frames recompose when
            // the visible window changes (same change-gate style as the intent description).
            bool legacyDisplayChanged = LegacyDisplayChanged();
            long now = _now();
            if (!versionChanged && !itmChanged && !legacyChanged && !intentChanged
                && !legacyDisplayChanged)
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
            LatchLegacyDisplay();

            return new DisplayRuleSnapshot(
                intent,
                _basePageName,
                Rows(itm.RuleStates),
                Rows(legacy.RuleStates),
                MergeActivity(),
                version,
                now,
                DateTime.UtcNow,
                _tickHasSegs
                    ? new byte[] { _tickSeg0, _tickSeg1, _tickSeg2 }
                    : null,
                _tickLegacyScreenName);
        }

        private bool LegacyDisplayChanged()
        {
            if (_tickHasSegs != _hasLastSegs)
                return true;
            if (!_tickHasSegs)
                return !string.Equals(_tickLegacyScreenName, _lastLegacyScreenName,
                    StringComparison.Ordinal);
            return _tickSeg0 != _lastSeg0 || _tickSeg1 != _lastSeg1 || _tickSeg2 != _lastSeg2
                || !string.Equals(_tickLegacyScreenName, _lastLegacyScreenName,
                    StringComparison.Ordinal);
        }

        private void LatchLegacyDisplay()
        {
            _hasLastSegs = _tickHasSegs;
            _lastSeg0 = _tickSeg0;
            _lastSeg1 = _tickSeg1;
            _lastSeg2 = _tickSeg2;
            _lastLegacyScreenName = _tickLegacyScreenName;
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
                    state.Status, state.RemainingMs,
                    ComposeLiveText(rule));
            }
            return rows;
        }

        // The rule condition's current source-property value, for the Overview's "Now"
        // column, read through the SAME property source the engine ticked this frame:
        // named lookups are memoized, so a rule the engine already evaluated costs no extra
        // fetch, and a rule it short-circuited resolves once and memoizes for the frame. The
        // boolean kinds (isTrue/isFalse — and a mapped control) render "on"/"off"; every other
        // readable kind renders the invariant round-trip of the numeric value; an event kind
        // (no readable value) and an unreadable property both render "—".
        private string ComposeLiveText(DisplayRule rule)
        {
            var src = rule?.When?.Source;
            if (src == null || string.IsNullOrEmpty(src.Name))
                return null;
            var kind = rule.When.Kind;
            if (kind == ConditionKind.IsTrue || kind == ConditionKind.IsFalse)
                return _properties.TryGetBool(src, out bool b) ? (b ? "on" : "off") : "—";
            if (kind == ConditionKind.ActionTriggered)
                return "—";   // an action fires; it carries no readable value
            return _properties.TryGetNumber(src, out double n)
                ? n.ToString(CultureInfo.InvariantCulture)
                : "—";
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

        // ── Legacy col01 resolve + write ─────────────────────────────────

        /// <summary>
        /// Resolves the legacy intent to a 3-byte frame and, when telemetry is live,
        /// hands it to the segment sink. Idle frames resolve nothing to the wire
        /// (exit blank-once stays on the driver via the device instance).
        /// </summary>
        private void DriveLegacyCol01(RuleIntent intent, bool telemetryLive, GameData data)
        {
            // Default: no segment payload this tick (idle / not driving).
            _tickHasSegs = false;
            _tickLegacyScreenName = null;

            if (!telemetryLive)
                return;

            string screenId = intent.Kind == TargetKind.LegacyScreen ? intent.ScreenId : null;
            LegacyScreen screen = null;
            if (!string.IsNullOrEmpty(screenId))
                _screensById.TryGetValue(screenId, out screen);

            byte s0 = SevenSegment.Blank, s1 = SevenSegment.Blank, s2 = SevenSegment.Blank;
            string screenName = null;

            if (screen != null && screen.ContentKind != LegacyContentKind.Unknown)
            {
                string text = FormatScreen(screen, data);
                if (text != null)
                {
                    byte[] frame = LegacyEffectClock.Apply(text, screen.Effect, _now());
                    s0 = frame[0];
                    s1 = frame[1];
                    s2 = frame[2];
                    screenName = !string.IsNullOrEmpty(screen.Name) ? screen.Name : screen.Id;
                }
                else
                {
                    // Unreadable dynamic source → blank (same degrade as a missing screen).
                    screenName = !string.IsNullOrEmpty(screen.Name) ? screen.Name : screen.Id;
                }
            }
            // else: no screen / base null / unknown kind → blank-once via change-gated blanks

            _tickSeg0 = s0;
            _tickSeg1 = s1;
            _tickSeg2 = s2;
            _tickHasSegs = true;
            _tickLegacyScreenName = screenName;

            // Sink write (declined-send retry lives inside the driver).
            TryWriteLegacySegments?.Invoke(s0, s1, s2);

            LogLegacyWriteTransition(screenId, screenName);
        }

        private string FormatScreen(LegacyScreen screen, GameData data)
        {
            StatusDataBase d = data != null ? data.NewData : null;
            switch (screen.ContentKind)
            {
                case LegacyContentKind.Text:
                case LegacyContentKind.Message:
                    return LegacyValueFormatter.FormatText(screen.Text);

                case LegacyContentKind.Speed:
                    return d == null ? null : LegacyValueFormatter.FormatSpeed(d.SpeedLocal);

                case LegacyContentKind.Gear:
                    return d == null ? null : LegacyValueFormatter.FormatGear(d.Gear);

                case LegacyContentKind.GearAndSpeed:
                    if (d == null)
                        return null;
                    int gear = LegacyValueFormatter.ParseGear(d.Gear);
                    long now = _now();
                    if (gear != _overlayGear)
                    {
                        _overlayGear = gear;
                        _overlayGearAtMs = now;
                    }
                    return LegacyValueFormatter.FormatGearAndSpeed(
                        d.Gear, d.SpeedLocal, _overlayGearAtMs, now);

                case LegacyContentKind.GearBrackets:
                    return d == null
                        ? null
                        : LegacyValueFormatter.FormatGearBrackets(
                            d.Gear, d.Rpms, d.CarSettings_RPMRedLineReached);

                case LegacyContentKind.Rpm:
                    return d == null ? null : LegacyValueFormatter.FormatRpm(d.Rpms);

                case LegacyContentKind.Position:
                    return d == null ? null : LegacyValueFormatter.FormatPosition(d.Position);

                case LegacyContentKind.Fuel:
                    return d == null ? null : LegacyValueFormatter.FormatFuel(d.Fuel);

                case LegacyContentKind.Property:
                    return LegacyValueFormatter.FormatProperty(_properties, screen.Source);

                default:
                    return null;
            }
        }

        // Flag-on: log write transitions (change-gated on screen id).
        private void LogLegacyWriteTransition(string screenId, string screenName)
        {
            if (string.Equals(screenId, _lastLegacyLogged, StringComparison.Ordinal))
                return;
            _lastLegacyLogged = screenId;
            if (screenId != null)
                _log("DisplayRules: legacy surface showing screen '" + screenId
                    + "'" + (screenName != null && screenName != screenId
                        ? " (" + screenName + ")" : ""));
            else
                _log("DisplayRules: legacy surface blank");
        }

        // Flag-off: exact pre-7b log-only message (byte-identical text).
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
        // display to the legacy page; the screen text is driven by DriveLegacyCol01
        // when the legacy world is non-empty (flag-on), else still log-only.
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

        private void IndexScreens(List<LegacyScreen> screens)
        {
            if (screens == null)
                return;
            foreach (var screen in screens)
                if (screen?.Id != null)
                    _screensById[screen.Id] = screen;
        }
    }
}
