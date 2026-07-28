using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Pure seat-surface arbiter (phase E4). Picks the display-plane winner from the
    /// priority ladder (seats + satellites + manual + rest), owns destination identity,
    /// cycle free-run anchor/cursor, dismissal latches, dwell floors, and the derived
    /// flagged-children aggregate. Not wired to director/runtime — tick tests only.
    ///
    /// Laws carried here (display-model-v2 + replan E4 + contract):
    /// - One winner; array order = rank; rest is the floor.
    /// - D8 destination-scoped latches (including Derived aggregates); re-arm on FreshFire
    ///   (D8 letter / open mid-window raw-carrier path left unasserted).
    /// - D9 same-destination handoff never repaints (bypasses dwell entirely).
    /// - Cycle free-runs; RESUME keeps cursor (Cycle_FreeRun_ResumeKeepsCursor).
    /// - Supersede retired: displaced untilDismissed RESUMES.
    /// - Manual target RESETS on GameChanged (ruling 7) — immediate, dwell-stamped.
    /// - Dwell 500/250; first selection does not start the dwell clock; manual bypasses.
    /// - Activation is evaluator-owned: this class never writes Active/Superseded.
    /// - returnToRestAfterMs = X ms since last press (no pause/restart on interruption).
    /// - Cross-surface: suppress-the-summon-only; expose DismissedCarrierIds for E5.
    /// </summary>
    public sealed class SeatArbiter
    {
        /// <summary>Minimum residency before an emitted winner may change (v9 carry).</summary>
        public const int MinDwellMs = 500;

        /// <summary>Earlier change when a strictly-higher-rank contender preempts.</summary>
        public const int PreemptFloorMs = 250;

        /// <summary>Surface key for the seat ladder in <see cref="ComposedResolutionRecord"/>.</summary>
        public const string DisplaySurfaceId = "display";

        public const string ManualCarrierId = "manual";
        public const string RestCarrierId = "rest";

        private readonly DisplayConfigV2 _config;
        private readonly string _deviceKey;
        private readonly IReadOnlyDictionary<ushort, string> _primaryHostByParam;
        private readonly Action<string> _warn;
        private readonly HashSet<string> _warnedKeys = new HashSet<string>(StringComparer.Ordinal);

        // Ranked contender sources built at construction from EffectiveRows + document.
        private readonly List<RowPlan> _rows = new List<RowPlan>();
        private readonly Dictionary<string, ContenderPlan> _contendersByCarrierId =
            new Dictionary<string, ContenderPlan>(StringComparer.Ordinal);
        private readonly List<AggregatePlan> _aggregates = new List<AggregatePlan>();
        private readonly Dictionary<string, HashSet<string>> _carriersByDestination =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, CycleEntry> _cycles =
            new Dictionary<string, CycleEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Flagged children that are not display contenders — labels + real surface only.
        /// Presence left null (E5 fills).
        /// </summary>
        private readonly List<ForeignCarrierPlan> _foreignCarriers = new List<ForeignCarrierPlan>();

        private readonly int _manualRank;
        private readonly int? _returnToRestAfterMs;
        private readonly string _defaultInSessionDestinationId;
        private readonly string _landingDestinationId;
        private readonly IdleSpec _idle;

        // ── Runtime (session) state ──────────────────────────────────────
        private readonly HashSet<string> _dismissalLatches =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Null until first press / adopt; cleared on GameChanged.</summary>
        private string _manualTarget;
        private bool _adoptedUnknownPage;
        private long? _lastManualPressAt;
        private bool _manualParked;

        private bool _hasSelection;
        private string _selectionDestinationId;
        private string _selectionCarrierId;
        private string _selectionRowId;
        private int _selectionRank = int.MaxValue;
        private long _selectionChangedAt;

        // Destination-owned cycle free-run state (anchor once per activation wave).
        private readonly Dictionary<string, CycleRuntime> _cycleState =
            new Dictionary<string, CycleRuntime>(StringComparer.Ordinal);

        /// <summary>Previous emitted effective page id (cycle member when cycle).</summary>
        private string _prevEmittedEffectivePageId;

        // Derived aggregate evaluator state (arbiter-owned clocks; evaluator machine).
        private readonly Dictionary<string, AggregateRuntime> _aggregateRuntimes =
            new Dictionary<string, AggregateRuntime>(StringComparer.Ordinal);

        /// <summary>
        /// Builds an arbiter over a NORMALIZED <see cref="DisplayConfigV2"/>
        /// (<see cref="DisplayConfigV2Validator.Normalize"/> already applied; consumers
        /// read <see cref="PriorityLadder.EffectiveRows"/>).
        /// </summary>
        public SeatArbiter(DisplayConfigV2 config, SeatArbiterOptions options = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            options = options ?? new SeatArbiterOptions();
            _deviceKey = options.DeviceKey ?? "";
            _primaryHostByParam = options.PrimaryHostByParam
                ?? new Dictionary<ushort, string>();
            _warn = options.Warn;

            if (_config.Cycles != null)
            {
                foreach (var c in _config.Cycles)
                {
                    if (c?.Id != null)
                        _cycles[c.Id] = c;
                }
            }

            var rest = _config.Priority?.Rest;
            _defaultInSessionDestinationId = DestinationIds.FromPageRef(rest?.InSessionPage)
                ?? DestinationIds.RestInSession;
            _landingDestinationId = ResolveLandingDestination(rest);
            _idle = rest?.Idle;
            // Never-navigated: no remembered target (landing is a separate fallback).
            _manualTarget = null;
            _adoptedUnknownPage = false;

            BuildPlans();
            _manualRank = FindManualRank();
            _returnToRestAfterMs = FindReturnToRestAfterMs();
            _selectionChangedAt = long.MinValue / 2;
        }

        /// <summary>Evaluate one tick. Deterministic given the same input sequence + clock.</summary>
        public SeatArbiterTickResult Tick(SeatArbiterTickInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            long now = input.NowMs;
            bool inGame = input.InGame;
            var snapshots = IndexSnapshots(input.CarrierSnapshots);

            // 1. Game change: manual remembered target RESETS immediately (ruling 7)
            // and stamps dwell so subsequent claims are constrained.
            if (input.GameChanged)
                ApplyGameChanged(now, inGame);

            // 2. Manual / adopted press FIRST (dismissal-before-evaluation): latch
            // press-time activations, then later re-arm consumes current-tick FreshFire.
            string walkResolved = null;
            bool manualBypassDwell = false;
            bool pressConsumedByDismissal = false;
            if (input.Manual.HasValue)
            {
                manualBypassDwell = true;
                var manualResult = ApplyManual(
                    input.Manual.Value, now, input.CompiledWalk, snapshots);
                walkResolved = manualResult.WalkResolved;
                pressConsumedByDismissal = manualResult.ConsumedByDismissal;
            }

            // 3. Re-arm latches for caller-supplied carriers (post-press, pre-aggregate).
            RearmLatches(snapshots);

            // 4. Evaluate derived aggregates (pin / visit via CarrierEvaluator.Derived).
            var aggregateSnaps = new List<CarrierTickSnapshot>();
            var aggregateMembership = new List<AggregateMembership>();
            EvaluateAggregates(now, inGame, snapshots, aggregateSnaps, aggregateMembership);

            // Merge derived snapshots into the lookup for re-arm + status emission.
            foreach (var a in aggregateSnaps)
                snapshots[a.CarrierId] = a;

            // 5. Second re-arm pass AFTER aggregate evaluation (derived snaps exist now).
            RearmLatches(snapshots);

            // 6. returnToRestAfterMs: X ms since LAST PRESS (no pause/restart). Evaluated
            // at selection time — once expired the manual row simply stops claiming;
            // rest transition remains dwell-gated.
            bool returnedToRest = false;
            bool manualClaimAllowed = EvaluateManualClaimAllowed(now, out returnedToRest);

            // 7. Logical winner (pre-dwell).
            var logical = SelectLogicalWinner(inGame, snapshots, manualClaimAllowed, now);

            // 8. Dwell floor → emitted selection.
            // Same-destination carrier handoff (D9) bypasses dwell entirely: metadata
            // updates immediately, no stamp, zero page intents.
            bool dwellHeld = false;
            if (!_hasSelection)
            {
                // First selection does not start the dwell clock (v9 oddity #11).
                SetSelection(logical, now, stampDwell: false);
            }
            else if (!SameSelection(logical))
            {
                bool sameDestination = string.Equals(
                    _selectionDestinationId, logical.DestinationId, StringComparison.Ordinal);
                if (sameDestination)
                {
                    // D9: same physical destination — handoff without dwell / stamp.
                    SetSelection(logical, now, stampDwell: false);
                }
                else if (manualBypassDwell)
                {
                    SetSelection(logical, now, stampDwell: true);
                }
                else
                {
                    long held = now - _selectionChangedAt;
                    int desiredRank = logical.Rank;
                    bool higherPreempt = desiredRank < _selectionRank && held >= PreemptFloorMs;
                    bool fullDwell = held >= MinDwellMs;
                    if (higherPreempt || fullDwell)
                        SetSelection(logical, now, stampDwell: true);
                    else
                        dwellHeld = true;
                }
            }

            // End cycle activation waves that no longer have any live claim (next win =
            // fresh anchor). Outranked-but-still-active keeps free-running (RESUME).
            PruneEndedCycleActivations(snapshots);

            // 9. Cycle free-run cursor for the emitted destination.
            var intent = BuildIntent(now, inGame, dwellHeld);

            // 10. Presence + labels for every known contender / foreign child.
            var statuses = BuildStatuses(snapshots, now);
            var allSnaps = snapshots.Values.ToList();

            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(
                    DisplaySurfaceId,
                    _selectionCarrierId,
                    intent.DestinationId),
            };

            var resolution = new ComposedResolutionRecord(
                now, _deviceKey, winners, statuses, allSnaps);

            var latchSnapshot = new List<string>(_dismissalLatches);
            latchSnapshot.Sort(StringComparer.Ordinal);

            return new SeatArbiterTickResult
            {
                Resolution = resolution,
                Intent = intent,
                Manual = new ManualRowState
                {
                    RememberedDestinationId = _manualTarget,
                    HasRememberedTarget = _manualTarget != null,
                    LandingDestinationId = _landingDestinationId,
                    OwnsDisplay = string.Equals(
                        _selectionCarrierId, ManualCarrierId, StringComparison.Ordinal),
                    MsSinceLastPress = _lastManualPressAt.HasValue
                        ? now - _lastManualPressAt.Value
                        : (long?)null,
                    ReturnedToRest = returnedToRest,
                    AdoptedUnknownPage = _adoptedUnknownPage,
                },
                Aggregates = aggregateMembership,
                WalkStepResolvedDestinationId = walkResolved,
                PressConsumedByDismissal = pressConsumedByDismissal,
                DismissedCarrierIds = latchSnapshot,
            };
        }

        // ── Construction ─────────────────────────────────────────────────

        private static string ResolveLandingDestination(RestBlock rest)
        {
            if (rest?.LandingPage == null || rest.LandingPage.DegradedAtLoad
                || rest.LandingPageUseFallback)
                return null;
            return DestinationIds.FromPageRef(rest.LandingPage);
        }

        private void BuildPlans()
        {
            var rows = _config.Priority?.EffectiveRows;
            if (rows == null)
                return;

            // Split children (childRef satellites) leave the home aggregate.
            var splitChildIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null || row.Kind != PriorityRowKind.Satellite || row.ChildRef == null)
                    continue;
                if (row.DegradedAtLoad || row.ChildRefAmbiguous)
                    continue;
                string childId = ChildRefCarrierId(row.ChildRef);
                if (childId != null)
                    splitChildIds.Add(childId);
            }

            // Pre-index flagged children by destination (and collect orphans).
            var flaggedByDest = BuildFlaggedMembership(splitChildIds);

            for (int rank = 0; rank < rows.Count; rank++)
            {
                var row = rows[rank];
                if (row == null)
                    continue;

                var plan = new RowPlan
                {
                    Rank = rank,
                    RowId = row.Id ?? (row.Kind == PriorityRowKind.Manual ? "manual" : "row-" + rank),
                    Kind = row.Kind,
                    DegradedAtLoad = row.DegradedAtLoad,
                };

                // Degraded rows still emit status (honesty law 10) but never compete.
                if (row.DegradedAtLoad)
                {
                    RegisterDegradedRow(plan, row);
                    _rows.Add(plan);
                    continue;
                }

                switch (row.Kind)
                {
                    case PriorityRowKind.Seat:
                    {
                        string dest = DestinationIds.FromPageRef(row.Target);
                        plan.DestinationId = dest;
                        if (row.Summons != null)
                        {
                            foreach (var s in row.Summons)
                            {
                                if (s == null || string.IsNullOrEmpty(s.Id))
                                    continue;
                                if (!s.EffectivelyEnabled)
                                {
                                    RegisterNonCompeting(
                                        s.Id, plan.RowId, rank, dest,
                                        ContenderKind.Summon,
                                        CarrierRowLabels.Off);
                                    continue;
                                }
                                if (dest == null)
                                {
                                    RegisterNonCompeting(
                                        s.Id, plan.RowId, rank, null,
                                        ContenderKind.Summon,
                                        CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere);
                                    continue;
                                }
                                RegisterContender(new ContenderPlan
                                {
                                    CarrierId = s.Id,
                                    RowId = plan.RowId,
                                    Rank = rank,
                                    DestinationId = dest,
                                    Kind = ContenderKind.Summon,
                                    Competes = true,
                                    SurfaceId = DisplaySurfaceId,
                                });
                                plan.SummonIds.Add(s.Id);
                            }
                        }

                        // Derived aggregate at home-seat rank (cycles have no aggregate).
                        if (dest != null && !DestinationIds.IsCycle(dest))
                        {
                            flaggedByDest.TryGetValue(dest, out var memberPack);
                            var members = memberPack?.MemberIds ?? new List<string>();
                            bool membershipDegraded = memberPack?.Degraded ?? false;
                            RegisterDerivedAggregate(
                                plan, dest, rank, members, row.BringUpLifetime,
                                membershipDegraded);
                        }
                        break;
                    }
                    case PriorityRowKind.Satellite:
                    {
                        if (row.ChildRef != null)
                        {
                            if (row.ChildRefAmbiguous)
                            {
                                // Emit a degraded marker so the ladder is not silently short.
                                string ambId = "degraded:" + plan.RowId;
                                RegisterNonCompeting(
                                    ambId, plan.RowId, rank, null,
                                    ContenderKind.ChildRefSatellite,
                                    CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere);
                                break;
                            }

                            string childId = ChildRefCarrierId(row.ChildRef);
                            string dest = ResolveChildDestination(row.ChildRef);
                            plan.DestinationId = dest;
                            if (childId == null || dest == null)
                            {
                                // Never register a null-destination contender.
                                string missId = childId ?? ("degraded:" + plan.RowId);
                                RegisterNonCompeting(
                                    missId, plan.RowId, rank, dest,
                                    ContenderKind.ChildRefSatellite,
                                    CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere);
                                break;
                            }

                            // One-member Derived aggregate honouring row.Lifetime
                            // (pin default, visit supported) — shares latch/re-arm.
                            var members = new List<string> { childId };
                            RegisterDerivedAggregate(
                                plan, dest, rank, members, row.Lifetime,
                                membershipDegraded: false);
                            // Foreign status for the child itself (real surface; E5 presence).
                            RegisterForeignIfAbsent(
                                childId,
                                ResolveChildSurfaceId(row.ChildRef),
                                dest);
                        }
                        else if (row.Summons != null)
                        {
                            if (row.SummonsIgnored)
                            {
                                // Row present but summons ignored — mark, do not compete.
                                break;
                            }
                            string dest = DestinationIds.FromPageRef(row.Target);
                            plan.DestinationId = dest;
                            foreach (var s in row.Summons)
                            {
                                if (s == null || string.IsNullOrEmpty(s.Id))
                                    continue;
                                if (!s.EffectivelyEnabled)
                                {
                                    RegisterNonCompeting(
                                        s.Id, plan.RowId, rank, dest,
                                        ContenderKind.Summon, CarrierRowLabels.Off);
                                    continue;
                                }
                                if (dest == null)
                                {
                                    RegisterNonCompeting(
                                        s.Id, plan.RowId, rank, null,
                                        ContenderKind.Summon,
                                        CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere);
                                    continue;
                                }
                                RegisterContender(new ContenderPlan
                                {
                                    CarrierId = s.Id,
                                    RowId = plan.RowId,
                                    Rank = rank,
                                    DestinationId = dest,
                                    Kind = ContenderKind.Summon,
                                    Competes = true,
                                    SurfaceId = DisplaySurfaceId,
                                });
                                plan.SummonIds.Add(s.Id);
                            }
                        }
                        break;
                    }
                    case PriorityRowKind.Manual:
                    {
                        plan.DestinationId = null; // runtime remembered target
                        RegisterContender(new ContenderPlan
                        {
                            CarrierId = ManualCarrierId,
                            RowId = plan.RowId,
                            Rank = rank,
                            DestinationId = null,
                            Kind = ContenderKind.Manual,
                            Competes = true,
                            SurfaceId = DisplaySurfaceId,
                        });
                        break;
                    }
                }

                _rows.Add(plan);
            }
        }

        private void RegisterDegradedRow(RowPlan plan, PriorityRow row)
        {
            string id = "degraded:" + plan.RowId;
            RegisterNonCompeting(
                id, plan.RowId, plan.Rank,
                DestinationIds.FromPageRef(row.Target),
                ContenderKind.Summon,
                CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere);
        }

        private void RegisterDerivedAggregate(
            RowPlan plan,
            string dest,
            int rank,
            List<string> members,
            Lifetime life,
            bool membershipDegraded)
        {
            var lifeKind = life == null || life.Kind == LifetimeKind.WhileTrue
                || life.Kind == LifetimeKind.Unknown
                ? CarrierLifetimeKind.WhileTrue
                : CarrierLifetimeKind.ForDuration;
            int durationMs = life != null && life.Kind == LifetimeKind.ForDuration
                && !life.DurationMsIgnored
                ? life.DurationMs
                : Lifetime.DefaultDurationMs;

            string derivedId = "bringUp:" + plan.RowId;
            var agg = new AggregatePlan
            {
                SeatId = plan.RowId,
                DestinationId = dest,
                Rank = rank,
                DerivedCarrierId = derivedId,
                MemberCarrierIds = members,
                LifetimeKind = lifeKind,
                DurationMs = durationMs,
                MembershipDegraded = membershipDegraded,
            };
            _aggregates.Add(agg);
            plan.DerivedCarrierId = derivedId;
            RegisterContender(new ContenderPlan
            {
                CarrierId = derivedId,
                RowId = plan.RowId,
                Rank = rank,
                DestinationId = dest,
                Kind = ContenderKind.Derived,
                Competes = true,
                SurfaceId = DisplaySurfaceId,
            });
            // Members latch with the destination on D8 dismiss.
            foreach (var memberId in members)
                RegisterDestinationCarrier(dest, memberId);
            // Derived itself latches with the destination (E4-01 / SA-001).
            RegisterDestinationCarrier(dest, derivedId);
            _aggregateRuntimes[derivedId] = new AggregateRuntime
            {
                Spec = CarrierSpec.Derived(derivedId, lifeKind, durationMs),
                Runtime = new CarrierRuntime(),
            };
        }

        private void RegisterNonCompeting(
            string carrierId,
            string rowId,
            int rank,
            string dest,
            ContenderKind kind,
            CarrierRowLabels labels)
        {
            if (carrierId == null || _contendersByCarrierId.ContainsKey(carrierId))
                return;
            _contendersByCarrierId[carrierId] = new ContenderPlan
            {
                CarrierId = carrierId,
                RowId = rowId,
                Rank = rank,
                DestinationId = dest,
                Kind = kind,
                Competes = false,
                SurfaceId = DisplaySurfaceId,
                StaticLabels = labels,
            };
        }

        private sealed class MemberPack
        {
            public List<string> MemberIds = new List<string>();
            public bool Degraded;
        }

        private Dictionary<string, MemberPack> BuildFlaggedMembership(
            HashSet<string> splitChildIds)
        {
            var byDest = new Dictionary<string, MemberPack>(StringComparer.Ordinal);

            // Hosted pages: flagged layers of that page.
            if (_config.Pages != null)
            {
                foreach (var page in _config.Pages)
                {
                    if (page == null || page.Kind != PageEntryKind.HostedPage
                        || page.DegradedAtLoad || string.IsNullOrEmpty(page.Id))
                        continue;
                    if (page.Layers == null)
                        continue;
                    string dest = DestinationIds.Hosted(page.Id);
                    string surface = "page:" + page.Id;
                    foreach (var layer in page.Layers)
                    {
                        if (layer == null || string.IsNullOrEmpty(layer.Id))
                            continue;
                        if (!layer.EffectivelyEnabled)
                        {
                            RegisterForeignIfAbsent(layer.Id, surface, dest);
                            // Disabled flagged child: still visible with Off label via foreign.
                            MarkForeignLabel(layer.Id, CarrierRowLabels.Off);
                            continue;
                        }
                        if (!layer.ActsAsEntrypoint || layer.ActsAsEntrypointIgnored)
                            continue;
                        if (splitChildIds.Contains(layer.Id))
                        {
                            // Split children still need a foreign surface row for labels.
                            RegisterForeignIfAbsent(layer.Id, surface, dest);
                            continue;
                        }
                        AddMember(byDest, dest, layer.Id);
                        RegisterForeignIfAbsent(layer.Id, surface, dest);
                    }
                }
            }

            // ITM pages: flagged field overrides whose primaryHost is that page.
            // Absence of PrimaryHostByParam where flags exist = degrade-visible.
            if (_config.Fields != null)
            {
                foreach (var kv in _config.Fields)
                {
                    ushort paramId = kv.Key;
                    var field = kv.Value;
                    if (field?.Overrides == null)
                        continue;
                    string surface = "field:" + paramId;
                    bool hasHost = _primaryHostByParam.TryGetValue(paramId, out var hostCatalogId)
                        && !string.IsNullOrEmpty(hostCatalogId);
                    string dest = hasHost ? DestinationIds.Itm(hostCatalogId) : null;

                    foreach (var ov in field.Overrides)
                    {
                        if (ov == null || string.IsNullOrEmpty(ov.Id))
                            continue;
                        if (ov.DegradedAtLoad)
                        {
                            RegisterForeignIfAbsent(ov.Id, surface, dest);
                            MarkForeignLabel(ov.Id, CarrierRowLabels.KeptAsIs);
                            continue;
                        }
                        if (!ov.Enabled)
                        {
                            RegisterForeignIfAbsent(ov.Id, surface, dest);
                            MarkForeignLabel(ov.Id, CarrierRowLabels.Off);
                            continue;
                        }
                        if (!ov.ActsAsEntrypoint || ov.ActsAsEntrypointIgnored)
                            continue;

                        if (!hasHost)
                        {
                            // Degrade-visible: row with labels, no silent drop; never a
                            // null-destination contender. Envelope edge = rebuild edge.
                            RegisterForeignIfAbsent(ov.Id, surface, null);
                            MarkForeignLabel(
                                ov.Id,
                                CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere);
                            WarnOnce(
                                "host-map:" + paramId,
                                "PrimaryHostByParam missing for flagged field param "
                                + paramId + " (carrier " + ov.Id + ") — degrade-visible");
                            // Mark any seat that would have hosted this as degraded membership
                            // once we know hosts — without host we attach to a synthetic bag.
                            AddOrphanDegrade(byDest, ov.Id);
                            continue;
                        }

                        if (splitChildIds.Contains(ov.Id))
                        {
                            RegisterForeignIfAbsent(ov.Id, surface, dest);
                            continue;
                        }
                        AddMember(byDest, dest, ov.Id);
                        RegisterForeignIfAbsent(ov.Id, surface, dest);
                    }
                }
            }

            return byDest;
        }

        private void AddOrphanDegrade(Dictionary<string, MemberPack> byDest, string carrierId)
        {
            // Orphans without a host do not join any seat aggregate; membership degrade
            // is recorded on a sentinel so diagnostics can still surface the fact.
            const string orphanKey = "orphan:unhosted";
            if (!byDest.TryGetValue(orphanKey, out var pack))
            {
                pack = new MemberPack { Degraded = true };
                byDest[orphanKey] = pack;
            }
            pack.Degraded = true;
            // Do not add carrierId as a member — no null-destination contender.
        }

        private static void AddMember(
            Dictionary<string, MemberPack> byDest, string dest, string carrierId)
        {
            if (!byDest.TryGetValue(dest, out var pack))
            {
                pack = new MemberPack();
                byDest[dest] = pack;
            }
            if (!pack.MemberIds.Contains(carrierId))
                pack.MemberIds.Add(carrierId);
        }

        private void RegisterForeignIfAbsent(string carrierId, string surfaceId, string dest)
        {
            if (carrierId == null)
                return;
            foreach (var f in _foreignCarriers)
            {
                if (string.Equals(f.CarrierId, carrierId, StringComparison.Ordinal))
                    return;
            }
            _foreignCarriers.Add(new ForeignCarrierPlan
            {
                CarrierId = carrierId,
                SurfaceId = surfaceId,
                DestinationId = dest,
            });
        }

        private void MarkForeignLabel(string carrierId, CarrierRowLabels labels)
        {
            foreach (var f in _foreignCarriers)
            {
                if (string.Equals(f.CarrierId, carrierId, StringComparison.Ordinal))
                {
                    f.StaticLabels |= labels;
                    return;
                }
            }
        }

        private string ResolveChildDestination(ChildRef childRef)
        {
            if (childRef == null)
                return null;
            if (!string.IsNullOrEmpty(childRef.PageId))
                return DestinationIds.Hosted(childRef.PageId);
            if (!string.IsNullOrEmpty(childRef.Field)
                && ushort.TryParse(childRef.Field, out var paramId)
                && _primaryHostByParam.TryGetValue(paramId, out var host))
                return DestinationIds.Itm(host);
            return null;
        }

        private static string ResolveChildSurfaceId(ChildRef childRef)
        {
            if (childRef == null)
                return DisplaySurfaceId;
            if (!string.IsNullOrEmpty(childRef.PageId))
                return "page:" + childRef.PageId;
            if (!string.IsNullOrEmpty(childRef.Field))
                return "field:" + childRef.Field;
            return DisplaySurfaceId;
        }

        private static string ChildRefCarrierId(ChildRef childRef)
        {
            if (childRef == null)
                return null;
            if (!string.IsNullOrEmpty(childRef.OverrideId))
                return childRef.OverrideId;
            if (!string.IsNullOrEmpty(childRef.LayerId))
                return childRef.LayerId;
            return null;
        }

        private void RegisterContender(ContenderPlan plan)
        {
            if (plan.CarrierId == null)
                return;
            // Never register a competing contender with a null destination (except manual).
            if (plan.Competes
                && plan.Kind != ContenderKind.Manual
                && plan.DestinationId == null)
            {
                plan.Competes = false;
                plan.StaticLabels |= CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere;
            }
            _contendersByCarrierId[plan.CarrierId] = plan;
            if (plan.DestinationId != null && plan.Competes)
                RegisterDestinationCarrier(plan.DestinationId, plan.CarrierId);
        }

        private void RegisterDestinationCarrier(string destinationId, string carrierId)
        {
            if (destinationId == null || carrierId == null)
                return;
            if (!_carriersByDestination.TryGetValue(destinationId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _carriersByDestination[destinationId] = set;
            }
            set.Add(carrierId);
        }

        private int FindManualRank()
        {
            foreach (var r in _rows)
                if (r.Kind == PriorityRowKind.Manual)
                    return r.Rank;
            return int.MaxValue;
        }

        private int? FindReturnToRestAfterMs()
        {
            var rows = _config.Priority?.EffectiveRows;
            if (rows == null)
                return null;
            foreach (var r in rows)
            {
                if (r != null && r.Kind == PriorityRowKind.Manual)
                    return r.ReturnToRestAfterMs;
            }
            return null;
        }

        private void WarnOnce(string key, string message)
        {
            if (!_warnedKeys.Add(key))
                return;
            _warn?.Invoke(message);
        }

        // ── Tick steps ───────────────────────────────────────────────────

        private static Dictionary<string, CarrierTickSnapshot> IndexSnapshots(
            IReadOnlyList<CarrierTickSnapshot> list)
        {
            var map = new Dictionary<string, CarrierTickSnapshot>(StringComparer.Ordinal);
            if (list == null)
                return map;
            foreach (var s in list)
            {
                if (s.CarrierId != null)
                    map[s.CarrierId] = s;
            }
            return map;
        }

        private void ApplyGameChanged(long now, bool inGame)
        {
            _manualTarget = null;
            _adoptedUnknownPage = false;
            _manualParked = false;
            _lastManualPressAt = null;

            // Immediate resting-target change (v9 carry): if manual currently owns,
            // drop to rest now and stamp dwell so later claims wait the floor.
            if (_hasSelection
                && string.Equals(_selectionCarrierId, ManualCarrierId, StringComparison.Ordinal))
            {
                SetSelection(new LogicalWinner
                {
                    Rank = int.MaxValue,
                    RowId = null,
                    CarrierId = RestCarrierId,
                    DestinationId = inGame
                        ? _defaultInSessionDestinationId
                        : DestinationIds.RestIdle,
                }, now, stampDwell: true);
            }
        }

        private void RearmLatches(Dictionary<string, CarrierTickSnapshot> snapshots)
        {
            if (_dismissalLatches.Count == 0)
                return;
            var rearm = new List<string>();
            foreach (var id in _dismissalLatches)
            {
                if (!snapshots.TryGetValue(id, out var snap))
                    continue;
                if (ShouldRearmDismissalLatch(snap))
                    rearm.Add(id);
            }
            foreach (var id in rearm)
                _dismissalLatches.Remove(id);
        }

        /// <summary>
        /// RULED (owner, 2026-07-28 — "accept as solution for now"): D8's LETTER.
        /// A dismissal latch re-arms ONLY on a genuine inactive→active edge
        /// (<see cref="CarrierTickSnapshot.FreshFire"/>). A re-fire INSIDE an unexpired
        /// window (FiredThisTick &amp;&amp; !FreshFire — a window restart) does NOT re-arm:
        /// the dismissal sticks until the activation truly ends and fires fresh.
        ///
        /// "For now": the accepted trade-off is that a rapidly re-firing condition stays
        /// dismissed for its whole window even when the user might want it back — if
        /// field reports read this as "my dismissal was ignored"/"it never came back",
        /// the alternative (re-arm on any FiredThisTick) is a one-line change HERE and
        /// an update to the two pinning fixtures. The policy lives in this one predicate;
        /// do not scatter it. Pinned by MidWindowRefire_DoesNotRearm_D8Letter.
        ///
        /// The Derived aggregate path follows mechanically (the aggregate is itself
        /// latched, E4-01, and never sees FreshFire on a window restart).
        /// </summary>
        private static bool ShouldRearmDismissalLatch(CarrierTickSnapshot snap)
        {
            if (!snap.FiredThisTick)
                return false;
            // D8 letter (ruled): no mid-window re-arm.
            return snap.FreshFire;
        }

        private sealed class ManualApplyResult
        {
            public string WalkResolved;
            public bool ConsumedByDismissal;
        }

        private ManualApplyResult ApplyManual(
            SeatManualInput manual,
            long now,
            IReadOnlyList<string> compiledWalk,
            Dictionary<string, CarrierTickSnapshot> snapshots)
        {
            var result = new ManualApplyResult();

            // Dismiss the live DESTINATION when an entrypoint owns the display.
            bool dismissed = false;
            if (_hasSelection
                && _selectionCarrierId != null
                && !string.Equals(_selectionCarrierId, ManualCarrierId, StringComparison.Ordinal)
                && !string.Equals(_selectionCarrierId, RestCarrierId, StringComparison.Ordinal)
                && _selectionDestinationId != null
                && !DestinationIds.IsRest(_selectionDestinationId))
            {
                DismissDestination(_selectionDestinationId, snapshots);
                dismissed = true;
            }

            // Press that performed a dismissal is CONSUMED: no walk step, no target change.
            // Remembered page stays where it was (round-7b dismiss-and-return).
            if (dismissed)
            {
                result.ConsumedByDismissal = true;
                _manualParked = true;
                _lastManualPressAt = now;
                return result;
            }

            // Adopt / walk only when no dismissal consumed the press.
            if (manual.AdoptedUnknownPage)
            {
                // Uncataloged adopt (director ManualNavigation(null)): rest-with-no-intent.
                // Clear remembered page; no destination request while the wheel sits there.
                _manualTarget = null;
                _adoptedUnknownPage = true;
            }
            else if (manual.WalkStep.HasValue)
            {
                _adoptedUnknownPage = false;
                string from = ResolvePageAdopt(manual.AdoptedDestinationId)
                    ?? EffectiveManualDestination();
                result.WalkResolved = StepWalk(from, manual.WalkStep.Value, compiledWalk);
                if (result.WalkResolved != null
                    && !DestinationIds.IsCycle(result.WalkResolved))
                    _manualTarget = result.WalkResolved;
            }
            else if (!string.IsNullOrEmpty(manual.AdoptedDestinationId))
            {
                string adopt = ResolvePageAdopt(manual.AdoptedDestinationId);
                if (adopt != null)
                {
                    _manualTarget = adopt;
                    _adoptedUnknownPage = false;
                }
            }

            _manualParked = true;
            _lastManualPressAt = now;
            return result;
        }

        /// <summary>
        /// Navigate/adopt rejects cycle destinations (ignored + warn-once). Remembered
        /// target must be a page ref (hosted / itm).
        /// </summary>
        private string ResolvePageAdopt(string destinationId)
        {
            if (string.IsNullOrEmpty(destinationId))
                return null;
            if (DestinationIds.IsCycle(destinationId))
            {
                WarnOnce(
                    "cycle-manual:" + destinationId,
                    "SeatManualInput.Navigate/adopt rejected cycle destination '"
                    + destinationId + "' — remembered target must be a page ref");
                return null;
            }
            if (DestinationIds.IsRest(destinationId))
                return destinationId;
            return destinationId;
        }

        private void DismissDestination(
            string destinationId,
            Dictionary<string, CarrierTickSnapshot> snapshots)
        {
            // Latch every currently-active summon, flagged child, AND Derived aggregate
            // of the destination (model law 5: the flagged-children summon itself).
            if (_carriersByDestination.TryGetValue(destinationId, out var carriers))
            {
                foreach (var id in carriers)
                {
                    if (IsActiveForLatch(id, snapshots))
                        _dismissalLatches.Add(id);
                }
            }

            // Ensure aggregate members + derived are latched even if registration missed.
            foreach (var agg in _aggregates)
            {
                if (!string.Equals(agg.DestinationId, destinationId, StringComparison.Ordinal))
                    continue;
                if (IsActiveForLatch(agg.DerivedCarrierId, snapshots))
                    _dismissalLatches.Add(agg.DerivedCarrierId);
                foreach (var memberId in agg.MemberCarrierIds)
                {
                    if (IsActiveForLatch(memberId, snapshots))
                        _dismissalLatches.Add(memberId);
                }
            }

            // Clear cycle activation so a later reclaim is a fresh activation wave.
            // RESUME applies only while the destination remains claimed (outranked but live);
            // a dismissal ends the wave.
            if (DestinationIds.IsCycle(destinationId))
                _cycleState.Remove(destinationId);
        }

        private bool IsActiveForLatch(
            string carrierId, Dictionary<string, CarrierTickSnapshot> snapshots)
        {
            if (snapshots.TryGetValue(carrierId, out var snap) && snap.Active)
                return true;
            // Derived may not be in this tick's caller snaps yet — use runtime Active.
            if (_aggregateRuntimes.TryGetValue(carrierId, out var art) && art.Runtime.Active)
                return true;
            return false;
        }

        private static string StepWalk(
            string current,
            int direction,
            IReadOnlyList<string> walk)
        {
            if (walk == null || walk.Count == 0)
                return current;
            int idx = -1;
            for (int i = 0; i < walk.Count; i++)
            {
                if (string.Equals(walk[i], current, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
                idx = 0;
            int next = ((idx + direction) % walk.Count + walk.Count) % walk.Count;
            return walk[next];
        }

        /// <summary>
        /// returnToRestAfterMs law: X ms since the LAST PRESS, evaluated at selection
        /// time. An interruption does not pause or restart the clock. Once expired the
        /// manual row simply stops claiming; the rest transition is still dwell-gated.
        /// </summary>
        private bool EvaluateManualClaimAllowed(long now, out bool returnedToRest)
        {
            returnedToRest = false;
            if (!_manualParked)
                return false;

            if (_returnToRestAfterMs.HasValue
                && _lastManualPressAt.HasValue
                && now - _lastManualPressAt.Value >= _returnToRestAfterMs.Value)
            {
                _manualParked = false;
                // Do not invent a remembered target — clear park only; remembered stays
                // until GameChanged / new press. Effective destination falls to landing.
                returnedToRest = true;
                return false;
            }

            return true;
        }

        private string EffectiveManualDestination()
            => _manualTarget
               ?? _landingDestinationId
               ?? _defaultInSessionDestinationId;

        private void EvaluateAggregates(
            long now,
            bool inGame,
            Dictionary<string, CarrierTickSnapshot> snapshots,
            List<CarrierTickSnapshot> outSnaps,
            List<AggregateMembership> outMembership)
        {
            foreach (var agg in _aggregates)
            {
                int active = 0;
                bool anyActiveUnlatched = false;
                bool anyFiredUnlatched = false;
                foreach (var memberId in agg.MemberCarrierIds)
                {
                    if (!snapshots.TryGetValue(memberId, out var snap))
                        continue;
                    if (snap.Active)
                        active++;
                    bool latched = _dismissalLatches.Contains(memberId);
                    if (snap.Active && !latched)
                        anyActiveUnlatched = true;
                    if (snap.FiredThisTick && !latched)
                        anyFiredUnlatched = true;
                }

                outMembership.Add(new AggregateMembership
                {
                    SeatId = agg.SeatId,
                    DestinationId = agg.DestinationId,
                    DerivedCarrierId = agg.DerivedCarrierId,
                    ActiveCount = active,
                    TotalCount = agg.MemberCarrierIds.Count,
                    MemberCarrierIds = agg.MemberCarrierIds,
                    MembershipDegraded = agg.MembershipDegraded,
                });

                if (!_aggregateRuntimes.TryGetValue(agg.DerivedCarrierId, out var art))
                    continue;

                var tickIn = new CarrierTickInput
                {
                    NowMs = now,
                    InGame = inGame,
                    DerivedSatisfiedNow = anyActiveUnlatched,
                    DerivedFiredThisTick = anyFiredUnlatched,
                };
                CarrierEvaluator.Evaluate(art.Spec, art.Runtime, tickIn, warnMissing: null);
                outSnaps.Add(CarrierTickSnapshot.From(art.Spec, art.Runtime, now));
            }
        }

        private LogicalWinner SelectLogicalWinner(
            bool inGame,
            Dictionary<string, CarrierTickSnapshot> snapshots,
            bool manualClaimAllowed,
            long now)
        {
            // Scan by rank. Claims above the manual row always compete. Claims below
            // cannot interrupt while the manual row is parked (spec §5 / E4).
            LogicalWinner bestAbove = null;
            LogicalWinner bestBelow = null;
            LogicalWinner manual = null;

            foreach (var row in _rows.OrderBy(r => r.Rank))
            {
                if (row.Kind == PriorityRowKind.Manual)
                {
                    // Manual claims only after first press (parked) and while returnToRest
                    // has not expired. Destination = remembered ?? landing ?? in-session.
                    if (manualClaimAllowed)
                    {
                        manual = new LogicalWinner
                        {
                            Rank = row.Rank,
                            RowId = row.RowId,
                            CarrierId = ManualCarrierId,
                            DestinationId = EffectiveManualDestination(),
                        };
                    }
                    continue;
                }

                if (row.DegradedAtLoad)
                    continue;

                LogicalWinner claim = null;
                foreach (var carrierId in row.SummonIds)
                {
                    if (!_contendersByCarrierId.TryGetValue(carrierId, out var c)
                        || !c.Competes)
                        continue;
                    if (IsLiveClaim(carrierId, snapshots, out _))
                    {
                        claim = new LogicalWinner
                        {
                            Rank = row.Rank,
                            RowId = row.RowId,
                            CarrierId = carrierId,
                            DestinationId = c.DestinationId,
                        };
                        break;
                    }
                }

                if (claim == null
                    && row.DerivedCarrierId != null
                    && IsLiveClaim(row.DerivedCarrierId, snapshots, out _))
                {
                    var c = _contendersByCarrierId[row.DerivedCarrierId];
                    if (c.Competes)
                    {
                        claim = new LogicalWinner
                        {
                            Rank = row.Rank,
                            RowId = row.RowId,
                            CarrierId = row.DerivedCarrierId,
                            DestinationId = c.DestinationId,
                        };
                    }
                }

                if (claim == null)
                    continue;

                if (row.Rank < _manualRank)
                {
                    if (bestAbove == null)
                        bestAbove = claim;
                }
                else
                {
                    if (bestBelow == null)
                        bestBelow = claim;
                }
            }

            if (bestAbove != null)
                return bestAbove;

            // Standing entrypoint: when parked (and claim allowed), manual owns at its
            // rank (remembered page). Rows below cannot interrupt while parked.
            if (manualClaimAllowed && inGame && manual != null)
                return manual;

            if (bestBelow != null)
                return bestBelow;

            // Rest floor: in-session page, or idle semantic outside a session.
            return new LogicalWinner
            {
                Rank = int.MaxValue,
                RowId = null,
                CarrierId = RestCarrierId,
                DestinationId = inGame
                    ? _defaultInSessionDestinationId
                    : DestinationIds.RestIdle,
            };
        }

        private bool IsLiveClaim(
            string carrierId,
            Dictionary<string, CarrierTickSnapshot> snapshots,
            out CarrierTickSnapshot snap)
        {
            snap = default;
            if (_dismissalLatches.Contains(carrierId))
                return false;
            if (!snapshots.TryGetValue(carrierId, out snap))
                return false;
            return snap.Active && snap.Eligible;
        }

        private bool SameSelection(LogicalWinner logical)
            => string.Equals(_selectionCarrierId, logical.CarrierId, StringComparison.Ordinal)
               && string.Equals(_selectionDestinationId, logical.DestinationId, StringComparison.Ordinal);

        private void PruneEndedCycleActivations(
            Dictionary<string, CarrierTickSnapshot> snapshots)
        {
            if (_cycleState.Count == 0)
                return;
            var dead = new List<string>();
            foreach (var dest in _cycleState.Keys)
            {
                if (string.Equals(_selectionDestinationId, dest, StringComparison.Ordinal))
                    continue;
                bool anyLive = false;
                if (_carriersByDestination.TryGetValue(dest, out var carriers))
                {
                    foreach (var id in carriers)
                    {
                        if (IsLiveClaim(id, snapshots, out _))
                        {
                            anyLive = true;
                            break;
                        }
                    }
                }
                if (!anyLive)
                    dead.Add(dest);
            }
            foreach (var d in dead)
                _cycleState.Remove(d);
        }

        private void SetSelection(LogicalWinner logical, long now, bool stampDwell)
        {
            _hasSelection = true;
            _selectionCarrierId = logical.CarrierId;
            _selectionDestinationId = logical.DestinationId;
            _selectionRowId = logical.RowId;
            _selectionRank = logical.Rank;
            if (stampDwell)
                _selectionChangedAt = now;

            // Cycle anchor: first win of a fresh activation (no prior cycle state).
            // RESUME (ruling 1): when the same destination returns after being outranked
            // while still claimed, state remains — cursor is not reset.
            if (DestinationIds.IsCycle(logical.DestinationId))
            {
                if (!_cycleState.ContainsKey(logical.DestinationId))
                {
                    _cycleState[logical.DestinationId] = new CycleRuntime
                    {
                        AnchorMs = now,
                    };
                }
            }

            // Manual ownership bookkeeping.
            if (string.Equals(logical.CarrierId, ManualCarrierId, StringComparison.Ordinal))
                _manualParked = true;
        }

        private SeatDisplayIntent BuildIntent(long now, bool inGame, bool dwellHeld)
        {
            string dest = _selectionDestinationId;
            string cycleMember = null;
            int cursor = -1;
            long phaseMs = -1;

            if (DestinationIds.IsCycle(dest))
            {
                string cycleId = DestinationIds.CycleId(dest);
                if (_cycles.TryGetValue(cycleId, out var cycle)
                    && cycle.Members != null && cycle.Members.Count > 0)
                {
                    if (!_cycleState.TryGetValue(dest, out var crt))
                    {
                        crt = new CycleRuntime { AnchorMs = now };
                        _cycleState[dest] = crt;
                    }
                    int period = Math.Max(1, cycle.PeriodMs);
                    long elapsed = Math.Max(0, now - crt.AnchorMs);
                    long step = elapsed / period;
                    cursor = (int)(step % cycle.Members.Count);
                    phaseMs = elapsed % period;
                    cycleMember = DestinationIds.FromPageRef(cycle.Members[cursor]);
                }
            }

            string effectivePage = cycleMember ?? dest;

            // DestinationChanged is computed from the physical page (effective), not
            // seat-level destination identity — so cycle advances request a page change
            // and same-member cycle↔seat handoffs do not.
            bool destChanged = _prevEmittedEffectivePageId != null
                && !string.Equals(_prevEmittedEffectivePageId, effectivePage, StringComparison.Ordinal);

            var intent = new SeatDisplayIntent
            {
                DestinationId = dest,
                CycleMemberDestinationId = cycleMember,
                EffectivePageDestinationId = effectivePage,
                CycleCursor = cursor,
                CyclePhaseMs = phaseMs,
                WinnerRowId = _selectionRowId,
                WinnerCarrierId = _selectionCarrierId,
                DestinationChanged = destChanged,
                DwellHeld = dwellHeld,
            };

            // Idle semantic published on every out-of-session tick (E4-15).
            // Shared IdleCompile helper (E7) — same reader as WheelScreenArbiter floor.
            // Seat publishes document-level IdleKind (page / blank / screen); park flag
            // comes from the helper so it cannot diverge from E6's blank compile.
            if (!inGame)
            {
                var compiled = IdleCompile.Resolve(_idle);
                intent.ParkOnLegacyForBlank = compiled.ParkOnLegacyForBlank;
                if (_idle == null || _idle.DegradedAtLoad)
                {
                    intent.IdleKind = Schema2.IdleKind.Blank;
                }
                else
                {
                    intent.IdleKind = _idle.Kind;
                    if (_idle.Kind == Schema2.IdleKind.Screen)
                        intent.IdleScreen = _idle.Screen;
                    else if (_idle.Kind == Schema2.IdleKind.Page)
                        intent.IdlePageDestinationId = DestinationIds.FromPageRef(_idle.Page);
                }
            }

            _prevEmittedEffectivePageId = effectivePage;
            return intent;
        }

        private List<CarrierResolutionStatus> BuildStatuses(
            Dictionary<string, CarrierTickSnapshot> snapshots,
            long now)
        {
            var list = new List<CarrierResolutionStatus>();
            string winningCarrier = _selectionCarrierId;
            var emittedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in _contendersByCarrierId)
            {
                string id = kv.Key;
                var plan = kv.Value;
                snapshots.TryGetValue(id, out var snap);
                bool latched = _dismissalLatches.Contains(id);
                bool hasSnap = snap.CarrierId != null;
                bool active = hasSnap && snap.Active;
                bool eligible = !hasSnap || snap.Eligible;
                bool activeEligible = active && eligible;

                CarrierPresence? presence;
                CarrierRowLabels labels = plan.StaticLabels;

                if (plan.Kind == ContenderKind.Manual)
                {
                    bool neverNavigated = _lastManualPressAt == null;
                    if (string.Equals(winningCarrier, ManualCarrierId, StringComparison.Ordinal))
                        presence = CarrierPresence.OnScreen;
                    else if (neverNavigated)
                        presence = CarrierPresence.Waiting;
                    else
                        presence = CarrierPresence.Outranked;

                    // Null destination while never-navigated so UI does not invent a page.
                    string manualDest = neverNavigated
                        ? null
                        : EffectiveManualDestination();
                    list.Add(new CarrierResolutionStatus(
                        id, DisplaySurfaceId, manualDest, presence, null, labels));
                    emittedIds.Add(id);
                    continue;
                }

                if (!plan.Competes)
                {
                    // Disabled/degraded: visible, marked, never dropped (OffScreen).
                    presence = CarrierPresence.OffScreen;
                    list.Add(new CarrierResolutionStatus(
                        id, plan.SurfaceId ?? DisplaySurfaceId,
                        plan.DestinationId, presence, null, labels));
                    emittedIds.Add(id);
                    continue;
                }

                // Dwell-held winner is OnScreen (winner-identity before !active).
                if (string.Equals(id, winningCarrier, StringComparison.Ordinal))
                {
                    presence = CarrierPresence.OnScreen;
                }
                else if (latched)
                {
                    // E4-08: Outranked when latched AND Active+Eligible; Waiting only
                    // when the condition is genuinely false. DISMISSED on both.
                    labels |= CarrierRowLabels.Dismissed;
                    presence = activeEligible
                        ? CarrierPresence.Outranked
                        : CarrierPresence.Waiting;
                }
                else if (hasSnap && !eligible)
                {
                    // E4-13(b): runs-gated — Waiting + OutOfSessionScope.
                    presence = CarrierPresence.Waiting;
                    labels |= CarrierRowLabels.OutOfSessionScope;
                }
                else if (!activeEligible)
                {
                    presence = CarrierPresence.Waiting;
                }
                else
                {
                    presence = CarrierPresence.Outranked;
                }

                int? remaining = hasSnap ? snap.RemainingMs : null;
                list.Add(new CarrierResolutionStatus(
                    id, plan.SurfaceId ?? DisplaySurfaceId,
                    plan.DestinationId, presence, remaining, labels));
                emittedIds.Add(id);
            }

            // Foreign-surface rows (flagged children E4 does not arbitrate): real surface
            // key, destination, LABELS ONLY; Presence left null for E5 (E4-07 option b).
            foreach (var foreign in _foreignCarriers)
            {
                if (emittedIds.Contains(foreign.CarrierId))
                    continue;
                // Skip if this id is also a display contender (already emitted).
                if (_contendersByCarrierId.ContainsKey(foreign.CarrierId))
                    continue;

                snapshots.TryGetValue(foreign.CarrierId, out var snap);
                CarrierRowLabels labels = foreign.StaticLabels;
                if (_dismissalLatches.Contains(foreign.CarrierId))
                    labels |= CarrierRowLabels.Dismissed;

                list.Add(new CarrierResolutionStatus(
                    foreign.CarrierId,
                    foreign.SurfaceId,
                    foreign.DestinationId,
                    presence: null,
                    snap.CarrierId != null ? snap.RemainingMs : null,
                    labels));
                emittedIds.Add(foreign.CarrierId);
            }

            // Rest status: never Outranked — OnScreen when the floor is showing,
            // OffScreen otherwise (spec §5: fixed floor, not a row that can be outranked).
            list.Add(new CarrierResolutionStatus(
                RestCarrierId, DisplaySurfaceId,
                _defaultInSessionDestinationId,
                string.Equals(winningCarrier, RestCarrierId, StringComparison.Ordinal)
                    ? CarrierPresence.OnScreen
                    : CarrierPresence.OffScreen,
                null, CarrierRowLabels.None));

            return list;
        }

        // ── Plans / runtime records ──────────────────────────────────────

        private enum ContenderKind
        {
            Summon,
            Derived,
            ChildRefSatellite,
            Manual,
        }

        private sealed class ContenderPlan
        {
            public string CarrierId;
            public string RowId;
            public int Rank;
            public string DestinationId;
            public ContenderKind Kind;
            public bool Competes = true;
            public string SurfaceId = DisplaySurfaceId;
            public CarrierRowLabels StaticLabels;
        }

        private sealed class RowPlan
        {
            public int Rank;
            public string RowId;
            public PriorityRowKind Kind;
            public string DestinationId;
            public List<string> SummonIds = new List<string>();
            public string DerivedCarrierId;
            public bool DegradedAtLoad;
        }

        private sealed class AggregatePlan
        {
            public string SeatId;
            public string DestinationId;
            public int Rank;
            public string DerivedCarrierId;
            public List<string> MemberCarrierIds;
            public CarrierLifetimeKind LifetimeKind;
            public int DurationMs;
            public bool MembershipDegraded;
        }

        private sealed class AggregateRuntime
        {
            public CarrierSpec Spec;
            public CarrierRuntime Runtime;
        }

        private sealed class CycleRuntime
        {
            public long AnchorMs;
        }

        private sealed class LogicalWinner
        {
            public int Rank;
            public string RowId;
            public string CarrierId;
            public string DestinationId;
        }

        private sealed class ForeignCarrierPlan
        {
            public string CarrierId;
            public string SurfaceId;
            public string DestinationId;
            public CarrierRowLabels StaticLabels;
        }
    }
}
