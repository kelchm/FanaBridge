using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.UI;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Exercises every state transition, recovery rung, deadline, and the push-accumulation
    /// window of <see cref="ItmLifecycleController"/>. The scenarios mirror the hardware
    /// behavior the design is built on (see docs/reference/protocol.md, "ITM Display"):
    /// pushes are the only acknowledgment, values are suspended around switches, exits gate
    /// the display off, resumes send the PageSet while off, and unexpected pushes are
    /// adopted in every state.
    /// </summary>
    public class ItmLifecycleControllerTests
    {
        // ── Test doubles ─────────────────────────────────────────────────

        private class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> Sent { get; } = new List<byte[]>();
            public bool SendReturns { get; set; } = true;
            public Func<byte[], bool>? Decide { get; set; }

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return Decide?.Invoke(copy) ?? SendReturns;
            }

            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private static ItmLifecycleController Make(out RecordingTransport t, out Clock clock,
            out List<string> logs, byte deviceId = 3)
        {
            t = new RecordingTransport();
            clock = new Clock();
            var l = new List<string>();
            logs = l;
            return new ItmLifecycleController(new ItmEncoder(t), deviceId, clock.Now, l.Add);
        }

        private static ItmLifecycleController Make(out RecordingTransport t, out Clock clock)
            => Make(out t, out clock, out _);

        // ── Wire frame classifiers (col03: [0]=0xFF, [1]=class, [2]=subcmd) ──
        private static bool IsGateOn(byte[] r) => r[1] == 0x05 && r[2] == 0x02 && r[3] == 0x01;
        private static bool IsGateOff(byte[] r) => r[1] == 0x05 && r[2] == 0x02 && r[3] == 0x00;
        private static bool IsEnable(byte[] r) => r[1] == 0x02 && r[2] == 0x02;
        private static bool IsPageSet(byte[] r) => r[1] == 0x05 && r[2] == 0x04;
        private static bool IsReset(byte[] r) => r[1] == 0x05 && r[2] == 0x05 && r[3] == 0x01;
        private static bool IsValueUpdate(byte[] r) => r[1] == 0x05 && r[2] == 0x01;
        private static Predicate<byte[]> IsPageSetTo(byte page)
            => r => r[1] == 0x05 && r[2] == 0x04 && r[4] == page;

        // ── Push builders (parsed entries, as the driver would hand them over) ──

        private static IReadOnlyList<ushort> ParamsOf(byte page, byte deviceId = 3)
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(deviceId))
                if (p.Number == page)
                    return p.Params;
            throw new InvalidOperationException("no such page " + page);
        }

        // The full subscription push for a page: params at handles firstHandle..N-1. The
        // handle base is arbitrary on real hardware (allocation is setup-specific), which is
        // exactly why matching is on the param set.
        private static List<ItmSubscription> PushFor(byte page, byte firstHandle = 0, byte deviceId = 3)
        {
            var ps = ParamsOf(page, deviceId);
            var list = new List<ItmSubscription>();
            for (int i = 0; i < ps.Count; i++)
                list.Add(new ItmSubscription((byte)(firstHandle + i), ps[i], 0x12));
            return list;
        }

        private static List<ItmSubscription> UnsubAll(int handles, byte firstHandle = 0)
        {
            var list = new List<ItmSubscription>();
            for (int i = 0; i < handles; i++)
                list.Add(new ItmSubscription((byte)(firstHandle + i), ItmParam.Unsubscribe));
            return list;
        }

        // ── Flow helpers ─────────────────────────────────────────────────

        private static void Tick(ItmLifecycleController c, Clock clock, long advance = 0, bool live = true)
        {
            clock.T += advance;
            c.Tick(live);
        }

        // Start + bring-up + confirming push: lands the controller in Synced on DefaultPage.
        private static void Sync(ItmLifecycleController c, RecordingTransport t, Clock clock, bool live = true)
        {
            c.Start();
            c.Tick(live);                              // bring-up commands drain, deadline armed
            c.OnPush(PushFor(c.DefaultPage));
            Tick(c, clock, c.AccumulateWindowMs, live);   // accumulation closes → confirmed
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            t.Sent.Clear();
        }

        // ── Bring-up ─────────────────────────────────────────────────────

        [Fact]
        public void Start_SendsGateEnablePageSet_InOrder_ThenAwaitsPush()
        {
            var c = Make(out var t, out _);
            c.Start();
            c.Tick(true);

            int gate = t.Sent.FindIndex(IsGateOn);
            int enable = t.Sent.FindIndex(IsEnable);
            int page = t.Sent.FindIndex(IsPageSet);
            Assert.True(gate >= 0 && enable >= 0 && page >= 0, "all three bring-up frames sent");
            Assert.True(gate < enable && enable < page, "order: gate → enable → PageSet");
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);
            Assert.False(c.ValuesAllowed);
        }

        [Fact]
        public void Start_TargetsConfiguredDefaultPage()
        {
            var c = Make(out var t, out _);
            c.DefaultPage = 5;
            c.Start();
            c.Tick(true);

            Assert.Contains(t.Sent, IsPageSetTo(5));
        }

        [Fact]
        public void BringUp_PushMatchingTarget_Syncs_AndAdoptsHandles()
        {
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(true);

            // Handles from an arbitrary base (6..11) — allocation is setup-specific, so
            // confirmation must match on the param set and adopt whatever handles came.
            c.OnPush(PushFor(1, firstHandle: 6));
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(1, c.CurrentPage);
            Assert.Equal(1, c.SyncGeneration);
            Assert.True(c.ValuesAllowed);
            var handles = c.Subscriptions.Select(kv => (int)kv.Key).ToList();
            Assert.Equal(new[] { 6, 7, 8, 9, 10, 11 }, handles);
            // The declared dataType travels with the adoption.
            Assert.All(c.Subscriptions, kv => Assert.Equal(0x12, kv.Value.DataType));
        }

        [Fact]
        public void BringUp_NoSeeding_NoValuesBeforePush()
        {
            // Values sent at guessed handles are ignored at best and — after a page change —
            // can land on re-bound parameters. There is no pre-push seed.
            var c = Make(out var t, out var clock);
            c.Start();
            Tick(c, clock, 100);

            Assert.Equal(0, c.SubscriptionCount);
            Assert.False(c.ValuesAllowed);
        }

        [Fact]
        public void BringUp_DeclinedWrites_RetriedUntilAccepted()
        {
            var c = Make(out var t, out var clock);
            t.SendReturns = false;
            c.Start();
            c.Tick(true);
            Assert.Equal(ItmLifecycleState.BringUp, c.State);   // stuck on the first step
            t.Sent.Clear();

            t.SendReturns = true;
            Tick(c, clock, 10);
            Assert.Contains(t.Sent, IsGateOn);
            Assert.Contains(t.Sent, IsEnable);
            Assert.Contains(t.Sent, IsPageSet);
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);
        }

        [Fact]
        public void BringUp_AcceptedSteps_NotResent_OnRetry()
        {
            var c = Make(out var t, out var clock);
            t.Decide = r => !IsPageSet(r);   // gate + enable accepted, PageSet declined
            c.Start();
            c.Tick(true);
            Assert.Equal(ItmLifecycleState.BringUp, c.State);
            t.Sent.Clear();

            t.Decide = null;
            Tick(c, clock, 10);
            Assert.DoesNotContain(t.Sent, IsGateOn);
            Assert.DoesNotContain(t.Sent, IsEnable);
            Assert.Contains(t.Sent, IsPageSet);
        }

        [Fact]
        public void BringUp_PushDeadlineMissed_EntersRecovery()
        {
            // A PageSet to the page the display is already on correctly pushes nothing, so a
            // restart onto the current page looks exactly like a lost command — the ladder
            // (ending in flip-away-and-back) disambiguates.
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(true);
            t.Sent.Clear();

            Tick(c, clock, c.PushDeadlineMs + 1);

            Assert.Equal(ItmLifecycleState.Recovery, c.State);
        }

        [Fact]
        public void Push_FragmentedAcrossReports_JudgedOnce()
        {
            // One tested setup delivers a push as one entry per report over ~15 ms; the
            // accumulation window must join them into a single judgment.
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(true);

            var full = PushFor(1);
            foreach (var entry in full)
            {
                c.OnPush(new List<ItmSubscription> { entry });
                Tick(c, clock, 2);
            }
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(1, c.SyncGeneration);   // exactly one adoption event
            Assert.Equal(6, c.SubscriptionCount);
        }

        [Fact]
        public void Push_PartialSet_DoesNotConfirm()
        {
            // Half a page's params is not a match — the deadline (not the fragment) decides.
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(true);

            var half = PushFor(1).Take(3).ToList();
            c.OnPush(half);
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);
        }

        [Fact]
        public void BringUp_PushForDifferentPage_Adopted()
        {
            // An unexpected push is adopted in every state — the firmware is the source of
            // truth and is never fought.
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(true);

            c.OnPush(PushFor(4));
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(4, c.CurrentPage);
        }

        [Fact]
        public void Push_ArrivingWhileCommandsStillDeclined_ConfirmsAtDrain()
        {
            // The push can beat the tail of the command sequence (a declined write being
            // retried). It is adopted immediately and confirms once the sequence completes.
            var c = Make(out var t, out var clock);
            t.Decide = r => !IsPageSet(r);       // PageSet keeps being declined
            c.Start();
            c.Tick(true);

            c.OnPush(PushFor(1));                 // push lands mid-procedure
            Tick(c, clock, c.AccumulateWindowMs); // accumulation judged; transition deferred
            Assert.Equal(ItmLifecycleState.BringUp, c.State);

            t.Decide = null;
            Tick(c, clock, 10);                   // PageSet finally accepted → confirm
            Assert.Equal(ItmLifecycleState.Synced, c.State);
        }

        // ── Switching (host page requests) ───────────────────────────────

        [Fact]
        public void RequestPage_SuspendsValues_QuietWindow_ThenPageSet()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.RequestPage(3);
            Assert.Equal(ItmLifecycleState.Switching, c.State);
            Assert.False(c.ValuesAllowed);        // suspended from the request instant

            Tick(c, clock, c.SwitchQuietMs - 10);
            Assert.DoesNotContain(t.Sent, IsPageSet);   // still inside the quiet window

            Tick(c, clock, 20);                    // quiet window over (and past PageSet spacing)
            Assert.Contains(t.Sent, IsPageSetTo(3));
        }

        [Fact]
        public void RequestPage_SamePage_Ignored()
        {
            // A PageSet to the current page pushes nothing (hardware-consistent), which would
            // guarantee a pointless recovery — never ask for one.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.RequestPage(c.CurrentPage);
            Tick(c, clock, 500);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void Switch_PushConfirms_ResumesValues()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            int gen = c.SyncGeneration;

            c.RequestPage(3);
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);
            c.OnPush(UnsubAll(6).Concat(PushFor(3)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(3, c.CurrentPage);
            Assert.Equal(gen + 1, c.SyncGeneration);
            Assert.True(c.ValuesAllowed);
        }

        [Fact]
        public void Switch_NoPush_EntersRecovery()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.RequestPage(3);
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);   // PageSet out
            Tick(c, clock, c.PushDeadlineMs + 1);

            Assert.Equal(ItmLifecycleState.Recovery, c.State);
            Assert.False(c.ValuesAllowed);
        }

        [Fact]
        public void PageSets_RespectSpacingFloor()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);                 // bring-up PageSet accepted at T=0
            c.SwitchQuietMs = 10;              // quiet window shorter than the spacing floor

            c.RequestPage(3);
            Tick(c, clock, 20);                // quiet over, but only ~70 ms since last PageSet
            Assert.DoesNotContain(t.Sent, IsPageSet);

            Tick(c, clock, c.PageSetSpacingMs);   // spacing satisfied now
            Assert.Contains(t.Sent, IsPageSetTo(3));
        }

        [Fact]
        public void WheelButtonPush_DuringSwitch_Adopted_HostRequestDropped()
        {
            // The user pressed the wheel button while our switch was in flight: adopt, never
            // fight — the queued host request is dropped, not replayed over the user's choice.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.RequestPage(3);
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);   // PageSet(3) out
            c.OnPush(UnsubAll(6).Concat(PushFor(5)).ToList());       // but page 5 arrives
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(5, c.CurrentPage);

            t.Sent.Clear();
            Tick(c, clock, 1000);
            Assert.DoesNotContain(t.Sent, IsPageSetTo(3));   // no replay of the host request
        }

        [Fact]
        public void Switching_PushDuringQuietWindow_DefersPageSet_AdoptsThePush()
        {
            // A push arriving during the switch quiet window means the display is already
            // changing (wheel button / late firmware). Don't PageSet over an accumulating push
            // — let it be judged first; it supersedes the host request. (Guards against
            // reintroducing the concurrent-traffic hazard the state machine is built to avoid.)
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);                                   // synced on page 1

            c.RequestPage(3);                                    // host switch → Switching, quiet window
            Tick(c, clock, 10);                                  // still within the quiet window
            c.OnPush(UnsubAll(8).Concat(PushFor(5)).ToList());   // a wheel-button push arrives
            Tick(c, clock, c.SwitchQuietMs - 5);                 // quiet elapsed, push still accumulating

            Assert.Equal(ItmLifecycleState.Switching, c.State);  // not judged yet
            Assert.DoesNotContain(t.Sent, IsPageSetTo(3));       // PageSet deferred, not sent over the push

            Tick(c, clock, c.AccumulateWindowMs);                // the push is judged → adopts page 5
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(5, c.CurrentPage);
            Assert.DoesNotContain(t.Sent, IsPageSetTo(3));       // host request dropped, never sent
        }

        [Fact]
        public void RequestPage_WhileSwitching_QueuedAndAppliedAfterConfirm()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.RequestPage(3);
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);
            c.RequestPage(4);                                        // second request mid-switch
            c.OnPush(UnsubAll(6).Concat(PushFor(3)).ToList());       // first switch confirms
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Switching, c.State);      // second switch begins
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);
            Assert.Contains(t.Sent, IsPageSetTo(4));
        }

        // ── Synced: wheel-button changes and subscription drops ──────────

        [Fact]
        public void WheelButton_UnsubThenSubs_AdoptedAsPageChange()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            int gen = c.SyncGeneration;

            // The button's change arrives as unsub burst + new subs (here: two reports
            // inside one accumulation window).
            c.OnPush(UnsubAll(6));
            Tick(c, clock, 10);
            c.OnPush(PushFor(2, firstHandle: 6));   // double-buffered region — new handles
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(2, c.CurrentPage);
            Assert.Equal(gen + 1, c.SyncGeneration);
            Assert.Empty(t.Sent);   // adopted — nothing sent back, never fought
        }

        [Fact]
        public void WheelButton_SubsArriveWithinGrace_NoRecovery()
        {
            // Unsub burst first, subs arriving in a separate (later) accumulation but within
            // the grace window: a page change, not a drop.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.OnPush(UnsubAll(6));
            Tick(c, clock, c.AccumulateWindowMs);     // unsub-only judgment → grace opens
            Assert.Equal(ItmLifecycleState.Synced, c.State);

            Tick(c, clock, 30);
            c.OnPush(PushFor(4));
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(4, c.CurrentPage);
        }

        [Fact]
        public void UnsubWithNothingFollowing_GraceExpiry_AdoptsLegacyPage()
        {
            // An unsubscribe-all with nothing following means the display moved to the legacy
            // ITM page (no telemetry parameters) — a valid destination the wheel button reaches.
            // Adopt it; never recover it back to a telemetry page (the "can't select legacy" bug).
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            t.Sent.Clear();

            c.OnPush(UnsubAll(6));
            Tick(c, clock, c.AccumulateWindowMs);     // grace opens
            Tick(c, clock, c.UnsubGraceMs + 1);       // nothing followed

            Assert.Equal(ItmLifecycleState.Synced, c.State);   // stays synced, not recovering
            Assert.Equal(LegacyPage(), c.CurrentPage);         // adopted the legacy page
            Assert.DoesNotContain(t.Sent, IsPageSet);          // never fought it back
        }

        [Fact]
        public void ValuesSuspended_WhileAPushIsAccumulating()
        {
            // An in-flight push means the page is changing under us — pause values until the
            // accumulation is judged.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.OnPush(UnsubAll(6));
            Assert.False(c.ValuesAllowed);

            c.OnPush(PushFor(2));
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.True(c.ValuesAllowed);
        }

        [Fact]
        public void HandleRebind_AdoptedFromPush()
        {
            // The same handle can be re-bound to a different parameter on any change — the
            // adopted map must follow the push, never a cached page→handle association.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);   // page 1: handle 2 = LAP (505)
            Assert.Equal(ItmParam.Lap, c.Subscriptions.First(kv => kv.Key == 2).Value.ParamId);

            c.OnPush(UnsubAll(6).Concat(PushFor(4)).ToList());   // page 4 rebinds handle 2
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(4, c.CurrentPage);
            Assert.Equal(ItmParam.LastLapTime, c.Subscriptions.First(kv => kv.Key == 2).Value.ParamId);
        }

        // ── Recovery ladder ──────────────────────────────────────────────

        // Drives a synced controller into Recovery by requesting a page and letting the
        // deadline lapse. On return the first ladder rung (re-PageSet 1/2) is active.
        private static void EnterRecovery(ItmLifecycleController c, RecordingTransport t, Clock clock, byte target = 3)
        {
            c.RequestPage(target);
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);   // PageSet out
            Tick(c, clock, c.PushDeadlineMs + 1);                    // deadline missed
            Assert.Equal(ItmLifecycleState.Recovery, c.State);
        }

        [Fact]
        public void Ladder_Rung1_RePageSets_Twice_WithSpacing()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock);
            t.Sent.Clear();

            Tick(c, clock, c.PageSetSpacingMs);                      // rung 1 attempt 1 sends
            Assert.Equal(1, t.Sent.Count(r => IsPageSetTo(3)(r)));

            Tick(c, clock, c.PushDeadlineMs + 1);                    // attempt 1 deadline
            Tick(c, clock, c.PageSetSpacingMs);                      // attempt 2 sends
            Assert.Equal(2, t.Sent.Count(r => IsPageSetTo(3)(r)));
            Assert.Equal(ItmLifecycleState.Recovery, c.State);
        }

        [Fact]
        public void Ladder_Rung1Confirmed_ResyncsAndResumes()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            int gen = c.SyncGeneration;
            EnterRecovery(c, t, clock);

            Tick(c, clock, c.PageSetSpacingMs);                      // rung 1 PageSet out
            c.OnPush(UnsubAll(6).Concat(PushFor(3)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(3, c.CurrentPage);
            Assert.Equal(gen + 1, c.SyncGeneration);
        }

        [Fact]
        public void Ladder_Rung2_FlipAwayAndBack()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock, target: 3);

            // Exhaust rung 1 (two re-PageSets, no push).
            Tick(c, clock, c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);
            Tick(c, clock, c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);
            t.Sent.Clear();

            // Rung 2: flip away to a different telemetry page (a genuine change MUST push).
            Tick(c, clock, c.PageSetSpacingMs);
            Assert.Contains(t.Sent, IsPageSetTo(1));    // flip page: first telemetry page ≠ 3

            c.OnPush(UnsubAll(6).Concat(PushFor(1)).ToList());   // flip page pushes
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Recovery, c.State);   // not synced — flipping back

            Tick(c, clock, c.PageSetSpacingMs);
            Assert.Contains(t.Sent, IsPageSetTo(3));    // flip back to the target

            c.OnPush(UnsubAll(6).Concat(PushFor(3)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(3, c.CurrentPage);
        }

        [Fact]
        public void Ladder_Rung3_GateCycle_WithPageSetWhileOff()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock, target: 3);

            // Exhaust rungs 1 (×2) and 2 (flip page silent).
            Tick(c, clock, c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);
            Tick(c, clock, c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);
            Tick(c, clock, c.PageSetSpacingMs);          // flip-away PageSet out
            Tick(c, clock, c.PushDeadlineMs + 1);        // flip silent → gate cycle
            t.Sent.Clear();

            Tick(c, clock, c.PageSetSpacingMs);
            // Gate cycle order: gate-off → PageSet(target) while off → gate-on.
            int off = t.Sent.FindIndex(IsGateOff);
            int page = t.Sent.FindIndex(r => IsPageSetTo(3)(r));
            int on = t.Sent.FindIndex(IsGateOn);
            Assert.True(off >= 0 && page >= 0 && on >= 0, "gate cycle frames all sent");
            Assert.True(off < page && page < on, "PageSet sent while gated off, never a bare gate-on");

            c.OnPush(PushFor(3));                        // the elicited push
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
        }

        [Fact]
        public void Ladder_Exhausted_Unavailable_WithBackoffRetries()
        {
            var c = Make(out var t, out var clock, out var logs);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock, target: 3);

            // Silence through the whole ladder.
            for (int i = 0; i < 4; i++)
            {
                Tick(c, clock, c.PageSetSpacingMs);
                Tick(c, clock, c.PushDeadlineMs + 1);
            }
            Assert.Equal(ItmLifecycleState.Unavailable, c.State);
            t.Sent.Clear();

            // Quiet during backoff.
            Tick(c, clock, 1000);
            Assert.Empty(t.Sent);

            // First backoff (5 s) expires → the strongest rung (gate cycle) retries.
            Tick(c, clock, 4500);
            Tick(c, clock, c.PageSetSpacingMs);
            Assert.Contains(t.Sent, IsGateOff);
            Assert.Contains(t.Sent, IsPageSetTo(3));
            Assert.Contains(t.Sent, IsGateOn);

            // Still silent → Unavailable again, with the next (30 s) backoff.
            Tick(c, clock, c.PushDeadlineMs + 1);
            Assert.Equal(ItmLifecycleState.Unavailable, c.State);
            t.Sent.Clear();
            Tick(c, clock, 10_000);                      // inside the 30 s backoff
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void Ladder_Exhausted_EmptyBackoffList_FallsBackDoesNotThrow()
        {
            // UnavailableBackoffMs is publicly settable; an empty (misconfigured) list must not
            // index out of range when the ladder exhausts — fall back to a default backoff.
            var c = Make(out var t, out var clock);
            c.UnavailableBackoffMs = new int[0];
            Sync(c, t, clock);
            EnterRecovery(c, t, clock, target: 3);

            for (int i = 0; i < 4; i++)
            {
                Tick(c, clock, c.PageSetSpacingMs);
                Tick(c, clock, c.PushDeadlineMs + 1);
            }

            Assert.Equal(ItmLifecycleState.Unavailable, c.State);   // reached it without throwing
        }

        [Fact]
        public void Unavailable_LatePush_AdoptedAndResyncs()
        {
            // Boot-cold firmware has answered a PageSet ~14 s late; a late push is just an
            // unexpected push, and every state adopts it.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock, target: 3);
            for (int i = 0; i < 4; i++)
            {
                Tick(c, clock, c.PageSetSpacingMs);
                Tick(c, clock, c.PushDeadlineMs + 1);
            }
            Assert.Equal(ItmLifecycleState.Unavailable, c.State);

            c.OnPush(PushFor(3));
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(3, c.CurrentPage);
        }

        [Fact]
        public void Recovery_ValuesSuspended_ThroughEveryRung()
        {
            // A streaming recovery has been observed to wedge the display where a quiet one
            // succeeded — the whole ladder runs with values suspended.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock);

            for (int i = 0; i < 4; i++)
            {
                Assert.False(c.ValuesAllowed);
                Tick(c, clock, c.PageSetSpacingMs);
                Assert.False(c.ValuesAllowed);
                Tick(c, clock, c.PushDeadlineMs + 1);
            }
            Assert.Equal(ItmLifecycleState.Unavailable, c.State);
            Assert.False(c.ValuesAllowed);
        }

        [Fact]
        public void Ladder_EscalationsAreLogged()
        {
            // The ladder doubles as field diagnostics — every rung logs what it tried.
            var c = Make(out var t, out var clock, out var logs);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock);
            for (int i = 0; i < 4; i++)
            {
                Tick(c, clock, c.PageSetSpacingMs);
                Tick(c, clock, c.PushDeadlineMs + 1);
            }

            Assert.Contains(logs, m => m.Contains("re-PageSet 1/2"));
            Assert.Contains(logs, m => m.Contains("re-PageSet 2/2"));
            Assert.Contains(logs, m => m.Contains("flip-away"));
            Assert.Contains(logs, m => m.Contains("gate cycle"));
            Assert.Contains(logs, m => m.Contains("unavailable"));
        }

        [Fact]
        public void Recovery_TargetPushDuringFlipAway_DoesNotConfirmMidFlip()
        {
            // A late push for the TARGET can land while the flip is in flight — but the flip
            // PageSet is already accepted, so the display is about to move again. Confirming
            // now would resume values mid-transition; instead the flip completes normally.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock, target: 3);
            Tick(c, clock, c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);
            Tick(c, clock, c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);
            Tick(c, clock, c.PageSetSpacingMs);          // flip-away out (expects page 1)

            // UnsubAll(8) clears every handle a page might occupy (page 3 uses 7), mirroring
            // how the firmware unsubscribes the whole outgoing page on each change.
            c.OnPush(UnsubAll(8).Concat(PushFor(3)).ToList());   // late TARGET push
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Recovery, c.State);   // not confirmed mid-flip
            Assert.False(c.ValuesAllowed);

            c.OnPush(UnsubAll(8).Concat(PushFor(1)).ToList());   // the flip page's push
            Tick(c, clock, c.AccumulateWindowMs);
            Tick(c, clock, c.PageSetSpacingMs);                   // flip-back PageSet out
            Assert.Contains(t.Sent, IsPageSetTo(3));

            c.OnPush(UnsubAll(8).Concat(PushFor(3)).ToList());   // target confirms
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(3, c.CurrentPage);
        }

        // ── Idle (game exit) — clear to placeholders, stay visible, never legacy ─────────

        [Fact]
        public void GameExit_ClearsToPlaceholders_StaysSynced_NoGateOff()
        {
            // A game exit clears the fields to --- (DisplayReset) and keeps the ITM page up —
            // no gate-off, so the display never drops to legacy at idle. Stays Synced.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            t.Sent.Clear();

            Tick(c, clock, 10, live: false);                 // game exits

            Assert.Contains(t.Sent, IsReset);                // fields cleared to ---
            Assert.DoesNotContain(t.Sent, IsGateOff);        // never gate off at idle
            Assert.Equal(ItmLifecycleState.Synced, c.State); // page stays visible

            // Idle afterwards: one reset, then quiet.
            t.Sent.Clear();
            Tick(c, clock, 60000, live: false);
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void GameExit_ResetDeclined_Retried()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            t.Sent.Clear();

            t.SendReturns = false;
            Tick(c, clock, 10, live: false);                 // reset declined
            t.Sent.Clear();

            t.SendReturns = true;
            Tick(c, clock, 10, live: false);                 // retried and accepted
            Assert.Contains(t.Sent, IsReset);
        }

        [Fact]
        public void GameReturn_OnDefaultPage_RepaintsInPlace_NoGateCycle()
        {
            // Already on the starting page: a game returning just repaints over the --- —
            // no reset, no gate cycle, no re-page.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);                               // synced on page 1 (the default)
            Tick(c, clock, 10, live: false);                 // exit → cleared to ---
            int gen = c.SyncGeneration;
            t.Sent.Clear();

            Tick(c, clock, 1000, live: true);                // game returns

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.DoesNotContain(t.Sent, IsGateOff);
            Assert.DoesNotContain(t.Sent, IsGateOn);         // no gate cycle
            Assert.DoesNotContain(t.Sent, IsPageSet);        // already on the starting page
            Assert.True(c.SyncGeneration > gen);             // repaint forced over the ---
        }

        [Fact]
        public void GameStart_OffStartingPage_SwitchesBackToIt()
        {
            // The starting (default) page is re-established each game launch: if the wheel
            // button left the display on a different page across the launch, a game starting
            // switches back to the starting page.
            var c = Make(out var t, out var clock);
            c.DefaultPage = 1;
            Sync(c, t, clock);                               // synced on page 1

            // Wheel button moved to page 5 during the last session.
            c.OnPush(UnsubAll(8).Concat(PushFor(5)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(5, c.CurrentPage);

            Tick(c, clock, 10, live: false);                 // game exits → cleared, on page 5
            t.Sent.Clear();

            Tick(c, clock, 1000, live: true);                // a game starts
            Assert.Equal(ItmLifecycleState.Switching, c.State);   // switching back to the default
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);
            Assert.Contains(t.Sent, IsPageSetTo(1));

            c.OnPush(UnsubAll(8).Concat(PushFor(1)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(1, c.CurrentPage);
        }

        [Fact]
        public void GameStart_OffStartingPage_RevertSuppressed_RepaintsInPlace()
        {
            // While the display-rules runtime owns page policy it suppresses the
            // controller's game-start revert (GameStartPageRevert = false): the engine
            // performs the revert itself through RequestPage, and a controller-initiated
            // switch is indistinguishable from wheel-button navigation to the layers
            // above — they would dismiss rules over a page change nobody made. The game
            // start must still repaint the current page in place (over the ---).
            var c = Make(out var t, out var clock);
            c.DefaultPage = 1;
            Sync(c, t, clock);                               // synced on page 1

            // Wheel button moved to page 5 during the last session.
            c.OnPush(UnsubAll(8).Concat(PushFor(5)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(5, c.CurrentPage);

            Tick(c, clock, 10, live: false);                 // game exits → cleared
            t.Sent.Clear();

            c.GameStartPageRevert = false;
            int gen = c.SyncGeneration;
            Tick(c, clock, 1000, live: true);                // a game starts

            Assert.Equal(ItmLifecycleState.Synced, c.State); // no switch initiated
            Assert.Equal(5, c.CurrentPage);                  // stays where the wheel left it
            Assert.DoesNotContain(t.Sent, IsPageSet);
            Assert.True(c.SyncGeneration > gen);             // repaint forced over the ---
        }

        [Fact]
        public void GameExit_DuringRecovery_LetsTheProcedureResolve()
        {
            // A mid-transition exit (not Synced) doesn't clear or gate off — the in-flight
            // recovery keeps running (it just isn't fed values), and resolves on its own.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            EnterRecovery(c, t, clock);
            t.Sent.Clear();

            Tick(c, clock, 10, live: false);

            Assert.Equal(ItmLifecycleState.Recovery, c.State);   // still recovering
            Assert.DoesNotContain(t.Sent, IsGateOff);
            Assert.DoesNotContain(t.Sent, IsReset);
        }

        [Fact]
        public void BringUp_WithoutLiveTelemetry_ClearsToPlaceholders_StaysSynced()
        {
            // Bring-up at connect with no game comes up clean (DisplayReset clears any stale
            // cache) and stays on the page showing --- — no auto-off, no gate-off.
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(false);
            Assert.Contains(t.Sent, IsReset);                // cold entry clears the stale cache
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);

            c.OnPush(PushFor(1));
            Tick(c, clock, c.AccumulateWindowMs, live: false);
            Assert.Equal(ItmLifecycleState.Synced, c.State); // up, showing placeholders

            t.Sent.Clear();
            Tick(c, clock, 60000, live: false);              // no game → stays lit, no gate-off
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.DoesNotContain(t.Sent, IsGateOff);
        }

        // ── Legacy ITM page (no telemetry parameters) ────────────────────

        // The legacy page's wire number on the standard device (device 3).
        private static byte LegacyPage()
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(3))
                if (p.IsLegacy) return p.Number;
            throw new InvalidOperationException("no legacy page");
        }

        [Fact]
        public void DefaultPageLegacy_BringUp_ConfirmsOnEmptyPush()
        {
            // Default page = Legacy: the legacy page carries no parameters, so its PageSet
            // pushes only an unsubscribe-all. That IS its confirmation — not a dropped-subs
            // failure that recovers away. (The "default page Legacy goes sideways" bug.)
            byte legacy = LegacyPage();
            var c = Make(out var t, out var clock);
            c.DefaultPage = legacy;
            c.Start();
            c.Tick(true);
            Assert.Contains(t.Sent, IsPageSetTo(legacy));

            c.OnPush(UnsubAll(8));                            // the legacy page's empty push
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(legacy, c.CurrentPage);
        }

        [Fact]
        public void DefaultPageLegacy_BringUp_ConfirmsOnMissedPush()
        {
            // If the display is already on the legacy page, the PageSet pushes nothing at all.
            // A missed push on a legacy target means we're on legacy — confirm, don't recover.
            byte legacy = LegacyPage();
            var c = Make(out var t, out var clock);
            c.DefaultPage = legacy;
            c.Start();
            c.Tick(true);
            t.Sent.Clear();

            Tick(c, clock, c.PushDeadlineMs + 1);            // no push arrives

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(legacy, c.CurrentPage);
            Assert.DoesNotContain(t.Sent, IsPageSet);        // did NOT recover to a telemetry page
        }

        [Fact]
        public void WheelButtonToLegacy_Adopted_NotRecoveredAway()
        {
            // Synced on a telemetry page, the user presses the wheel button to the legacy page:
            // the firmware sends an unsubscribe-all with nothing following. Adopt "on legacy" —
            // never re-PageSet back to a telemetry page. (The "can't select legacy" bug.)
            byte legacy = LegacyPage();
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);                               // synced on page 1
            t.Sent.Clear();

            c.OnPush(UnsubAll(8));                            // wheel button → legacy (empty push)
            Tick(c, clock, c.AccumulateWindowMs);            // unsub-only → grace opens
            Assert.Equal(ItmLifecycleState.Synced, c.State);

            Tick(c, clock, c.UnsubGraceMs + 1);              // nothing follows → adopt legacy

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(legacy, c.CurrentPage);
            Assert.DoesNotContain(t.Sent, IsPageSet);        // did NOT fight it back to telemetry
        }

        [Fact]
        public void ColdEntry_InvalidDefaultPage_FallsBackToARealPage()
        {
            // A 0 (unset) or out-of-range starting page must never become PageSet(0) or a
            // target no push can confirm — bring-up falls back to the device's first page.
            var c = Make(out var t, out var clock);
            c.DefaultPage = 0;
            c.Start();
            c.Tick(true);

            var pageSet = t.Sent.FirstOrDefault(IsPageSet);
            Assert.NotNull(pageSet);
            Assert.NotEqual(0, pageSet[4]);   // byte 4 = page number, never 0
        }

        [Fact]
        public void ArmedForLegacy_NonEmptyPush_AdoptsThatPage_NotLegacy()
        {
            // Armed for the legacy page (starting page = legacy), a NON-empty telemetry push
            // must not be mistaken for the legacy page's empty confirmation — it's a real
            // telemetry page (a straggler re-push, or the page gate-on landed on). Adopt it as
            // itself, never confirm "on legacy" while holding telemetry subscriptions.
            byte legacy = LegacyPage();
            var c = Make(out var t, out var clock);
            c.DefaultPage = legacy;
            c.Start();
            c.Tick(true);                                // bring-up armed for legacy
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);

            c.OnPush(PushFor(2));                        // a telemetry page pushes instead
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(2, c.CurrentPage);              // adopted page 2, not mislabeled legacy
            Assert.Equal(ParamsOf(2).Count, c.SubscriptionCount);   // page 2's real params
        }

        [Fact]
        public void FromLegacy_WheelButtonToTelemetryPage_Adopted()
        {
            // From the legacy page, the wheel button to a telemetry page pushes its full set —
            // adopt it normally.
            byte legacy = LegacyPage();
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            c.OnPush(UnsubAll(8));
            Tick(c, clock, c.AccumulateWindowMs);
            Tick(c, clock, c.UnsubGraceMs + 1);              // now on legacy
            Assert.Equal(legacy, c.CurrentPage);

            c.OnPush(PushFor(2));                            // wheel button → a telemetry page
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(2, c.CurrentPage);
        }

        // ── User enable/disable ──────────────────────────────────────────

        [Fact]
        public void UserDisable_GatesOffOnce_ThenDormant()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.SetUserEnabled(false);
            Tick(c, clock, 10);
            Assert.Equal(ItmLifecycleState.Disabled, c.State);
            Assert.Single(t.Sent, IsGateOff);

            Tick(c, clock, 5000);
            Assert.Single(t.Sent, IsGateOff);   // no repeats
        }

        [Fact]
        public void UserDisable_GateOffDeclined_Retried()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            t.SendReturns = false;
            c.SetUserEnabled(false);
            Tick(c, clock, 10);
            t.Sent.Clear();

            t.SendReturns = true;
            Tick(c, clock, 10);
            Assert.Contains(t.Sent, IsGateOff);
        }

        [Fact]
        public void UserDisable_FromIdle_StillEnforcesOff()
        {
            var c = Make(out var t, out var clock);
            c.SetUserEnabled(false);
            c.Tick(true);

            Assert.Contains(t.Sent, IsGateOff);
            Assert.DoesNotContain(t.Sent, IsGateOn);
            Assert.DoesNotContain(t.Sent, IsEnable);
        }

        [Fact]
        public void ReEnable_RunsColdBringUp()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            c.SetUserEnabled(false);
            Tick(c, clock, 10);
            t.Sent.Clear();

            c.SetUserEnabled(true);
            c.Start();
            Tick(c, clock, c.PageSetSpacingMs);

            Assert.Contains(t.Sent, IsGateOn);
            Assert.Contains(t.Sent, IsEnable);
            Assert.Contains(t.Sent, IsPageSet);
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);
        }

        // ── Wheel change / stop ──────────────────────────────────────────

        [Fact]
        public void WheelChanged_TreatedAsColdStart()
        {
            // A hot-swap is invisible on the ITM channel and resets the display cold; the
            // identity layer's event drops everything and re-runs the full bring-up.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            Assert.Equal(6, c.SubscriptionCount);

            c.OnWheelChanged();
            Assert.Equal(0, c.SubscriptionCount);   // stale handles must not survive
            Assert.False(c.ValuesAllowed);
            Tick(c, clock, c.PageSetSpacingMs);

            Assert.Contains(t.Sent, IsGateOn);
            Assert.Contains(t.Sent, IsEnable);
            Assert.Contains(t.Sent, IsPageSet);
        }

        [Fact]
        public void WheelChanged_WhileIdle_DoesNothing()
        {
            var c = Make(out var t, out var clock);
            c.OnWheelChanged();
            c.Tick(true);
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void WheelChanged_WhileDisabled_ReassertsGateOff()
        {
            // The new wheel comes up with whatever gate setting persisted — the user's "off"
            // is re-asserted on it.
            var c = Make(out var t, out var clock);
            c.SetUserEnabled(false);
            c.Tick(true);
            t.Sent.Clear();

            c.OnWheelChanged();
            Tick(c, clock, 10);

            Assert.Contains(t.Sent, IsGateOff);
            Assert.Equal(ItmLifecycleState.Disabled, c.State);
        }

        [Fact]
        public void Stop_DropsEverything_StartRunsColdAgain()
        {
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.Stop();
            Assert.Equal(ItmLifecycleState.Idle, c.State);
            Assert.Equal(0, c.SubscriptionCount);
            c.Tick(true);
            Assert.Empty(t.Sent);                    // nothing on the wire from Stop

            c.Start();
            Tick(c, clock, c.PageSetSpacingMs);
            Assert.Contains(t.Sent, IsGateOn);       // full cold bring-up
        }

        // ── Deadline/accumulation interplay ──────────────────────────────

        [Fact]
        public void PushArrivingJustBeforeDeadline_NotMissed()
        {
            var c = Make(out var t, out var clock);
            c.Start();
            c.Tick(true);

            Tick(c, clock, c.PushDeadlineMs - 5);     // 5 ms before the deadline
            c.OnPush(PushFor(1));                      // accumulation opens — deadline defers
            Tick(c, clock, 10);                        // past the nominal deadline
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);   // not missed

            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
        }

        [Fact]
        public void Describe_SurfacesStateForDeviceStatus()
        {
            var c = Make(out var t, out var clock);
            Assert.Equal("Idle", c.Describe());
            Sync(c, t, clock);
            Assert.Contains("page 1", c.Describe());
            Assert.Contains("6 params", c.Describe());
        }

        [Fact]
        public void Describe_RoundTripsThroughTheDisplayTabCaption()
        {
            // The Display tab's current-page card parses Describe()'s literal wording
            // (FanaBridge.UI.DisplayOverviewRender.CurrentPageCaption). This drives the
            // REAL producer through that parser, state by state, so a rewording here
            // can't silently degrade the card to echoing a raw log line — if this test
            // fails after a Describe() change, update the parser with the new wording.
            var c = Make(out var t, out var clock);
            Assert.Equal("ITM idle", DisplayOverviewRender.CurrentPageCaption(c.Describe(), 3));

            c.SetUserEnabled(false);                    // → Disabled
            Assert.Equal(ItmLifecycleState.Disabled, c.State);
            Assert.Equal("ITM off", DisplayOverviewRender.CurrentPageCaption(c.Describe(), 3));

            c.SetUserEnabled(true);                     // re-arms the cold bring-up
            t.SendReturns = false;                      // stall it so BringUp is observable
            c.Tick(true);
            Assert.Equal(ItmLifecycleState.BringUp, c.State);
            Assert.Equal("Bringing up…", DisplayOverviewRender.CurrentPageCaption(c.Describe(), 3));

            t.SendReturns = true;
            c.Tick(true);                               // bring-up drains → AwaitPush
            c.OnPush(PushFor(c.DefaultPage));
            Tick(c, clock, c.AccumulateWindowMs);       // push confirmed → Synced
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal("Page 1 · Lap Info", DisplayOverviewRender.CurrentPageCaption(c.Describe(), 3));

            c.RequestPage(3);                           // a switch whose push never comes
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);
            Tick(c, clock, c.PushDeadlineMs + 1);       // deadline missed → Recovery
            Assert.Equal(ItmLifecycleState.Recovery, c.State);
            Assert.Equal("Recovering…", DisplayOverviewRender.CurrentPageCaption(c.Describe(), 3));
        }

        // ── Regressions (review findings) ────────────────────────────────

        [Fact]
        public void Start_HonorsDefaultPageSetBeforeFirstTick()
        {
            // Start() defers the cold entry to the next Tick, so a DefaultPage assigned after
            // Start() but before Tick() (the device instance's frame order) is still honored —
            // otherwise bring-up targets the ctor default and the configured page is ignored.
            var c = Make(out var t, out _);
            c.Start();
            c.DefaultPage = 5;      // configured page arrives after Start(), before the tick
            c.Tick(true);

            Assert.Contains(t.Sent, IsPageSetTo(5));
            Assert.DoesNotContain(t.Sent, IsPageSetTo(1));
        }

        [Fact]
        public void UserOff_ReassertedAfterReconnect()
        {
            // The user's "off" must be re-enforced after a reconnect: the re-appearing device
            // (power cycle, re-seat) comes up with whatever gate setting persisted, not
            // necessarily off. Edge-only application would send nothing on reconnect.
            var c = Make(out var t, out var clock);
            c.SetUserEnabled(false);
            c.Tick(true);
            Assert.Contains(t.Sent, IsGateOff);

            c.Stop();               // connection lost
            t.Sent.Clear();

            c.SetUserEnabled(false);   // same setting, fresh connection
            c.Tick(true);
            Assert.Contains(t.Sent, IsGateOff);   // re-asserted, not skipped as unchanged
        }

        [Fact]
        public void WheelChange_DropsOpenAccumulation_NoSelfConfirm()
        {
            // A push that arrived just before the wheel changed (still accumulating) must not
            // survive the cold restart and confirm the NEW wheel's bring-up against the OLD
            // wheel's page.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.OnPush(PushFor(1));   // old wheel re-pushes page 1; accumulation opens
            c.OnWheelChanged();     // ...and the wheel is swapped mid-window
            Assert.Equal(0, c.SubscriptionCount);   // old handles dropped

            Tick(c, clock, c.AccumulateWindowMs + 10);   // the stale window must not be judged
            Assert.Equal(ItmLifecycleState.AwaitPush, c.State);   // still awaiting the NEW push
            Assert.False(c.ValuesAllowed);
        }

        [Fact]
        public void StalePreSwapReports_DrainedAfterColdEntry_DoNotSelfConfirm()
        {
            // The device instance drains buffered pushes AFTER OnWheelChanged. A stale report
            // from the old wheel (same param set as the bring-up target) fed post-restart must
            // not confirm the new wheel's bring-up without a genuine fresh push.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);

            c.OnWheelChanged();                 // cold restart; bring-up queued
            Tick(c, clock, c.PageSetSpacingMs); // bring-up drains, expectation armed for page 1
            c.OnPush(PushFor(1));               // a STALE page-1 report from the old wheel
            Tick(c, clock, c.AccumulateWindowMs);

            // It matches the armed page, so by protocol it is indistinguishable from a genuine
            // page-1 push — confirmation is correct here. The guarantee under test is the
            // buffer being CLEARED on wheel change (FanatecWheelbase), so this stale report
            // never reaches the controller in production; see the wheelbase test.
            Assert.Equal(ItmLifecycleState.Synced, c.State);
        }

        [Fact]
        public void Switching_StragglerRepushOfCurrentPage_DoesNotCancelSwitch()
        {
            // A straggler re-announcement of the page we're LEAVING (not a user action) must
            // not be mistaken for a wheel-button change and silently cancel the host switch.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);   // synced on page 1

            c.RequestPage(3);
            Tick(c, clock, c.SwitchQuietMs + c.PageSetSpacingMs);   // PageSet(3) out
            c.OnPush(PushFor(1));   // straggler re-push of the CURRENT page (1)
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.Equal(ItmLifecycleState.Switching, c.State);   // switch NOT cancelled
            c.OnPush(UnsubAll(8).Concat(PushFor(3)).ToList());     // the real page-3 push
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(3, c.CurrentPage);
        }

        [Fact]
        public void Synced_FragmentMatchingNoPage_SuspendsValues_NoForcedRepaint()
        {
            // A wheel-button change fragmented slower than the accumulation window leaves a
            // set matching no page. It must suspend values (grace) and wait for the rest, not
            // force-repaint onto a half-rebound handle map.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            int gen = c.SyncGeneration;

            // First fragment: unsubscribe page 1's h2 (LAP) and leave a partial set.
            c.OnPush(new List<ItmSubscription> { new ItmSubscription(2, ItmParam.Unsubscribe) });
            Tick(c, clock, c.AccumulateWindowMs);

            Assert.False(c.ValuesAllowed);              // suspended, not streaming
            Assert.Equal(gen, c.SyncGeneration);        // no forced repaint

            // The rest of the change lands within grace → adopted as the new page.
            c.OnPush(UnsubAll(8).Concat(PushFor(4)).ToList());
            Tick(c, clock, c.AccumulateWindowMs);
            Assert.Equal(ItmLifecycleState.Synced, c.State);
            Assert.Equal(4, c.CurrentPage);
            Assert.True(c.ValuesAllowed);
        }

        [Fact]
        public void GraceWindow_SuspendsValues()
        {
            // Grace opens on an unsubscribe-only push in Synced (the front half of a page
            // change). Values must be suspended for its duration — the firmware may be
            // re-binding the surviving handles.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            Assert.True(c.ValuesAllowed);

            c.OnPush(UnsubAll(6));
            Tick(c, clock, c.AccumulateWindowMs);   // unsub-only judged → grace opens
            Assert.False(c.ValuesAllowed);          // suspended through grace
        }

        [Fact]
        public void WheelChange_WhileIdle_ColdRestarts()
        {
            // A hot-swap while idle (game exited, display showing --- on the ITM page) is a
            // cold event — drop the old wheel's handles and re-run the full bring-up.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            Tick(c, clock, 10, live: false);        // game exit → cleared to ---, still Synced
            t.Sent.Clear();

            c.OnWheelChanged();
            Assert.Equal(0, c.SubscriptionCount);   // old wheel's handles dropped
            Tick(c, clock, c.PageSetSpacingMs, live: false);

            Assert.Contains(t.Sent, IsGateOn);      // full cold bring-up
            Assert.Contains(t.Sent, IsPageSet);
            Assert.DoesNotContain(t.Sent, IsValueUpdate);   // nothing until the fresh push
        }

        [Fact]
        public void Subscriptions_SnapshotStableUntilNextPush()
        {
            // The handle map is exposed as a cached snapshot (allocation-free per-frame reads);
            // it must reflect the latest push and refresh when the map changes.
            var c = Make(out var t, out var clock);
            Sync(c, t, clock);
            var first = c.Subscriptions;
            Assert.Same(first, c.Subscriptions);       // same instance until the map changes
            Assert.Equal(6, first.Count);

            Push(c, clock, UnsubAll(8).Concat(PushFor(5)).ToList());
            Assert.NotSame(first, c.Subscriptions);    // rebuilt after the push
            Assert.Equal(6, c.Subscriptions.Count);    // page 5 (tyres) — 6 params
        }

        // Delivers a push mid-session and runs the judgment tick.
        private static void Push(ItmLifecycleController c, Clock clock, List<ItmSubscription> entries)
        {
            c.OnPush(entries);
            Tick(c, clock, c.AccumulateWindowMs);
        }
    }
}
