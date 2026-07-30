using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Pure wheel-screen plane arbiter (phase E6). Ranked <c>wheelScreen.rules</c> over the
    /// idle floor (<c>priority.rest.idle</c>), with v9 special-channel wire laws:
    /// win-edge send once, keepalive every <see cref="SpecialCommands.KeepaliveMs"/> while
    /// held, declined send does not latch, release arms reclaim (contract §6.2).
    ///
    /// Not wired to director/runtime — tick tests only. Catalog capability is injected
    /// (E0 envelope); no local capability tables.
    /// </summary>
    public sealed class WheelScreenArbiter
    {
        /// <summary>Surface key for this plane in <see cref="ComposedResolutionRecord"/>.</summary>
        public static string SurfaceId => DestinationIds.WheelScreenSurfaceId;

        /// <summary>
        /// Carrier id used when the idle floor owns the plane (screen/blank).
        /// Reserved spelling <see cref="DestinationIds.RestIdle"/> — never collides with
        /// authored rule ids (validator reserves the <c>rest:</c> family).
        /// </summary>
        public static string IdleFloorCarrierId => DestinationIds.RestIdle;

        private readonly DisplayConfigV2 _config;
        private readonly string _deviceKey;
        private readonly ScreenCommandsCapability _screenCommands;
        private readonly bool _isItmWheel;
        private readonly Action<string> _warn;
        private readonly HashSet<string> _warnedKeys = new HashSet<string>(StringComparer.Ordinal);

        private readonly List<RulePlan> _rules = new List<RulePlan>();
        private readonly IdleSpec _idle;
        private readonly Dictionary<string, PlaylistEntry> _playlists =
            new Dictionary<string, PlaylistEntry>(StringComparer.OrdinalIgnoreCase);

        // ── Latch / keepalive (session) state — accepted-send only ──────
        private bool _latched;
        private WheelScreenCommand _latchedCommand = WheelScreenCommand.Unknown;
        private string _latchedRuleId;
        private long _sentAtMs;

        /// <summary>
        /// Playlist program anchor — set on idle ENTRY, cleared on re-entry to session
        /// (OQ-P1 RESTART). Runtime-only; never persisted.
        /// </summary>
        private long? _playlistAnchorMs;
        private bool _wasIdle;

        private bool _pendingSend;
        private WheelScreenCommand _pendingCommand = WheelScreenCommand.Unknown;
        private string _pendingRuleId;
        private long _pendingAtMs;

        /// <summary>
        /// Builds an arbiter over a NORMALIZED <see cref="DisplayConfigV2"/>
        /// (<see cref="DisplayConfigV2Validator.Normalize"/> already applied).
        /// </summary>
        public WheelScreenArbiter(DisplayConfigV2 config, WheelScreenArbiterOptions options = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            options = options ?? new WheelScreenArbiterOptions();
            _deviceKey = options.DeviceKey ?? "";
            _screenCommands = options.ScreenCommands;
            _isItmWheel = options.IsItmWheel;
            _warn = options.Warn;
            _idle = _config.Priority?.Rest?.Idle;

            if (_config.Playlists != null)
            {
                foreach (var pl in _config.Playlists)
                {
                    if (pl?.Id != null && !pl.DegradedAtLoad)
                        _playlists[pl.Id] = pl;
                }
            }

            BuildPlans();
        }

        /// <summary>Evaluate one tick. Deterministic given the same input sequence + clock.</summary>
        public WheelScreenArbiterTickResult Tick(WheelScreenArbiterTickInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            long now = input.NowMs;
            bool inGame = input.InGame;
            var snapshots = IndexSnapshots(input.CarrierSnapshots);
            var dismissed = IndexDismissed(input.DismissedCarrierIds);

            // Playlist clock: RESTART on every idle re-entry (OQ-P1).
            UpdatePlaylistAnchor(inGame, now);

            // 1. Apply prior-tick send feedback (declined = no latch; accepted = latch + stamp).
            ApplySendFeedback(input.PreviousSendAccepted);

            // 2. Ranked rules over idle floor → desired winner.
            var winner = SelectWinner(
                inGame, snapshots, dismissed, now, input.SeatManualOwnsDisplay);

            // 3. Release edge: latched screen and plane no longer holds a screen.
            bool surfaceHeld = winner.Kind == WheelScreenOutcomeKind.Screen;
            bool releaseEdge = false;
            if (_latched && !surfaceHeld)
            {
                releaseEdge = true;
                _latched = false;
                _latchedCommand = WheelScreenCommand.Unknown;
                _latchedRuleId = null;
                _pendingSend = false;
            }

            // 4. Win-edge / keepalive send signals (only while surface held).
            bool sendRequested = false;
            WheelScreenCommand? sendCommand = null;
            byte? sendPattern = null;
            string sendCarrierId = null;

            // Win-edge fires on command change even when the carrier is unchanged
            // (E6 law: _latchedCommand != cmd). Playlist step boundaries that keep the
            // same idle-floor carrier (e.g. logo → blank) therefore re-send correctly;
            // screen → page releases col01, page → screen reclaims via surfaceHeld edge.
            if (surfaceHeld && winner.Command.HasValue
                && winner.Command.Value != WheelScreenCommand.Unknown)
            {
                var cmd = winner.Command.Value;
                string ruleId = winner.CarrierId;
                bool winEdge = !_latched
                    || _latchedCommand != cmd
                    || !string.Equals(_latchedRuleId, ruleId, StringComparison.Ordinal);
                bool keepaliveDue = _latched
                    && now - _sentAtMs >= SpecialCommands.KeepaliveMs;

                if (winEdge || keepaliveDue)
                {
                    sendRequested = true;
                    sendCommand = cmd;
                    sendPattern = PatternOf(cmd);
                    sendCarrierId = ruleId;
                    _pendingSend = true;
                    _pendingCommand = cmd;
                    _pendingRuleId = ruleId;
                    _pendingAtMs = now;
                }
            }

            // 5. Record slice (presence owned only for this surface).
            var statuses = BuildStatuses(snapshots, dismissed, winner);
            var snapsList = input.CarrierSnapshots ?? Array.Empty<CarrierTickSnapshot>();
            var winners = new List<SurfaceWinner>
            {
                new SurfaceWinner(
                    SurfaceId,
                    winner.Kind == WheelScreenOutcomeKind.Screen ? winner.CarrierId : null,
                    winner.DestinationId),
            };
            var resolution = new ComposedResolutionRecord(
                now, _deviceKey, winners, statuses, snapsList);

            var intent = new WheelScreenIntent
            {
                Kind = winner.Kind,
                DeferReason = winner.DeferReason,
                Command = winner.Command,
                WinnerCarrierId = winner.CarrierId,
                DestinationId = winner.DestinationId,
                LatchedCommand = _latched ? _latchedCommand : (WheelScreenCommand?)null,
                Latched = _latched,
            };

            return new WheelScreenArbiterTickResult
            {
                Resolution = resolution,
                Intent = intent,
                SurfaceHeld = surfaceHeld,
                ReleaseEdge = releaseEdge,
                SendRequested = sendRequested,
                SendCommand = sendCommand,
                SendPattern = sendPattern,
                SendCarrierId = sendCarrierId,
                WinnerCapabilityUntested = winner.CapabilityUntested,
            };
        }

        // ── Construction ─────────────────────────────────────────────────

        private void BuildPlans()
        {
            var rules = _config.WheelScreen?.Rules;
            if (rules != null)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    if (rule == null || string.IsNullOrEmpty(rule.Id))
                        continue;

                    bool degraded = rule.DegradedAtLoad || !rule.Enabled
                        || rule.Screen == WheelScreenCommand.Unknown;
                    bool? supported = ScreenSupported(rule.Screen);
                    if (supported == false)
                        degraded = true;

                    CarrierRowLabels staticLabels = CarrierRowLabels.None;
                    if (!rule.Enabled)
                        staticLabels |= CarrierRowLabels.Off;
                    if (supported == false || rule.DegradedAtLoad
                        || rule.Screen == WheelScreenCommand.Unknown)
                        staticLabels |= CarrierRowLabels.CantRunHere | CarrierRowLabels.KeptAsIs;

                    // Runtime-diagnostics echo; key by rule id so every untested rule is named.
                    // Authoring-time voice is the validator when a catalog is present.
                    if (supported == null && rule.Screen != WheelScreenCommand.Unknown)
                    {
                        WarnOnce(
                            "screen-untested-rule:" + rule.Id,
                            "wheel-screen rule '" + rule.Id
                            + "': screen capability is untested (null) — not gated (runtime)");
                    }

                    _rules.Add(new RulePlan
                    {
                        Id = rule.Id,
                        Screen = rule.Screen,
                        Rank = i,
                        Competes = !degraded && rule.EffectivelyEnabled
                            && rule.Screen != WheelScreenCommand.Unknown
                            && supported != false,
                        StaticLabels = staticLabels,
                        CapabilityUntested = supported == null
                            && rule.Screen != WheelScreenCommand.Unknown,
                    });
                }
            }

            // Idle capability warning at construction (not deferred to first out-of-session tick).
            EmitIdleCapabilityWarningsAtBuild();
        }

        private void EmitIdleCapabilityWarningsAtBuild()
        {
            // Absent idle compiles to blank at runtime; warn on blank capability now.
            if (_idle == null || _idle.DegradedAtLoad)
            {
                EmitIdleScreenCapabilityWarn(WheelScreenCommand.Blank, "rest.idle (default blank)");
                return;
            }

            switch (_idle.Kind)
            {
                case IdleKind.Blank:
                    if (!_idle.ParkOnLegacyForBlank)
                        EmitIdleScreenCapabilityWarn(WheelScreenCommand.Blank, "rest.idle blank");
                    break;
                case IdleKind.Screen:
                    if (!_idle.ScreenIgnored && _idle.Screen != WheelScreenCommand.Unknown)
                        EmitIdleScreenCapabilityWarn(_idle.Screen, "rest.idle screen");
                    break;
            }
        }

        private void EmitIdleScreenCapabilityWarn(WheelScreenCommand cmd, string label)
        {
            bool? supported = ScreenSupported(cmd);
            if (supported != null)
                return;
            WarnOnce(
                "idle-untested:" + cmd,
                label + " capability is untested (null) — not gated (runtime)");
        }

        // ── Selection ────────────────────────────────────────────────────

        private Winner SelectWinner(
            bool inGame,
            Dictionary<string, CarrierTickSnapshot> snapshots,
            HashSet<string> dismissed,
            long nowMs,
            bool seatManualOwnsDisplay = false)
        {
            // Rules rank by array order over the idle floor.
            foreach (var plan in _rules)
            {
                if (!plan.Competes)
                    continue;

                snapshots.TryGetValue(plan.Id, out var snap);
                bool hasSnap = snap.CarrierId != null;
                if (!hasSnap || !snap.Active || !snap.Eligible)
                    continue;

                // untilDismissed: DismissedCarrierIds suppresses unless FreshFire re-arms.
                if (dismissed.Contains(plan.Id) && !snap.FreshFire)
                    continue;

                return Winner.ForScreen(plan.Id, plan.Screen, plan.CapabilityUntested);
            }

            // Idle floor only out of session; in-session silence unless a rule won.
            if (inGame)
                return Winner.Silence();

            // The FLOOR yields while the seat's manual row owns the display — a
            // manual press must page even over a blank/logo idle choice. Ranked
            // rules above already had their chance (rules-over-rest unchanged).
            if (seatManualOwnsDisplay)
                return Winner.Deferred(WheelScreenDeferReason.PageIdle);

            return SelectIdleFloor(nowMs);
        }

        private Winner SelectIdleFloor(long nowMs)
        {
            // Shared IdleCompile helper (E7 / contract §6.2) — same reader as SeatArbiter.
            // Absent or degraded rest.idle = blank floor; Silence is not the default.
            // Playlist expands here: active step's compile result, never raw playlist kind.
            var compiled = IdleCompile.Resolve(
                _idle, _screenCommands, _playlists, nowMs, _playlistAnchorMs,
                isItmWheel: _isItmWheel);
            switch (compiled.Kind)
            {
                case IdleCompileKind.Page:
                    return Winner.Deferred(WheelScreenDeferReason.PageIdle);

                case IdleCompileKind.ParkOnLegacyForBlank:
                    return Winner.Deferred(WheelScreenDeferReason.ParkOnLegacyForBlank);

                case IdleCompileKind.PaintBlankFrame:
                    return Winner.Deferred(WheelScreenDeferReason.PaintBlankFrame);

                case IdleCompileKind.Silence:
                    return Winner.Silence();

                case IdleCompileKind.FirmwareBlank:
                case IdleCompileKind.FirmwareScreen:
                    return Winner.ForScreen(
                        IdleFloorCarrierId,
                        compiled.ScreenCommand ?? WheelScreenCommand.Blank,
                        compiled.CapabilityUntested);

                default:
                    return Winner.ForScreen(
                        IdleFloorCarrierId, WheelScreenCommand.Blank, capabilityUntested: true);
            }
        }

        /// <summary>
        /// Playlist program clock (OQ-P1): anchor at idle ENTRY, clear when in-session.
        /// Fresh idle fire restarts the program from step 0.
        /// </summary>
        private void UpdatePlaylistAnchor(bool inGame, long now)
        {
            if (inGame)
            {
                _wasIdle = false;
                _playlistAnchorMs = null;
                return;
            }

            if (!_wasIdle)
            {
                _playlistAnchorMs = now;
                _wasIdle = true;
            }
        }

        // ── Send feedback / latch ────────────────────────────────────────

        private void ApplySendFeedback(bool? previousSendAccepted)
        {
            if (!_pendingSend)
                return;

            if (previousSendAccepted == true)
            {
                _latched = true;
                _latchedCommand = _pendingCommand;
                _latchedRuleId = _pendingRuleId;
                _sentAtMs = _pendingAtMs;
            }
            // false / null: win-edge stays unlatched; keepalive keeps old stamp (latched).
            _pendingSend = false;
        }

        // ── Record slice ─────────────────────────────────────────────────

        private List<CarrierResolutionStatus> BuildStatuses(
            Dictionary<string, CarrierTickSnapshot> snapshots,
            HashSet<string> dismissed,
            Winner winner)
        {
            var list = new List<CarrierResolutionStatus>(
                _rules.Count + 1);
            string winId = winner.Kind == WheelScreenOutcomeKind.Screen
                ? winner.CarrierId
                : null;

            foreach (var plan in _rules)
            {
                snapshots.TryGetValue(plan.Id, out var snap);
                bool hasSnap = snap.CarrierId != null;
                bool active = hasSnap && snap.Active;
                bool eligible = !hasSnap || snap.Eligible;
                bool latchedDismissed = dismissed.Contains(plan.Id)
                    && !(hasSnap && snap.FreshFire);

                CarrierRowLabels labels = plan.StaticLabels;
                CarrierPresence presence;
                string dest = DestinationFor(plan.Screen);

                if (!plan.Competes)
                {
                    presence = CarrierPresence.OffScreen;
                }
                else if (string.Equals(plan.Id, winId, StringComparison.Ordinal))
                {
                    // OnScreen only when a screen is held (SurfaceHeld) — one OnScreen
                    // on this surface when a screen is the winner.
                    presence = CarrierPresence.OnScreen;
                    if (plan.CapabilityUntested)
                        labels |= CarrierRowLabels.Untested;
                }
                else if (latchedDismissed)
                {
                    // REALIGNMENT #1: latched + Active+Eligible → Dismissed (first-class
                    // presence; not Outranked). Label stamped alongside for consistency.
                    labels |= CarrierRowLabels.Dismissed;
                    presence = active && eligible
                        ? CarrierPresence.Dismissed
                        : CarrierPresence.Waiting;
                }
                else if (hasSnap && !eligible)
                {
                    presence = CarrierPresence.Waiting;
                    labels |= CarrierRowLabels.OutOfSessionScope;
                }
                else if (!active || !eligible)
                {
                    presence = CarrierPresence.Waiting;
                }
                else
                {
                    presence = CarrierPresence.Outranked;
                }

                int? remaining = hasSnap ? snap.RemainingMs : null;
                list.Add(new CarrierResolutionStatus(
                    plan.Id, SurfaceId, dest, presence, remaining, labels));
            }

            // Idle floor row: OnScreen when the floor holds a screen; OffScreen otherwise.
            // Never Outranked (fixed floor, same law as seat rest).
            bool floorOnScreen = string.Equals(winId, IdleFloorCarrierId, StringComparison.Ordinal);
            string floorDest = floorOnScreen ? winner.DestinationId : null;
            CarrierRowLabels floorLabels = CarrierRowLabels.None;
            if (floorOnScreen && winner.CapabilityUntested)
                floorLabels |= CarrierRowLabels.Untested;
            list.Add(new CarrierResolutionStatus(
                IdleFloorCarrierId,
                SurfaceId,
                floorDest,
                floorOnScreen ? CarrierPresence.OnScreen : CarrierPresence.OffScreen,
                null,
                floorLabels));

            return list;
        }

        // ── Capability ───────────────────────────────────────────────────

        private bool? ScreenSupported(WheelScreenCommand cmd)
        {
            var sc = _screenCommands;
            if (sc == null)
                return null;
            switch (cmd)
            {
                case WheelScreenCommand.Logo: return sc.Logo;
                case WheelScreenCommand.Blank: return sc.Blank;
                case WheelScreenCommand.White: return sc.White;
                case WheelScreenCommand.LogoInverted: return sc.LogoInverted;
                default: return null;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private void WarnOnce(string key, string message)
        {
            if (_warn == null || !_warnedKeys.Add(key))
                return;
            _warn(message);
        }

        private static Dictionary<string, CarrierTickSnapshot> IndexSnapshots(
            IReadOnlyList<CarrierTickSnapshot> snaps)
        {
            var map = new Dictionary<string, CarrierTickSnapshot>(StringComparer.Ordinal);
            if (snaps == null)
                return map;
            foreach (var s in snaps)
            {
                if (s.CarrierId != null)
                    map[s.CarrierId] = s;
            }
            return map;
        }

        private static HashSet<string> IndexDismissed(IReadOnlyCollection<string> ids)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (ids == null)
                return set;
            foreach (var id in ids)
            {
                if (id != null)
                    set.Add(id);
            }
            return set;
        }

        /// <summary>
        /// Firmware pattern byte for a known <see cref="WheelScreenCommand"/>.
        /// Returns null for <see cref="WheelScreenCommand.Unknown"/> (matches
        /// <see cref="ScreenSpelling"/>'s null-for-unknown convention — never invent blank).
        /// </summary>
        public static byte? PatternOf(WheelScreenCommand command)
        {
            switch (command)
            {
                case WheelScreenCommand.Logo: return SpecialCommands.PatternLogo;
                case WheelScreenCommand.LogoInverted: return SpecialCommands.PatternLogoInverted;
                case WheelScreenCommand.White: return SpecialCommands.PatternWhite;
                case WheelScreenCommand.Blank: return SpecialCommands.PatternBlank;
                default: return null;
            }
        }

        /// <summary>Document spelling for destination identity / diagnostics.</summary>
        public static string ScreenSpelling(WheelScreenCommand command)
        {
            switch (command)
            {
                case WheelScreenCommand.Logo: return "logo";
                case WheelScreenCommand.LogoInverted: return "logoInverted";
                case WheelScreenCommand.White: return "white";
                case WheelScreenCommand.Blank: return "blank";
                default: return null;
            }
        }

        /// <summary>User-facing label (delegates to <see cref="SpecialCommands.Label"/>).</summary>
        public static string LabelOf(WheelScreenCommand command)
            => SpecialCommands.Label(ToSpecial(command));

        private static SpecialCommand ToSpecial(WheelScreenCommand command)
        {
            switch (command)
            {
                case WheelScreenCommand.Logo: return SpecialCommand.LogoScreen;
                case WheelScreenCommand.LogoInverted: return SpecialCommand.LogoInvertedScreen;
                case WheelScreenCommand.White: return SpecialCommand.WhiteScreen;
                case WheelScreenCommand.Blank: return SpecialCommand.BlankScreen;
                default: return SpecialCommand.Unknown;
            }
        }

        private static string DestinationFor(WheelScreenCommand command)
        {
            string spelling = ScreenSpelling(command);
            return spelling == null ? null : DestinationIds.Screen(spelling);
        }

        private sealed class RulePlan
        {
            public string Id;
            public WheelScreenCommand Screen;
            public int Rank;
            public bool Competes;
            public CarrierRowLabels StaticLabels;
            public bool CapabilityUntested;
        }

        private readonly struct Winner
        {
            public WheelScreenOutcomeKind Kind { get; }
            public WheelScreenDeferReason DeferReason { get; }
            public string CarrierId { get; }
            public WheelScreenCommand? Command { get; }
            public string DestinationId { get; }
            public bool CapabilityUntested { get; }

            private Winner(
                WheelScreenOutcomeKind kind,
                WheelScreenDeferReason deferReason,
                string carrierId,
                WheelScreenCommand? command,
                string destinationId,
                bool capabilityUntested)
            {
                Kind = kind;
                DeferReason = deferReason;
                CarrierId = carrierId;
                Command = command;
                DestinationId = destinationId;
                CapabilityUntested = capabilityUntested;
            }

            public static Winner Silence()
                => new Winner(
                    WheelScreenOutcomeKind.Silence, WheelScreenDeferReason.None,
                    null, null, null, false);

            public static Winner Deferred(WheelScreenDeferReason reason)
                => new Winner(
                    WheelScreenOutcomeKind.DeferredToDisplayPlane, reason,
                    null, null, null, false);

            public static Winner ForScreen(
                string carrierId, WheelScreenCommand command, bool capabilityUntested)
                => new Winner(
                    WheelScreenOutcomeKind.Screen,
                    WheelScreenDeferReason.None,
                    carrierId,
                    command,
                    DestinationFor(command),
                    capabilityUntested);
        }
    }
}
