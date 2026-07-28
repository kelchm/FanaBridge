using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E3 gate: tick-by-tick differential traces over DisplayRuleEngine's hottest
    /// path. Goldens were captured against the unmodified engine (STAGE 1) and must stay
    /// byte-identical after the evaluator extraction (STAGE 3). Existing engine suite
    /// files are untouched.
    /// </summary>
    public class EvaluatorDifferentialTraceTests
    {
        // ── Shared doubles (mirror DisplayRuleEngineTests; do not import from it) ──

        private sealed class Clock { public long T; public long Now() => T; }

        private sealed class FakePropertyReader : IPropertyReader
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public void Set(string name, double value) => _values[name] = value;
            public void Set(string name, bool value) => _values[name] = value;
            public void Clear(string name) => _values.Remove(name);

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = raw is bool b ? (b ? 1 : 0) : (double)raw;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = raw is bool b ? b : Math.Abs((double)raw) > 1e-9;
                return true;
            }
        }

        private sealed class Harness
        {
            public readonly Clock Clock = new Clock();
            public readonly FakePropertyReader Props = new FakePropertyReader();
            public DisplayRuleEngine Engine = null!;

            public RuleEngineResult Tick(long advance = 0, bool inGame = true,
                string[]? actions = null, ItmPage? manual = null, bool manualNullPage = false)
            {
                Clock.T += advance;
                ManualNavigation? man = null;
                if (manualNullPage)
                    man = new ManualNavigation(null);
                else if (manual.HasValue)
                    man = new ManualNavigation(manual.Value);
                return Engine.Tick(new RuleEngineInput
                {
                    InGame = inGame,
                    Properties = Props,
                    TriggeredActions = actions,
                    Manual = man,
                });
            }
        }

        private static DisplayRule Rule(string id, RuleCondition when, RuleTarget show,
            HoldSpec hold, RuleEligibility eligible = RuleEligibility.InGame)
            => new DisplayRule { Id = id, When = when, Show = show, Hold = hold, Eligible = eligible };

        private static RuleCondition Level(ConditionKind kind, string name, double? value = null,
            double? hysteresis = null)
            => new RuleCondition
            {
                Kind = kind,
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name },
                Value = value,
                Hysteresis = hysteresis,
            };

        private static RuleCondition Edge(ConditionKind kind, string name)
            => new RuleCondition
            {
                Kind = kind,
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name },
            };

        private static RuleCondition Action(string name)
            => new RuleCondition
            {
                Kind = ConditionKind.ActionTriggered,
                Source = new PropertySpec { Kind = PropertyKind.FanaBridgeAction, Name = name },
            };

        private static RuleTarget Page(ItmPage page)
            => new RuleTarget { Kind = TargetKind.Page, Page = page };

        private static HoldSpec While() => new HoldSpec { Kind = HoldKind.WhileActive };
        private static HoldSpec For(int ms) => new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = ms };
        private static HoldSpec Indef() => new HoldSpec { Kind = HoldKind.UntilDismissed };

        // ── Reflection into private RuleRuntime (pre- and post-extraction shape) ──

        private static readonly FieldInfo RulesField =
            typeof(DisplayRuleEngine).GetField("_rules", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DisplayRuleEngine._rules not found");

        private sealed class RuleSnap
        {
            public string Id = "";
            public bool Satisfied;
            public bool Active;
            public bool Superseded;
            public bool EligibleNow;
            public bool HasPrev;
            public double Prev;
            public long ExpiresAt;
            public HoldKind HoldKind;
        }

        private static List<RuleSnap> ReadRuntimes(DisplayRuleEngine engine)
        {
            var list = (System.Collections.IList)RulesField.GetValue(engine)!;
            var snaps = new List<RuleSnap>(list.Count);
            foreach (object rt in list)
            {
                var t = rt.GetType();
                // Prefer nested CarrierRuntime (post-extraction); fall back to flat fields.
                var stateField = t.GetField("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object stateObj = stateField != null ? stateField.GetValue(rt)! : rt;

                var ruleField = t.GetField("Rule", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Rule field missing");
                var rule = (DisplayRule)ruleField.GetValue(rt)!;

                // When State is nested, bools live on State; when flat, on RuleRuntime.
                // Post-E3 ownership uses properties with internal setters — try field then property.
                object host = stateField != null ? stateObj : rt;
                var ht = host.GetType();
                object Member(string n)
                {
                    var f = ht.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null)
                        return f.GetValue(host)!;
                    var p = ht.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null)
                        return p.GetValue(host)!;
                    throw new InvalidOperationException(n);
                }
                bool B(string n) => (bool)Member(n);
                long L(string n) => (long)Member(n);
                double D(string n) => (double)Member(n);

                snaps.Add(new RuleSnap
                {
                    Id = rule.Id,
                    Satisfied = B("Satisfied"),
                    Active = B("Active"),
                    Superseded = B("Superseded"),
                    EligibleNow = B("EligibleNow"),
                    HasPrev = B("HasPrev"),
                    Prev = D("Prev"),
                    ExpiresAt = L("ExpiresAt"),
                    HoldKind = rule.Hold.Kind,
                });
            }
            return snaps;
        }

        // ── Trace serialization ──────────────────────────────────────────

        private static string SerializeTrace(string scenario, IReadOnlyList<string> tickLines)
        {
            var sb = new StringBuilder();
            sb.Append("SCENARIO ").Append(scenario).Append('\n');
            foreach (var line in tickLines)
                sb.Append(line).Append('\n');
            return sb.ToString();
        }

        private static string FormatTick(long t, RuleEngineResult result,
            IReadOnlyList<RuleSnap> after, IReadOnlyList<DisplayActivityEvent> newEvents,
            IReadOnlyList<bool> freshFire)
        {
            var sb = new StringBuilder();
            sb.Append("t=").Append(t);

            string? winnerId = null;
            foreach (var s in after)
            {
                if (s.Active && !s.Superseded)
                {
                    winnerId = s.Id;
                    break;
                }
            }
            sb.Append(" winner=").Append(winnerId ?? "-");
            sb.Append(" sel=").Append(result.Intent.SourceRuleId ?? "-");
            sb.Append(" intent=").Append(IntentTag(result.Intent));
            sb.Append('\n');

            for (int i = 0; i < after.Count; i++)
            {
                var s = after[i];
                var st = result.RuleStates.First(x => x.RuleId == s.Id);
                int? rem = null;
                if (s.Active && s.HoldKind == HoldKind.ForDuration)
                    rem = (int)Math.Max(0, s.ExpiresAt - t);

                sb.Append("  ").Append(s.Id)
                    .Append(" sat=").Append(s.Satisfied ? 1 : 0)
                    .Append(" act=").Append(s.Active ? 1 : 0)
                    .Append(" fresh=").Append(freshFire[i] ? 1 : 0)
                    .Append(" sup=").Append(s.Superseded ? 1 : 0)
                    .Append(" elig=").Append(s.EligibleNow ? 1 : 0)
                    .Append(" hasPrev=").Append(s.HasPrev ? 1 : 0)
                    .Append(" prev=").Append(s.HasPrev ? s.Prev.ToString("G17") : "-")
                    .Append(" rem=").Append(rem.HasValue ? rem.Value.ToString() : "-")
                    .Append(" status=").Append(st.Status)
                    .Append('\n');
            }

            foreach (var e in newEvents)
            {
                sb.Append("  EVT ").Append(e.Kind)
                    .Append(" rule=").Append(e.RuleId ?? "-")
                    .Append(" label=").Append(NormalizeLabel(e.Text))
                    .Append('\n');
            }

            return sb.ToString().TrimEnd('\n');
        }

        private static string IntentTag(RuleIntent intent)
        {
            switch (intent.Kind)
            {
                case TargetKind.Page:
                    return intent.Page.HasValue ? "P:" + intent.Page.Value : "P:null";
                case TargetKind.SegmentScreen:
                    return "S:" + (intent.ScreenId ?? "null");
                case TargetKind.Special:
                    return "X:" + intent.Command;
                default:
                    return intent.Kind.ToString();
            }
        }

        /// <summary>Collapse formatter-built labels to stable tokens (page names etc. are fine).</summary>
        private static string NormalizeLabel(string text)
            => text?.Replace('\r', ' ').Replace('\n', ' ') ?? "";

        private static string RunScenario(string name, Action<Harness, List<string>> script)
        {
            var h = new Harness();
            var lines = new List<string>();
            script(h, lines);
            return SerializeTrace(name, lines);
        }

        private static void RecordTick(Harness h, List<string> lines, long advance = 0,
            bool inGame = true, string[]? actions = null, ItmPage? manual = null,
            bool manualNullPage = false)
        {
            var prevAct = ReadRuntimes(h.Engine).Select(s => (s.Id, s.Active, s.Superseded)).ToList();
            // Capture by activity version (not timestamp) so consecutive ticks at the same
            // injected time attribute events correctly (E3-005).
            long verBefore = h.Engine.ActivityVersion;
            var result = h.Tick(advance, inGame, actions, manual, manualNullPage);
            long t = h.Clock.T;
            var after = ReadRuntimes(h.Engine);

            var allEvents = h.Engine.GetActivityEvents();
            int newCount = (int)(h.Engine.ActivityVersion - verBefore);
            // Ring is oldest-first; newly appended events are the last newCount entries
            // (ring wrap: GetActivityEvents already unwraps to chronological order).
            var newEvents = newCount <= 0
                ? (IReadOnlyList<DisplayActivityEvent>)Array.Empty<DisplayActivityEvent>()
                : allEvents.Skip(Math.Max(0, allEvents.Count - newCount)).ToList();

            // Fresh-fire identity: Fire() logs RuleFired only when !Active || Superseded.
            // Activity stream is authoritative; delta is a cross-check.
            var fresh = new List<bool>(after.Count);
            foreach (var s in after)
            {
                var p = prevAct.First(x => x.Id == s.Id);
                bool deltaFresh = (!p.Active || p.Superseded) && s.Active && !s.Superseded;
                bool eventFresh = newEvents.Any(e => e.Kind == ActivityKind.RuleFired && e.RuleId == s.Id);
                fresh.Add(eventFresh || deltaFresh);
            }

            lines.Add(FormatTick(t, result, after, newEvents, fresh));
        }

        // ── Corpus scenarios ─────────────────────────────────────────────

        private static Dictionary<string, string> BuildAllTraces()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            // Every ConditionKind × legal HoldKind (level × 3 holds; edge × for/indef;
            // whileActive-on-edge = post-coercion forDuration; event × for/indef).
            map["level_lt_while"] = RunScenario("level_lt_while", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                        Page(ItmPage.FuelErsDrs), While()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 50);
                RecordTick(h, lines, advance: 100);
            });

            map["level_lt_for"] = RunScenario("level_lt_for", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                        Page(ItmPage.FuelErsDrs), For(1000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines);
                RecordTick(h, lines, advance: 999);
                RecordTick(h, lines, advance: 1); // expiry while still true — no refire
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 50);
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines, advance: 100); // rising edge refire
            });

            map["level_lt_indef"] = RunScenario("level_lt_indef", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                        Page(ItmPage.FuelErsDrs), Indef()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines);
                RecordTick(h, lines, advance: 60_000);
                h.Props.Set(BuiltInProperties.Fuel, 50);
                RecordTick(h, lines, advance: 100); // level indef dismisses on false
            });

            map["level_gt_while"] = RunScenario("level_gt_while", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.GreaterThan, BuiltInProperties.Speed, 100),
                        Page(ItmPage.TyreTemps), While()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Speed, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Speed, 120);
                RecordTick(h, lines, advance: 50);
                h.Props.Set(BuiltInProperties.Speed, 80);
                RecordTick(h, lines, advance: 50);
            });

            map["level_le_ge_eq_ne_bool"] = RunScenario("level_le_ge_eq_ne_bool", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("le", Level(ConditionKind.LessOrEqual, BuiltInProperties.Fuel, 10),
                        Page(ItmPage.FuelErsDrs), While()),
                    Rule("ge", Level(ConditionKind.GreaterOrEqual, BuiltInProperties.Speed, 100),
                        Page(ItmPage.TyreTemps), While()),
                    Rule("eq", Level(ConditionKind.Equals, BuiltInProperties.TcLevel, 5),
                        Page(ItmPage.CarSettings), While()),
                    Rule("ne", Level(ConditionKind.NotEquals, BuiltInProperties.AbsLevel, 0),
                        Page(ItmPage.LapInfo), While()),
                    Rule("t", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                        Page(ItmPage.FuelErsDrs), While()),
                    Rule("f", Level(ConditionKind.IsFalse, BuiltInProperties.DrsAvailable),
                        Page(ItmPage.TyreTemps), While()),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 10);
                h.Props.Set(BuiltInProperties.Speed, 100);
                h.Props.Set(BuiltInProperties.TcLevel, 5);
                h.Props.Set(BuiltInProperties.AbsLevel, 1);
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                h.Props.Set(BuiltInProperties.DrsAvailable, 0.0);
                RecordTick(h, lines);
            });

            // Edge kinds × holds
            map["edge_changes_for"] = RunScenario("edge_changes_for", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(2000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines); // first sample never fires
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 100);
                RecordTick(h, lines, advance: 1999);
                RecordTick(h, lines, advance: 1); // exact expiry
            });

            map["edge_increases_for"] = RunScenario("edge_increases_for", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Increases, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(1000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 49);
                RecordTick(h, lines, advance: 50); // down — no fire
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 50); // up — fire
            });

            map["edge_decreases_for"] = RunScenario("edge_decreases_for", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Decreases, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(1000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 50);
                h.Props.Set(BuiltInProperties.BrakeBias, 49);
                RecordTick(h, lines, advance: 50);
            });

            map["edge_changes_indef"] = RunScenario("edge_changes_indef", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), Indef()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 100);
                RecordTick(h, lines, advance: 10_000);
            });

            // whileActive-on-edge coercion path: runtime hold is ForDuration (validator
            // rewrites WhileActive → ForDuration on edge). KindRaw may still say whileActive.
            map["edge_whileActive_coerced_for"] = RunScenario("edge_whileActive_coerced_for", (h, lines) =>
            {
                var hold = new HoldSpec { KindRaw = "whileActive", DurationMs = HoldSpec.DefaultDurationMs };
                hold.CoerceKind(HoldKind.ForDuration);
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.Gear),
                        Page(ItmPage.CarSettings), hold) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Gear, 2);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Gear, 3);
                RecordTick(h, lines, advance: 10);
                RecordTick(h, lines, advance: HoldSpec.DefaultDurationMs);
            });

            map["event_action_for"] = RunScenario("event_action_for", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Action("showPit"), Page(ItmPage.TyreTemps), For(1500)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                RecordTick(h, lines);
                RecordTick(h, lines, advance: 100, actions: new[] { "showPit" });
                RecordTick(h, lines, advance: 1499);
                RecordTick(h, lines, advance: 1);
            });

            map["event_action_indef"] = RunScenario("event_action_indef", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Action("showPit"), Page(ItmPage.TyreTemps), Indef()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                RecordTick(h, lines, actions: new[] { "showPit" });
                RecordTick(h, lines, advance: 5000);
            });

            // Hysteresis × every hold at exact boundary ticks — release-band-through-expiry refire
            map["hyst_while_lt"] = RunScenario("hyst_while_lt", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10, hysteresis: 2),
                        Page(ItmPage.FuelErsDrs), While()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 9); // enter
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Fuel, 11); // in release band (value+h=12)
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 12); // exact release boundary StillHolds: x < v+h → 12 < 12 false
                RecordTick(h, lines, advance: 100);
            });

            map["hyst_for_release_band_through_expiry_refire"] = RunScenario(
                "hyst_for_release_band_through_expiry_refire", (h, lines) =>
            {
                // Value enters, hold expires while value sits inside the release band,
                // then crosses the raw threshold again without first crossing the release
                // boundary — must NOT re-fire (still "satisfied" via hysteresis latch;
                // ForDuration only fires on rising edge of satisfied).
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10, hysteresis: 2),
                        Page(ItmPage.FuelErsDrs), For(1000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 5); // enter, rising, fire
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Fuel, 11); // inside release band; still satisfied
                RecordTick(h, lines, advance: 500);
                RecordTick(h, lines, advance: 500); // t=1000 expiry while sat in band
                RecordTick(h, lines, advance: 100); // still in band, no rising → no refire
                h.Props.Set(BuiltInProperties.Fuel, 9); // crosses raw threshold again without
                                                        // leaving release band first
                RecordTick(h, lines, advance: 100); // still satisfied (was), no rising → no fire
                h.Props.Set(BuiltInProperties.Fuel, 13); // leave band → unsatisfied
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 9); // genuine rising
                RecordTick(h, lines, advance: 100);
            });

            map["hyst_gt_for_release_band"] = RunScenario("hyst_gt_for_release_band", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.GreaterThan, BuiltInProperties.Speed, 100, hysteresis: 5),
                        Page(ItmPage.TyreTemps), For(800)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Speed, 110);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Speed, 97); // in band (release at <= 95): still holds x > v-h = 95
                RecordTick(h, lines, advance: 400);
                RecordTick(h, lines, advance: 400); // expiry in band
                h.Props.Set(BuiltInProperties.Speed, 101); // raw threshold again, no leave
                RecordTick(h, lines, advance: 50);
                h.Props.Set(BuiltInProperties.Speed, 90); // leave
                RecordTick(h, lines, advance: 50);
                h.Props.Set(BuiltInProperties.Speed, 110);
                RecordTick(h, lines, advance: 50);
            });

            map["hyst_indef_holds_band"] = RunScenario("hyst_indef_holds_band", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10, hysteresis: 2),
                        Page(ItmPage.FuelErsDrs), Indef()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.Fuel, 11);
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 12.0);
                RecordTick(h, lines, advance: 100); // releases
            });

            // Eligibility flips mid-hold
            map["elig_flip_mid_for"] = RunScenario("elig_flip_mid_for", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(5000), RuleEligibility.InGame) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 100);
                RecordTick(h, lines, advance: 100, inGame: false); // clears all
                RecordTick(h, lines, advance: 100, inGame: true); // clean slate
                h.Props.Set(BuiltInProperties.BrakeBias, 52); // first sample after reset? HasPrev cleared
                RecordTick(h, lines, advance: 100); // first sample after elig — no fire
                h.Props.Set(BuiltInProperties.BrakeBias, 53);
                RecordTick(h, lines, advance: 100);
            });

            map["elig_idle_and_always"] = RunScenario("elig_idle_and_always", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("idle", Level(ConditionKind.IsTrue, BuiltInProperties.PitLimiterOn),
                        Page(ItmPage.TyreTemps), While(), RuleEligibility.Idle),
                    Rule("any", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                        Page(ItmPage.FuelErsDrs), While(), RuleEligibility.Always),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.PitLimiterOn, true);
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                RecordTick(h, lines, inGame: true); // idle ineligible; always on
                RecordTick(h, lines, advance: 100, inGame: false); // idle becomes eligible — but
                                                                   // priority: idle is index 0
            });

            // Supersede: untilDismissed displaced by higher winner then winner ends
            map["supersede_indef_displaced"] = RunScenario("supersede_indef_displaced", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("hi", Action("show"), Page(ItmPage.TyreTemps), For(2000)),
                    Rule("lo", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), Indef()),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 100); // lo on screen
                RecordTick(h, lines, advance: 600, actions: new[] { "show" }); // hi preempts (dwell)
                RecordTick(h, lines, advance: 2000); // hi ends; lo superseded → armed
            });

            map["indef_fired_while_outranked_takes_over"] = RunScenario(
                "indef_fired_while_outranked_takes_over", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("hi", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(2000)),
                    Rule("lo", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                        Page(ItmPage.FuelErsDrs), Indef()),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 50);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines, advance: 100); // lo waiting
                RecordTick(h, lines, advance: 2000); // lo takes over, not superseded
            });

            // Manual-navigation dismissal
            map["manual_nav_dismiss"] = RunScenario("manual_nav_dismiss", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                        Page(ItmPage.FuelErsDrs), Indef()) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines);
                RecordTick(h, lines, advance: 100, manual: ItmPage.TyreTemps);
                RecordTick(h, lines, advance: 1000); // still true, no relatch
                h.Props.Set(BuiltInProperties.Fuel, 50);
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.Fuel, 5);
                RecordTick(h, lines, advance: 100); // fresh rising
            });

            // Dwell-floor interactions
            map["dwell_blocks_then_allows"] = RunScenario("dwell_blocks_then_allows", (h, lines) =>
            {
                // a higher priority than b. Stamp dwell via a→base→a, then a ends while
                // b is active: b is blocked until MinDwellMs (not PreemptFloor — lower prio).
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("a", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                        Page(ItmPage.TyreTemps), While()),
                    Rule("b", Level(ConditionKind.IsTrue, BuiltInProperties.PitLimiterOn),
                        Page(ItmPage.CarSettings), While()),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                h.Props.Set(BuiltInProperties.PitLimiterOn, false);
                RecordTick(h, lines); // a first selection (no dwell stamp)
                h.Props.Set(BuiltInProperties.DrsEnabled, false);
                RecordTick(h, lines, advance: DisplayRuleEngine.MinDwellMs + 50); // → base, stamps
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                RecordTick(h, lines, advance: 50); // a back — real dwell stamp
                h.Props.Set(BuiltInProperties.PitLimiterOn, true);
                h.Props.Set(BuiltInProperties.DrsEnabled, false);
                // a ends, b active; held < MinDwell → selection stays on a's page (anti-flap)
                RecordTick(h, lines, advance: 100);
                // still blocked immediately before MinDwell
                RecordTick(h, lines, advance: DisplayRuleEngine.MinDwellMs - 100 - 1);
                // at MinDwell since a's re-selection: b allowed
                RecordTick(h, lines, advance: 1);
            });

            map["dwell_preempt_floor"] = RunScenario("dwell_preempt_floor", (h, lines) =>
            {
                // Level rules so we control activation without actions list noise.
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("hi", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                        Page(ItmPage.TyreTemps), While()),
                    Rule("lo", Level(ConditionKind.IsTrue, BuiltInProperties.PitLimiterOn),
                        Page(ItmPage.CarSettings), While()),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.PitLimiterOn, true);
                h.Props.Set(BuiltInProperties.DrsEnabled, false);
                RecordTick(h, lines); // lo wins; first selection
                // Establish dwell by a prior change: turn lo off and on after settle, then preempt.
                h.Props.Set(BuiltInProperties.PitLimiterOn, false);
                RecordTick(h, lines, advance: DisplayRuleEngine.MinDwellMs + 100);
                h.Props.Set(BuiltInProperties.PitLimiterOn, true);
                RecordTick(h, lines, advance: 50); // lo back — selection change starts dwell
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                RecordTick(h, lines, advance: DisplayRuleEngine.PreemptFloorMs); // hi may preempt at 250
            });

            map["dwell_same_rank_waits_min"] = RunScenario("dwell_same_rank_waits_min", (h, lines) =>
            {
                // Lower-priority b after higher a: MinDwell applies (not PreemptFloor).
                // a must end so b becomes winner while a is still the selected intent.
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("a", Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                        Page(ItmPage.TyreTemps), While()),
                    Rule("b", Level(ConditionKind.IsTrue, BuiltInProperties.PitLimiterOn),
                        Page(ItmPage.CarSettings), While()),
                }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                h.Props.Set(BuiltInProperties.PitLimiterOn, false);
                RecordTick(h, lines); // a first
                h.Props.Set(BuiltInProperties.DrsEnabled, false);
                RecordTick(h, lines, advance: DisplayRuleEngine.MinDwellMs + 50); // base stamps
                h.Props.Set(BuiltInProperties.DrsEnabled, true);
                RecordTick(h, lines, advance: 50); // a re-selected, dwell stamps NOW
                h.Props.Set(BuiltInProperties.PitLimiterOn, true); // b waiting under a
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.DrsEnabled, false); // a ends; b wants; held=100+?
                // held from a's re-selection: need total held < MinDwell then >= MinDwell
                RecordTick(h, lines, advance: 50); // held ~200 from stamp — still blocked if stamp was 50 ago +100 +50
                RecordTick(h, lines, advance: DisplayRuleEngine.MinDwellMs); // past MinDwell — b allowed
            });

            // Edge gap / non-finite
            map["edge_gap_and_nan"] = RunScenario("edge_gap_and_nan", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(1000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Clear(BuiltInProperties.BrakeBias);
                RecordTick(h, lines, advance: 10);
                h.Props.Set(BuiltInProperties.BrakeBias, double.NaN);
                RecordTick(h, lines, advance: 10);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 10);
            });

            // ForDuration refire restarts window
            map["for_refire_restarts"] = RunScenario("for_refire_restarts", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(5000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.BrakeBias, 50);
                RecordTick(h, lines);
                h.Props.Set(BuiltInProperties.BrakeBias, 51);
                RecordTick(h, lines, advance: 100);
                h.Props.Set(BuiltInProperties.BrakeBias, 52);
                RecordTick(h, lines, advance: 1000); // restart; not fresh (already active)
                RecordTick(h, lines, advance: 4999);
                RecordTick(h, lines, advance: 1);
            });

            // Exact ±Epsilon edge boundaries + equals/notEquals release
            map["edge_epsilon_boundary"] = RunScenario("edge_epsilon_boundary", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(
                    new[] { Rule("r", Edge(ConditionKind.Changes, BuiltInProperties.BrakeBias),
                        Page(ItmPage.CarSettings), For(1000)) },
                    ItmPage.LapInfo, null, h.Clock.Now);
                double eps = CarrierEvaluator.Epsilon;
                h.Props.Set(BuiltInProperties.BrakeBias, 50.0);
                RecordTick(h, lines);
                // change by exactly Epsilon — not a fire (Abs(delta) > Epsilon required)
                h.Props.Set(BuiltInProperties.BrakeBias, 50.0 + eps);
                RecordTick(h, lines, advance: 10);
                // just past Epsilon — fire
                h.Props.Set(BuiltInProperties.BrakeBias, 50.0 + eps + eps);
                RecordTick(h, lines, advance: 10);
            });

            map["level_equals_notEquals"] = RunScenario("level_equals_notEquals", (h, lines) =>
            {
                h.Engine = DisplayRuleEngine.ForItm(new[]
                {
                    Rule("eq", Level(ConditionKind.Equals, BuiltInProperties.TcLevel, 5),
                        Page(ItmPage.CarSettings), While()),
                    Rule("ne", Level(ConditionKind.NotEquals, BuiltInProperties.AbsLevel, 0),
                        Page(ItmPage.LapInfo), While()),
                }, ItmPage.TyreTemps, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.TcLevel, 5);
                h.Props.Set(BuiltInProperties.AbsLevel, 0);
                RecordTick(h, lines); // eq on; ne false
                h.Props.Set(BuiltInProperties.TcLevel, 5 + CarrierEvaluator.Epsilon); // still equals
                RecordTick(h, lines, advance: 50);
                h.Props.Set(BuiltInProperties.TcLevel, 5 + CarrierEvaluator.Epsilon * 2); // leaves equals
                h.Props.Set(BuiltInProperties.AbsLevel, 1); // ne true
                RecordTick(h, lines, advance: 50);
                h.Props.Set(BuiltInProperties.AbsLevel, 0); // ne release
                RecordTick(h, lines, advance: 50);
            });

            // Live-rule mutation after engine construction (E3-002)
            map["live_mutate_after_construction"] = RunScenario("live_mutate_after_construction", (h, lines) =>
            {
                var rule = Rule("r", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                    Page(ItmPage.FuelErsDrs), While());
                h.Engine = DisplayRuleEngine.ForItm(new[] { rule }, ItmPage.LapInfo, null, h.Clock.Now);
                h.Props.Set(BuiltInProperties.Fuel, 15);
                RecordTick(h, lines); // not satisfied at threshold 10
                rule.When.Value = 20; // live mutation
                RecordTick(h, lines, advance: 50); // now satisfied
                rule.Hold.Kind = HoldKind.ForDuration;
                rule.Hold.DurationMs = 800;
                rule.When.Kind = ConditionKind.Changes;
                rule.When.Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Gear };
                rule.When.Value = null;
                h.Props.Set(BuiltInProperties.Gear, 1);
                RecordTick(h, lines, advance: 50); // first sample
                h.Props.Set(BuiltInProperties.Gear, 2);
                RecordTick(h, lines, advance: 50); // edge fire, duration from live hold
                RecordTick(h, lines, advance: 800); // expiry
            });

            return map;
        }

        // ── Golden pin ───────────────────────────────────────────────────

        // Set true only to regenerate the fixture after a deliberate corpus change.
        private const bool DumpGoldens = false;

        [Fact]
        public void DifferentialTraces_MatchGoldens()
        {
            var actual = BuildAllTraces();
            var combined = CombineTraces(actual);

            if (DumpGoldens)
            {
                var alt = System.IO.Path.Combine(AppContext.BaseDirectory, "e3-trace-goldens.txt");
                System.IO.File.WriteAllText(alt, combined);
                Assert.Fail("DumpGoldens=true — wrote " + alt);
            }

            string expected = LoadGoldenFixture();
            Assert.Equal(expected, combined);
        }

        private static string CombineTraces(Dictionary<string, string> actual)
        {
            var dump = new StringBuilder();
            foreach (var kv in actual.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                dump.Append("===== ").Append(kv.Key).Append(" =====\n");
                dump.Append(kv.Value);
                if (!kv.Value.EndsWith("\n"))
                    dump.Append('\n');
            }
            return dump.ToString();
        }

        private static string LoadGoldenFixture()
        {
            var asm = typeof(EvaluatorDifferentialTraceTests).Assembly;
            const string suffix = ".Display.Fixtures.evaluator-differential-traces.golden.txt";
            string resource = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using (var stream = asm.GetManifestResourceStream(resource))
            using (var reader = new System.IO.StreamReader(stream!))
                return reader.ReadToEnd();
        }
    }
}
