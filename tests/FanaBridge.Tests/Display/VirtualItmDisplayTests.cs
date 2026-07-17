using System;
using System.Collections.Generic;
using FanaBridge.Display.Session;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The wire-driven virtual ITM panel (<see cref="VirtualItmDisplay"/>), tested at
    /// the state-model level: frames are produced by the REAL <see cref="ItmEncoder"/>
    /// through the REAL <see cref="TappedDeviceTransport"/> (the twin attached as its
    /// observer — the same seam production uses), and subscription pushes are raw
    /// col03-IN reports. No driver, no adapters: what's under test is that the panel
    /// state the twin maintains — gate, page, handle table, painted values, suffixes,
    /// placeholders — follows the BYTES, per docs/reference/protocol.md §0x05.
    /// </summary>
    public class VirtualItmDisplayTests
    {
        private const byte Dev = ItmEncoder.DefaultDeviceId;   // the wheel OLED, device 3

        // ── Harness ──────────────────────────────────────────────────────

        private sealed class AcceptingTransport : IDeviceTransport
        {
            public bool IsConnected => true;
            public bool SendCol03(byte[] data) => true;
            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int Col03MaxInputReportLength => 64;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private readonly Clock _clock = new Clock();
        private readonly VirtualItmDisplay _twin;
        private readonly ItmEncoder _encoder;

        public VirtualItmDisplayTests()
        {
            _twin = new VirtualItmDisplay(nowMs: () => _clock.T);
            var tap = new TappedDeviceTransport(new AcceptingTransport());
            tap.AttachObserver(_twin);
            _encoder = new ItmEncoder(tap);
        }

        // The lifecycle's cold bring-up frame sequence (reset, gate, enable, PageSet).
        private void BringUp(byte page = 1)
        {
            _encoder.ResetDisplay();
            _encoder.SetItmMode(true);
            _encoder.EnableItm();
            _encoder.SetPage(Dev, page);
        }

        // Lets the snapshot throttle window pass and flushes any held change.
        private DisplayValuesSnapshot Settle(ItmLifecycleState state = ItmLifecycleState.Synced)
        {
            _clock.T += 250;
            _twin.Tick(state);
            return _twin.Snapshot;
        }

        // ── Push reports (raw col03-IN, entries per protocol.md §pushes) ─

        // Page 1 (Lap Info): h0=SPEED h1=GEAR(u8) h2=LAP h3=POSITION h4=LAP_TIME h5=LAST_LAP.
        private static readonly byte[] LapInfoPush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");

        // Page 5 (Tyre Temps): h0=SPEED h1=GEAR h2=FL h3=RL h4=FR h5=RR.
        private static readonly byte[] TyrePush = HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static byte[] UnsubReport(params byte[] fwHandles)
        {
            var list = new List<byte> { 0xFF, 0x05, 0x01 };
            foreach (var h in fwHandles) { list.Add(Dev); list.Add(h); list.Add(0xFF); list.Add(0xFF); list.Add(0x00); }
            return list.ToArray();
        }

        // A one-entry subscribe report (the GTSWX-style fragmented framing).
        private static byte[] SubEntry(byte fwHandle, ushort paramId, byte dataType)
            => new byte[] { 0xFF, 0x05, 0x01, Dev, fwHandle, (byte)(paramId & 0xFF), (byte)(paramId >> 8), dataType };

        // ── Values / defs (the quick guide's page-1 example screen) ──────

        private static List<ItmValue> LapInfoValues() => new List<ItmValue>
        {
            ItmValue.Int16(0, ItmParam.Speed, 268),
            ItmValue.UInt8(1, ItmParam.Gear, 6),
            ItmValue.UInt8(2, ItmParam.Lap, 15),
            ItmValue.UInt8(3, ItmParam.Position, 2),
            ItmValue.Float32(4, ItmParam.LapTime, 96.911f),
            ItmValue.Float32(5, ItmParam.LastLapTime, 134.169f),
        };

        private static List<ItmParamDef> LapInfoDefs() => new List<ItmParamDef>
        {
            ItmParamDef.WithSuffix(0x82, "/73"),
            ItmParamDef.WithSuffix(0x83, "/20"),
        };

        // Bring-up + push + values + defs: the settled page-1 screen.
        private void SyncPageOneWithValues()
        {
            BringUp(1);
            _twin.OnSubscriptionReport(LapInfoPush);
            _clock.T += 50;
            Assert.True(_encoder.SendValues(LapInfoValues(), Dev));
            Assert.True(_encoder.SetParamDefs(LapInfoDefs(), Dev));
        }

        private static string FieldValue(DisplayValueSlot slot)
            => Assert.Single(Assert.IsAssignableFrom<DisplayValueSlot>(slot).Fields).Value;

        // ── Bootstrap / grounding ────────────────────────────────────────

        [Fact]
        public void ColdConstruction_PublishesNothing()
        {
            Assert.Null(_twin.Snapshot);
            Assert.Null(_twin.GateOn);
            Assert.Equal(0, _twin.WirePage);
            Assert.Equal(0, _twin.SubscriptionCount);
            Assert.False(_twin.SessionEnableObserved);
        }

        [Fact]
        public void BringUp_GroundsGateSessionAndPage_ShowingPlaceholders()
        {
            BringUp(1);
            Assert.True(_twin.GateOn);
            Assert.True(_twin.SessionEnableObserved);
            Assert.Equal(1, _twin.WirePage);

            _twin.OnSubscriptionReport(LapInfoPush);
            Assert.Equal(6, _twin.SubscriptionCount);
            Assert.Equal(1, _twin.WirePage);   // the push confirms the page it announced

            var snap = Settle();
            Assert.NotNull(snap);
            Assert.Equal(ItmPage.LapInfo, snap.Page);
            Assert.Equal("Lap Info", snap.PageName);
            Assert.True(snap.ShowingPlaceholders);
            Assert.Equal("--- / -", FieldValue(snap.LeftTop));
            Assert.Equal("--- / -", FieldValue(snap.LeftBottom));
            Assert.Equal("--:--.-", FieldValue(snap.RightTop));
            Assert.Equal("--:--.-", FieldValue(snap.RightBottom));
            Assert.Equal("-", snap.GearText);
            Assert.Equal("---", snap.SpeedText);
        }

        // ── Painting from the wire ───────────────────────────────────────

        [Fact]
        public void SyncedWithValuesAndSuffixes_RendersTheGuideExamplePage()
        {
            SyncPageOneWithValues();
            var snap = Settle();

            Assert.Equal(ItmPage.LapInfo, snap.Page);
            Assert.Equal(1, snap.WirePage);
            Assert.Equal(ItmLifecycleState.Synced, snap.State);
            Assert.False(snap.ShowingPlaceholders);

            Assert.Equal("LAPS:", snap.LeftTop.Label);
            Assert.Equal("15 /73", FieldValue(snap.LeftTop));
            Assert.Equal("POSITION:", snap.LeftBottom.Label);
            Assert.Equal("02 /20", FieldValue(snap.LeftBottom));
            Assert.Equal("CURRENT LAP:", snap.RightTop.Label);
            Assert.Equal("01:36.911", FieldValue(snap.RightTop));
            Assert.Equal("LAST LAP:", snap.RightBottom.Label);
            Assert.Equal("02:14.169", FieldValue(snap.RightBottom));

            Assert.Equal("6", snap.GearText);
            Assert.Equal("268", snap.SpeedText);
        }

        [Fact]
        public void ValueAtAnUnsubscribedHandle_DoesNotPaint()
        {
            // The firmware ignores values at handles it never subscribed (§Control
            // Model) — the twin's whole point is to show that a host writing through
            // a stale table paints nothing.
            SyncPageOneWithValues();
            var before = Settle();

            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(9, ItmParam.Lap, 99) }, Dev);
            var after = Settle();
            Assert.Same(before, after);   // nothing on the screen moved
            Assert.Equal("15 /73", FieldValue(after.LeftTop));
        }

        [Fact]
        public void ValueAtASubscribedHandle_ForTheWrongParam_DoesNotPaint()
        {
            // h2 is subscribed to LAP; a POSITION value routed through it is a
            // host/firmware disagreement, surfaced by not painting (modeled — see the
            // class doc).
            SyncPageOneWithValues();
            Settle();

            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Position, 9) }, Dev);
            var snap = Settle();
            Assert.Equal("15 /73", FieldValue(snap.LeftTop));
            Assert.Equal("02 /20", FieldValue(snap.LeftBottom));
        }

        [Fact]
        public void DisplayReset_ClearsToPlaceholders_AndFieldsRepopulatePerHandle()
        {
            SyncPageOneWithValues();
            Settle();

            _encoder.ResetDisplay();
            var reset = Settle();
            Assert.True(reset.ShowingPlaceholders);
            Assert.Equal("--- / -", FieldValue(reset.LeftTop));
            Assert.Equal("-", reset.GearText);
            Assert.Equal(6, _twin.SubscriptionCount);   // subscriptions survive the reset

            // One value arriving after the reset repopulates ITS field only; the
            // suffix decoration was not cleared by the reset and renders again.
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 16) }, Dev);
            var repainted = Settle();
            Assert.False(repainted.ShowingPlaceholders);
            Assert.Equal("16 /73", FieldValue(repainted.LeftTop));
            Assert.Equal("--- / -", FieldValue(repainted.LeftBottom));
            Assert.Equal("--:--.-", FieldValue(repainted.RightTop));
            Assert.Equal("-", repainted.GearText);
        }

        // ── Page identity ────────────────────────────────────────────────

        [Fact]
        public void HostPageSet_IsAuthoritative_AndClearsToPlaceholders()
        {
            SyncPageOneWithValues();
            Settle();

            _encoder.SetPage(Dev, 5);
            Assert.Equal(5, _twin.WirePage);
            var snap = Settle();
            Assert.Equal(ItmPage.TyreTemps, snap.Page);
            Assert.True(snap.ShowingPlaceholders);
        }

        [Fact]
        public void WheelButtonPageChange_IsInferredFromThePushedParamSet()
        {
            // Wheel-button navigation is invisible on the OUT wire — no PageSet is
            // sent. The unsubscribe + new-page push alone must move the twin.
            SyncPageOneWithValues();
            Settle();

            _twin.OnSubscriptionReport(UnsubReport(0, 1, 0x82, 0x83, 4, 5));
            _clock.T += 2;
            _twin.OnSubscriptionReport(TyrePush);
            Assert.Equal(5, _twin.WirePage);

            var snap = Settle();
            Assert.Equal(ItmPage.TyreTemps, snap.Page);
            Assert.Equal("FL TIRE TEMP:", snap.LeftTop.Label);
            Assert.True(snap.ShowingPlaceholders);   // the old page's values don't leak
        }

        [Fact]
        public void FragmentedPush_OneEntryPerReport_IdentifiesThePage_WithoutFlapping()
        {
            // The GTSWX-style framing: one entry per report, ~2 ms apart (protocol.md
            // §pushes: never treat a single report as the complete set). Mid-fragment
            // partial sets must hold the current page, not flap to Unknown.
            SyncPageOneWithValues();
            Settle();

            _twin.OnSubscriptionReport(UnsubReport(0, 1, 0x82, 0x83, 4, 5));
            _clock.T += 2;
            _twin.OnSubscriptionReport(SubEntry(0x00, ItmParam.Speed, 0x34));
            _clock.T += 2;
            _twin.OnSubscriptionReport(SubEntry(0x01, ItmParam.Gear, 0x12));
            _clock.T += 2;
            _twin.OnSubscriptionReport(SubEntry(0x82, ItmParam.TyreFlTemp, 0x32));
            Assert.Equal(1, _twin.WirePage);   // partial set — page held through the grace window

            _clock.T += 2;
            _twin.OnSubscriptionReport(SubEntry(0x83, ItmParam.TyreRlTemp, 0x32));
            _clock.T += 2;
            _twin.OnSubscriptionReport(SubEntry(0x84, ItmParam.TyreFrTemp, 0x32));
            _clock.T += 2;
            _twin.OnSubscriptionReport(SubEntry(0x85, ItmParam.TyreRrTemp, 0x32));
            Assert.Equal(5, _twin.WirePage);   // the completed set identifies Tyre Temps
        }

        [Fact]
        public void StablePartialSetPastGrace_IsHonestlyUnknown()
        {
            SyncPageOneWithValues();
            Settle();

            _twin.OnSubscriptionReport(UnsubReport(0, 1, 0x82, 0x83, 4, 5));
            _twin.OnSubscriptionReport(SubEntry(0x00, ItmParam.Speed, 0x34));
            _twin.OnSubscriptionReport(SubEntry(0x01, ItmParam.Gear, 0x12));
            _twin.OnSubscriptionReport(SubEntry(0x82, ItmParam.TyreFlTemp, 0x32));

            var snap = Settle();   // 250 ms — well past the identity grace window
            Assert.Equal(0, _twin.WirePage);
            Assert.Null(snap.Page);
            Assert.Null(snap.PageName);   // the UI renders this as an unrecognized page
            Assert.Null(snap.LeftTop);
        }

        [Fact]
        public void UnsubscribeAllWithNothingFollowing_LandsOnTheLegacyPage()
        {
            SyncPageOneWithValues();
            Settle();

            _twin.OnSubscriptionReport(UnsubReport(0, 1, 0x82, 0x83, 4, 5));
            var snap = Settle();   // grace expired with an empty set
            Assert.Equal(6, _twin.WirePage);
            Assert.Equal(ItmPage.Legacy, snap.Page);
            Assert.Equal("Legacy", snap.PageName);
            Assert.Null(snap.LeftTop);
            Assert.Null(snap.GearText);
        }

        [Fact]
        public void SingleUnsubscribe_IsTableBookkeeping_NotAVisualEvent()
        {
            SyncPageOneWithValues();
            Settle();

            // Within the grace window (the front edge of a change in flight): the
            // handle leaves the table, but the painted glyphs stay on screen.
            _twin.OnSubscriptionReport(UnsubReport(0x82));
            Assert.Equal(5, _twin.SubscriptionCount);
            Assert.Equal(1, _twin.WirePage);
            _clock.T += 10;
            _twin.Tick(ItmLifecycleState.Synced);
            Assert.Equal("15 /73", FieldValue(_twin.Snapshot.LeftTop));
        }

        // ── Gate ─────────────────────────────────────────────────────────

        [Fact]
        public void GateOff_BlanksTheScreen_AndDropsTheHandleTable()
        {
            SyncPageOneWithValues();
            Settle();

            _encoder.SetItmMode(false);
            Assert.False(_twin.GateOn);
            Assert.Equal(0, _twin.SubscriptionCount);   // a gate cycle resets the table
            Assert.Equal(1, _twin.WirePage);            // …but the page is retained for re-enable

            var snap = Settle();
            Assert.Null(snap.Page);        // blank: the panel is showing true legacy (col01)
            Assert.Equal(0, snap.WirePage);
            Assert.Null(snap.PageName);
            Assert.Null(snap.LeftTop);
            Assert.Null(snap.GearText);
        }

        [Fact]
        public void GateCycleToTheSamePage_RetainsPaintedValues_ButNeedsAFreshPushForNewOnes()
        {
            SyncPageOneWithValues();
            Settle();

            // gate off → PageSet(1) while off → gate on: lands back on page 1
            // (§ITM Mode, hardware-confirmed), which retains every painted field
            // (§DisplayReset, hardware-verified).
            _encoder.SetItmMode(false);
            _encoder.SetPage(Dev, 1);
            _encoder.SetItmMode(true);
            Assert.Equal(1, _twin.WirePage);
            var reshown = Settle();
            Assert.Equal("15 /73", FieldValue(reshown.LeftTop));
            Assert.Equal("6", reshown.GearText);

            // The gate cycle reset the handle table (§Firmware Subscription Pushes):
            // values at pre-cycle handles are ignored until a fresh push re-subscribes.
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 16) }, Dev);
            Assert.Equal("15 /73", FieldValue(Settle().LeftTop));

            _twin.OnSubscriptionReport(LapInfoPush);
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 16) }, Dev);
            Assert.Equal("16 /73", FieldValue(Settle().LeftTop));
        }

        [Fact]
        public void PageSetWhileGatedOff_IsRecorded_AndAppliedOnGateOn()
        {
            SyncPageOneWithValues();
            Settle();

            _encoder.SetItmMode(false);
            _encoder.SetPage(Dev, 5);          // recorded, not applied (§ITM Mode)
            Assert.Equal(1, _twin.WirePage);   // still the retained pre-gate page

            _encoder.SetItmMode(true);
            Assert.Equal(5, _twin.WirePage);   // applied at gate-on
            var snap = Settle();
            Assert.Equal(ItmPage.TyreTemps, snap.Page);
            Assert.True(snap.ShowingPlaceholders);   // a genuine page change — nothing painted yet
        }

        [Fact]
        public void BareGateCycle_LandsOnTheLegacyPage()
        {
            SyncPageOneWithValues();
            Settle();

            _encoder.SetItmMode(false);
            _encoder.SetItmMode(true);         // no PageSet recorded while off
            Assert.Equal(6, _twin.WirePage);   // the legacy ITM page (§ITM Mode)
            Assert.Equal(ItmPage.Legacy, Settle().Page);
        }

        [Fact]
        public void RedundantGateOnReassert_ChangesNothing()
        {
            SyncPageOneWithValues();
            var settled = Settle();

            _encoder.SetItmMode(true);   // already on
            Assert.Same(settled, Settle());
        }

        // ── Never-stuck parsing ──────────────────────────────────────────

        [Fact]
        public void UnknownOrGarbledInput_IsIgnored_NeverThrown_NeverDesyncs()
        {
            SyncPageOneWithValues();
            var settled = Settle();

            _twin.OnCol03Sent(null);
            _twin.OnCol03Sent(new byte[0]);
            _twin.OnCol03Sent(new byte[] { 0x01, 0x02, 0x03, 0x04 });          // no FF prefix
            _twin.OnCol03Sent(new byte[] { 0xFF, 0x05, 0x77, 0x01, 0x02 });    // unknown subcommand
            _twin.OnCol03Sent(new byte[] { 0xFF, 0x09, 0x01 });                // unknown class
            _twin.OnSubscriptionReport(null);
            _twin.OnSubscriptionReport(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            // Nothing moved, and the frames that follow still apply cleanly.
            Assert.Same(settled, Settle());
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 16) }, Dev);
            Assert.Equal("16 /73", FieldValue(Settle().LeftTop));
        }

        // ── Snapshot publication contract ────────────────────────────────

        [Fact]
        public void UnchangedReasserts_KeepTheSameSnapshotReference()
        {
            // The driver re-sends unchanged values every RefreshIntervalMs and re-sends
            // ParamDefs after every sync — neither moves anything on the screen, so
            // neither recomposes.
            SyncPageOneWithValues();
            var settled = Settle();

            for (int i = 0; i < 5; i++)
            {
                _clock.T += 300;
                _encoder.SendValues(LapInfoValues(), Dev);
                _encoder.SetParamDefs(LapInfoDefs(), Dev);
                _twin.Tick(ItmLifecycleState.Synced);
            }
            Assert.Same(settled, _twin.Snapshot);
        }

        [Fact]
        public void ChangedValue_Recomposes_ButOnlyAfterTheThrottleWindow()
        {
            SyncPageOneWithValues();
            var settled = Settle();

            // A change landing inside the 250 ms window is held…
            _clock.T += 100;
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 16) }, Dev);
            Assert.Same(settled, _twin.Snapshot);

            // …and composes once the window passes, with no further edge.
            _clock.T += 200;
            _twin.Tick(ItmLifecycleState.Synced);
            var recomposed = _twin.Snapshot;
            Assert.NotSame(settled, recomposed);
            Assert.Equal("16 /73", FieldValue(recomposed.LeftTop));
        }

        [Fact]
        public void OutFrameCompose_IsDeferredToTick_NotRunOnTheTapPath()
        {
            // The tap fires synchronously on the sending thread, and for the encoder's
            // batched sends it runs while the transport's col03 write lock is held. So an
            // accepted OUT frame applies its state on the tap path but must NOT compose the
            // snapshot there — the render is deferred to Tick, which the owner runs after
            // the driver's sends complete, off the lock (research R7). With the throttle
            // window fully open, a value arriving via the tap therefore does not itself
            // publish a new snapshot; the next Tick does.
            SyncPageOneWithValues();
            var settled = Settle();

            _clock.T += 300;   // throttle window open — a compose would be allowed
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 16) }, Dev);
            Assert.Same(settled, _twin.Snapshot);   // the tap path did not render

            _twin.Tick(ItmLifecycleState.Synced);   // deferred compose runs here, off the wire
            Assert.NotSame(settled, _twin.Snapshot);
            Assert.Equal("16 /73", FieldValue(_twin.Snapshot.LeftTop));
        }

        [Fact]
        public void Tick_StampsTheHostLifecycleState()
        {
            BringUp(1);
            _twin.OnSubscriptionReport(LapInfoPush);
            var snap = Settle(ItmLifecycleState.AwaitPush);
            Assert.Equal(ItmLifecycleState.AwaitPush, snap.State);

            var recovered = Settle(ItmLifecycleState.Recovery);
            Assert.NotSame(snap, recovered);
            Assert.Equal(ItmLifecycleState.Recovery, recovered.State);
        }

        // ── Re-grounding ─────────────────────────────────────────────────

        [Fact]
        public void ColdStart_DropsEverything_AndTheNextBringUpRegrounds()
        {
            SyncPageOneWithValues();
            Settle();

            _twin.OnColdStart();
            Assert.Null(_twin.Snapshot);   // a stale screen never outlives its session
            Assert.Null(_twin.GateOn);
            Assert.Equal(0, _twin.WirePage);
            Assert.Equal(0, _twin.SubscriptionCount);
            Assert.False(_twin.SessionEnableObserved);

            // The fresh bring-up re-grounds the twin from its own frames.
            _clock.T += 10;
            BringUp(5);
            _twin.OnSubscriptionReport(TyrePush);
            var snap = Settle();
            Assert.Equal(ItmPage.TyreTemps, snap.Page);
            Assert.True(snap.ShowingPlaceholders);
        }

        // ── Multi-device addressing ──────────────────────────────────────

        [Fact]
        public void FramesForAnotherDisplayDevice_AreIgnored()
        {
            SyncPageOneWithValues();
            var settled = Settle();

            _encoder.SetPage(1, 4);   // the base's display, not this panel
            _encoder.SendValues(new List<ItmValue> { ItmValue.UInt8(2, ItmParam.Lap, 99) }, 1);
            _encoder.SetParamDefs(new List<ItmParamDef> { ItmParamDef.WithSuffix(0x82, "/99") }, 1);
            _twin.OnSubscriptionReport(HexToBytes("ff0501" + "01002a0032"));   // push entry for device 1

            Assert.Equal(1, _twin.WirePage);
            Assert.Equal(6, _twin.SubscriptionCount);
            Assert.Same(settled, Settle());
        }
    }
}
