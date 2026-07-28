using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Shared <c>priority.rest.idle</c> compile (E7 / contract §6.2). One helper consumed
    /// by both <see cref="SeatArbiter"/> (idle publishing) and
    /// <see cref="WheelScreenArbiter"/> (floor selection) so a degrade cannot diverge.
    /// </summary>
    public static class IdleCompile
    {
        /// <summary>
        /// Resolve idle into the three-branch blank table plus page/screen outcomes.
        /// Absent or degraded idle compiles as blank (capability + park still apply).
        /// Returns a readonly struct — zero per-tick heap allocation.
        /// </summary>
        /// <param name="idle">Document idle (may be null / degraded).</param>
        /// <param name="screenCommands">
        /// Catalog screen-command capability (tri-state). Null = every command untested.
        /// Unused by seat publishing; required for the blank capability split on the
        /// wheel-screen floor.
        /// </param>
        public static IdleCompileResult Resolve(
            IdleSpec idle,
            ScreenCommandsCapability screenCommands = null)
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

                default:
                    // Unknown kind: treat as degraded → blank.
                    return CompileBlank(idle.ParkOnLegacyForBlank, screenCommands);
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
        /// Document-level idle kind for seat publishing (E4 <c>IdleKind</c> survives for
        /// page / painted / park / blank / screen — same as pre-helper mapping).
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
