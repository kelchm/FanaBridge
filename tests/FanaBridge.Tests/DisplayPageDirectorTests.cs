using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Exercises <see cref="DisplayPageDirector"/> against a fake <see cref="IItmPageControl"/>:
    /// one request per intent change (never per frame), the manual-navigation detection matrix
    /// (baseline, repaint, recovery, wheel button, legacy landing, unknown page), the single
    /// re-issue after a request is lost to a cold landing, legacy-screen intents, and the
    /// catalog-driven wire mapping (standard vs Bentley page tables). Plus the production
    /// <see cref="ItmLifecyclePageControl"/> wrapper over a real lifecycle controller.
    /// </summary>
    public class DisplayPageDirectorTests
    {
        // ── Test doubles ─────────────────────────────────────────────────

        private sealed class FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Idle;
            public byte? CurrentWirePage { get; set; }
            public long SyncGeneration { get; set; }
            public List<byte> Requests { get; } = new List<byte>();

            public void RequestPage(byte wirePage) => Requests.Add(wirePage);

            /// <summary>A push-confirmed landing: Synced on the page, generation bumped —
            /// the only way the real controller ever changes its confirmed page.</summary>
            public void Land(byte wirePage)
            {
                State = ItmLifecycleState.Synced;
                CurrentWirePage = wirePage;
                SyncGeneration++;
            }

            /// <summary>A repaint-style resync (e.g. game start on the page already shown):
            /// the generation bumps, the page does not change.</summary>
            public void Resync() => SyncGeneration++;

            /// <summary>A cold entry (wheel change, restart): the page is forgotten.</summary>
            public void Cold()
            {
                State = ItmLifecycleState.BringUp;
                CurrentWirePage = null;
            }
        }

        private sealed class Harness
        {
            public readonly FakePageControl Control = new FakePageControl();
            public readonly List<string> Log = new List<string>();
            public DisplayPageDirector Director = null!;

            public static Harness Create(byte itmDeviceId = 2)
            {
                var h = new Harness();
                h.Director = new DisplayPageDirector(h.Control, itmDeviceId, () => 0, h.Log.Add);
                return h;
            }

            public DirectorTickResult Tick(RuleIntent intent) => Director.Tick(intent);

            /// <summary>Lands the fake and runs a baseline tick so later landings are
            /// post-baseline. Standard table: wire 1 = LapInfo, so the base intent's page
            /// equals the landing and no request is issued.</summary>
            public void SyncBaseline(byte wirePage = 1, ItmPage intentPage = ItmPage.LapInfo)
            {
                Control.Land(wirePage);
                var r = Tick(Page(intentPage));
                Assert.Null(r.Manual);
                Assert.Empty(Control.Requests);
            }

            public int LogCount(string fragment)
            {
                int n = 0;
                foreach (var line in Log)
                    if (line.Contains(fragment))
                        n++;
                return n;
            }
        }

        private static RuleIntent Page(ItmPage page, string? ruleId = null)
            => new RuleIntent(TargetKind.Page, page, null, ruleId);

        private static RuleIntent Screen(string id, string? ruleId = "r1")
            => new RuleIntent(TargetKind.LegacyScreen, null, id, ruleId);

        // ── Request issuance ─────────────────────────────────────────────

        [Fact]
        public void IntentChange_IssuesExactlyOneRequest()
        {
            var h = Harness.Create();
            h.SyncBaseline();

            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal((byte?)5, r.RequestedWirePage);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);

            // The intent persists for many frames — the request must not repeat.
            for (int i = 0; i < 30; i++)
            {
                r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
                Assert.Null(r.RequestedWirePage);
            }
            Assert.Single(h.Control.Requests);
        }

        [Fact]
        public void IntentMatchingCurrentPage_NeverRequests()
        {
            var h = Harness.Create();
            h.SyncBaseline();
            for (int i = 0; i < 10; i++)
                h.Tick(Page(ItmPage.LapInfo));
            Assert.Empty(h.Control.Requests);
        }

        [Fact]
        public void EachIntentChange_RequestsOnce()
        {
            var h = Harness.Create();
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "a"));
            h.Tick(Page(ItmPage.FuelErsDrs, "b"));
            h.Tick(Page(ItmPage.TyreTemps, "a"));
            Assert.Equal(new byte[] { 5, 2, 5 }, h.Control.Requests);
        }

        [Fact]
        public void RequestConfirmed_NoFurtherRequests_AndNotManual()
        {
            var h = Harness.Create();
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));

            h.Control.Land(5);   // our request's confirming push
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Null(r.Manual);
            Assert.Null(r.RequestedWirePage);
            Assert.Single(h.Control.Requests);
        }

        [Fact]
        public void IdleAndDisabled_HoldRequests_UntilTheLifecycleWakes()
        {
            // Idle/Disabled drop RequestPage on the floor — issuing there would latch a
            // request the controller never saw and suppress the real one later.
            var h = Harness.Create();
            h.Control.State = ItmLifecycleState.Idle;
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.State = ItmLifecycleState.Disabled;
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Empty(h.Control.Requests);

            // Bring-up: the controller queues requests mid-procedure — now it may go out.
            h.Control.State = ItmLifecycleState.BringUp;
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal((byte?)5, r.RequestedWirePage);
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);
        }

        [Fact]
        public void RequestsAllowedWhileInFlight_ControllerQueueingHonored()
        {
            // A rule firing while a switch is in flight: the request goes to the controller
            // (which queues it) — once, not per frame.
            var h = Harness.Create();
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));

            h.Control.State = ItmLifecycleState.Switching;   // switch to 5 in flight
            var r = h.Tick(Page(ItmPage.FuelErsDrs, "r2"));
            Assert.Equal((byte?)2, r.RequestedWirePage);
            h.Tick(Page(ItmPage.FuelErsDrs, "r2"));
            Assert.Equal(new byte[] { 5, 2 }, h.Control.Requests);
        }

        // ── Manual-navigation detection matrix ───────────────────────────

        [Fact]
        public void FirstSyncedObservation_IsNeverManual()
        {
            // The director may join mid-session (config swap): whatever page it first sees
            // is the baseline, even at a generation it has never observed.
            var h = Harness.Create();
            h.Control.SyncGeneration = 41;
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.LapTimes));
            Assert.Null(r.Manual);
            Assert.Empty(h.Control.Requests);
        }

        [Fact]
        public void RepaintResync_SamePage_IsNotManual()
        {
            // Game start on the page already shown bumps the generation without a page
            // change — a repaint, not navigation.
            var h = Harness.Create();
            h.SyncBaseline();
            h.Control.Resync();
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.Null(r.Manual);
        }

        [Fact]
        public void RecoveryReestablishingRequestedPage_IsNotManual()
        {
            var h = Harness.Create();
            h.SyncBaseline();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            h.Control.Land(5);
            h.Tick(Page(ItmPage.TyreTemps, "r1"));

            // Spontaneous session drop: the lifecycle recovers and re-lands the same page.
            h.Control.State = ItmLifecycleState.Recovery;
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Null(r.RequestedWirePage);   // the controller is already driving there
            h.Control.Land(5);
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Null(r.Manual);
            Assert.Single(h.Control.Requests);
        }

        [Fact]
        public void WheelButtonLanding_IsManual_AndNotFoughtThatTick()
        {
            var h = Harness.Create();
            h.SyncBaseline();

            h.Control.Land(4);   // driver pressed the display button → Lap Times
            var r = h.Tick(Page(ItmPage.LapInfo));   // engine has not seen the manual yet
            Assert.True(r.Manual.HasValue);
            Assert.Equal(ItmPage.LapTimes, r.Manual.Value.Page);
            Assert.Empty(h.Control.Requests);        // adopt, never fight

            // Next frame the engine adopted the page as its resting target — quiet.
            r = h.Tick(Page(ItmPage.LapTimes));
            Assert.Null(r.Manual);
            Assert.Empty(h.Control.Requests);
        }

        [Fact]
        public void LegacyLanding_IsManualToTheLegacyPageIdentity()
        {
            var h = Harness.Create();
            h.SyncBaseline();
            h.Control.Land(6);   // standard table: wire 6 = the legacy ITM page
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.True(r.Manual.HasValue);
            Assert.Equal(ItmPage.Legacy, r.Manual.Value.Page);
            Assert.Empty(h.Control.Requests);
        }

        [Fact]
        public void UnknownWirePageLanding_IsNeverManual_LogsOnce()
        {
            var h = Harness.Create();
            h.SyncBaseline();

            h.Control.Land(9);   // not in any catalog
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.Null(r.Manual);
            Assert.Equal(1, h.LogCount("not in this device's page catalog"));
            // No manual explanation → the intent is re-asserted, once.
            Assert.Equal(new byte[] { 1 }, h.Control.Requests);

            h.Control.Land(9);   // still there on the next landing — no repeat of either
            r = h.Tick(Page(ItmPage.LapInfo));
            Assert.Null(r.Manual);
            Assert.Equal(1, h.LogCount("not in this device's page catalog"));
            Assert.Single(h.Control.Requests);
        }

        [Fact]
        public void UncatalogedPageAdoption_IsManualWithoutIdentity_AndNeverFought()
        {
            // The wheel button reaches a page whose parameter set matches no catalog
            // entry: the controller adopts it — Synced, generation bumped, page unknown
            // (the seam reports null). The director must adopt it too: manual navigation
            // WITHOUT a page identity, and no request while the display sits there.
            var h = Harness.Create();
            h.SyncBaseline();

            h.Control.CurrentWirePage = null;
            h.Control.SyncGeneration++;
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.True(r.Manual.HasValue);
            Assert.Null(r.Manual.Value.Page);          // no identity to report
            Assert.Null(r.RequestedWirePage);          // adopt, never fight
            Assert.Equal(1, h.LogCount("outside this device's page catalog"));

            // The engine adopted "wherever the wheel is": its resting intent carries no
            // page, and the director stays quiet however long the display sits there —
            // including across further adoptions of the same unnamed page.
            var resting = new RuleIntent(TargetKind.Page, null, null, null);
            for (int i = 0; i < 10; i++)
            {
                if (i == 5)
                    h.Control.SyncGeneration++;        // firmware re-announces the set
                r = h.Tick(resting);
                Assert.Null(r.Manual);
                Assert.Null(r.RequestedWirePage);
            }
            Assert.Empty(h.Control.Requests);
            Assert.Equal(1, h.LogCount("outside this device's page catalog"));

            // A fresh rule fire may still claim the screen — like after any manual move.
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal((byte?)5, r.RequestedWirePage);
        }

        [Fact]
        public void ReturnFromUncatalogedPage_ToThePreviousPage_IsNotManual()
        {
            var h = Harness.Create();
            h.SyncBaseline();                          // on wire 1
            h.Control.CurrentWirePage = null;          // adopted an uncataloged set
            h.Control.SyncGeneration++;
            h.Tick(Page(ItmPage.LapInfo));             // manual (no identity) reported

            h.Control.Land(1);                         // wheel button back to Lap Info
            var r = h.Tick(new RuleIntent(TargetKind.Page, null, null, null));
            Assert.Null(r.Manual);                     // re-confirmation of the page last seen
            Assert.Empty(h.Control.Requests);
        }

        [Fact]
        public void PageChangeWithoutGenerationAdvance_IsNotManual()
        {
            // Manual detection rides sync-generation edges exclusively — a page mutation
            // with no adopted push (impossible on the real controller) must not register.
            var h = Harness.Create();
            h.SyncBaseline();
            h.Control.CurrentWirePage = 4;
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.Null(r.Manual);
        }

        // ── Lost-request re-issue (once per landing) ─────────────────────

        [Fact]
        public void ColdStartLandingElsewhere_ReassertsIntentOnce()
        {
            var h = Harness.Create();
            h.Control.Cold();
            h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal(new byte[] { 5 }, h.Control.Requests);   // queued with the controller

            // Bring-up settles on its default page — the queued request did not survive.
            h.Control.Land(1);
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Null(r.Manual);                                   // baseline, never manual
            Assert.Equal(new byte[] { 5, 5 }, h.Control.Requests);   // re-issued exactly once

            for (int i = 0; i < 10; i++)
                h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Equal(2, h.Control.Requests.Count);               // no storm

            h.Control.Land(5);
            r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Null(r.Manual);
            Assert.Equal(2, h.Control.Requests.Count);
        }

        [Fact]
        public void WheelChange_ReestablishesBaseline_ColdLandingNotManual()
        {
            var h = Harness.Create();
            h.Control.Land(4);
            h.Tick(Page(ItmPage.LapTimes));      // baseline on Lap Times, no request
            Assert.Empty(h.Control.Requests);

            h.Control.Cold();                    // wheel swapped mid-session
            h.Tick(Page(ItmPage.LapTimes));      // re-asks while the controller brings up
            Assert.Equal(new byte[] { 4 }, h.Control.Requests);

            h.Control.Land(1);                   // the new wheel came up on its default
            var r = h.Tick(Page(ItmPage.LapTimes));
            Assert.Null(r.Manual);               // cold-start landing: baseline, never manual
            Assert.Equal(new byte[] { 4, 4 }, h.Control.Requests);   // one re-issue
        }

        // ── Legacy-screen intents ────────────────────────────────────────

        [Fact]
        public void LegacyScreenIntent_RequestsLegacyPage_AndReportsScreenId()
        {
            var h = Harness.Create();
            h.SyncBaseline();

            var r = h.Tick(Screen("PIT"));
            Assert.Equal((byte?)6, r.RequestedWirePage);
            Assert.Equal("PIT", r.LegacyScreenId);

            h.Control.Land(6);
            r = h.Tick(Screen("PIT"));
            Assert.Null(r.Manual);                 // landing on the requested page
            Assert.Null(r.RequestedWirePage);
            Assert.Equal("PIT", r.LegacyScreenId);   // reported every tick the intent holds
            Assert.Equal(new byte[] { 6 }, h.Control.Requests);
        }

        [Fact]
        public void PageIntent_CarriesNoLegacyScreenId()
        {
            var h = Harness.Create();
            h.SyncBaseline();
            var r = h.Tick(Page(ItmPage.TyreTemps, "r1"));
            Assert.Null(r.LegacyScreenId);
        }

        // ── Catalog-driven wire mapping (Bentley table) ──────────────────

        [Fact]
        public void Bentley_ResolvesWiresFromItsOwnTable()
        {
            // Device 4 renumbers: Lap Times sits at wire 3 (standard: 4).
            var h = Harness.Create(itmDeviceId: 4);
            h.SyncBaseline();
            var r = h.Tick(Page(ItmPage.LapTimes, "r1"));
            Assert.Equal((byte?)3, r.RequestedWirePage);
        }

        [Fact]
        public void Bentley_LegacyPageIsWire5()
        {
            var h = Harness.Create(itmDeviceId: 4);
            h.SyncBaseline();
            var r = h.Tick(Screen("PIT"));
            Assert.Equal((byte?)5, r.RequestedWirePage);
            Assert.Equal("PIT", r.LegacyScreenId);
        }

        [Fact]
        public void Bentley_MissingCarSettingsPage_NeverRequested_WarnsOnce()
        {
            var h = Harness.Create(itmDeviceId: 4);
            h.SyncBaseline();

            var r = h.Tick(Page(ItmPage.CarSettings, "r1"));
            Assert.Null(r.RequestedWirePage);
            h.Tick(Page(ItmPage.CarSettings, "r1"));
            Assert.Empty(h.Control.Requests);
            Assert.Equal(1, h.LogCount("does not have"));
        }

        [Fact]
        public void Bentley_ManualIdentity_UsesItsOwnTable()
        {
            // Wire 4 is Tyre Temps on the Bentley (Lap Times on the standard table).
            var h = Harness.Create(itmDeviceId: 4);
            h.SyncBaseline();
            h.Control.Land(4);
            var r = h.Tick(Page(ItmPage.LapInfo));
            Assert.True(r.Manual.HasValue);
            Assert.Equal(ItmPage.TyreTemps, r.Manual.Value.Page);
        }

        // ── The production wrapper over the real controller ──────────────

        private sealed class StubTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength => 64;
            public bool SendCol03(byte[] data) => true;
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

        [Fact]
        public void ItmLifecyclePageControl_TranslatesUnknownPageToNull_AndPassesThrough()
        {
            long t = 0;
            var controller = new ItmLifecycleController(new ItmEncoder(new StubTransport()),
                deviceId: 3, nowMs: () => t, log: _ => { });
            IItmPageControl control = new ItmLifecyclePageControl(controller);

            // Idle: page unknown → null, never 0.
            Assert.Equal(ItmLifecycleState.Idle, control.State);
            Assert.Null(control.CurrentWirePage);

            // Bring the controller to Synced on page 1 with a real confirming push.
            controller.Start();
            controller.Tick(true);
            var page1 = new List<ItmSubscription>();
            byte handle = 0;
            foreach (var p in ItmDeviceCatalog.PagesFor(3))
                if (p.Number == 1)
                    foreach (var paramId in p.Params)
                        page1.Add(new ItmSubscription(handle++, paramId, 0x12));
            controller.OnPush(page1);
            t += controller.AccumulateWindowMs;
            controller.Tick(true);

            Assert.Equal(ItmLifecycleState.Synced, control.State);
            Assert.Equal((byte?)1, control.CurrentWirePage);
            Assert.Equal(controller.SyncGeneration, control.SyncGeneration);

            // RequestPage reaches the controller: a different page starts the switch.
            control.RequestPage(2);
            Assert.Equal(ItmLifecycleState.Switching, controller.State);
        }
    }
}
