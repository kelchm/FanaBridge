using System;
using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Composition
{
    /// <summary>
    /// v2 display composition orchestrator (E8 round 1). One public
    /// <see cref="Tick"/> per frame in contract §6.2 order-law order:
    /// evaluator → E4 SeatArbiter → E6 WheelScreenArbiter → E5 FrameComposer →
    /// writes → director. Manual/adopt edges from the director feed the
    /// <b>next</b> tick only (adopt-edge law; e8-seam-adjudication correction #1).
    /// Field plans apply at tick end for the next frame (lag-1; correction #2).
    /// Never calls BeginFrame (correction #3). Not wired into the live path this round.
    /// </summary>
    public sealed class DisplayCompositionV2
    {
        private readonly DisplayConfigV2 _config;
        private readonly WheelCatalog _catalog;
        private readonly Func<long> _now;
        private readonly Action<string> _log;
        private readonly IPropertyReader _properties;
        private readonly string _deviceKey;

        private readonly SeatArbiter _seat;
        private readonly WheelScreenArbiter _wheelScreen;
        private readonly FrameComposer _frame;
        private readonly DisplayPageDirector _director;
        private readonly CompiledWalk _walk;
        private readonly IReadOnlyList<string> _wheelScreenRuleIds;
        private readonly List<CarrierEntry> _carriers;
        private readonly ItmPageTable _pageTable;
        private readonly ConditionParamPlan _conditionPlan;
        private readonly string _landingHostedPageId;
        private readonly string _inSessionDestinationId;

        // ── Cross-tick edges (order law: press / adopt feed NEXT tick) ──
        private SeatManualInput? _pendingManual;
        private bool _pendingPressLastTick;
        private bool? _lastSendAccepted;
        private WheelScreenLatchSet _wsLatches = WheelScreenLatchSet.Empty;

        // ── Last-tick diagnostics / test seams ─────────────────────────
        private DirectorIntent _lastDirectorIntent;
        private FrameComposerTickInput _lastFrameInput;
        private IReadOnlyList<FieldRegionPlan> _lastFieldPlans =
            Array.Empty<FieldRegionPlan>();
        private SeatManualInput? _lastSeatManualInput;
        private bool _lastSeatPressThisTick;

        /// <summary>
        /// Builds a composition over a NORMALIZED <see cref="DisplayConfigV2"/>
        /// (<see cref="DisplayConfigV2Validator.Normalize"/> already applied, catalog
        /// present when available). Clock is mandatory — never a private Stopwatch.
        /// </summary>
        public DisplayCompositionV2(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            IItmPageControl pageControl,
            byte itmDeviceId,
            Func<long> nowMs,
            Action<string> log,
            IPropertyReader properties,
            DisplayCompositionV2Options options = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _catalog = catalog; // may be null (empty capability envelope)
            if (pageControl == null)
                throw new ArgumentNullException(nameof(pageControl));
            _now = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
            _log = log ?? (_ => { });
            _properties = properties ?? throw new ArgumentNullException(nameof(properties));

            options = options ?? new DisplayCompositionV2Options();
            _deviceKey = options.DeviceKey ?? "";

            var capabilities = FieldCapability.FromCatalog(_catalog);
            var primaryHosts = FieldCapability.PrimaryHostMapFromCapabilities(capabilities);
            var screenCommands = _catalog?.ScreenCommands;

            _seat = new SeatArbiter(_config, new SeatArbiterOptions
            {
                PrimaryHostByParam = primaryHosts,
                DeviceKey = _deviceKey,
                Warn = _log,
            });
            _wheelScreen = new WheelScreenArbiter(_config, new WheelScreenArbiterOptions
            {
                ScreenCommands = screenCommands,
                DeviceKey = _deviceKey,
                Warn = _log,
            });
            _frame = new FrameComposer(_config, new FrameComposerOptions
            {
                Capabilities = capabilities,
                PrimaryHostByParam = primaryHosts,
                DeviceKey = _deviceKey,
                Warn = _log,
            });

            _director = new DisplayPageDirector(pageControl, itmDeviceId, _now, _log);
            _director.RejectUncommandedChanges = _config.Settings != null
                && _config.Settings.RejectUncommandedChanges;

            _walk = WalkCompiler.Compile(_config, _catalog);
            _wheelScreenRuleIds = CollectWheelScreenRuleIds(_config);
            _carriers = BuildCarrierTable(_config);
            _pageTable = ItmPageTable.ForDevice(itmDeviceId);
            _conditionPlan = ConditionParamPlanner.Plan(
                _config, options.HasEncoder, _log);

            var rest = _config.Priority?.Rest;
            _inSessionDestinationId = DestinationIds.FromPageRef(rest?.InSessionPage);
            _landingHostedPageId = ResolveHostedId(DestinationIds.FromPageRef(rest?.LandingPage))
                ?? ResolveHostedId(_inSessionDestinationId);

            BaseWirePage = ResolveBaseWirePage(options.DefaultWirePage);
            Config = _config;
            ConditionPlan = _conditionPlan;
        }

        /// <summary>Rebuild identity — the ReferenceEquals gate at runtime wiring.</summary>
        public DisplayConfigV2 Config { get; }

        /// <summary>
        /// Effective base wire page (rest in-session when cataloged, else device default).
        /// Feeds SetPagePolicy when runtime-wired.
        /// </summary>
        public byte BaseWirePage { get; }

        /// <summary>Host-local condition-param plan (once per rebuild).</summary>
        public ConditionParamPlan ConditionPlan { get; }

        /// <summary>
        /// Segment sink — identical shape to DisplayRuleStack. Null = resolve only.
        /// Returns false when a send was attempted and declined.
        /// </summary>
        public Func<byte, byte, byte, bool> TryWriteLegacySegments { get; set; }

        /// <summary>
        /// Special-screen sink: pattern byte → accepted. Null sink is NOT accepted
        /// (v9 parity — special latch stays open and retries).
        /// </summary>
        public Func<byte, bool> TryShowSpecialScreen { get; set; }

        /// <summary>Special-command release (arm exit-blank + invalidate segment gates).</summary>
        public Action OnSpecialReleased { get; set; }

        /// <summary>
        /// Mapper plan application seam (G2 <c>ConfigureFromPlans</c>). Invoked at tick
        /// END with this tick's field plans — effective next frame (lag-1 design law).
        /// Null = plans are produced but not applied (tests without a mapper).
        /// </summary>
        public Action<IReadOnlyList<FieldRegionPlan>, IPropertyReader> ApplyFieldPlans { get; set; }

        /// <summary>Last DirectorIntent handed to the page director (test / diagnostics).</summary>
        public DirectorIntent LastDirectorIntent => _lastDirectorIntent;

        /// <summary>Last frame-composer input (ORDER-LAW probe / diagnostics).</summary>
        public FrameComposerTickInput LastFrameInput => _lastFrameInput;

        /// <summary>Last field plans produced this tick (before / at ApplyFieldPlans).</summary>
        public IReadOnlyList<FieldRegionPlan> LastFieldPlans => _lastFieldPlans;

        /// <summary>
        /// Seat manual input actually fed this tick (previous director adopt edge).
        /// Null when no pending edge — adopt-edge probe surface.
        /// </summary>
        public SeatManualInput? LastSeatManualInput => _lastSeatManualInput;

        /// <summary>
        /// Wheel-screen press flag fed this tick (previous director Manual/Adopt edge).
        /// </summary>
        public bool LastSeatPressThisTick => _lastSeatPressThisTick;

        /// <summary>
        /// Test-only: transform the E6 result after the arbiter tick and before E5 +
        /// the write gate (RISK-2 same-tick law probe). Null in production.
        /// </summary>
        public Func<WheelScreenArbiterTickResult, WheelScreenArbiterTickResult>
            WheelScreenResultHook { get; set; }

        /// <summary>
        /// One frame in strict order-law order. Returns the merged
        /// <see cref="ComposedResolutionRecord"/> (contract §6) including the director
        /// device block.
        /// </summary>
        public ComposedResolutionRecord Tick(DisplayCompositionV2TickInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            long now = _now();
            bool inGame = input.InGame;
            bool gameChanged = input.GameChanged;
            string gameId = input.GameId;

            // ── 1. Evaluate every carrier (document order, sorted by id for determinism) ─
            var content = input.Content ?? new SegmentContentContext();
            var props = content.Properties ?? _properties;
            content.Properties = props;
            content.InGame = inGame;

            var tickIn = new CarrierTickInput
            {
                NowMs = now,
                InGame = inGame,
                Properties = props,
                GameId = gameId,
                GameChanged = gameChanged,
            };

            var snapshots = new List<CarrierTickSnapshot>(_carriers.Count);
            for (int i = 0; i < _carriers.Count; i++)
            {
                var entry = _carriers[i];
                CarrierEvaluator.Evaluate(entry.Spec, entry.Runtime, tickIn, warnMissing: null);
                snapshots.Add(CarrierTickSnapshot.From(entry.Spec, entry.Runtime, now));
            }

            // ── 2. E4 seat (manual = PREVIOUS tick's director adopt edge only) ─
            SeatManualInput? manual = _pendingManual;
            _pendingManual = null;
            _lastSeatManualInput = manual;

            var seatResult = _seat.Tick(new SeatArbiterTickInput
            {
                NowMs = now,
                InGame = inGame,
                GameChanged = gameChanged,
                CarrierSnapshots = snapshots,
                Manual = manual,
                CompiledWalk = _walk.DestinationIds,
            });

            // ── 3. Wheel-screen dismissal latch (press from previous director edge) ─
            bool pressThisTick = _pendingPressLastTick;
            _lastSeatPressThisTick = pressThisTick;
            _wsLatches = WheelScreenDismissal.Apply(
                pressThisTick,
                snapshots,
                _wheelScreenRuleIds,
                _wsLatches);

            // ── 4. E6 wheel-screen ───────────────────────────────────────────
            var wsResult = _wheelScreen.Tick(new WheelScreenArbiterTickInput
            {
                NowMs = now,
                InGame = inGame,
                CarrierSnapshots = snapshots,
                DismissedCarrierIds = _wsLatches.Ids,
                PreviousSendAccepted = _lastSendAccepted,
            });

            // RISK-2 probe hook: mutation between E6 and E5 must be same-tick visible.
            var hook = WheelScreenResultHook;
            if (hook != null)
                wsResult = hook(wsResult) ?? wsResult;

            // ── 5. E5 frame (hold + reclaim from THIS tick's E6 result) ──────
            string displayed = seatResult.Intent?.EffectivePageDestinationId;
            string segmentHosted = ResolveSegmentHostedPageId(displayed);

            var frameInput = new FrameComposerTickInput
            {
                NowMs = now,
                SegmentHostedPageId = segmentHosted,
                DisplayedDestinationId = displayed,
                SegmentSurfaceHeldByWheelScreen = wsResult.SurfaceHeld,
                ReclaimEdge = wsResult.ReleaseEdge,
                CarrierSnapshots = snapshots,
                DismissedCarrierIds = seatResult.DismissedCarrierIds,
                Content = content,
            };
            _lastFrameInput = frameInput;

            var frameResult = _frame.Tick(frameInput);

            // ── 6. WRITES (special first, then segments — v9 exclusivity order) ─
            if (wsResult.SendRequested && wsResult.SendPattern.HasValue)
            {
                var sink = TryShowSpecialScreen;
                _lastSendAccepted = sink != null && sink(wsResult.SendPattern.Value);
            }
            else if (wsResult.ReleaseEdge)
            {
                OnSpecialReleased?.Invoke();
                _lastSendAccepted = null;
            }
            else
            {
                _lastSendAccepted = null;
            }

            // col01 exclusivity (contract §6.2): no segment write while wheel-screen holds,
            // except the reclaim edge (SegmentFrameWritable / ReclaimFrame from E5).
            if (frameResult.SegmentFrameWritable || frameResult.ReclaimFrame)
            {
                var segs = frameResult.SegmentFrame;
                if (segs != null && segs.Length >= 3)
                {
                    var write = TryWriteLegacySegments;
                    write?.Invoke(segs[0], segs[1], segs[2]);
                }
            }

            // Field plans at tick END → next frame (lag-1; adjudication correction #2).
            var plans = frameResult.FieldPlans ?? Array.Empty<FieldRegionPlan>();
            _lastFieldPlans = plans;
            ApplyFieldPlans?.Invoke(plans, props);

            // ── 7. Director (after writes; Manual/Adopt feed NEXT tick) ──────
            var directorIntent = ToDirectorIntent(seatResult.Intent, wsResult);
            _lastDirectorIntent = directorIntent;
            var directed = _director.Tick(directorIntent);

            _pendingManual = MapDirectorManual(directed);
            _pendingPressLastTick = directed.Manual.HasValue || directed.AdoptedThisTick;

            // ── 8. Merge + stamp device block (contract §6.1) ────────────────
            var merged = ComposedResolutionMerger.Merge(
                seatResult.Resolution,
                frameResult.Resolution,
                wsResult.Resolution,
                onPresenceConflict: msg => _log(msg));

            return new ComposedResolutionRecord(
                merged.TickMs,
                merged.DeviceKey,
                merged.SurfaceWinners,
                merged.CarrierStatuses,
                merged.CarrierSnapshots,
                directed.PageKnowledge,
                directed.RevertedThisTick,
                directed.AdoptWarnedThisTick);
        }

        // ── Director intent (v9 ToDirectorIntent shape reference) ─────────

        /// <summary>
        /// Maps seat + wheel-screen outcomes onto <see cref="DirectorIntent"/> using the
        /// same kind split as <c>DisplayRuleStack.ToDirectorIntent</c> (Page /
        /// SegmentScreen / Special). Shape reference only — does not call the v9 helper.
        /// </summary>
        internal static DirectorIntent ToDirectorIntent(
            SeatDisplayIntent seat,
            WheelScreenArbiterTickResult ws)
        {
            // Special holds col01 — director must not page-navigate (v9 Special path).
            if (ws != null
                && ws.SurfaceHeld
                && ws.Intent != null
                && ws.Intent.Kind == WheelScreenOutcomeKind.Screen)
            {
                return new DirectorIntent(
                    DirectorIntentKind.Special,
                    page: null,
                    screenId: null,
                    sourceRuleId: ws.Intent.WinnerCarrierId);
            }

            if (seat == null)
            {
                return new DirectorIntent(
                    DirectorIntentKind.Page, page: null, screenId: null, sourceRuleId: null);
            }

            string effective = seat.EffectivePageDestinationId;
            string sourceRuleId = string.Equals(
                    seat.WinnerCarrierId, SeatArbiter.RestCarrierId, StringComparison.Ordinal)
                || string.Equals(seat.WinnerCarrierId, SeatArbiter.ManualCarrierId, StringComparison.Ordinal)
                ? null
                : seat.WinnerCarrierId;

            if (TryHostedId(effective, out string hostedId))
            {
                return new DirectorIntent(
                    DirectorIntentKind.SegmentScreen,
                    page: null,
                    screenId: hostedId,
                    sourceRuleId: sourceRuleId);
            }

            if (TryItmPage(effective, out ItmPage itmPage))
            {
                return new DirectorIntent(
                    DirectorIntentKind.Page,
                    page: itmPage,
                    screenId: null,
                    sourceRuleId: sourceRuleId);
            }

            // Rest / unknown / uncataloged: no concrete page request (v9 resting).
            return new DirectorIntent(
                DirectorIntentKind.Page,
                page: null,
                screenId: null,
                sourceRuleId: sourceRuleId);
        }

        private static SeatManualInput? MapDirectorManual(DirectorTickResult directed)
        {
            if (!directed.Manual.HasValue)
                return null;

            var nav = directed.Manual.Value;
            if (nav.Page.HasValue)
            {
                string dest = CatalogPageIdAdapter.ToDestinationId(nav.Page.Value);
                if (dest != null)
                    return SeatManualInput.Navigate(dest);
                return SeatManualInput.NavigateUnknownPage();
            }

            // Uncataloged adopt (ManualNavigation(null)).
            return SeatManualInput.NavigateUnknownPage();
        }

        private string ResolveSegmentHostedPageId(string displayedDestinationId)
        {
            if (TryHostedId(displayedDestinationId, out string hosted))
                return hosted;
            // Buffer continuity while an ITM page owns the display: land on remembered
            // hosted / landing page so col01 stays a continuous stream.
            return _landingHostedPageId;
        }

        private byte ResolveBaseWirePage(byte defaultWirePage)
        {
            ItmPage? configured = null;
            if (TryItmPage(_inSessionDestinationId, out ItmPage page))
                configured = page;
            var resolution = _pageTable.ResolveBase(configured, defaultWirePage);
            return resolution.Wire;
        }

        private static string ResolveHostedId(string destinationId)
            => TryHostedId(destinationId, out string id) ? id : null;

        private static bool TryHostedId(string destinationId, out string hostedId)
        {
            hostedId = null;
            if (destinationId == null
                || !destinationId.StartsWith("hosted:", StringComparison.Ordinal))
                return false;
            hostedId = destinationId.Substring("hosted:".Length);
            return hostedId.Length > 0;
        }

        private static bool TryItmPage(string destinationId, out ItmPage page)
        {
            page = default;
            if (destinationId == null
                || !destinationId.StartsWith("itm:", StringComparison.Ordinal))
                return false;
            string catalogId = destinationId.Substring("itm:".Length);
            return TryCatalogIdToItmPage(catalogId, out page);
        }

        private static bool TryCatalogIdToItmPage(string catalogPageId, out ItmPage page)
        {
            // Inverse of CatalogPageIdAdapter.FromItmPage (explicit pair table).
            page = default;
            if (string.IsNullOrEmpty(catalogPageId))
                return false;
            if (string.Equals(catalogPageId, "lapInfo", StringComparison.Ordinal))
            {
                page = ItmPage.LapInfo;
                return true;
            }
            if (string.Equals(catalogPageId, "fuelErsDrs", StringComparison.Ordinal))
            {
                page = ItmPage.FuelErsDrs;
                return true;
            }
            if (string.Equals(catalogPageId, "carSettings", StringComparison.Ordinal))
            {
                page = ItmPage.CarSettings;
                return true;
            }
            if (string.Equals(catalogPageId, "lapTimes", StringComparison.Ordinal))
            {
                page = ItmPage.LapTimes;
                return true;
            }
            if (string.Equals(catalogPageId, "tyreTemps", StringComparison.Ordinal))
            {
                page = ItmPage.TyreTemps;
                return true;
            }
            if (string.Equals(catalogPageId, "legacy", StringComparison.Ordinal))
            {
                page = ItmPage.Legacy;
                return true;
            }
            return false;
        }

        private static IReadOnlyList<string> CollectWheelScreenRuleIds(DisplayConfigV2 config)
        {
            var list = new List<string>();
            var rules = config.WheelScreen?.Rules;
            if (rules == null)
                return list;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (r != null && r.EffectivelyEnabled && r.Id != null)
                    list.Add(r.Id);
            }
            // Deterministic order for any ordered emission that keys off this list.
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static List<CarrierEntry> BuildCarrierTable(DisplayConfigV2 config)
        {
            // Collect then sort by carrier id so evaluation order is never Dictionary-order.
            var byId = new Dictionary<string, CarrierEntry>(StringComparer.Ordinal);

            void Add(string id, Condition condition, Lifetime lifetime, RunsWhen runs,
                string owningFieldParamId = null)
            {
                if (string.IsNullOrEmpty(id) || byId.ContainsKey(id))
                    return;
                var spec = CarrierSpec.FromV2(id, condition, lifetime, runs, owningFieldParamId);
                byId[id] = new CarrierEntry
                {
                    Spec = spec,
                    Runtime = new CarrierRuntime(),
                };
            }

            // Priority summons (document / effective-row order, then sorted at emit).
            var rows = config.Priority?.EffectiveRows;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row?.Summons == null)
                        continue;
                    for (int j = 0; j < row.Summons.Count; j++)
                    {
                        var s = row.Summons[j];
                        if (s == null || !s.EffectivelyEnabled)
                            continue;
                        Add(s.Id, s.Condition, s.Lifetime, s.Runs);
                    }
                }
            }

            // Hosted page layers.
            if (config.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var page = config.Pages[i];
                    if (page == null || page.Kind != PageEntryKind.HostedPage || page.Layers == null)
                        continue;
                    for (int j = 0; j < page.Layers.Count; j++)
                    {
                        var layer = page.Layers[j];
                        if (layer == null || !layer.EffectivelyEnabled)
                            continue;
                        Add(layer.Id, layer.Condition, layer.Lifetime, layer.Runs);
                    }
                }
            }

            // Field overrides — sorted param id for determinism.
            if (config.Fields != null)
            {
                var keys = new List<ushort>(config.Fields.Keys);
                keys.Sort();
                for (int i = 0; i < keys.Count; i++)
                {
                    ushort paramId = keys[i];
                    if (!config.Fields.TryGetValue(paramId, out var entry) || entry?.Overrides == null)
                        continue;
                    string owning = paramId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    for (int j = 0; j < entry.Overrides.Count; j++)
                    {
                        var ov = entry.Overrides[j];
                        if (ov == null || !ov.EffectivelyEnabled)
                            continue;
                        Add(ov.Id, ov.Condition, ov.Lifetime, ov.Runs, owning);
                    }
                }
            }

            // Wheel-screen rules.
            if (config.WheelScreen?.Rules != null)
            {
                for (int i = 0; i < config.WheelScreen.Rules.Count; i++)
                {
                    var r = config.WheelScreen.Rules[i];
                    if (r == null || !r.EffectivelyEnabled)
                        continue;
                    Add(r.Id, r.Condition, r.Lifetime, r.Runs);
                }
            }

            var list = new List<CarrierEntry>(byId.Count);
            var ids = new List<string>(byId.Keys);
            ids.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
                list.Add(byId[ids[i]]);
            return list;
        }

        private sealed class CarrierEntry
        {
            public CarrierSpec Spec;
            public CarrierRuntime Runtime;
        }
    }
}
