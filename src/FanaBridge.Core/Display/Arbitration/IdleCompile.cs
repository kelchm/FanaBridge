using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Shared <c>priority.rest.idle</c> compile (E7 / contract §6.2). One helper consumed
    /// by both <see cref="SeatArbiter"/> (idle publishing) and
    /// <see cref="WheelScreenArbiter"/> (floor selection) so a degrade cannot diverge.
    /// Playlists expand here (amendment A1 §7): capability filter → step selection →
    /// publish the ACTIVE STEP's ordinary compile result (never <see cref="IdleKind.Playlist"/>).
    /// </summary>
    public static class IdleCompile
    {
        /// <summary>
        /// Resolve idle into the three-branch blank table plus page/screen outcomes.
        /// Absent or degraded idle compiles as blank (capability + park still apply).
        /// Playlist kind expands via <paramref name="playlists"/> + clock
        /// (<paramref name="nowMs"/> − <paramref name="anchorMs"/>); anchor is idle-entry
        /// (restart on re-entry, OQ-P1).
        /// Returns a readonly struct — zero per-tick heap allocation on the non-playlist path.
        /// </summary>
        /// <param name="idle">Document idle (may be null / degraded).</param>
        /// <param name="screenCommands">
        /// Catalog screen-command capability (tri-state). Null = every command untested.
        /// Unused by seat publishing; required for the blank capability split on the
        /// wheel-screen floor and for playlist step filtering.
        /// </param>
        /// <param name="playlists">Id → entry map (case-insensitive). Null/empty = no programs.</param>
        /// <param name="nowMs">Arbiter clock (ms).</param>
        /// <param name="anchorMs">
        /// Idle-entry program anchor. Null when not yet entered idle; treated as
        /// <paramref name="nowMs"/> (step 0) so a first tick still resolves.
        /// </param>
        public static IdleCompileResult Resolve(
            IdleSpec idle,
            ScreenCommandsCapability screenCommands = null,
            IReadOnlyDictionary<string, PlaylistEntry> playlists = null,
            long nowMs = 0,
            long? anchorMs = null)
        {
            // Absent / degraded → blank path (spec §10 / §14).
            if (idle == null || idle.DegradedAtLoad)
                return CompileBlank(parkOnLegacy: false, screenCommands);

            switch (idle.Kind)
            {
                case IdleKind.Page:
                    return IdleCompileResult.Page(
                        DestinationIds.FromPageRef(idle.Page));

                case IdleKind.Screen:
                    if (idle.ScreenIgnored || idle.Screen == WheelScreenCommand.Unknown)
                        return CompileBlank(idle.ParkOnLegacyForBlank, screenCommands);
                    return CompileScreen(idle.Screen, screenCommands);

                case IdleKind.Blank:
                    return CompileBlank(idle.ParkOnLegacyForBlank, screenCommands);

                case IdleKind.Playlist:
                    return ResolvePlaylist(
                        idle, screenCommands, playlists, nowMs, anchorMs);

                default:
                    // Unknown kind: treat as degraded → blank.
                    return CompileBlank(idle.ParkOnLegacyForBlank, screenCommands);
            }
        }

        /// <summary>
        /// Expand a playlist idle: capability-filter first, then pick the active step
        /// from elapsed time, then compile that step's destination. All-skipped → idle
        /// floor (CompileBlank), never Silence (P6 rider a).
        /// </summary>
        private static IdleCompileResult ResolvePlaylist(
            IdleSpec idle,
            ScreenCommandsCapability screenCommands,
            IReadOnlyDictionary<string, PlaylistEntry> playlists,
            long nowMs,
            long? anchorMs)
        {
            // Floor for missing / degraded / all-skipped programs: same blank compile
            // the document's blank idle would produce — including ParkOnLegacyForBlank
            // on command-less ITM (validator stamps the flag on playlist idle too).
            bool floorPark = idle.ParkOnLegacyForBlank;

            if (string.IsNullOrWhiteSpace(idle.Playlist)
                || playlists == null
                || !playlists.TryGetValue(idle.Playlist, out var playlist)
                || playlist == null
                || playlist.DegradedAtLoad)
            {
                return CompileBlank(floorPark, screenCommands);
            }

            // 1) Capability FILTER first — skipped steps contribute no time.
            // Stack-allocated small filter via List only when steps exist (cold path).
            var survivors = FilterSurvivingSteps(playlist, screenCommands);
            if (survivors == null || survivors.Count == 0)
            {
                // P6 rider (a): all-skipped → idle floor, never Silence.
                return CompileBlank(floorPark, screenCommands);
            }

            long anchor = anchorMs ?? nowMs;
            long elapsed = nowMs - anchor;
            if (elapsed < 0)
                elapsed = 0;

            var terminal = playlist.Terminal;
            if (terminal == PlaylistTerminal.Unknown)
                terminal = PlaylistTerminal.Hold; // runtime coerce; raw preserved on entry

            int activeIndex = SelectActiveStepIndex(survivors, elapsed, terminal);
            var active = survivors[activeIndex];
            return CompileStepDestination(active.Destination, screenCommands);
        }

        /// <summary>
        /// Filter steps the wheel can render. <c>false</c> capability drops;
        /// <c>null</c> untested does NOT drop. Degraded / nested-playlist / untimeable
        /// steps also drop.
        /// </summary>
        private static List<PlaylistStep> FilterSurvivingSteps(
            PlaylistEntry playlist, ScreenCommandsCapability sc)
        {
            if (playlist.Steps == null || playlist.Steps.Count == 0)
                return null;

            var survivors = new List<PlaylistStep>(playlist.Steps.Count);
            bool isHold = playlist.Terminal != PlaylistTerminal.Loop;
            // Under hold, the last non-degraded-destination step can omit duration.
            // Under loop, every step needs a duration to contribute time.

            // First pass: collect non-degraded destination steps (capability filter).
            var candidates = new List<PlaylistStep>(playlist.Steps.Count);
            for (int i = 0; i < playlist.Steps.Count; i++)
            {
                var step = playlist.Steps[i];
                if (step == null || step.DegradedAtLoad)
                    continue;
                if (!StepDestinationSurvives(step.Destination, sc))
                    continue;
                candidates.Add(step);
            }

            if (candidates.Count == 0)
                return survivors;

            for (int i = 0; i < candidates.Count; i++)
            {
                var step = candidates[i];
                bool isFinal = i == candidates.Count - 1;
                if (!step.DurationMsPresent)
                {
                    // OQ-P3: absent duration legal on held final under hold; otherwise skip.
                    if (isHold && isFinal)
                    {
                        survivors.Add(step);
                        continue;
                    }
                    continue; // untimeable — skip
                }
                survivors.Add(step);
            }

            return survivors;
        }

        /// <summary>
        /// Whether a step destination can render on this wheel. Nested playlist /
        /// unknown / missing → false. Capability <c>false</c> → false; null untested
        /// → true (warn-and-allow).
        /// </summary>
        internal static bool StepDestinationSurvives(
            IdleSpec dest, ScreenCommandsCapability sc)
        {
            if (dest == null || dest.DegradedAtLoad)
                return false;

            switch (dest.Kind)
            {
                case IdleKind.Blank:
                    return true; // blank always has a compile path (firmware/paint/park)

                case IdleKind.Screen:
                    if (dest.ScreenIgnored || dest.Screen == WheelScreenCommand.Unknown)
                        return false;
                    // Blank-as-screen is illegal on idle; same for steps.
                    if (dest.Screen == WheelScreenCommand.Blank)
                        return false;
                    bool? supported = CapabilityOf(sc, dest.Screen);
                    // false drops; null untested does NOT drop.
                    return supported != false;

                case IdleKind.Page:
                    if (dest.Page == null || dest.Page.DegradedAtLoad)
                        return false;
                    return DestinationIds.FromPageRef(dest.Page) != null;

                case IdleKind.Playlist:
                    // No nesting.
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Walk surviving step durations against elapsed. Hold pins the last step;
        /// loop takes elapsed modulo the program length. Floor clamp is runtime-only
        /// (SeatArbiter.MinDwellMs) — never rewrites the document.
        /// </summary>
        private static int SelectActiveStepIndex(
            List<PlaylistStep> survivors, long elapsed, PlaylistTerminal terminal)
        {
            int n = survivors.Count;
            if (n == 1)
                return 0;

            // Effective durations (clamped at the destination dwell floor).
            long total = 0;
            // Use a small stack array for lengths when possible — list is fine.
            var lengths = new long[n];
            for (int i = 0; i < n; i++)
            {
                long d = EffectiveStepDurationMs(survivors[i], isFinal: i == n - 1, terminal);
                lengths[i] = d;
                total += d;
            }

            if (terminal == PlaylistTerminal.Loop)
            {
                if (total <= 0)
                    return 0;
                long phase = elapsed % total;
                long cursor = 0;
                for (int i = 0; i < n; i++)
                {
                    cursor += lengths[i];
                    if (phase < cursor)
                        return i;
                }
                return n - 1;
            }

            // hold: walk until elapsed exhausts, then pin last.
            long cursorHold = 0;
            for (int i = 0; i < n - 1; i++)
            {
                cursorHold += lengths[i];
                if (elapsed < cursorHold)
                    return i;
            }
            return n - 1;
        }

        /// <summary>
        /// Effective runtime duration for a surviving step. Destination-switching steps
        /// clamp at <see cref="SeatArbiter.MinDwellMs"/> (P2). Final under hold ignores
        /// duration (infinite hold). Authored value never rewritten.
        /// </summary>
        internal static long EffectiveStepDurationMs(
            PlaylistStep step, bool isFinal, PlaylistTerminal terminal)
        {
            if (isFinal && terminal != PlaylistTerminal.Loop)
            {
                // Held final: duration ignored; contribute a sentinel only for loop math
                // (hold path never uses the final length for wrapping).
                return long.MaxValue / 4;
            }

            if (step == null || !step.DurationMsPresent)
                return SeatArbiter.MinDwellMs;

            int authored = step.DurationMs;
            if (authored < SeatArbiter.MinDwellMs)
            {
                step.DurationClampedAtRuntime = true;
                return SeatArbiter.MinDwellMs;
            }
            return authored;
        }

        private static IdleCompileResult CompileStepDestination(
            IdleSpec dest, ScreenCommandsCapability sc)
        {
            if (dest == null)
                return CompileBlank(parkOnLegacy: false, sc);

            switch (dest.Kind)
            {
                case IdleKind.Page:
                    return IdleCompileResult.Page(DestinationIds.FromPageRef(dest.Page));

                case IdleKind.Screen:
                    return CompileScreen(dest.Screen, sc);

                case IdleKind.Blank:
                    return CompileBlank(dest.ParkOnLegacyForBlank, sc);

                default:
                    return CompileBlank(parkOnLegacy: false, sc);
            }
        }

        private static IdleCompileResult CompileBlank(
            bool parkOnLegacy, ScreenCommandsCapability sc)
        {
            if (parkOnLegacy)
                return IdleCompileResult.ParkOnLegacy();

            bool? blankSupported = CapabilityOf(sc, WheelScreenCommand.Blank);
            if (blankSupported == false)
                return IdleCompileResult.PaintBlankFrame();

            // true or null (untested): firmware blank command, warn-and-allow when null.
            return IdleCompileResult.FirmwareBlank(capabilityUntested: blankSupported == null);
        }

        private static IdleCompileResult CompileScreen(
            WheelScreenCommand cmd, ScreenCommandsCapability sc)
        {
            bool? supported = CapabilityOf(sc, cmd);
            if (supported == false)
            {
                // Non-blank unsupported → inert silence on the wheel-screen plane.
                // Blank unsupported is handled in CompileBlank; named screens go silent.
                // Playlist filter never routes here for unsupported steps (P6).
                return IdleCompileResult.Silence();
            }
            return IdleCompileResult.FirmwareScreen(cmd, capabilityUntested: supported == null);
        }

        /// <summary>Tri-state capability for one command (null envelope = untested).</summary>
        public static bool? CapabilityOf(ScreenCommandsCapability sc, WheelScreenCommand cmd)
        {
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
    }

    /// <summary>
    /// Outcome of <see cref="IdleCompile.Resolve"/> (contract §6.2 table).
    /// Readonly struct — no per-tick heap allocation on the arbiter hot path.
    /// </summary>
    public readonly struct IdleCompileResult
    {
        private IdleCompileResult(
            IdleCompileKind kind,
            IdleKind publishedKind,
            WheelScreenCommand? screen,
            string pageDestinationId,
            bool capabilityUntested)
        {
            Kind = kind;
            PublishedIdleKind = publishedKind;
            ScreenCommand = screen;
            PageDestinationId = pageDestinationId;
            CapabilityUntested = capabilityUntested;
        }

        /// <summary>Compiled branch for the wheel-screen / write plane.</summary>
        public IdleCompileKind Kind { get; }

        /// <summary>
        /// Published idle kind for seat publishing — always a step/floor kind
        /// (page / blank / screen), never <see cref="IdleKind.Playlist"/>.
        /// </summary>
        public IdleKind PublishedIdleKind { get; }

        /// <summary>Firmware screen when <see cref="Kind"/> is a screen/blank command.</summary>
        public WheelScreenCommand? ScreenCommand { get; }

        /// <summary>Page destination when <see cref="Kind"/> is <see cref="IdleCompileKind.Page"/>.</summary>
        public string PageDestinationId { get; }

        /// <summary>True when the winning command's capability is untested (null).</summary>
        public bool CapabilityUntested { get; }

        /// <summary>Seat publish: park-on-Legacy blank compile.</summary>
        public bool ParkOnLegacyForBlank
            => Kind == IdleCompileKind.ParkOnLegacyForBlank;

        public static IdleCompileResult FirmwareBlank(bool capabilityUntested)
            => new IdleCompileResult(
                IdleCompileKind.FirmwareBlank,
                IdleKind.Blank,
                WheelScreenCommand.Blank,
                null,
                capabilityUntested);

        public static IdleCompileResult FirmwareScreen(WheelScreenCommand cmd, bool capabilityUntested)
            => new IdleCompileResult(
                IdleCompileKind.FirmwareScreen,
                IdleKind.Screen,
                cmd,
                null,
                capabilityUntested);

        public static IdleCompileResult Page(string pageDestinationId)
            => new IdleCompileResult(
                IdleCompileKind.Page,
                IdleKind.Page,
                null,
                pageDestinationId,
                false);

        public static IdleCompileResult PaintBlankFrame()
            => new IdleCompileResult(
                IdleCompileKind.PaintBlankFrame,
                IdleKind.Blank,
                null,
                null,
                false);

        public static IdleCompileResult ParkOnLegacy()
            => new IdleCompileResult(
                IdleCompileKind.ParkOnLegacyForBlank,
                IdleKind.Blank,
                null,
                null,
                false);

        public static IdleCompileResult Silence()
            => new IdleCompileResult(
                IdleCompileKind.Silence,
                IdleKind.Screen,
                null,
                null,
                false);
    }

    /// <summary>Compiled idle branch (contract §6.2 three-row blank table + page/screen).</summary>
    public enum IdleCompileKind
    {
        /// <summary>Firmware blank command — E6 holds col01, sends Blank.</summary>
        FirmwareBlank = 0,
        /// <summary>Named firmware screen command — E6 holds col01.</summary>
        FirmwareScreen,
        /// <summary><c>rest.idle</c> is a page — display plane owns the wheel.</summary>
        Page,
        /// <summary>Blank absent/false on a segment wheel — E5/E7 paints all-off.</summary>
        PaintBlankFrame,
        /// <summary>Blank absent/false on ITM (<c>ParkOnLegacyForBlank</c>) — park + paint.</summary>
        ParkOnLegacyForBlank,
        /// <summary>Inert: unsupported non-blank screen; plane silent.</summary>
        Silence,
    }
}
