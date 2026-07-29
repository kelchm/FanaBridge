using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Tests.Display.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E6: pure WheelScreenArbiter tests. Ports v9 special-channel laws from
    /// LegacyRuleCol01Tests (Special_*) onto the arbiter, plus idle-floor, runs,
    /// untilDismissed, capability tri-state, and record-slice conventions.
    /// </summary>
    public class WheelScreenArbiterTests
    {
        // ── Snapshot / config helpers ────────────────────────────────────

        private static CarrierTickSnapshot Snap(
            string id, bool active, bool fired = false, bool fresh = false,
            bool eligible = true, int? remaining = null)
            => new CarrierTickSnapshot(
                id, conditionSatisfied: active, active, fresh, fired,
                eligible, expiresAtMs: 0, remaining);

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc)
            => DisplayConfigV2Validator.Normalize(doc, _ => { });

        private static Condition LevelTrue(string? builtIn = null)
            => new Condition
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = builtIn ?? BuiltInProperties.IsInPitLane,
                },
                Operator = ConditionOperator.IsTrue,
            };

        private static WheelScreenRule Rule(
            string id, WheelScreenCommand screen,
            RunsWhen runs = RunsWhen.InGame,
            LifetimeKind life = LifetimeKind.WhileTrue,
            int? durationMs = null,
            bool enabled = true)
        {
            var lifeObj = new Lifetime { Kind = life };
            if (durationMs.HasValue)
                lifeObj.DurationMs = durationMs.Value;
            var r = new WheelScreenRule
            {
                Id = id,
                Screen = screen,
                Condition = LevelTrue(),
                Lifetime = lifeObj,
                Runs = runs,
                Enabled = enabled,
            };
            return r;
        }

        private static DisplayConfigV2 Doc(
            WheelScreenRule[]? rules = null,
            IdleKind idleKind = IdleKind.Blank,
            WheelScreenCommand idleScreen = WheelScreenCommand.Logo,
            bool parkOnLegacy = false,
            bool omitIdle = false,
            bool degradedIdlePage = false)
        {
            IdleSpec? idle = null;
            if (!omitIdle)
            {
                idle = new IdleSpec { Kind = idleKind };
                if (idleKind == IdleKind.Screen)
                    idle.Screen = idleScreen;
                if (idleKind == IdleKind.Page)
                {
                    idle.Page = new PageRef
                    {
                        Kind = PageRefKind.HostedPage,
                        Id = degradedIdlePage ? "p-missing" : "p-a",
                    };
                }
                if (parkOnLegacy)
                    idle.ParkOnLegacyForBlank = true;
            }

            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A",
                    },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = idle,
                    },
                },
                WheelScreen = new WheelScreenPlane
                {
                    Rules = rules != null
                        ? new List<WheelScreenRule>(rules)
                        : new List<WheelScreenRule>(),
                },
            };
            return Normalize(doc);
        }

        private static ScreenCommandsCapability Caps(
            bool? logo = true, bool? blank = true,
            bool? white = true, bool? logoInverted = true)
            => new ScreenCommandsCapability
            {
                Logo = logo,
                Blank = blank,
                White = white,
                LogoInverted = logoInverted,
            };

        private static WheelScreenArbiter Arb(
            DisplayConfigV2? doc = null,
            ScreenCommandsCapability? caps = null,
            Action<string>? warn = null)
        {
            return new WheelScreenArbiter(
                doc ?? Doc(new[] { Rule("s1", WheelScreenCommand.Logo) }),
                new WheelScreenArbiterOptions
                {
                    ScreenCommands = caps ?? Caps(),
                    DeviceKey = "test",
                    Warn = warn,
                });
        }

        private static WheelScreenArbiterTickInput In(
            long now,
            bool inGame = true,
            bool? prevAccepted = null,
            IReadOnlyCollection<string>? dismissed = null,
            params CarrierTickSnapshot[] snaps)
            => new WheelScreenArbiterTickInput
            {
                NowMs = now,
                InGame = inGame,
                PreviousSendAccepted = prevAccepted,
                DismissedCarrierIds = dismissed ?? Array.Empty<string>(),
                CarrierSnapshots = snaps,
            };

        /// <summary>Win + accept on the same logical step (two ticks: request then ack).</summary>
        private static WheelScreenArbiterTickResult WinAndAccept(
            WheelScreenArbiter a, ref long t,
            bool inGame = true,
            params CarrierTickSnapshot[] snaps)
        {
            var r = a.Tick(In(t, inGame, prevAccepted: null, dismissed: null, snaps));
            Assert.True(r.SendRequested);
            t += 16;
            return a.Tick(In(t, inGame, prevAccepted: true, dismissed: null, snaps));
        }

        private static CarrierResolutionStatus StatusOf(
            WheelScreenArbiterTickResult r, string carrierId)
            => r.Resolution.CarrierStatuses.First(s => s.CarrierId == carrierId);

        private sealed class FakeProps : IPropertyReader
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public void Set(string name, double value) => _values[name] = value;

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = (double)raw;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = Math.Abs((double)raw) > 1e-9;
                return true;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Ported v9 special-channel laws (LegacyRuleCol01Tests.Special_*)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Port of Special_WinEdge_SendsLogoFrameOnce_ChangeGatedAcrossHeldTicks.
        /// Win-edge sends once; held ticks between keepalives re-send nothing.
        /// </summary>
        [Fact]
        public void Ported_WinEdge_SendsOnce_ChangeGatedAcrossHeldTicks()
        {
            var a = Arb();
            long t = 0;
            var snaps = new[] { Snap("s1", active: true) };

            var r0 = a.Tick(In(t, snaps: snaps));
            Assert.True(r0.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, r0.SendCommand);
            Assert.Equal(SpecialCommands.PatternLogo, r0.SendPattern);
            Assert.True(r0.SurfaceHeld);
            Assert.False(r0.Intent.Latched); // not latched until accepted

            t += 16;
            var r1 = a.Tick(In(t, prevAccepted: true, snaps: snaps));
            Assert.True(r1.Intent.Latched);
            Assert.False(r1.SendRequested); // held, no keepalive yet

            t += 16;
            var r2 = a.Tick(In(t, snaps: snaps));
            Assert.False(r2.SendRequested);
            Assert.True(r2.SurfaceHeld);
            Assert.Single(r2.Resolution.SurfaceWinners);
        }

        /// <summary>
        /// E6 signal half of Special_Release_ContentReclaims_ByteGolden.
        /// Asserts ReleaseEdge only — full blank-once + content reclaim lands at the
        /// E6/E7 integration boundary (E7 owns col01 writes).
        /// </summary>
        [Fact]
        public void Release_EdgeArmsReclaimSignal()
        {
            var a = Arb();
            long t = 0;
            var live = new[] { Snap("s1", active: true) };
            WinAndAccept(a, ref t, snaps: live);

            t += 2000;
            var released = a.Tick(In(t, snaps: Snap("s1", active: false)));
            Assert.True(released.ReleaseEdge);
            Assert.False(released.SurfaceHeld);
            Assert.False(released.Intent.Latched);
            Assert.Equal(WheelScreenOutcomeKind.Silence, released.Intent.Kind);
            Assert.False(released.SendRequested);
        }

        /// <summary>
        /// E6 signal half of Special_Release_EmptyResolution_WritesBlankOnce.
        /// Subsequent silent ticks stay quiet — byte-golden blank-once is E7.
        /// </summary>
        [Fact]
        public void Release_SubsequentSilentTicksStayQuiet()
        {
            var a = Arb();
            long t = 0;
            WinAndAccept(a, ref t, snaps: Snap("s1", active: true));

            t += 2000;
            var r1 = a.Tick(In(t, snaps: Snap("s1", active: false)));
            Assert.True(r1.ReleaseEdge);

            t += 16;
            var r2 = a.Tick(In(t, snaps: Snap("s1", active: false)));
            Assert.False(r2.ReleaseEdge);
            Assert.False(r2.SendRequested);
            Assert.False(r2.SurfaceHeld);
        }

        /// <summary>
        /// Port of Special_DeclinedSend_RetriesNextTick + Special_NullSink_DoesNotLatch.
        /// Declined (or missing) acceptance does not latch; next tick re-requests.
        /// </summary>
        [Fact]
        public void Ported_DeclinedSend_DoesNotLatch_RetriesNextTick()
        {
            var a = Arb();
            long t = 0;
            var snaps = new[] { Snap("s1", active: true) };

            var r0 = a.Tick(In(t, snaps: snaps));
            Assert.True(r0.SendRequested);
            Assert.False(r0.Intent.Latched);

            t += 16;
            var r1 = a.Tick(In(t, prevAccepted: false, snaps: snaps));
            Assert.False(r1.Intent.Latched);
            Assert.True(r1.SendRequested); // still win-edge (unlatched)
            Assert.True(r1.SurfaceHeld);   // exclusivity while desired

            t += 16;
            var r2 = a.Tick(In(t, prevAccepted: true, snaps: snaps));
            Assert.True(r2.Intent.Latched);
            Assert.False(r2.SendRequested);
        }

        /// <summary>
        /// Port of Special_Keepalive_ResendsHeldScreen_InsideRevertWindow.
        /// Re-send at t+KeepaliveMs; no re-send just before; no re-send after release.
        /// </summary>
        [Fact]
        public void Ported_Keepalive_ResendsAtKeepaliveMs_NotBefore_NotAfterRelease()
        {
            var a = Arb();
            long t = 0;
            var snaps = new[] { Snap("s1", active: true) };

            var r0 = a.Tick(In(t, snaps: snaps));
            Assert.True(r0.SendRequested);
            long sendOrigin = t;

            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: snaps));

            // Just before keepalive — silent.
            t = sendOrigin + SpecialCommands.KeepaliveMs - 16;
            var before = a.Tick(In(t, snaps: snaps));
            Assert.False(before.SendRequested);
            Assert.True(before.Intent.Latched);

            // Due → re-send.
            t = sendOrigin + SpecialCommands.KeepaliveMs;
            var due = a.Tick(In(t, snaps: snaps));
            Assert.True(due.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, due.SendCommand);
            Assert.Equal(SpecialCommands.PatternLogo, due.SendPattern);

            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: snaps));

            // Second keepalive cadence.
            t = sendOrigin + 2L * SpecialCommands.KeepaliveMs;
            var due2 = a.Tick(In(t, snaps: snaps));
            Assert.True(due2.SendRequested);

            // Release — no further keepalives.
            t += 16;
            var rel = a.Tick(In(t, prevAccepted: true, snaps: Snap("s1", active: false)));
            Assert.True(rel.ReleaseEdge);

            t += SpecialCommands.KeepaliveMs;
            var after = a.Tick(In(t, snaps: Snap("s1", active: false)));
            Assert.False(after.SendRequested);
            Assert.False(after.ReleaseEdge);
        }

        /// <summary>
        /// Port of Special_DeclinedTransition_CaptionKeepsAcceptedScreen (mirror truth).
        /// During declined retries of a NEW screen, LatchedCommand stays on the last accept.
        /// </summary>
        [Fact]
        public void Ported_DeclinedTransition_LatchedCommandKeepsAcceptedScreen()
        {
            var doc = Doc(new[]
            {
                Rule("w1", WheelScreenCommand.White),
                Rule("s1", WheelScreenCommand.Logo),
            });
            var a = Arb(doc);
            long t = 0;

            // Logo wins (w1 inactive).
            var logoOnly = new[] { Snap("s1", active: true), Snap("w1", active: false) };
            var both = new[] { Snap("s1", active: true), Snap("w1", active: true) };

            var r0 = a.Tick(In(t, snaps: logoOnly));
            Assert.Equal("s1", r0.Intent.WinnerCarrierId);
            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: logoOnly));

            // White outranks (array order); send declined → latched stays logo.
            t += 16;
            var declined = a.Tick(In(t, snaps: both));
            Assert.Equal("w1", declined.Intent.WinnerCarrierId);
            Assert.Equal(WheelScreenCommand.White, declined.Intent.Command);
            Assert.True(declined.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, declined.Intent.LatchedCommand);

            t += 16;
            var still = a.Tick(In(t, prevAccepted: false, snaps: both));
            Assert.Equal(WheelScreenCommand.Logo, still.Intent.LatchedCommand);
            Assert.True(still.SendRequested);

            t += 16;
            var accepted = a.Tick(In(t, prevAccepted: true, snaps: both));
            Assert.Equal(WheelScreenCommand.White, accepted.Intent.LatchedCommand);
            Assert.False(accepted.SendRequested);
        }

        /// <summary>
        /// Evaluator→arbiter trace of Special_IdleEligible_FiresAtIdle:
        /// RunsWhen produces eligibility (always/idle fire out of session; inGame does not).
        /// </summary>
        [Fact]
        public void Ported_IdleEligible_FiresAtIdle_EvaluatorToArbiterTrace()
        {
            var alwaysRule = Rule("s-always", WheelScreenCommand.Logo, runs: RunsWhen.Always);
            var inGameRule = Rule("s-ingame", WheelScreenCommand.White, runs: RunsWhen.InGame);
            var doc = Doc(new[] { alwaysRule, inGameRule }, idleKind: IdleKind.Blank);
            var a = Arb(doc);

            var alwaysSpec = CarrierSpec.FromV2(
                alwaysRule.Id, alwaysRule.Condition, alwaysRule.Lifetime, alwaysRule.Runs);
            var inGameSpec = CarrierSpec.FromV2(
                inGameRule.Id, inGameRule.Condition, inGameRule.Lifetime, inGameRule.Runs);
            var alwaysRt = new CarrierRuntime();
            var inGameRt = new CarrierRuntime();

            // Out of session, pit-true: always eligible, inGame not.
            var idleProps = new FakeProps();
            idleProps.Set(BuiltInProperties.IsInPitLane, 1.0);
            CarrierEvaluator.Evaluate(alwaysSpec, alwaysRt, new CarrierTickInput
            {
                NowMs = 0, InGame = false, Properties = idleProps,
            }, warnMissing: null);
            CarrierEvaluator.Evaluate(inGameSpec, inGameRt, new CarrierTickInput
            {
                NowMs = 0, InGame = false, Properties = idleProps,
            }, warnMissing: null);
            Assert.True(alwaysRt.EligibleNow);
            Assert.False(inGameRt.EligibleNow);

            var snaps = new[]
            {
                CarrierTickSnapshot.From(alwaysSpec, alwaysRt, 0),
                CarrierTickSnapshot.From(inGameSpec, inGameRt, 0),
            };
            var r = a.Tick(In(0, inGame: false, snaps: snaps));
            Assert.True(r.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, r.SendCommand);
            Assert.Equal("s-always", r.Intent.WinnerCarrierId);
            Assert.True(r.SurfaceHeld);

            // inGame rule alone out of session must not win (falls to blank floor).
            var onlyInGame = new[] { CarrierTickSnapshot.From(inGameSpec, inGameRt, 0) };
            var floor = a.Tick(In(16, inGame: false, prevAccepted: false, snaps: onlyInGame));
            Assert.Equal(WheelScreenArbiter.IdleFloorCarrierId, floor.Intent.WinnerCarrierId);
            Assert.Equal(WheelScreenCommand.Blank, floor.Intent.Command);
        }

        /// <summary>
        /// Merge-law record half of Special_Snapshot_BlankSegments_AndCommandLabelCaption.
        /// One OnScreen on the wheel-screen surface when a screen is held.
        /// Blank mirror segments / accepted caption / command label land at the E7
        /// mirror/output owner (not this arbiter slice).
        /// </summary>
        [Fact]
        public void RecordSlice_OneOnScreenWhenScreenHeld()
        {
            var a = Arb();
            long t = 0;
            var r = WinAndAccept(a, ref t, snaps: Snap("s1", active: true));

            Assert.Equal(CarrierPresence.OnScreen, StatusOf(r, "s1").Presence);
            Assert.Equal(WheelScreenArbiter.SurfaceId, StatusOf(r, "s1").SurfaceId);
            Assert.Equal(DestinationIds.Screen("logo"), StatusOf(r, "s1").DestinationId);

            int onScreen = r.Resolution.CarrierStatuses
                .Count(s => s.Presence == CarrierPresence.OnScreen);
            Assert.Equal(1, onScreen);

            Assert.Equal(WheelScreenArbiter.SurfaceId,
                r.Resolution.SurfaceWinners[0].SurfaceId);
            Assert.Equal("s1", r.Resolution.SurfaceWinners[0].WinnerCarrierId);
        }

        /// <summary>
        /// Port of Special_FlagOffReleasesLatch_FlagOnResends — when the plane
        /// loses then regains a winner, release fires and the next win re-sends.
        /// </summary>
        [Fact]
        public void Ported_ReleaseThenReclaim_FreshWinEdgeResends()
        {
            var a = Arb();
            long t = 0;
            WinAndAccept(a, ref t, snaps: Snap("s1", active: true));

            t += 16;
            var rel = a.Tick(In(t, snaps: Snap("s1", active: false)));
            Assert.True(rel.ReleaseEdge);

            t += 16;
            var again = a.Tick(In(t, snaps: Snap("s1", active: true)));
            Assert.True(again.SendRequested);
            Assert.False(again.Intent.Latched);
            Assert.False(again.ReleaseEdge);
        }

        // ════════════════════════════════════════════════════════════════
        // Keepalive / feedback edge traces (E6-006)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Keepalive_LateTick_Plus20000ThenPlus35000_AnchoredToLastAccept()
        {
            var a = Arb();
            long t = 0;
            var snaps = new[] { Snap("s1", active: true) };

            a.Tick(In(t, snaps: snaps));
            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: snaps)); // stamp at 0

            // First late tick well past the 15s grid.
            t = 20000;
            var late1 = a.Tick(In(t, snaps: snaps));
            Assert.True(late1.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, late1.SendCommand);

            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: snaps)); // stamp at 20000

            // Next due relative to last accept, not a fixed grid origin.
            t = 35000;
            var late2 = a.Tick(In(t, snaps: snaps));
            Assert.True(late2.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, late2.SendCommand);
        }

        [Fact]
        public void Keepalive_HigherWinnerAtDueTick_WinEdgeNotKeepaliveOfLoser()
        {
            var doc = Doc(new[]
            {
                Rule("high", WheelScreenCommand.White),
                Rule("low", WheelScreenCommand.Logo),
            });
            var a = Arb(doc);
            long t = 0;
            var lowOnly = new[] { Snap("low", active: true), Snap("high", active: false) };
            var both = new[] { Snap("low", active: true), Snap("high", active: true) };

            a.Tick(In(t, snaps: lowOnly));
            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: lowOnly));

            // At the keepalive due tick a higher winner appears — win-edge of White.
            t = SpecialCommands.KeepaliveMs;
            var takeover = a.Tick(In(t, snaps: both));
            Assert.True(takeover.SendRequested);
            Assert.Equal("high", takeover.Intent.WinnerCarrierId);
            Assert.Equal(WheelScreenCommand.White, takeover.SendCommand);
            // Latched still logo until the new send is accepted.
            Assert.Equal(WheelScreenCommand.Logo, takeover.Intent.LatchedCommand);
        }

        [Fact]
        public void DeclinedSend_ConsecutiveDeclines_RetryEveryTick_Indefinitely()
        {
            // v9 DeclinedSend_RetriedNextFrame / Special_DeclinedSend_RetriesNextTick:
            // declined does not latch; every subsequent tick re-requests while desired.
            var a = Arb();
            long t = 0;
            var snaps = new[] { Snap("s1", active: true) };

            var r0 = a.Tick(In(t, snaps: snaps));
            Assert.True(r0.SendRequested);

            for (int i = 0; i < 5; i++)
            {
                t += 16;
                var declined = a.Tick(In(t, prevAccepted: false, snaps: snaps));
                Assert.False(declined.Intent.Latched);
                Assert.True(declined.SendRequested);
                Assert.True(declined.SurfaceHeld);
                Assert.Equal(WheelScreenCommand.Logo, declined.SendCommand);
            }

            t += 16;
            var accepted = a.Tick(In(t, prevAccepted: true, snaps: snaps));
            Assert.True(accepted.Intent.Latched);
            Assert.False(accepted.SendRequested);
        }

        [Fact]
        public void Feedback_AfterWinnerLoss_DoesNotRelatch_OrResend()
        {
            var a = Arb();
            long t = 0;
            WinAndAccept(a, ref t, snaps: Snap("s1", active: true));

            t += 16;
            // Win-edge of nothing: release. A stale accepted feedback must not re-latch.
            var rel = a.Tick(In(t, prevAccepted: true, snaps: Snap("s1", active: false)));
            Assert.True(rel.ReleaseEdge);
            Assert.False(rel.Intent.Latched);
            Assert.False(rel.SendRequested);

            t += 16;
            var quiet = a.Tick(In(t, prevAccepted: true, snaps: Snap("s1", active: false)));
            Assert.False(quiet.ReleaseEdge);
            Assert.False(quiet.Intent.Latched);
            Assert.False(quiet.SendRequested);
        }

        [Fact]
        public void Feedback_AfterWinnerChange_Accepted_LatchesNewCommand()
        {
            var doc = Doc(new[]
            {
                Rule("w1", WheelScreenCommand.White),
                Rule("s1", WheelScreenCommand.Logo),
            });
            var a = Arb(doc);
            long t = 0;
            var logoOnly = new[] { Snap("s1", active: true), Snap("w1", active: false) };
            var both = new[] { Snap("s1", active: true), Snap("w1", active: true) };

            WinAndAccept(a, ref t, snaps: logoOnly);

            t += 16;
            var change = a.Tick(In(t, snaps: both));
            Assert.True(change.SendRequested);
            Assert.Equal(WheelScreenCommand.White, change.SendCommand);

            t += 16;
            var after = a.Tick(In(t, prevAccepted: true, snaps: both));
            Assert.True(after.Intent.Latched);
            Assert.Equal(WheelScreenCommand.White, after.Intent.LatchedCommand);
            Assert.False(after.SendRequested);
        }

        [Fact]
        public void Feedback_FirstTick_PreviousAcceptedIgnored_NoLatchWithoutPending()
        {
            var a = Arb();
            // First tick with a spurious accepted feedback and no prior pending send.
            var r = a.Tick(In(0, prevAccepted: true, snaps: Snap("s1", active: true)));
            Assert.True(r.SendRequested);
            Assert.False(r.Intent.Latched); // feedback had nothing to apply
        }

        // ════════════════════════════════════════════════════════════════
        // Idle-floor ladder
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void IdleFloor_RuleOutranksFloor()
        {
            var doc = Doc(
                new[] { Rule("s1", WheelScreenCommand.White, runs: RunsWhen.Always) },
                idleKind: IdleKind.Screen,
                idleScreen: WheelScreenCommand.Logo);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false, snaps: Snap("s1", active: true)));
            Assert.Equal(WheelScreenCommand.White, r.Intent.Command);
            Assert.Equal("s1", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void IdleFloor_Screen_HoldsCommandWhenNoRule()
        {
            var doc = Doc(
                rules: Array.Empty<WheelScreenRule>(),
                idleKind: IdleKind.Screen,
                idleScreen: WheelScreenCommand.Logo);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenOutcomeKind.Screen, r.Intent.Kind);
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            Assert.Equal(WheelScreenArbiter.IdleFloorCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.RestIdle, r.Intent.WinnerCarrierId);
            Assert.True(r.SurfaceHeld);
            Assert.True(r.SendRequested);
            Assert.Equal(CarrierPresence.OnScreen,
                StatusOf(r, WheelScreenArbiter.IdleFloorCarrierId).Presence);
        }

        [Fact]
        public void IdleFloor_Blank_HoldsBlankScreen()
        {
            var doc = Doc(rules: Array.Empty<WheelScreenRule>(), idleKind: IdleKind.Blank);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenCommand.Blank, r.Intent.Command);
            Assert.True(r.SurfaceHeld);
            Assert.Equal(SpecialCommands.PatternBlank, r.SendPattern);
        }

        [Fact]
        public void IdleFloor_Absent_IsBlankFloor()
        {
            var doc = Doc(rules: Array.Empty<WheelScreenRule>(), omitIdle: true);
            Assert.Null(doc.Priority.Rest.Idle);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenOutcomeKind.Screen, r.Intent.Kind);
            Assert.Equal(WheelScreenCommand.Blank, r.Intent.Command);
            Assert.Equal(DestinationIds.RestIdle, r.Intent.WinnerCarrierId);
            Assert.True(r.SurfaceHeld);
            Assert.True(r.SendRequested);
            Assert.Equal(SpecialCommands.PatternBlank, r.SendPattern);
        }

        [Fact]
        public void IdleFloor_Degraded_FallsBackToBlank()
        {
            var doc = Doc(
                rules: Array.Empty<WheelScreenRule>(),
                idleKind: IdleKind.Page,
                degradedIdlePage: true);
            Assert.True(doc.Priority.Rest.Idle.DegradedAtLoad);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenOutcomeKind.Screen, r.Intent.Kind);
            Assert.Equal(WheelScreenCommand.Blank, r.Intent.Command);
            Assert.True(r.SurfaceHeld);
            Assert.True(r.SendRequested);
        }

        [Fact]
        public void IdleFloor_Page_IsDeferredToDisplayPlane()
        {
            var doc = Doc(rules: Array.Empty<WheelScreenRule>(), idleKind: IdleKind.Page);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenOutcomeKind.DeferredToDisplayPlane, r.Intent.Kind);
            Assert.Equal(WheelScreenDeferReason.PageIdle, r.Intent.DeferReason);
            Assert.False(r.SurfaceHeld);
            Assert.False(r.SendRequested);
            Assert.Equal(CarrierPresence.OffScreen,
                StatusOf(r, WheelScreenArbiter.IdleFloorCarrierId).Presence);
        }

        [Fact]
        public void IdleFloor_Blank_ParkOnLegacy_IsDeferred()
        {
            var doc = Doc(
                rules: Array.Empty<WheelScreenRule>(),
                idleKind: IdleKind.Blank,
                parkOnLegacy: true);
            doc.Priority.Rest.Idle.ParkOnLegacyForBlank = true;
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenOutcomeKind.DeferredToDisplayPlane, r.Intent.Kind);
            Assert.Equal(WheelScreenDeferReason.ParkOnLegacyForBlank, r.Intent.DeferReason);
            Assert.False(r.SurfaceHeld);
            Assert.False(r.SendRequested);
        }

        [Fact]
        public void IdleFloor_Blank_Unsupported_IsDeferredPaintBlankFrame()
        {
            var doc = Doc(rules: Array.Empty<WheelScreenRule>(), idleKind: IdleKind.Blank);
            var a = Arb(doc, Caps(blank: false));

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenOutcomeKind.DeferredToDisplayPlane, r.Intent.Kind);
            Assert.Equal(WheelScreenDeferReason.PaintBlankFrame, r.Intent.DeferReason);
            Assert.False(r.SurfaceHeld);
            Assert.False(r.SendRequested);
        }

        [Fact]
        public void InSession_NoRule_IsSilence_EvenWithIdleScreen()
        {
            var doc = Doc(
                rules: Array.Empty<WheelScreenRule>(),
                idleKind: IdleKind.Screen,
                idleScreen: WheelScreenCommand.Logo);
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: true));
            Assert.Equal(WheelScreenOutcomeKind.Silence, r.Intent.Kind);
            Assert.False(r.SurfaceHeld);
        }

        [Fact]
        public void InSession_InGameRule_CanWin()
        {
            var doc = Doc(new[]
            {
                Rule("s1", WheelScreenCommand.Logo, runs: RunsWhen.InGame),
            });
            var a = Arb(doc);

            var r = a.Tick(In(0, inGame: true, snaps: Snap("s1", active: true, eligible: true)));
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            Assert.True(r.SurfaceHeld);
        }

        // ════════════════════════════════════════════════════════════════
        // Floor id collision (E6-OP-03 / E6-002)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void FloorCarrierId_IsRestIdle_NoCollisionWithAuthoredIdleRule()
        {
            Assert.Equal("rest:idle", WheelScreenArbiter.IdleFloorCarrierId);
            Assert.Equal(DestinationIds.RestIdle, WheelScreenArbiter.IdleFloorCarrierId);

            // Authored rule id "idle" is reserved and degraded by the validator.
            var raw = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a" },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo,
                        },
                    },
                },
                WheelScreen = new WheelScreenPlane
                {
                    Rules = new List<WheelScreenRule>
                    {
                        Rule("idle", WheelScreenCommand.White, runs: RunsWhen.Always),
                    },
                },
            };
            var warns = new List<string>();
            var normalized = DisplayConfigV2Validator.Normalize(raw, warns.Add, catalog: null);
            Assert.True(normalized.WheelScreen.Rules[0].DegradedAtLoad);
            Assert.Contains(warns, m => m.Contains("reserved runtime id"));

            var a = Arb(normalized);
            var r = a.Tick(In(0, inGame: false, snaps: Snap("idle", active: true)));
            // Floor owns (rule degraded); single rest:idle OnScreen row.
            Assert.Equal(DestinationIds.RestIdle, r.Intent.WinnerCarrierId);
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            var keys = r.Resolution.CarrierStatuses
                .Select(s => (s.CarrierId, s.SurfaceId))
                .ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.Equal(1, r.Resolution.CarrierStatuses
                .Count(s => s.Presence == CarrierPresence.OnScreen));
        }

        [Fact]
        public void FloorCarrierId_AuthoredRestColonIdle_DegradedByValidator()
        {
            var raw = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a" },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
                WheelScreen = new WheelScreenPlane
                {
                    Rules = new List<WheelScreenRule>
                    {
                        Rule("rest:idle", WheelScreenCommand.White, runs: RunsWhen.Always),
                    },
                },
            };
            var normalized = DisplayConfigV2Validator.Normalize(raw, _ => { });
            Assert.True(normalized.WheelScreen.Rules[0].DegradedAtLoad);
            Assert.True(DisplayConfigV2Validator.IsReservedRuntimeCarrierId("rest:idle"));
            Assert.True(DisplayConfigV2Validator.IsReservedRuntimeCarrierId("rest"));
            Assert.True(DisplayConfigV2Validator.IsReservedRuntimeCarrierId("manual"));
            Assert.True(DisplayConfigV2Validator.IsReservedRuntimeCarrierId("idle"));
            Assert.False(DisplayConfigV2Validator.IsReservedRuntimeCarrierId("w-logo60"));
        }

        // ════════════════════════════════════════════════════════════════
        // Model worked example (E6-OP-06)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Model_LogoSixtySecondsThenBlank()
        {
            // Model doc: logo forDuration 60000 runs:idle OVER rest.idle blank.
            // Timeline: in-session silence → session end Logo win-edge → keepalives →
            // expiry hand-off to Blank floor with SurfaceHeld held and NO ReleaseEdge.
            var rule = Rule(
                "w-logo60", WheelScreenCommand.Logo,
                runs: RunsWhen.Idle,
                life: LifetimeKind.ForDuration,
                durationMs: 60000);
            var doc = Doc(new[] { rule }, idleKind: IdleKind.Blank);
            var a = Arb(doc);

            // In-session: runs:idle → evaluator marks Eligible=false → silence.
            var inSession = a.Tick(In(0, inGame: true,
                snaps: Snap("w-logo60", active: true, eligible: false, remaining: 60000)));
            Assert.Equal(WheelScreenOutcomeKind.Silence, inSession.Intent.Kind);
            Assert.False(inSession.SurfaceHeld);

            // Session end: logo wins, win-edge send.
            long t = 0;
            var live = new[] { Snap("w-logo60", active: true, remaining: 60000) };
            var win = a.Tick(In(t, inGame: false, snaps: live));
            Assert.Equal("w-logo60", win.Intent.WinnerCarrierId);
            Assert.Equal(WheelScreenCommand.Logo, win.Intent.Command);
            Assert.True(win.SendRequested);
            Assert.True(win.SurfaceHeld);

            t += 16;
            a.Tick(In(t, inGame: false, prevAccepted: true, snaps: live));

            // Keepalive at +15s.
            t = SpecialCommands.KeepaliveMs;
            var keep = a.Tick(In(t, inGame: false,
                snaps: Snap("w-logo60", active: true, remaining: 60000 - (int)t)));
            Assert.True(keep.SendRequested);
            Assert.Equal(WheelScreenCommand.Logo, keep.SendCommand);
            t += 16;
            a.Tick(In(t, inGame: false, prevAccepted: true,
                snaps: Snap("w-logo60", active: true, remaining: 45000)));

            // Expiry: rule inactive → blank floor. Plane still owns col01 → no ReleaseEdge.
            t = 60000;
            var handoff = a.Tick(In(t, inGame: false,
                snaps: Snap("w-logo60", active: false, remaining: null)));
            Assert.Equal(DestinationIds.RestIdle, handoff.Intent.WinnerCarrierId);
            Assert.Equal(WheelScreenCommand.Blank, handoff.Intent.Command);
            Assert.True(handoff.SurfaceHeld);
            Assert.False(handoff.ReleaseEdge);
            Assert.True(handoff.SendRequested); // win-edge Logo→Blank
            // Latched still Logo until Blank send is accepted.
            Assert.Equal(WheelScreenCommand.Logo, handoff.Intent.LatchedCommand);

            t += 16;
            var afterBlank = a.Tick(In(t, inGame: false, prevAccepted: true,
                snaps: Snap("w-logo60", active: false)));
            Assert.Equal(WheelScreenCommand.Blank, afterBlank.Intent.LatchedCommand);
            Assert.True(afterBlank.SurfaceHeld);
            Assert.False(afterBlank.ReleaseEdge);
            Assert.False(afterBlank.SendRequested);
        }

        // ════════════════════════════════════════════════════════════════
        // Untested capability on the record (E6-OP-07)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Untested_CommandStillTakesSurface_StampedOnRecord()
        {
            var raw = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a" },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
                WheelScreen = new WheelScreenPlane
                {
                    Rules = new List<WheelScreenRule>
                    {
                        Rule("s1", WheelScreenCommand.Logo),
                    },
                },
            };
            var normalized = DisplayConfigV2Validator.Normalize(raw, _ => { }, catalog: null);
            var a = Arb(normalized, Caps(logo: null));

            var r = a.Tick(In(0, snaps: Snap("s1", active: true)));
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            Assert.True(r.SurfaceHeld);
            Assert.True(r.SendRequested);
            Assert.True(r.WinnerCapabilityUntested);
            Assert.True((StatusOf(r, "s1").RowLabels & CarrierRowLabels.Untested) != 0);
        }

        [Fact]
        public void Untested_IdleFloor_StampedOnFloorRow()
        {
            var doc = Doc(rules: Array.Empty<WheelScreenRule>(), idleKind: IdleKind.Blank);
            var a = Arb(doc, Caps(blank: null));

            var r = a.Tick(In(0, inGame: false));
            Assert.Equal(WheelScreenCommand.Blank, r.Intent.Command);
            Assert.True(r.SurfaceHeld);
            Assert.True(r.WinnerCapabilityUntested);
            Assert.True((StatusOf(r, DestinationIds.RestIdle).RowLabels
                & CarrierRowLabels.Untested) != 0);
        }

        // ════════════════════════════════════════════════════════════════
        // untilDismissed + DismissedCarrierIds + FreshFire re-entry
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void UntilDismissed_DismissedCarrierIds_SuppressesWinner()
        {
            var doc = Doc(new[]
            {
                Rule("s1", WheelScreenCommand.Logo, life: LifetimeKind.UntilDismissed),
            });
            var a = Arb(doc);
            long t = 0;
            WinAndAccept(a, ref t, snaps: Snap("s1", active: true));

            t += 16;
            var dismissed = a.Tick(In(t,
                dismissed: new[] { "s1" },
                snaps: Snap("s1", active: true)));
            Assert.True(dismissed.ReleaseEdge);
            Assert.False(dismissed.SurfaceHeld);
            var st = StatusOf(dismissed, "s1");
            Assert.Equal(CarrierPresence.Dismissed, st.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed,
                st.RowLabels & CarrierRowLabels.Dismissed);
        }

        [Fact]
        public void Dismissed_IsFirstClassPresence_WheelScreenPlane()
        {
            // REALIGNMENT #1: wheel-screen latched Active+Eligible is Presence=Dismissed
            // (+ RowLabels.Dismissed), not Outranked.
            var doc = Doc(new[]
            {
                Rule("s1", WheelScreenCommand.Logo, life: LifetimeKind.UntilDismissed),
            });
            var a = Arb(doc);
            long t = 0;
            WinAndAccept(a, ref t, snaps: Snap("s1", active: true));

            t += 16;
            var r = a.Tick(In(t,
                dismissed: new[] { "s1" },
                snaps: Snap("s1", active: true)));
            var st = StatusOf(r, "s1");
            Assert.Equal(WheelScreenArbiter.SurfaceId, st.SurfaceId);
            Assert.Equal(CarrierPresence.Dismissed, st.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed, st.RowLabels & CarrierRowLabels.Dismissed);
            Assert.False(r.SurfaceHeld);
            Assert.Equal(0, r.Resolution.CarrierStatuses
                .Count(s => s.Presence == CarrierPresence.OnScreen));
        }

        [Fact]
        public void UntilDismissed_FreshFire_ReEntersDespiteDismissedSet()
        {
            var doc = Doc(new[]
            {
                Rule("s1", WheelScreenCommand.Logo, life: LifetimeKind.UntilDismissed),
            });
            var a = Arb(doc);

            // Dismissed but FreshFire this tick → re-entry.
            var r = a.Tick(In(0,
                dismissed: new[] { "s1" },
                snaps: Snap("s1", active: true, fired: true, fresh: true)));
            Assert.Equal("s1", r.Intent.WinnerCarrierId);
            Assert.True(r.SendRequested);
            // Fresh-fire re-arm: not labeled Dismissed while re-entering.
            Assert.Equal(CarrierRowLabels.None,
                StatusOf(r, "s1").RowLabels & CarrierRowLabels.Dismissed);
        }

        // ════════════════════════════════════════════════════════════════
        // Capability tri-state (§14)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Capability_False_Inert_CantRunHere()
        {
            var raw = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a" },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
                WheelScreen = new WheelScreenPlane
                {
                    Rules = new List<WheelScreenRule>
                    {
                        Rule("s1", WheelScreenCommand.White),
                    },
                },
            };
            var normalized = DisplayConfigV2Validator.Normalize(raw, _ => { }, catalog: null);
            var a = Arb(normalized, Caps(white: false));

            var r = a.Tick(In(0, snaps: Snap("s1", active: true)));
            Assert.Equal(WheelScreenOutcomeKind.Silence, r.Intent.Kind);
            var st = StatusOf(r, "s1");
            Assert.Equal(CarrierPresence.OffScreen, st.Presence);
            Assert.True((st.RowLabels & CarrierRowLabels.CantRunHere) != 0);
        }

        [Fact]
        public void Capability_Null_WarnsAndAllows()
        {
            var warns = new List<string>();
            var raw = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a" },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
                WheelScreen = new WheelScreenPlane
                {
                    Rules = new List<WheelScreenRule>
                    {
                        Rule("s1", WheelScreenCommand.Logo),
                        Rule("s2", WheelScreenCommand.Logo),
                    },
                },
            };
            var normalized = DisplayConfigV2Validator.Normalize(raw, _ => { }, catalog: null);
            var a = Arb(normalized, Caps(logo: null), warn: warns.Add);

            var r = a.Tick(In(0, snaps: Snap("s1", active: true)));
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            Assert.True(r.SendRequested);
            // Keyed by rule id — both rules named at construction.
            Assert.Contains(warns, m => m.Contains("s1") && m.Contains("untested"));
            Assert.Contains(warns, m => m.Contains("s2") && m.Contains("untested"));
        }

        [Fact]
        public void Capability_True_Allows()
        {
            var a = Arb(caps: Caps(logo: true));
            var r = a.Tick(In(0, snaps: Snap("s1", active: true)));
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            Assert.True(r.SendRequested);
        }

        [Fact]
        public void PatternOf_Unknown_ReturnsNull()
        {
            Assert.Null(WheelScreenArbiter.PatternOf(WheelScreenCommand.Unknown));
            Assert.Equal(SpecialCommands.PatternLogo,
                WheelScreenArbiter.PatternOf(WheelScreenCommand.Logo));
        }

        // ════════════════════════════════════════════════════════════════
        // Runs gating
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Runs_Ineligible_OutOfSessionScope_FloorMayWin()
        {
            var doc = Doc(
                new[] { Rule("s1", WheelScreenCommand.White, runs: RunsWhen.InGame) },
                idleKind: IdleKind.Screen,
                idleScreen: WheelScreenCommand.Logo);
            var a = Arb(doc);

            // Out of session: runs:inGame → Eligible=false from evaluator.
            var r = a.Tick(In(0, inGame: false,
                snaps: Snap("s1", active: true, eligible: false)));
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.Command);
            Assert.Equal(WheelScreenArbiter.IdleFloorCarrierId, r.Intent.WinnerCarrierId);
            var st = StatusOf(r, "s1");
            Assert.Equal(CarrierPresence.Waiting, st.Presence);
            Assert.True((st.RowLabels & CarrierRowLabels.OutOfSessionScope) != 0);
        }

        [Fact]
        public void Runs_ArrayOrder_FirstActiveEligibleWins()
        {
            var doc = Doc(new[]
            {
                Rule("high", WheelScreenCommand.White),
                Rule("low", WheelScreenCommand.Logo),
            });
            var a = Arb(doc);

            var both = a.Tick(In(0,
                snaps: new[] { Snap("high", active: true), Snap("low", active: true) }));
            Assert.Equal("high", both.Intent.WinnerCarrierId);
            Assert.Equal(CarrierPresence.Outranked, StatusOf(both, "low").Presence);

            var onlyLow = a.Tick(In(16,
                prevAccepted: false,
                snaps: new[] { Snap("high", active: false), Snap("low", active: true) }));
            Assert.Equal("low", onlyLow.Intent.WinnerCarrierId);
        }

        // ════════════════════════════════════════════════════════════════
        // Merge-law / surface conventions + OP-05 physical surface honesty
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void RecordSlice_SurfaceKey_IsWheelScreen()
        {
            Assert.Equal("wheelScreen", DestinationIds.WheelScreenSurface());
            Assert.Equal("wheelScreen", WheelScreenArbiter.SurfaceId);

            var a = Arb();
            var r = a.Tick(In(0, snaps: Snap("s1", active: true)));
            Assert.All(r.Resolution.CarrierStatuses,
                s => Assert.Equal("wheelScreen", s.SurfaceId));
            Assert.Equal("wheelScreen", r.Resolution.SurfaceWinners[0].SurfaceId);
        }

        [Fact]
        public void RecordSlice_PresenceOnlyOnWheelScreenSurface()
        {
            var a = Arb();
            long t = 0;
            var r = WinAndAccept(a, ref t, snaps: Snap("s1", active: true));
            Assert.All(r.Resolution.CarrierStatuses,
                s => Assert.NotNull(s.Presence)); // E6 owns presence for its surface
            Assert.Equal("test", r.Resolution.DeviceKey);
            Assert.Equal(t, r.Resolution.TickMs);
        }

        [Fact]
        public void Merge_AtMostOneOnScreenPerPhysicalSurface_WhenWheelHolds()
        {
            // E6 holds blank floor; E5 page row would have been OnScreen — demoted.
            var e6Doc = Doc(rules: Array.Empty<WheelScreenRule>(), idleKind: IdleKind.Blank);
            var e6 = Arb(e6Doc);
            var e6r = e6.Tick(In(0, inGame: false));
            Assert.True(e6r.SurfaceHeld);

            var e5Doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-speed",
                        Base = new ContentWithEffect
                        {
                            Content = new ContentObject
                            {
                                Kind = ContentKind.Text, Text = "SPD",
                            },
                        },
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "l-top",
                                Content = new ContentObject
                                {
                                    Kind = ContentKind.Text, Text = "TOP",
                                },
                                Condition = LevelTrue(),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-speed",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
            e5Doc = Normalize(e5Doc);
            var e5 = new FrameComposer(e5Doc, new FrameComposerOptions { DeviceKey = "test" });
            var e5r = e5.Tick(new FrameComposerTickInput
            {
                NowMs = 0,
                SegmentHostedPageId = "p-speed",
                DisplayedDestinationId = DestinationIds.Hosted("p-speed"),
                SegmentSurfaceHeldByWheelScreen = e6r.SurfaceHeld,
                CarrierSnapshots = new[] { Snap("l-top", active: true) },
                Content = new SegmentContentContext(),
            });

            // Page row demoted; wheel-screen floor OnScreen — at most one on the glass.
            Assert.All(
                e5r.Resolution.CarrierStatuses.Where(s =>
                    s.SurfaceId != null && s.SurfaceId.StartsWith("page:", StringComparison.Ordinal)),
                s => Assert.NotEqual(CarrierPresence.OnScreen, s.Presence));

            int glassOnScreen =
                e5r.Resolution.CarrierStatuses.Count(s =>
                    s.Presence == CarrierPresence.OnScreen
                    && s.SurfaceId != null
                    && s.SurfaceId.StartsWith("page:", StringComparison.Ordinal))
                + e6r.Resolution.CarrierStatuses.Count(s =>
                    s.Presence == CarrierPresence.OnScreen
                    && s.SurfaceId == DestinationIds.WheelScreenSurfaceId);
            Assert.Equal(1, glassOnScreen);
        }

        [Fact]
        public void Keepalive_Declined_LeavesStampOld_RetriesEveryTick()
        {
            var a = Arb();
            long t = 0;
            var snaps = new[] { Snap("s1", active: true) };

            a.Tick(In(t, snaps: snaps));
            t += 16;
            a.Tick(In(t, prevAccepted: true, snaps: snaps));

            t += SpecialCommands.KeepaliveMs;
            var due = a.Tick(In(t, snaps: snaps));
            Assert.True(due.SendRequested);
            Assert.True(due.Intent.Latched);

            // Declined keepalive: stay latched, stamp old → retry next tick.
            t += 16;
            var declined = a.Tick(In(t, prevAccepted: false, snaps: snaps));
            Assert.True(declined.Intent.Latched);
            Assert.True(declined.SendRequested);
        }

        [Fact]
        public void DestinationIds_Screen_Spelling()
        {
            Assert.Equal("screen:logo", DestinationIds.Screen("logo"));
            Assert.Equal("screen:logoInverted", DestinationIds.Screen("logoInverted"));
            Assert.Equal(SpecialCommands.KeepaliveMs, 15000);
        }

        // ════════════════════════════════════════════════════════════════
        // Examples builtIn lint (E6-OP-06)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Examples_EveryBuiltInName_ResolvesAgainstPropertySpec()
        {
            var examplesDir = Path.Combine(
                TestPaths.RepoRoot(), "scratch", "plans", "display-customization", "examples");
            Assert.True(Directory.Exists(examplesDir), "examples dir missing: " + examplesDir);

            var known = new HashSet<string>(
                BuiltInProperties.All, StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();

            foreach (var path in Directory.GetFiles(examplesDir, "*.v2.json"))
            {
                var root = JToken.Parse(File.ReadAllText(path));
                foreach (var src in root.SelectTokens("$..source"))
                {
                    if (src?["kind"]?.Value<string>() != "builtIn")
                        continue;
                    string? name = src["name"]?.Value<string>();
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (!known.Contains(name!))
                        unknown.Add(Path.GetFileName(path) + ": " + name);
                }
            }

            Assert.True(unknown.Count == 0,
                "unknown builtIn name(s) in examples: " + string.Join(", ", unknown));
        }
    }
}
