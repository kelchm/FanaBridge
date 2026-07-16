using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using FanaBridge.Display;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// The mapped-control trigger path: owns the FanaBridge actions a device's display
    /// rules reference (<see cref="PropertyKind.FanaBridgeAction"/> condition sources)
    /// and hands their fires to the rule engine.
    ///
    /// Registration: each referenced action name is registered once per SimHub plugin
    /// manager via <c>PluginManager.AddAction(name, typeof(FanatecPlugin), handler)</c> —
    /// the same instance-level registry the <c>this.AddAction(...)</c> extension writes
    /// to, namespaced by the plugin type exactly like the existing
    /// <c>AddEvent("WheelChanged")</c> registrations. That registry keeps the FIRST
    /// handler registered for a name and silently ignores repeats, and a hub only lives
    /// as long as its rule stack (rebuilt on reconnect, wheel swap, ITM toggle, config
    /// swap) — so the handler handed to the host is never a hub's own method: it is a
    /// stable <see cref="DisplayActionRouter"/> callback that fans each fire out to the
    /// hubs currently referencing the name. SimHub recreates its plugin manager
    /// in-process on every game change and the registry dies with it (the issue-#37
    /// lifetime rule), so <see cref="EnsureRegistered(PluginManager)"/> runs every frame
    /// and re-registers whenever the manager reference changes; the manager itself is
    /// held only for that reference comparison, never dereferenced later.
    ///
    /// Threading: SimHub fires action handlers on arbitrary threads (input events, UI).
    /// Handlers only enqueue the action name; <see cref="DrainTriggered"/> empties the
    /// queue on the DataUpdate thread each frame into
    /// <see cref="RuleEngineInput.TriggeredActions"/>. The queue is bounded — beyond
    /// <see cref="MaxPending"/> undrained fires (a stalled frame loop), new fires are
    /// dropped with a one-time warning rather than growing without limit.
    /// </summary>
    public sealed class DisplayActionHub
    {
        /// <summary>Most undrained fires the queue holds; beyond this they drop (+warn).</summary>
        internal const int MaxPending = 64;

        private readonly IReadOnlyList<string> _actionNames;
        private readonly Action<string> _log;

        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private int _pending;           // approximate queue depth, for the bound
        private bool _overflowWarned;   // once per hub — a stalled loop shouldn't spam

        // What we last registered against — the PluginManager in production, any token
        // through the test seam. Reference identity only; never dereferenced.
        private object _registeredWith;

        public DisplayActionHub(DisplayCustomizationConfig config, Action<string> log = null)
        {
            _actionNames = CollectActionNames(config);
            _log = log ?? (_ => { });
        }

        /// <summary>The distinct action names this device's rules reference.</summary>
        public IReadOnlyList<string> ActionNames => _actionNames;

        /// <summary>
        /// Subscribes this hub with the shared <see cref="DisplayActionRouter"/> and
        /// registers its action names with <paramref name="pm"/> if that hasn't happened
        /// for this exact manager yet. Call once per frame from the DataUpdate thread —
        /// a manager restart (new reference) triggers the re-registration the fresh
        /// registry needs; the same manager is a no-op.
        /// </summary>
        public void EnsureRegistered(PluginManager pm)
        {
            if (pm == null)
                return;
            EnsureRegistered(pm, DisplayActionRouter.Shared, (name, fire) =>
                // Namespaced by the plugin type, like the plugin's AddEvent
                // registrations; actionEnd (button release) is not a trigger.
                pm.AddAction(name, typeof(FanatecPlugin), (m, a) => fire(name)));
        }

        /// <summary>
        /// Registration core, seam-injected for tests: <paramref name="register"/> binds
        /// one action name to a fire callback (the router's, never this hub's — see the
        /// class comment). Re-runs whenever <paramref name="registrationToken"/> changes
        /// reference; the per-hub latch keeps the frame path to one reference compare.
        /// </summary>
        internal void EnsureRegistered(object registrationToken, DisplayActionRouter router,
            Action<string, Action<string>> register)
        {
            if (registrationToken == null || ReferenceEquals(registrationToken, _registeredWith))
                return;
            _registeredWith = registrationToken;
            if (_actionNames.Count > 0)
                router.Register(registrationToken, this, register, _log);
        }

        /// <summary>Handler target — safe from any thread.</summary>
        public void OnTriggered(string actionName)
        {
            if (actionName == null)
                return;
            if (Interlocked.Increment(ref _pending) > MaxPending)
            {
                Interlocked.Decrement(ref _pending);
                if (!_overflowWarned)
                {
                    _overflowWarned = true;
                    _log("DisplayActions: more than " + MaxPending
                        + " undrained action fires — dropping further fires until drained");
                }
                return;
            }
            _queue.Enqueue(actionName);
        }

        /// <summary>
        /// Empties the fires accumulated since the last call into <paramref name="into"/>
        /// (appended, oldest first). DataUpdate thread, once per frame.
        /// </summary>
        public void DrainTriggered(List<string> into)
        {
            while (_queue.TryDequeue(out string name))
            {
                Interlocked.Decrement(ref _pending);
                into?.Add(name);
            }
        }

        // The distinct (ordinal) FanaBridgeAction names referenced by any rule's
        // condition source, both surfaces. Field mappings can't reference actions
        // (the validator drops such mappings — an action is not a value).
        private static IReadOnlyList<string> CollectActionNames(DisplayCustomizationConfig config)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddFrom(config?.Itm?.Rules, names, seen);
            AddFrom(config?.Legacy?.Rules, names, seen);
            return names;
        }

        private static void AddFrom(List<DisplayRule> rules, List<string> names, HashSet<string> seen)
        {
            if (rules == null)
                return;
            foreach (var rule in rules)
            {
                var source = rule?.When?.Source;
                if (source != null && source.Kind == PropertyKind.FanaBridgeAction
                    && !string.IsNullOrEmpty(source.Name) && seen.Add(source.Name))
                    names.Add(source.Name);
            }
        }
    }

    /// <summary>
    /// Routes host action fires to the hubs that currently want them. Exists because the
    /// host's action registry is keyed by (plugin type, action name) and KEEPS THE FIRST
    /// handler registered under a name — later registrations are silent no-ops. A hub
    /// only lives as long as its rule stack (rebuilt within one plugin-manager generation
    /// on reconnect, wheel swap, ITM toggle, config swap), so binding a hub's own method
    /// as the host handler would strand every fire in the first, long-discarded hub. The
    /// host instead gets a stable router-level callback, registered once per name per
    /// manager, and the router fans each fire out to every live hub referencing the name
    /// (several devices may share one action name — each gets the fire).
    ///
    /// Hubs are held weakly: a discarded stack's hub drops out on collection with no
    /// unregistration ceremony, and until then its bounded queue absorbs stray fires
    /// harmlessly (nothing drains it). The registration token (the PluginManager in
    /// production) is compared by reference only, never dereferenced — an in-process
    /// manager restart (issue #37) killed the host registry, so a new token resets the
    /// router and everything registers afresh. Fires arrive on arbitrary threads; all
    /// shared state is guarded by one small lock.
    /// </summary>
    internal sealed class DisplayActionRouter
    {
        /// <summary>The process-wide router production wiring uses. Tests build their own
        /// instances so parallel test runs never share routing state.</summary>
        internal static readonly DisplayActionRouter Shared = new DisplayActionRouter();

        private readonly object _gate = new object();
        private object _registrationToken;   // reference identity only, never dereferenced
        private readonly HashSet<string> _hostRegistered =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<WeakReference<DisplayActionHub>>> _routes =
            new Dictionary<string, List<WeakReference<DisplayActionHub>>>(StringComparer.Ordinal);

        /// <summary>
        /// Subscribes <paramref name="hub"/>'s action names and host-registers each name
        /// once per registration token. <paramref name="hostRegister"/> binds one name to
        /// the router's fire callback; a failure costs that name, never the frame loop.
        /// </summary>
        internal void Register(object registrationToken, DisplayActionHub hub,
            Action<string, Action<string>> hostRegister, Action<string> log)
        {
            var registered = new List<string>();
            lock (_gate)
            {
                if (!ReferenceEquals(registrationToken, _registrationToken))
                {
                    // New manager: the old registry — and the handlers in it — died with
                    // the old one. Start over.
                    _registrationToken = registrationToken;
                    _hostRegistered.Clear();
                    _routes.Clear();
                }

                foreach (var name in hub.ActionNames)
                {
                    Subscribe(name, hub);
                    if (_hostRegistered.Contains(name))
                        continue;   // the host keeps the first handler — it is already Fire
                    try
                    {
                        hostRegister(name, Fire);
                        _hostRegistered.Add(name);
                        registered.Add(name);
                    }
                    catch (Exception ex)
                    {
                        log("DisplayActions: could not register action '" + name + "' — "
                            + ex.Message);
                    }
                }
            }
            if (registered.Count > 0)
                log("DisplayActions: registered " + registered.Count + " action(s): "
                    + string.Join(", ", registered));
        }

        /// <summary>The handler target the host invokes — safe from any thread. Fans the
        /// fire out to every live hub subscribed to the name.</summary>
        internal void Fire(string actionName)
        {
            DisplayActionHub[] targets;
            lock (_gate)
            {
                if (actionName == null || !_routes.TryGetValue(actionName, out var subs))
                    return;
                targets = Snapshot(subs);
            }
            // Enqueue outside the lock — OnTriggered is lock-free, but keeping foreign
            // code out of the gate is cheap insurance.
            foreach (var hub in targets)
                hub.OnTriggered(actionName);
        }

        private void Subscribe(string name, DisplayActionHub hub)
        {
            if (!_routes.TryGetValue(name, out var subs))
            {
                subs = new List<WeakReference<DisplayActionHub>>();
                _routes[name] = subs;
            }
            for (int i = subs.Count - 1; i >= 0; i--)
            {
                if (!subs[i].TryGetTarget(out var existing))
                    subs.RemoveAt(i);        // a discarded stack's hub, since collected
                else if (ReferenceEquals(existing, hub))
                    return;                  // already subscribed
            }
            subs.Add(new WeakReference<DisplayActionHub>(hub));
        }

        private static DisplayActionHub[] Snapshot(List<WeakReference<DisplayActionHub>> subs)
        {
            var live = new List<DisplayActionHub>(subs.Count);
            for (int i = 0; i < subs.Count; i++)
                if (subs[i].TryGetTarget(out var hub))
                    live.Add(hub);
            if (live.Count != subs.Count)
            {
                // Compact the dead entries while we hold the lock.
                subs.Clear();
                foreach (var hub in live)
                    subs.Add(new WeakReference<DisplayActionHub>(hub));
            }
            return live.ToArray();
        }
    }
}
