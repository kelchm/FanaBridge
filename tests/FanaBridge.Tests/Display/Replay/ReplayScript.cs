using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using GameReaderCommon;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>One pure scripted action (no wall clock, no threads).</summary>
    internal abstract class ReplayStep
    {
        public abstract void Apply(ReplaySession session);
    }

    internal sealed class AdvanceMsStep : ReplayStep
    {
        public AdvanceMsStep(int ms) => Ms = ms;
        public int Ms { get; }
        public override void Apply(ReplaySession session) => session.Clock.T += Ms;
    }

    internal sealed class TelemetryStep : ReplayStep
    {
        public string? Gear { get; set; }
        public double? Speed { get; set; }
        public double? Rpm { get; set; }
        public int? Position { get; set; }
        public double? Fuel { get; set; }
        public int? IsInPitLane { get; set; }
        public int? PitLimiterOn { get; set; }
        public bool? GameRunning { get; set; }
        public string? GameName { get; set; }

        public override void Apply(ReplaySession session)
        {
            if (Gear != null) session.Telemetry.Gear = Gear;
            if (Speed.HasValue) session.Telemetry.Speed = Speed.Value;
            if (Rpm.HasValue) session.Telemetry.Rpm = Rpm.Value;
            if (Position.HasValue) session.Telemetry.Position = Position.Value;
            if (Fuel.HasValue) session.Telemetry.Fuel = Fuel.Value;
            if (IsInPitLane.HasValue) session.Telemetry.IsInPitLane = IsInPitLane.Value;
            if (PitLimiterOn.HasValue) session.Telemetry.PitLimiterOn = PitLimiterOn.Value;
            if (GameRunning.HasValue) session.Telemetry.GameRunning = GameRunning.Value;
            if (GameName != null) session.Telemetry.GameName = GameName;
        }
    }

    internal sealed class FrameStep : ReplayStep
    {
        public override void Apply(ReplaySession session) => session.Frame();
    }

    internal sealed class PushItmReportStep : ReplayStep
    {
        public PushItmReportStep(byte[] report) => Report = report;
        public byte[] Report { get; }
        public override void Apply(ReplaySession session)
            => session.Transport.Itm.Enqueue(Report);
    }

    internal sealed class SetTransportAcceptsStep : ReplayStep
    {
        public SetTransportAcceptsStep(bool accept) => Accept = accept;
        public bool Accept { get; }
        public override void Apply(ReplaySession session)
            => session.Transport.SetAccepts(Accept);
    }

    internal sealed class DisconnectStep : ReplayStep
    {
        public override void Apply(ReplaySession session) => session.Transport.Disconnect();
    }

    internal sealed class ReconnectStep : ReplayStep
    {
        public override void Apply(ReplaySession session) => session.Transport.Connect(0);
    }

    /// <summary>Re-apply current settings JSON to simulate a mid-session config reload.</summary>
    internal sealed class ReloadConfigStep : ReplayStep
    {
        public override void Apply(ReplaySession session) => session.ReloadConfig();
    }

    /// <summary>Bump the wheelbase wheel-change counter so ITM cold-restarts.</summary>
    internal sealed class WheelChangeStep : ReplayStep
    {
        public override void Apply(ReplaySession session) => session.SimulateWheelChange();
    }

    /// <summary>Mutable telemetry bag shared by script steps.</summary>
    internal sealed class TelemetryState
    {
        public string Gear = "4";
        public double Speed = 142.0;
        public double Rpm = 7000;
        public int Position = 2;
        public double Fuel = 5.0; // default BELOW greaterThan 10 threshold
        public int IsInPitLane;
        public int PitLimiterOn;
        public bool GameRunning = true;
        public string GameName = "iRacing";
    }

    /// <summary>
    /// Builds a deterministic script for a matrix cell: bring-up, optional knowledge
    /// land, condition stimulus, press path, wire fault injection, hysteresis sequence.
    /// </summary>
    internal static class ReplayScript
    {
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")!
                .MakeGenericType(typeof(object));

        public static IReadOnlyList<ReplayStep> For(ReplayCell cell)
        {
            var steps = new List<ReplayStep>();

            // Baseline telemetry: fuel low so greaterThan-10 is inactive at start.
            steps.Add(new TelemetryStep
            {
                Gear = "4",
                Speed = 142,
                Rpm = 7000,
                Position = 2,
                Fuel = 5,
                IsInPitLane = 0,
                PitLimiterOn = 0,
                GameRunning = cell.Runs != ReplayRuns.Idle,
                GameName = "iRacing",
            });

            // Bring-up frames.
            steps.Add(new FrameStep());
            steps.Add(new AdvanceMsStep(16));
            steps.Add(new FrameStep());

            if (cell.IsItmDevice)
            {
                if (cell.Knowledge == ReplayKnowledge.KnownPage)
                {
                    // Firmware answers with page 1 (Lap Info) subscriptions.
                    steps.Add(new PushItmReportStep(LapInfoPush(cell.ItmDeviceId)));
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(80));
                    steps.Add(new FrameStep()); // judged → Synced
                    steps.Add(new AdvanceMsStep(30));
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(60));
                    steps.Add(new FrameStep());
                }
                else
                {
                    // Unknown-at-connect: tick without a page announce.
                    steps.Add(new AdvanceMsStep(80));
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(80));
                    steps.Add(new FrameStep());
                }
            }
            else
            {
                // Segment-only: a few live frames.
                steps.Add(new FrameStep());
                steps.Add(new FrameStep());
            }

            // Declined-sends injection window (before stimulus).
            if (cell.Wire == ReplayWire.DeclinedSends)
            {
                steps.Add(new SetTransportAcceptsStep(false));
                steps.Add(new FrameStep());
                steps.Add(new FrameStep());
                steps.Add(new SetTransportAcceptsStep(true));
            }

            // Lifecycle recovery: disconnect/reconnect mid-session.
            if (cell.Wire == ReplayWire.LifecycleRecovery)
            {
                steps.Add(new DisconnectStep());
                steps.Add(new FrameStep());
                steps.Add(new AdvanceMsStep(50));
                steps.Add(new ReconnectStep());
                steps.Add(new FrameStep());
                if (cell.IsItmDevice && cell.Knowledge == ReplayKnowledge.KnownPage)
                {
                    steps.Add(new PushItmReportStep(LapInfoPush(cell.ItmDeviceId)));
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(80));
                    steps.Add(new FrameStep());
                }
            }

            // Condition stimulus / hysteresis boundary sequence.
            if (cell.Block == ReplayBlock.Hysteresis && cell.Hysteresis.HasValue)
                AppendHysteresisSequence(steps, cell);
            else
                AppendConditionStimulus(steps, cell);

            // Press paths via ITM page announces (wheel-side).
            // FR-8: ManualPress vs AdoptedPress are distinct stimuli.
            // ManualPress: cataloged page change that the director reports as manual nav.
            // AdoptedPress: different page + second land on the same page to exercise the
            // adopt edge (generation advance while uncommanded) rather than a duplicate report.
            if (cell.IsItmDevice && cell.Press != ReplayPress.None)
            {
                switch (cell.Press)
                {
                    case ReplayPress.ManualPress:
                        // Tyre temps (wire 5) — cataloged manual navigation.
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 5)));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(30));
                        steps.Add(new FrameStep());
                        break;
                    case ReplayPress.AdoptedPress:
                        // Fuel/ERS (wire 4) first, then re-land generation edge for adopt.
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 4)));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                        // Second generation on same page is not continuous re-adopt;
                        // land a different uncommanded page so the director adopt edge fires.
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 6))); // Legacy
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                        break;
                    case ReplayPress.RejectOnRevert:
                        // Out-of-intent page while rejectUncommanded is on.
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 7)));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(30));
                        steps.Add(new FrameStep());
                        break;
                }
            }

            // Kept-behavior scripts must DRIVE their named behavior (FR-7).
            AppendKeptBehavior(steps, cell);

            // Idle-runs cells: drop out of game after stimulus.
            if (cell.Runs == ReplayRuns.Idle)
            {
                steps.Add(new TelemetryStep { GameRunning = false });
                steps.Add(new FrameStep());
                steps.Add(new FrameStep());
                // Re-enter idle stimulus
                steps.Add(new TelemetryStep { Fuel = 50, IsInPitLane = 1 });
                steps.Add(new FrameStep());
                steps.Add(new FrameStep());
            }

            // Final settle frames.
            steps.Add(new AdvanceMsStep(100));
            steps.Add(new FrameStep());
            steps.Add(new FrameStep());

            return steps;
        }

        /// <summary>
        /// FR-7: each named kept-behavior cell injects stimuli for its claimed law.
        /// </summary>
        private static void AppendKeptBehavior(List<ReplayStep> steps, ReplayCell cell)
        {
            string? name = cell.KeptBehaviorName;
            if (name == null)
                return;

            switch (name)
            {
                case "game-start-manual-reset":
                    steps.Add(new TelemetryStep { GameName = "AssettoCorsa", GameRunning = true });
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    steps.Add(new TelemetryStep { GameName = "iRacing", GameRunning = true });
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    break;

                case "dismissal-law-generalization":
                case "supersede-retired-untilDismissed-resumes":
                    // Activate, then a manual press that dismisses untilDismissed claims.
                    steps.Add(new TelemetryStep { Fuel = 50, PitLimiterOn = 1 });
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(50));
                    steps.Add(new FrameStep());
                    if (cell.IsItmDevice)
                    {
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 5)));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                    }
                    // Drop the high claim so superseded/dismissed resume can re-fire.
                    steps.Add(new TelemetryStep { PitLimiterOn = 0 });
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    break;

                case "wheel-screen-release-reclaim-ordering":
                    // Hold a wheel-screen special, then release and observe reclaim.
                    steps.Add(new TelemetryStep { IsInPitLane = 1, Fuel = 50 });
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(50));
                    steps.Add(new FrameStep());
                    steps.Add(new TelemetryStep { IsInPitLane = 0 });
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    break;

                case "config-reload-mid-crossing-x-wheel-screen-hold":
                    // Cross into the condition, hold wheel-screen, then reload mid-hold.
                    steps.Add(new TelemetryStep { Fuel = 50, IsInPitLane = 1 });
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    steps.Add(new ReloadConfigStep());
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    steps.Add(new TelemetryStep { IsInPitLane = 0 });
                    steps.Add(new FrameStep());
                    break;

                case "wheel-change-x-reject-fight-x-keepalive":
                    // Uncommanded page under reject, then wheel-change (lifecycle already
                    // injected by Wire=LifecycleRecovery for this cell).
                    if (cell.IsItmDevice)
                    {
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 7)));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                        steps.Add(new WheelChangeStep());
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(100));
                        steps.Add(new FrameStep());
                    }
                    break;

                case "walk-wrap-over-removed-members":
                    // Manual walk: successive page announces wrap the compiled walk.
                    if (cell.IsItmDevice)
                    {
                        foreach (byte page in new byte[] { 1, 2, 3, 1 })
                        {
                            steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, page)));
                            steps.Add(new FrameStep());
                            steps.Add(new AdvanceMsStep(80));
                            steps.Add(new FrameStep());
                        }
                    }
                    break;

                case "reject-uncommanded-fresh-fight":
                case "reject-uncommanded-in-window-reassert":
                case "reject-uncommanded-exhausted-surrender":
                    // Press path already injects RejectOnRevert; extend reassert/exhaust.
                    if (cell.IsItmDevice)
                    {
                        int repeats = name.Contains("exhausted") ? 4
                            : name.Contains("reassert") ? 2
                            : 1;
                        for (int i = 0; i < repeats; i++)
                        {
                            steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 7)));
                            steps.Add(new FrameStep());
                            steps.Add(new AdvanceMsStep(name.Contains("reassert") ? 50 : 200));
                            steps.Add(new FrameStep());
                        }
                    }
                    break;

                case "cycle-free-run-resume":
                    // Let the cycle tick across a period, interrupt, then resume.
                    steps.Add(new TelemetryStep { Fuel = 50 });
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(1500));
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(1500));
                    steps.Add(new FrameStep());
                    steps.Add(new AdvanceMsStep(1500));
                    steps.Add(new FrameStep());
                    break;

                case "blank-compile-three-row-split":
                    // Idle-floor blank path: leave game so rest.idle compiles.
                    steps.Add(new TelemetryStep { GameRunning = false });
                    steps.Add(new FrameStep());
                    steps.Add(new FrameStep());
                    break;

                case "unknown-page-at-connect-propagation":
                    // Knowledge axis already omits page announce; give a late land.
                    if (cell.IsItmDevice)
                    {
                        steps.Add(new AdvanceMsStep(200));
                        steps.Add(new FrameStep());
                        steps.Add(new PushItmReportStep(PagePush(cell.ItmDeviceId, 1)));
                        steps.Add(new FrameStep());
                        steps.Add(new AdvanceMsStep(80));
                        steps.Add(new FrameStep());
                    }
                    break;

                case "hysteresis-boundary-x-declined-send-x-cycle-flip":
                    // Hysteresis sequence is appended by the Hysteresis block path; for
                    // this kept cell, drive a threshold cross with declined window open.
                    steps.Add(new SetTransportAcceptsStep(false));
                    steps.Add(new TelemetryStep { Fuel = 5 });
                    steps.Add(new FrameStep());
                    steps.Add(new TelemetryStep { Fuel = 15 });
                    steps.Add(new FrameStep());
                    steps.Add(new SetTransportAcceptsStep(true));
                    steps.Add(new FrameStep());
                    break;

                case "param-budget-at-16":
                case "param-budget-at-17":
                case "suffix-blink-v2-only":
                case "itm-special-outranks-legacy-special":
                    // Document axes + condition stimulus already exercise these.
                    break;
            }
        }

        private static void AppendConditionStimulus(List<ReplayStep> steps, ReplayCell cell)
        {
            if (ReplayMatrix.IsEdge(cell.Condition))
            {
                // Baseline sample then edge.
                steps.Add(new TelemetryStep { Gear = "3" });
                steps.Add(new FrameStep());
                steps.Add(new AdvanceMsStep(16));
                string next = cell.Condition == ReplayCondition.Decreases ? "2" : "5";
                steps.Add(new TelemetryStep { Gear = next });
                steps.Add(new FrameStep());
                steps.Add(new AdvanceMsStep(100));
                steps.Add(new FrameStep());
                return;
            }

            if (ReplayMatrix.IsBoolLevel(cell.Condition))
            {
                int on = cell.Condition == ReplayCondition.IsFalse ? 0 : 1;
                int off = 1 - on;
                steps.Add(new TelemetryStep { IsInPitLane = off, PitLimiterOn = off });
                steps.Add(new FrameStep());
                steps.Add(new TelemetryStep { IsInPitLane = on, PitLimiterOn = on });
                steps.Add(new FrameStep());
                steps.Add(new AdvanceMsStep(100));
                steps.Add(new FrameStep());
                return;
            }

            // Level numeric: activate past threshold 10.
            double activate = cell.Condition switch
            {
                ReplayCondition.LessThan => 5,
                ReplayCondition.LessOrEqual => 10,
                ReplayCondition.GreaterThan => 15,
                ReplayCondition.GreaterOrEqual => 10,
                ReplayCondition.Equals => 10,
                ReplayCondition.NotEquals => 15,
                _ => 15,
            };
            steps.Add(new TelemetryStep { Fuel = activate });
            steps.Add(new FrameStep());
            steps.Add(new AdvanceMsStep(100));
            steps.Add(new FrameStep());
            steps.Add(new AdvanceMsStep(100));
            steps.Add(new FrameStep());
        }

        /// <summary>
        /// Hysteresis boundary: below → enter → within → exit → above + exact-boundary tick.
        /// Threshold 10, hysteresis 2 (adjudication block).
        /// </summary>
        private static void AppendHysteresisSequence(List<ReplayStep> steps, ReplayCell cell)
        {
            // Values depend on operator direction; use GreaterThan-style for GT/GE
            // and LessThan-style for LT/LE; EQ/NE get a dedicated path.
            double below, enter, within, exit, above, exact;

            switch (cell.Condition)
            {
                case ReplayCondition.GreaterThan:
                case ReplayCondition.GreaterOrEqual:
                    // Activate above 10; release band down to 8.
                    below = 5; enter = 15; within = 9; exit = 7; above = 20; exact = 10;
                    break;
                case ReplayCondition.LessThan:
                case ReplayCondition.LessOrEqual:
                    below = 15; enter = 5; within = 11; exit = 13; above = 20; exact = 10;
                    // naming: "below" means inactive side
                    break;
                case ReplayCondition.Equals:
                    below = 5; enter = 10; within = 10; exit = 12; above = 15; exact = 10;
                    break;
                default: // NotEquals
                    below = 10; enter = 15; within = 15; exit = 10; above = 10; exact = 10;
                    break;
            }

            void Fuel(double f)
            {
                steps.Add(new TelemetryStep { Fuel = f });
                steps.Add(new FrameStep());
                steps.Add(new AdvanceMsStep(50));
            }

            Fuel(below);
            Fuel(enter);
            Fuel(within);
            Fuel(exit);
            Fuel(above);
            Fuel(exact);
            steps.Add(new FrameStep());
        }

        public static GameData ToGameData(TelemetryState t)
        {
            var s = FormatterServices.GetUninitializedObject(StatusDataType);
            Set(s, "Gear", t.Gear);
            Set(s, "SpeedLocal", t.Speed);
            Set(s, "Rpms", t.Rpm);
            Set(s, "Position", t.Position);
            Set(s, "Fuel", t.Fuel);
            Set(s, "IsInPitLane", t.IsInPitLane);
            Set(s, "PitLimiterOn", t.PitLimiterOn);
            Set(s, "CurrentLap", 3);
            Set(s, "TotalLaps", 12);
            Set(s, "OpponentsCount", 16);

            var d = new GameData { NewData = (StatusDataBase)s };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { t.GameRunning });
            // GameName if present on GameData
            var gn = typeof(GameData).GetProperty("GameName");
            if (gn != null && gn.CanWrite)
                gn.GetSetMethod(true)!.Invoke(d, new object[] { t.GameName });
            return d;
        }

        private static void Set(object s, string p, object v)
        {
            var prop = s.GetType().GetProperty(p);
            if (prop == null)
                return;
            prop.GetSetMethod(true)!.Invoke(s, new[] { v });
        }

        /// <summary>Page-1 (Lap Info) subscription push for device id.</summary>
        public static byte[] LapInfoPush(byte deviceId)
            => HexToBytes(
                "ff0501"
                + deviceId.ToString("x2") + "00010034"
                + deviceId.ToString("x2") + "01040012"
                + deviceId.ToString("x2") + "82f90132"
                + deviceId.ToString("x2") + "83f50132"
                + deviceId.ToString("x2") + "04fd012a"
                + deviceId.ToString("x2") + "05fe012a");

        /// <summary>Minimal page announce for a wire page index.</summary>
        public static byte[] PagePush(byte deviceId, byte page)
            => HexToBytes(
                "ff0501"
                + deviceId.ToString("x2") + page.ToString("x2") + "010034"
                + deviceId.ToString("x2") + "01040012");

        public static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
