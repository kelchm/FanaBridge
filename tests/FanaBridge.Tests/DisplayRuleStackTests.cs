using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Display;
using FanaBridge.Protocol;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Integration at the <see cref="DisplayRuleStack"/> level with fakes: the engine's
    /// input wiring (properties, drained actions, manual navigation) flowing into the
    /// page director, and the snapshot recomposition gate. Everything below the stack
    /// has its own suites (engine, director, property source, action hub).
    /// </summary>
    public class DisplayRuleStackTests
    {
        // ── Fakes / helpers ──────────────────────────────────────────────

        private sealed class FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Idle;
            public byte? CurrentWirePage { get; set; }
            public long SyncGeneration { get; set; }
            public List<byte> Requests { get; } = new List<byte>();
            public void RequestPage(byte wirePage) => Requests.Add(wirePage);

            public void Land(byte wirePage)
            {
                State = ItmLifecycleState.Synced;
                CurrentWirePage = wirePage;
                SyncGeneration++;
            }
        }

        private sealed class Harness
        {
            public readonly FakePageControl Control = new FakePageControl();
            public readonly List<string> Log = new List<string>();
            public long T;
            public DisplayRuleStack Stack = null!;

            public static Harness Create(string configJson, byte itmDeviceId = 2,
                byte defaultWirePage = 1)
            {
                var h = new Harness();
                var config = DisplayConfigSerializer.Load(configJson, h.Log.Add);
                h.Stack = new DisplayRuleStack(config, h.Control, itmDeviceId,
                    defaultWirePage, h.Log.Add, () => h.T);
                return h;
            }

            public DisplayRuleSnapshot? Tick(GameData data) => Stack.Tick(null, data);
        }

        // GameData by reflection (see ItmTelemetryTests).
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);
        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        private static GameData SpeedData(double speed)
        {
            var s = NewStatus();
            Set(s, "SpeedLocal", speed);
            return Data(s);
        }

        private const string SpeedRuleConfig =
            "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
            + "{ \"id\": \"r1\", \"name\": \"Fast\", "
            + "\"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Speed\" }, \"value\": 100 }, "
            + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

        private const string ActionRuleConfig =
            "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
            + "{ \"id\": \"a1\", "
            + "\"when\": { \"kind\": \"actionTriggered\", \"source\": { \"kind\": \"fanaBridgeAction\", \"name\": \"ShowTyres\" } }, "
            + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" }, "
            + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } } ] } }";

        // ── Engine input → director output ───────────────────────────────

        [Fact]
        public void RuleFires_ExactlyOneRequest_ForTheRightWirePage()
        {
            var h = Harness.Create(SpeedRuleConfig);
            h.Control.Land(1);                 // synced on Lap Info (the base)

            h.Tick(SpeedData(50));             // armed, resting on base — nothing to do
            Assert.Empty(h.Control.Requests);

            h.Tick(SpeedData(150));            // fires → Tyre Temps (standard wire 5)
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);

            for (int i = 0; i < 30; i++)       // holds across frames — no request spam
            {
                h.T += 16;
                h.Tick(SpeedData(150));
            }
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void RuleReleases_ReturnsToBase_WithOneRequest()
        {
            var h = Harness.Create(SpeedRuleConfig);
            h.Control.Land(1);
            h.Tick(SpeedData(150));
            h.Control.Land(5);                 // the switch confirmed
            h.T += 5000;                       // past the dwell floor
            h.Tick(SpeedData(50));             // condition released → base (wire 1)
            Assert.Equal(new byte[] { 5, 1 }, h.Control.Requests);
        }

        [Fact]
        public void ActionFire_ReachesTheEngine_ThroughTheHubDrain()
        {
            var h = Harness.Create(ActionRuleConfig);
            h.Control.Land(1);
            h.Tick(SpeedData(0));
            Assert.Empty(h.Control.Requests);

            // SimHub fires the mapped action (any thread); the next frame drains it.
            h.Stack.Actions.OnTriggered("ShowTyres");
            h.T += 16;
            h.Tick(SpeedData(0));
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void ManualNavigation_AdoptedNextTick_NeverFought()
        {
            var h = Harness.Create(SpeedRuleConfig);
            h.Control.Land(1);
            h.Tick(SpeedData(50));             // baseline

            h.Control.Land(4);                 // driver pressed the button → Lap Times
            h.Tick(SpeedData(50));             // director detects; engine sees it NEXT tick
            h.T += 16;
            h.Tick(SpeedData(50));             // engine rests on Lap Times now
            h.T += 16;
            h.Tick(SpeedData(50));
            Assert.Empty(h.Control.Requests);  // adopt, never fight
        }

        [Fact]
        public void BasePage_FallsBackToTheDeviceDefaultPageSetting()
        {
            // No config base page: the stack maps the device's ITM default page (wire)
            // to its identity — here wire 5 = Tyre Temps on the standard table.
            var h = Harness.Create(SpeedRuleConfig, defaultWirePage: 5);
            h.Control.Land(1);                 // display came up on Lap Info
            h.Tick(SpeedData(50));             // resting target is Tyre Temps
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void ConfigBasePage_WinsOverTheDefaultPageSetting()
        {
            const string basePageConfig =
                "{ \"schemaVersion\": 1, \"itm\": { \"basePage\": \"lapTimes\", \"rules\": [] } }";
            var h = Harness.Create(basePageConfig, defaultWirePage: 5);
            h.Control.Land(1);
            h.Tick(SpeedData(50));
            Assert.Equal(new byte[] { 4 }, h.Control.Requests);   // Lap Times, wire 4
        }

        [Fact]
        public void BentleyDeviceId_ResolvesItsOwnWireNumbers()
        {
            var h = Harness.Create(SpeedRuleConfig, itmDeviceId: 4);
            h.Control.Land(1);
            h.Tick(SpeedData(150));
            Assert.Equal(new byte[] { 4 }, h.Control.Requests);   // Tyre Temps = wire 4 on Bentley
        }

        [Fact]
        public void BaseWirePage_ExposesTheEngineBase_ForTheDriverHandoff()
        {
            // The device instance feeds this to the ITM driver as the effective default
            // page while the stack is live, so the lifecycle and the engine share one
            // base-page authority (config base page wins over the device setting).
            const string basePageConfig =
                "{ \"schemaVersion\": 1, \"itm\": { \"basePage\": \"lapTimes\", \"rules\": [] } }";
            var h = Harness.Create(basePageConfig, defaultWirePage: 5);
            Assert.Equal(4, h.Stack.BaseWirePage);                // Lap Times = wire 4

            var fallback = Harness.Create(SpeedRuleConfig, defaultWirePage: 5);
            Assert.Equal(5, fallback.Stack.BaseWirePage);         // no config base: the setting
        }

        [Fact]
        public void UncatalogedPage_AdoptedThroughTheStack_NeverFought()
        {
            var h = Harness.Create(SpeedRuleConfig);
            h.Control.Land(1);
            h.Tick(SpeedData(50));                 // baseline, resting on base

            // The wheel reaches a page outside the catalog: the controller adopts it —
            // Synced, generation bumped, page unknown. The stack must adopt it too:
            // no request may ever fight the driver's choice, however long it holds.
            h.Control.CurrentWirePage = null;
            h.Control.SyncGeneration++;
            for (int i = 0; i < 5; i++)
            {
                h.T += 16;
                h.Tick(SpeedData(50));
            }
            Assert.Empty(h.Control.Requests);
        }

        // ── Legacy surface (P2: log-only) ────────────────────────────────

        [Fact]
        public void ItmRuleTargetingLegacyScreen_RequestsLegacyPage_AndLogsTheScreen()
        {
            const string config =
                "{ \"schemaVersion\": 1, "
                + "\"itm\": { \"rules\": [ { \"id\": \"r1\", "
                + "\"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Speed\" }, \"value\": 100 }, "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"pit\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] }, "
                + "\"legacy\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ] } }";

            var h = Harness.Create(config);
            h.Control.Land(1);
            h.Tick(SpeedData(150));
            Assert.Equal(new byte[] { 6 }, h.Control.Requests);   // the legacy page
            Assert.Contains(h.Log, m => m.Contains("legacy screen 'pit'"));
        }

        [Fact]
        public void LegacyEngineIntent_LoggedOnChange_NotPerFrame()
        {
            const string config =
                "{ \"schemaVersion\": 1, "
                + "\"legacy\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ], "
                + "\"rules\": [ { \"id\": \"l1\", "
                + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"pit\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

            var h = Harness.Create(config);
            h.Control.Land(1);

            var s = NewStatus();
            Set(s, "IsInPitLane", 1);
            for (int i = 0; i < 5; i++)
            {
                h.T += 16;
                h.Tick(Data(s));
            }
            Assert.Single(h.Log, m => m.Contains("legacy surface wants screen 'pit'"));
        }

        // ── Snapshot ─────────────────────────────────────────────────────

        [Fact]
        public void Snapshot_ComposedOnChange_NullWhenNothingChanged()
        {
            var h = Harness.Create(SpeedRuleConfig);
            h.Control.Land(1);

            var first = h.Tick(SpeedData(50));
            Assert.NotNull(first);                       // first composition always publishes
            Assert.Equal("Lap Info", first!.IntentDescription);
            var row = Assert.Single(first.ItmRules);
            Assert.Equal("r1", row.RuleId);
            Assert.Equal("Fast", row.Label);             // the user's name, via the formatter
            Assert.Equal(RuleStatus.Armed, row.Status);

            h.T += 16;
            Assert.Null(h.Tick(SpeedData(50)));          // nothing changed — no recompose

            h.T += 16;
            var fired = h.Tick(SpeedData(150));          // rule fired: status + activity
            Assert.NotNull(fired);
            Assert.Equal(RuleStatus.OnScreen, Assert.Single(fired!.ItmRules).Status);
            Assert.Contains(fired.Activity, e => e.Kind == ActivityKind.RuleFired);
            Assert.True(fired.ActivityVersion > first.ActivityVersion);
        }

        [Fact]
        public void Snapshot_FollowsAlternateFlips()
        {
            // An Alternate target flips the emitted intent every period with NO activity
            // event and NO status change (the rule stays OnScreen) — the published
            // IntentDescription must still follow what the display actually shows.
            const string config =
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", "
                + "\"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Speed\" }, \"value\": 100 }, "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\", \"periodMs\": 2000 }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";
            var h = Harness.Create(config);
            h.Control.Land(1);

            var fired = h.Tick(SpeedData(150));
            Assert.NotNull(fired);
            Assert.Equal("Fuel / ERS / DRS", fired!.IntentDescription);

            h.T += 500;
            Assert.Null(h.Tick(SpeedData(150)));          // same phase — nothing changed

            h.T += 1500;                                  // into the second phase
            var flipped = h.Tick(SpeedData(150));
            Assert.NotNull(flipped);
            Assert.Equal("Tire Temps", flipped!.IntentDescription);
        }

        [Fact]
        public void Snapshot_IncludesLegacyRules()
        {
            const string config =
                "{ \"schemaVersion\": 1, "
                + "\"legacy\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ], "
                + "\"rules\": [ { \"id\": \"l1\", "
                + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"pit\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";
            var h = Harness.Create(config);
            h.Control.Land(1);
            var snapshot = h.Tick(SpeedData(0));
            Assert.NotNull(snapshot);
            Assert.Empty(snapshot!.ItmRules);
            Assert.Equal("l1", Assert.Single(snapshot.LegacyRules).RuleId);
        }
    }
}
