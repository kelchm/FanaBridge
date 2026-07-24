using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The mapped-control trigger path: action-name collection from a config,
    /// registration via the injected registrar seam (once per registration token —
    /// the production token is the SimHub plugin manager, whose in-process restarts
    /// wipe registrations), the cross-thread enqueue/drain hand-off, and the bounded
    /// queue. No live PluginManager is touched here — the production overload is a
    /// thin AddAction wrapper over the same core.
    /// </summary>
    public class DisplayActionHubTests
    {
        private static DisplayCustomizationConfig Load(string json)
            => DisplayConfigSerializer.Load(json, _ => { });

        // A config whose rules reference the given action names (one ITM rule per
        // name; the last name is duplicated into a legacy rule to prove dedupe).
        private static DisplayCustomizationConfig ConfigWithActions(params string[] names)
        {
            string Rule(string id, string name, string target) =>
                "{ \"id\": \"" + id + "\", "
                + "\"when\": { \"kind\": \"actionTriggered\", \"source\": { \"kind\": \"fanaBridgeAction\", \"name\": \"" + name + "\" } }, "
                + "\"show\": " + target + ", \"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } }";

            var itmRules = string.Join(", ", names.Select(
                (n, i) => Rule("itm" + i, n, "{ \"kind\": \"page\", \"page\": \"tyreTemps\" }")));
            string? legacyRule = names.Length > 0
                ? Rule("leg0", names[names.Length - 1],
                    "{ \"kind\": \"segmentScreen\", \"screenId\": \"S1\" }")
                : null;

            return Load("{ \"schemaVersion\": 1, "
                + "\"itm\": { \"rules\": [ " + itmRules + " ] }, "
                + "\"segmentDisplay\": { \"screens\": [ { \"id\": \"S1\", \"text\": \"PIT\" } ], \"rules\": [ "
                + (legacyRule ?? "") + " ] } }");
        }

        [Fact]
        public void CollectsActionNames_AcrossBothRuleSets_Deduped()
        {
            var hub = new DisplayActionHub(ConfigWithActions("ShowTyres", "ShowFuel"));
            Assert.Equal(new[] { "ShowTyres", "ShowFuel" }, hub.ActionNames);
        }

        [Fact]
        public void ConfigWithoutActionRules_HasNoNames()
        {
            var hub = new DisplayActionHub(Load("{ \"schemaVersion\": 1 }"));
            Assert.Empty(hub.ActionNames);
        }

        // ── Registration (seam) ──────────────────────────────────────────
        // Each test builds its OWN router — the production wiring shares one
        // (DisplayActionRouter.Shared), but parallel tests must not share state.

        [Fact]
        public void EnsureRegistered_RegistersEachNameOnce_PerToken()
        {
            var hub = new DisplayActionHub(ConfigWithActions("A", "B"));
            var router = new DisplayActionRouter();
            var registered = new List<string>();
            var token = new object();

            hub.EnsureRegistered(token, router, (name, _) => registered.Add(name));
            hub.EnsureRegistered(token, router, (name, _) => registered.Add(name));   // same token: no-op
            Assert.Equal(new[] { "A", "B" }, registered);
        }

        [Fact]
        public void NewToken_ReRegisters()
        {
            // A plugin manager restart hands the frame path a new manager reference;
            // its action registry is fresh, so everything must register again.
            var hub = new DisplayActionHub(ConfigWithActions("A"));
            var router = new DisplayActionRouter();
            var registered = new List<string>();

            hub.EnsureRegistered(new object(), router, (name, _) => registered.Add(name));
            hub.EnsureRegistered(new object(), router, (name, _) => registered.Add(name));
            Assert.Equal(new[] { "A", "A" }, registered);
        }

        [Fact]
        public void RegistrarThrows_OtherActionsStillRegister()
        {
            var log = new List<string>();
            var hub = new DisplayActionHub(ConfigWithActions("Bad", "Good"), log.Add);
            var registered = new List<string>();

            hub.EnsureRegistered(new object(), new DisplayActionRouter(), (name, _) =>
            {
                if (name == "Bad") throw new InvalidOperationException("host rejected");
                registered.Add(name);
            });

            Assert.Equal(new[] { "Good" }, registered);
            Assert.Contains(log, m => m.Contains("Bad"));
        }

        [Fact]
        public void StackRebuild_KeepFirstHostRegistry_FiresReachTheLiveHub()
        {
            // The host's action registry keeps the FIRST handler registered under a name
            // and silently ignores repeats — so when a stack rebuild (reconnect, wheel
            // swap, ITM toggle) constructs a fresh hub against the SAME manager, the
            // fresh hub can never re-bind the name. Fires must still reach it: the
            // handler the host holds is the router's, not any hub's.
            var router = new DisplayActionRouter();
            var host = new Dictionary<string, Action<string>>();   // keep-first, like SimHub
            void Register(string name, Action<string> fire)
            {
                if (!host.ContainsKey(name))
                    host[name] = fire;
            }
            var token = new object();

            var hub1 = new DisplayActionHub(ConfigWithActions("ShowTyres"));
            hub1.EnsureRegistered(token, router, Register);

            var hub2 = new DisplayActionHub(ConfigWithActions("ShowTyres"));
            hub2.EnsureRegistered(token, router, Register);

            host["ShowTyres"]("ShowTyres");        // a wheel-button fire after the rebuild
            var drained = new List<string>();
            hub2.DrainTriggered(drained);
            Assert.Equal(new[] { "ShowTyres" }, drained);
        }

        [Fact]
        public void TwoDevicesSharingAnActionName_BothReceiveTheFire()
        {
            // Action names are namespaced per plugin type, not per device — two devices
            // whose configs reference the same name share ONE host registration, and the
            // router fans each fire out to both.
            var router = new DisplayActionRouter();
            var registered = new List<string>();
            var fires = new Dictionary<string, Action<string>>();
            var token = new object();

            var hubA = new DisplayActionHub(ConfigWithActions("Shared"));
            var hubB = new DisplayActionHub(ConfigWithActions("Shared"));
            hubA.EnsureRegistered(token, router, (n, f) => { registered.Add(n); fires[n] = f; });
            hubB.EnsureRegistered(token, router, (n, f) => { registered.Add(n); fires[n] = f; });

            Assert.Equal(new[] { "Shared" }, registered);   // host-registered once per manager
            fires["Shared"]("Shared");
            var a = new List<string>();
            var b = new List<string>();
            hubA.DrainTriggered(a);
            hubB.DrainTriggered(b);
            Assert.Equal(new[] { "Shared" }, a);
            Assert.Equal(new[] { "Shared" }, b);
        }

        // ── Enqueue / drain ──────────────────────────────────────────────

        [Fact]
        public void Fires_DrainInOrder_ThenQueueIsEmpty()
        {
            var hub = new DisplayActionHub(ConfigWithActions("A", "B"));
            hub.OnTriggered("A");
            hub.OnTriggered("B");
            hub.OnTriggered("A");

            var drained = new List<string>();
            hub.DrainTriggered(drained);
            Assert.Equal(new[] { "A", "B", "A" }, drained);

            drained.Clear();
            hub.DrainTriggered(drained);
            Assert.Empty(drained);
        }

        [Fact]
        public void RegisteredHandler_RoutesFiresIntoTheQueue()
        {
            // End-to-end through the seam: the callback the registrar receives is the
            // router's fire path SimHub's action handler will invoke.
            var hub = new DisplayActionHub(ConfigWithActions("ShowTyres"));
            var fires = new Dictionary<string, Action<string>>();
            hub.EnsureRegistered(new object(), new DisplayActionRouter(),
                (name, fire) => fires[name] = fire);

            fires["ShowTyres"]("ShowTyres");
            var drained = new List<string>();
            hub.DrainTriggered(drained);
            Assert.Equal(new[] { "ShowTyres" }, drained);
        }

        [Fact]
        public async Task ConcurrentFires_AllDrained()
        {
            // Handlers may fire on any thread; a frame's drain must see every fire.
            var hub = new DisplayActionHub(ConfigWithActions("A"));
            var tasks = new Task[4];
            for (int t = 0; t < tasks.Length; t++)
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                        hub.OnTriggered("A");
                });
            await Task.WhenAll(tasks);

            var drained = new List<string>();
            hub.DrainTriggered(drained);
            Assert.Equal(40, drained.Count);
        }

        [Fact]
        public void QueueIsBounded_DropsBeyondLimit_WarnsOnce()
        {
            var log = new List<string>();
            var hub = new DisplayActionHub(ConfigWithActions("A"), log.Add);

            for (int i = 0; i < DisplayActionHub.MaxPending + 20; i++)
                hub.OnTriggered("A");

            var drained = new List<string>();
            hub.DrainTriggered(drained);
            Assert.Equal(DisplayActionHub.MaxPending, drained.Count);
            Assert.Single(log, m => m.Contains("dropping"));
        }

        [Fact]
        public void Drain_RestoresCapacity()
        {
            var hub = new DisplayActionHub(ConfigWithActions("A"));
            for (int i = 0; i < DisplayActionHub.MaxPending; i++)
                hub.OnTriggered("A");
            hub.DrainTriggered(new List<string>());

            hub.OnTriggered("A");   // fits again after the drain
            var drained = new List<string>();
            hub.DrainTriggered(drained);
            Assert.Single(drained);
        }
    }
}
