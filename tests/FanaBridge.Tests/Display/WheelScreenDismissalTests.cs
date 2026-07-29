using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>Phase E7 E: wheel-plane dismissal wiring (pure, dormant).</summary>
    public class WheelScreenDismissalTests
    {
        private static CarrierTickSnapshot Snap(
            string id, bool active, bool fresh = false, bool fired = false)
            => new CarrierTickSnapshot(
                id, conditionSatisfied: active, active, fresh, fired,
                eligible: true, expiresAtMs: 0, remainingMs: null);

        private static readonly string[] WsRules = { "ws-a", "ws-b", "ws-c" };

        [Fact]
        public void Press_LatchesEveryActiveCarrier()
        {
            var snaps = new[]
            {
                Snap("ws-a", active: true),
                Snap("ws-b", active: false),
                Snap("ws-c", active: true),
            };
            var latches = WheelScreenDismissal.Apply(pressThisTick: true, snaps, WsRules);
            Assert.Equal(new[] { "ws-a", "ws-c" }, latches.Ids.ToArray());
        }

        [Fact]
        public void NoPress_KeepsPriorLatches()
        {
            var prior = new WheelScreenLatchSet(new[] { "ws-a" });
            var snaps = new[] { Snap("ws-a", active: true), Snap("ws-b", active: true) };
            var latches = WheelScreenDismissal.Apply(false, snaps, WsRules, prior);
            Assert.Equal(new[] { "ws-a" }, latches.Ids.ToArray());
        }

        [Fact]
        public void FreshFire_RearmsLatch()
        {
            var prior = new WheelScreenLatchSet(new[] { "ws-a", "ws-b" });
            var snaps = new[]
            {
                Snap("ws-a", active: true, fresh: true, fired: true),
                Snap("ws-b", active: true, fresh: false, fired: true),
            };
            var latches = WheelScreenDismissal.Apply(false, snaps, WsRules, prior);
            Assert.Equal(new[] { "ws-b" }, latches.Ids.ToArray());
        }

        [Fact]
        public void PressThenFreshFireSameTick_Rearms()
        {
            var snaps = new[] { Snap("ws-a", active: true, fresh: true, fired: true) };
            var latches = WheelScreenDismissal.Apply(true, snaps, WsRules, priorLatches: default);
            Assert.Empty(latches.Ids);
        }

        [Fact]
        public void MidWindowFiredThisTick_DoesNotRearm()
        {
            var prior = new WheelScreenLatchSet(new[] { "ws-a" });
            var snaps = new[] { Snap("ws-a", active: true, fresh: false, fired: true) };
            var latches = WheelScreenDismissal.Apply(false, snaps, WsRules, prior);
            Assert.Equal(new[] { "ws-a" }, latches.Ids.ToArray());
        }

        [Fact]
        public void NullSnapshots_Safe()
        {
            var prior = new WheelScreenLatchSet(new[] { "ws-a" });
            var latches = WheelScreenDismissal.Apply(true, null, WsRules, prior);
            Assert.Equal(new[] { "ws-a" }, latches.Ids.ToArray());
        }

        [Fact]
        public void MixedSurface_OnlyWheelScreenRulesLatch()
        {
            // Mixed snapshots include display-plane summons — must not latch them.
            var snaps = new[]
            {
                Snap("ws-a", active: true),
                Snap("e-pit", active: true),       // display seat summon
                Snap("l-alert", active: true),     // layer
            };
            var latches = WheelScreenDismissal.Apply(true, snaps, WsRules);
            Assert.Equal(new[] { "ws-a" }, latches.Ids.ToArray());
            Assert.DoesNotContain("e-pit", latches.Ids);
            Assert.DoesNotContain("l-alert", latches.Ids);
        }

        [Fact]
        public void ScopedTypes_CannotCross()
        {
            // Compile-time separation: WheelScreenLatchSet ≠ DisplayLatchSet.
            var ws = new WheelScreenLatchSet(new[] { "ws-a" });
            var display = new DisplayLatchSet(new[] { "e-pit" });
            Assert.Equal("ws-a", ws.Ids[0]);
            Assert.Equal("e-pit", display.Ids[0]);
            // Apply accepts only WheelScreenLatchSet for prior.
            var again = WheelScreenDismissal.Apply(false, null, WsRules, ws);
            Assert.Equal(new[] { "ws-a" }, again.Ids.ToArray());
        }
    }
}
