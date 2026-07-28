using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Wheel-screen-scoped dismissal latch set. Distinct from
    /// <see cref="DisplayLatchSet"/> so E4 and E6 latch sets cannot be crossed at compile time.
    /// </summary>
    public readonly struct WheelScreenLatchSet
    {
        public WheelScreenLatchSet(IReadOnlyList<string> ids)
        {
            Ids = ids ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> Ids { get; }

        public static WheelScreenLatchSet Empty { get; } =
            new WheelScreenLatchSet(Array.Empty<string>());
    }

    /// <summary>
    /// Display-surface-scoped dismissal latch set (E4 → E5). Distinct from
    /// <see cref="WheelScreenLatchSet"/>.
    /// </summary>
    public readonly struct DisplayLatchSet
    {
        public DisplayLatchSet(IReadOnlyList<string> ids)
        {
            Ids = ids ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> Ids { get; }

        public static DisplayLatchSet Empty { get; } =
            new DisplayLatchSet(Array.Empty<string>());
    }

    /// <summary>
    /// Pure wheel-screen-plane dismissal glue (E7 / contract §6.2 + §3.1).
    /// Given a manual/adopted press event and carrier snapshots, filters to the
    /// wheel-screen rule-id set and produces a <see cref="WheelScreenLatchSet"/> for E6
    /// (<see cref="WheelScreenArbiterTickInput.DismissedCarrierIds"/>).
    ///
    /// Dormant until E8 wires press events: nothing live calls this yet.
    /// </summary>
    public static class WheelScreenDismissal
    {
        /// <summary>
        /// Apply one press tick: latch every Active wheel-screen carrier (ids must be in
        /// <paramref name="wheelScreenRuleIds"/>), then re-arm any latched carrier whose
        /// snapshot shows <see cref="CarrierTickSnapshot.FreshFire"/>
        /// (contract §3.1 letter of D8 — mid-window <c>FiredThisTick &amp;&amp; !FreshFire</c>
        /// does not re-arm).
        /// </summary>
        /// <param name="pressThisTick">True on a manual/adopted page press this tick.</param>
        /// <param name="snapshots">
        /// Pre-evaluated snapshots (may include mixed surfaces — filtered internally).
        /// </param>
        /// <param name="wheelScreenRuleIds">
        /// Rule ids belonging to the wheel-screen plane (from
        /// <c>DisplayConfigV2.WheelScreen.Rules</c>). Ids outside this set are ignored.
        /// </param>
        /// <param name="priorLatches">
        /// Latches carried from previous ticks (default empty). Not mutated.
        /// </param>
        /// <returns>New wheel-screen latch set for this tick (sorted for determinism).</returns>
        public static WheelScreenLatchSet Apply(
            bool pressThisTick,
            IReadOnlyList<CarrierTickSnapshot> snapshots,
            IReadOnlyCollection<string> wheelScreenRuleIds,
            WheelScreenLatchSet priorLatches = default)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            if (wheelScreenRuleIds != null)
            {
                foreach (var id in wheelScreenRuleIds)
                {
                    if (id != null)
                        allowed.Add(id);
                }
            }

            var latches = new HashSet<string>(StringComparer.Ordinal);
            var prior = priorLatches.Ids;
            if (prior != null)
            {
                foreach (var id in prior)
                {
                    if (id != null && allowed.Contains(id))
                        latches.Add(id);
                }
            }

            if (snapshots == null)
                return new WheelScreenLatchSet(Sorted(latches));

            // Press: latch every currently Active carrier that is a wheel-screen rule.
            if (pressThisTick)
            {
                foreach (var snap in snapshots)
                {
                    if (snap.CarrierId != null
                        && snap.Active
                        && allowed.Contains(snap.CarrierId))
                        latches.Add(snap.CarrierId);
                }
            }

            // Re-arm on FreshFire (contract §3.1) — even on the press tick, a FreshFire
            // that lands the same tick may re-arm after latching (E6 consumes the set
            // with the same FreshFire exception; we clear here so the set is honest).
            foreach (var snap in snapshots)
            {
                if (snap.CarrierId == null)
                    continue;
                if (!allowed.Contains(snap.CarrierId))
                    continue;
                if (snap.FreshFire && latches.Contains(snap.CarrierId))
                    latches.Remove(snap.CarrierId);
            }

            return new WheelScreenLatchSet(Sorted(latches));
        }

        private static IReadOnlyList<string> Sorted(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }
}
