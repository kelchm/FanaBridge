using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E7: director extensions — reject-uncommanded revert (clear-on-land,
    /// bounded re-issue, out-of-table same machine, adopted-target update), adopt
    /// reporting, unknown-page-at-connect, optimistic twin, and flag-off parity.
    /// </summary>
    public class DisplayPageDirectorE7Tests
    {
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

        private sealed class Clock
        {
            public long T;
        }

        private sealed class Harness
        {
            public readonly FakePageControl Control = new FakePageControl();
            public readonly List<string> Log = new List<string>();
            public readonly Clock Clock = new Clock();
            public DisplayPageDirector Director = null!;

            public static Harness Create(bool reject = false)
            {
                var h = new Harness();
                h.Director = new DisplayPageDirector(
                    h.Control, itmDeviceId: 2, () => h.Clock.T, h.Log.Add);
                h.Director.RejectUncommandedChanges = reject;
                return h;
            }

            public DirectorTickResult Tick(RuleIntent intent) => Director.Tick(intent);

            public void SyncBaseline(byte wirePage = 1, ItmPage intentPage = ItmPage.LapInfo)
            {
                Control.Land(wirePage);
                var r = Tick(Page(intentPage));
                Assert.Null(r.Manual);
                Assert.False(r.AdoptedThisTick);
                Assert.False(r.RevertedThisTick);
            }
        }

        private static RuleIntent Page(ItmPage page, string ruleId = null)
            => new RuleIntent(TargetKind.Page, page, null, ruleId);

        // ── A. Reject-uncommanded ────────────────────────────────────────

        [Fact]
        public void Reject_UncommandedLanding_RevertsOnce_ToLastCommanded()
        {
            var h = Harness.Create(reject: true);
            h.SyncBaseline(); // on wire 1
            // Command tyre temps (wire 5).
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal((byte?)5, r.RequestedWirePage);
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            // Wheel button → Lap Times (wire 4): one push-back to 5.
            h.Control.Land(4);
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.False(r.AdoptedThisTick);
            Assert.Null(r.Manual);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);

            // Same uncommanded observation does not re-issue while we wait (no new edge).
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.False(r.RevertedThisTick);
            Assert.Single(h.Control.Requests);
        }

        [Fact]
        public void RevertLands_ThenNewPressSamePage_RevertsAgain()
        {
            // Clear-on-land: a post-success recurrence is a FRESH fight, not adopt.
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            // First uncommanded → revert.
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);

            // Push-back lands — fight resolved (clear outstanding).
            h.Control.Land(5);
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.False(r.RevertedThisTick);
            Assert.False(r.AdoptedThisTick);
            h.Control.Requests.Clear();

            // New press to same page within old debounce window → FRESH revert, not adopt.
            h.Clock.T = DisplayPageDirector.RejectDebounceMs / 2;
            h.Control.Land(4);
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.False(r.AdoptedThisTick);
            Assert.False(r.AdoptWarnedThisTick);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void RevertUnlanded_ReassertWithinWindow_AdoptsWithWarn()
        {
            // Unresolved fight: re-assert of same page inside inclusive window → adopt+warn.
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            // First uncommanded → revert (do NOT land the push-back).
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);

            // Still on 4: generation edge re-confirms fromWire within window → adopt.
            h.Clock.T = DisplayPageDirector.RejectDebounceMs / 2;
            h.Control.SyncGeneration++; // re-confirm edge without page change
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.AdoptedThisTick);
            Assert.True(r.AdoptWarnedThisTick);
            Assert.True(r.Manual.HasValue);
            Assert.Equal(ItmPage.LapTimes, r.Manual.Value.Page);
            Assert.Contains(h.Log, l => l.Contains("adopting with warning"));
        }

        [Fact]
        public void RevertIgnored_RetriesThenAdoptsWithWarn()
        {
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            // Issue 1: uncommanded 4.
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.Equal(1, h.Control.Requests.Count);

            // Past the inclusive debounce window; firmware still on 4.
            for (int attempt = 2; attempt <= DisplayPageDirector.MaxRevertAttempts; attempt++)
            {
                h.Clock.T = DisplayPageDirector.RejectDebounceMs + 1
                    + (attempt - 1) * (DisplayPageDirector.RejectDebounceMs + 1);
                h.Control.SyncGeneration++;
                r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
                Assert.True(r.RevertedThisTick, "expected re-issue #" + attempt);
                Assert.False(r.AdoptedThisTick);
            }
            Assert.Equal(DisplayPageDirector.MaxRevertAttempts, h.Control.Requests.Count);

            // Next edge past window after cap → adopt with "not accepting".
            h.Clock.T += DisplayPageDirector.RejectDebounceMs + 1;
            h.Control.SyncGeneration++;
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.AdoptedThisTick);
            Assert.True(r.AdoptWarnedThisTick);
            Assert.False(r.RevertedThisTick);
            Assert.Contains(h.Log, l => l.Contains("the wheel is not accepting page changes"));
            Assert.Equal(DisplayPageDirector.MaxRevertAttempts, h.Control.Requests.Count);
        }

        [Fact]
        public void RejectDebounce_ExactBoundary_IsInclusive()
        {
            // At exactly RejectDebounceMs after issue, reassert is still in-window → adopt.
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Control.Land(4);
            h.Tick(Page(ItmPage.TyreTemps, "r1")); // issue at T=0

            h.Clock.T = DisplayPageDirector.RejectDebounceMs; // exact boundary
            h.Control.SyncGeneration++;
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.AdoptedThisTick);
            Assert.True(r.AdoptWarnedThisTick);
        }

        [Fact]
        public void Reject_ReassertAfterDebounce_AfterLand_RevertsAgain()
        {
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Control.Land(4);
            h.Tick(Page(ItmPage.TyreTemps, "r1")); // revert #1
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Clock.T = DisplayPageDirector.RejectDebounceMs + 1;
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.False(r.AdoptedThisTick);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void CatalogedAdopt_UpdatesFutureRevertTarget()
        {
            // Command 5, reject 4, adopt 4 after in-window reassert → next fight reverts to 4.
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            // Uncommanded 4, unlanded reassert → adopt 4 (target updates to 4).
            h.Control.Land(4);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Clock.T = 100;
            h.Control.SyncGeneration++;
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.AdoptedThisTick);
            h.Control.Requests.Clear();

            // Later uncommanded change to wire 3 (Car Settings) → push-back to adopted 4, not 5.
            h.Clock.T = DisplayPageDirector.RejectDebounceMs + 500;
            h.Control.Land(3);
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.Equal(new byte[] { 4 }, h.Control.Requests);
        }

        // ── Out-of-table wire pages (same state machine, identity null) ─

        [Fact]
        public void OutOfTable_FirstUncommanded_Reverts()
        {
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Control.Land(99); // outside catalog
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.False(r.AdoptedThisTick);
            Assert.Null(r.Manual);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void OutOfTable_ReassertWithinWindow_AdoptsWithWarn()
        {
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Control.Land(99);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Clock.T = 100;
            h.Control.SyncGeneration++;
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.AdoptedThisTick);
            Assert.True(r.AdoptWarnedThisTick);
            Assert.True(r.Manual.HasValue);
            Assert.Null(r.Manual.Value.Page); // identity null
        }

        [Fact]
        public void OutOfTable_AfterWindow_RevertsAgainAfterLand()
        {
            var h = Harness.Create(reject: true);
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Control.Land(99);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5); // push-back lands
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Requests.Clear();

            h.Clock.T = DisplayPageDirector.RejectDebounceMs + 1;
            h.Control.Land(99);
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void FlagOff_UncommandedLanding_AdoptsExactlyAsNow()
        {
            var h = Harness.Create(reject: false);
            h.SyncBaseline();
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.True(r.Manual.HasValue);
            Assert.Equal(ItmPage.LapTimes, r.Manual.Value.Page);
            Assert.True(r.AdoptedThisTick);
            Assert.False(r.RevertedThisTick);
            Assert.Empty(h.Control.Requests);
        }

        [Fact]
        public void AdoptReporting_SurfacesOnTickResult()
        {
            var h = Harness.Create(reject: false);
            h.SyncBaseline();
            Assert.False(h.Tick(Page(ItmPage.LapInfo)).AdoptedThisTick);

            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.True(r.AdoptedThisTick);
            Assert.True(r.Manual.HasValue);
        }

        // ── Page knowledge / optimistic twin ─────────────────────────────

        [Fact]
        public void ConnectBeforeFirstAnnouncement_PageKnowledgeIsUnknown()
        {
            var h = Harness.Create(reject: false);
            h.Control.State = ItmLifecycleState.BringUp;
            h.Control.CurrentWirePage = null;
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.False(r.PageKnowledge.IsKnown);
            Assert.Equal(CurrentPageKnowledge.Unknown.IsKnown, r.PageKnowledge.IsKnown);
        }

        [Fact]
        public void AfterBaseline_PageKnowledgeIsKnown()
        {
            var h = Harness.Create();
            h.SyncBaseline(wirePage: 1);
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.True(r.PageKnowledge.IsKnown);
            Assert.Equal((byte?)1, r.PageKnowledge.WirePage);
            Assert.Equal(ItmPage.LapInfo, r.PageKnowledge.Page);
        }

        [Fact]
        public void OptimisticTwin_MidSwitch_ReportsCommandedPage()
        {
            var h = Harness.Create();
            h.SyncBaseline(wirePage: 1);
            // Command tyre temps (wire 5) — request issued, lifecycle not yet Synced on 5.
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal((byte?)5, r.RequestedWirePage);

            // Simulate mid-switch: not Synced, but commanded is 5.
            h.Control.State = ItmLifecycleState.Switching;
            h.Control.CurrentWirePage = 1; // still showing old while switching
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.PageKnowledge.IsKnown);
            Assert.Equal((byte?)5, r.PageKnowledge.WirePage);
            Assert.Equal(ItmPage.TyreTemps, r.PageKnowledge.Page);
        }

        [Fact]
        public void ConnectBeforeFirstAnnouncement_Reject_DoesNotRevert_NothingCommanded()
        {
            var h = Harness.Create(reject: true);
            h.Control.State = ItmLifecycleState.BringUp;
            h.Control.CurrentWirePage = null;
            var r = h.Tick(Page(ItmPage.LapInfo));
            h.Control.Requests.Clear();

            var h2 = Harness.Create(reject: true);
            h2.Control.SyncGeneration = 9;
            h2.Control.Land(4);
            r = h2.Tick(Page(ItmPage.LapInfo));
            Assert.Null(r.Manual);
            Assert.False(r.RevertedThisTick);
            Assert.False(r.AdoptedThisTick);
            Assert.True(r.PageKnowledge.IsKnown);

            var h3 = Harness.Create(reject: true);
            h3.Control.SyncGeneration = 1;
            h3.Control.Land(4);
            var rest = new RuleIntent(TargetKind.Page, null, null, null);
            r = h3.Tick(rest);
            Assert.Null(r.RequestedWirePage);
            Assert.False(r.RevertedThisTick);

            h3.Control.Land(5);
            r = h3.Tick(rest);
            Assert.True(r.AdoptedThisTick);
            Assert.False(r.RevertedThisTick);
            Assert.True(r.Manual.HasValue);
            Assert.Empty(h3.Control.Requests);
        }

        [Fact]
        public void ConnectBeforeFirstAnnouncement_Adopt_DoesNotInventPage()
        {
            var h = Harness.Create(reject: false);
            h.Control.State = ItmLifecycleState.BringUp;
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.False(r.PageKnowledge.IsKnown);
            Assert.Null(r.Manual);
        }

        [Fact]
        public void RejectDebounceMs_IsPinned()
        {
            Assert.Equal(2000, DisplayPageDirector.RejectDebounceMs);
            Assert.Equal(3, DisplayPageDirector.MaxRevertAttempts);
        }

        // ── Director + lifecycle trace (unexpected push / pending) ───────

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> Sent { get; } = new List<byte[]>();
            public bool SendReturns { get; set; } = true;

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return SendReturns;
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

        private static List<ItmSubscription> PushFor(byte page, byte deviceId = 3)
        {
            IReadOnlyList<ushort> ps = null;
            foreach (var p in ItmDeviceCatalog.PagesFor(deviceId))
                if (p.Number == page)
                    ps = p.Params;
            if (ps == null)
                throw new InvalidOperationException("no such page " + page);
            var list = new List<ItmSubscription>();
            for (int i = 0; i < ps.Count; i++)
                list.Add(new ItmSubscription((byte)i, ps[i], 0x12));
            return list;
        }

        [Fact]
        public void DirectorPlusLifecycle_UnexpectedPushDuringRevert_PendingAndReissue()
        {
            // Real lifecycle: director issues revert while Synced; unexpected push
            // mid-switch abandons / confirms via controller; director observes generations.
            var t = new RecordingTransport();
            long clock = 0;
            var life = new ItmLifecycleController(
                new ItmEncoder(t), deviceId: 3, () => clock, _ => { });
            life.Start();
            life.Tick(true);
            life.OnPush(PushFor(life.DefaultPage));
            clock += life.AccumulateWindowMs;
            life.Tick(true);
            Assert.Equal(ItmLifecycleState.Synced, life.State);

            var control = new ItmLifecyclePageControl(life);
            var director = new DisplayPageDirector(control, itmDeviceId: 3, () => clock, _ => { });
            director.RejectUncommandedChanges = true;

            // Command page 5 (tyre temps on device 3).
            var r = director.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal((byte?)5, r.RequestedWirePage);
            Assert.Equal(ItmLifecycleState.Switching, life.State);

            // Drain switch to 5.
            clock += life.SwitchQuietMs;
            life.Tick(true);
            life.OnPush(PushFor(5));
            clock += life.AccumulateWindowMs;
            life.Tick(true);
            Assert.Equal(ItmLifecycleState.Synced, life.State);
            director.Tick(Page(ItmPage.TyreTemps, "r1"));

            // Unexpected uncommanded push to page 4 while Synced.
            life.OnPush(PushFor(4));
            clock += life.AccumulateWindowMs;
            life.Tick(true);
            r = director.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.True(r.RevertedThisTick);
            Assert.Equal((byte?)5, r.RequestedWirePage);
            // Director-issued RequestPage while Synced starts a switch (not a queued pending
            // behind an already-in-flight procedure — we were Synced when the revert fired).
            Assert.Equal(ItmLifecycleState.Switching, life.State);

            // Pending field is 0 (no mid-procedure queue) while the revert switch runs.
            var pending = typeof(ItmLifecycleController).GetField(
                "_pendingRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(pending);
            Assert.Equal((byte)0, pending!.GetValue(life));
        }
    }
}
