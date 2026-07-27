using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Tests for the wheelbase identity state machine — the layer where field
    /// bugs have historically lived (settle/commit ordering, SRM precedence,
    /// the full disconnect reset). Runs against injected fakes: a connectable
    /// transport with scriptable family streams, a scripted HID bus, and a
    /// manual clock.
    /// </summary>
    public class FanatecWheelbaseTests
    {
        // ── Test doubles ─────────────────────────────────────────────────

        private sealed class FakeTransport : IConnectableTransport
        {
            public bool ConnectResult = true;
            public int ConnectedPid;
            public int DisconnectCount;
            public bool Connected;

            public FakeReportStream Identity { get; } = new FakeReportStream();
            public FakeReportStream Itm { get; } = new FakeReportStream();
            public FakeReportStream Srm { get; } = new FakeReportStream();
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public bool Connect(int productId)
            {
                ConnectedPid = productId;
                Connected = ConnectResult;
                return ConnectResult;
            }

            public void Disconnect() { DisconnectCount++; Connected = false; }
            public void Dispose() => Disconnect();

            public bool IsConnected => Connected;
            public bool IsDevicePresent => Connected;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                Connected ? FanatecTransport.TransportConnectStatus.Connected
                          : FanatecTransport.TransportConnectStatus.NoDeviceForPid;

            // When set, each sent frame's replies are enqueued onto the Identity
            // stream — models the device responding to the connect-time
            // enable/trigger elicit (ReadInitial flushes the stream first, so
            // pre-seeding cannot reach that path).
            public Queue<byte[]> RespondOnSend { get; } = new Queue<byte[]>();

            public bool SendCol03(byte[] data)
            {
                Sent.Add((byte[])data.Clone());
                while (RespondOnSend.Count > 0)
                    Identity.Enqueue(RespondOnSend.Dequeue());
                return true;
            }

            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => Identity;
            public IReportStream ItmReports => Itm;
            public IReportStream SrmReports => Srm;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col03MaxInputReportLength => 64;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class FakeBus : IHidBusEnumerator
        {
            public List<HidDeviceInfo> Devices = new List<HidDeviceInfo>();
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) => Devices;
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private static FanatecWheelbase Make(out FakeTransport t, out FakeBus bus, out Clock clock)
        {
            t = new FakeTransport();
            bus = new FakeBus();
            clock = new Clock();
            return new FanatecWheelbase(t, bus, clock.Now);
        }

        // Wire codes resolved from the decode tables rather than hardcoded, so a
        // table correction can't silently strand these tests on stale bytes.
        private static byte WheelWire(string code) =>
            FanatecDeviceTables.Wheels.First(kv => kv.Value == code).Key;
        private static byte HubWire(string code) =>
            FanatecDeviceTables.Hubs.First(kv => kv.Value == code).Key;

        // A pushed FF 08 system report: signature at offset 0, identity bytes at
        // the FanatecIdentity offsets.
        private static byte[] Ff08(byte baseType, byte wire, byte module = 0)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = baseType;
            b[FanatecIdentity.OffWireCode] = wire;
            b[FanatecIdentity.OffModule] = module;
            return b;
        }

        // Real 0xDD capture (kit fw 6.12, CSSWFORMV2) — same bytes pinned in
        // SrmConverterIdentityTests.
        private static byte[] SrmReply() =>
            new byte[] { 0xFF, 0xDD, 0x06, 0x12, 0x0A, 0x2F, 0x00, 0x00 };

        private static bool ContainsDeFa(byte[] frame)
        {
            for (int i = 0; i + 1 < frame.Length; i++)
                if (frame[i] == 0xDE && frame[i + 1] == 0xFA) return true;
            return false;
        }

        // Connects and commits a settled identity: enqueue → drain → settle → commit.
        private static void CommitIdentity(FanatecWheelbase wb, FakeTransport t, Clock clock, byte[] frame)
        {
            t.Identity.Enqueue(frame);
            clock.T += 10;
            wb.UpdateIdentity();               // drained + offered to the settler
            clock.T += 250;                    // ride out the 200 ms settle window
            Assert.True(wb.UpdateIdentity());  // committed
        }

        // ── Discovery / base-PID selection ────────────────────────────────

        [Fact]
        public void AutoConnect_NoDevices_FailsWithReason()
        {
            var wb = Make(out var t, out _, out _);

            Assert.False(wb.AutoConnect());
            Assert.Contains("No Fanatec devices", wb.LastConnectError);
            Assert.Equal(0, t.ConnectedPid);
        }

        [Fact]
        public void AutoConnect_PicksTheCol03CapableDevice()
        {
            var wb = Make(out var t, out var bus, out _);
            bus.Devices.Add(new HidDeviceInfo(0x1839, 8, 8, "Pedals"));          // accessory
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "ClubSport DD"));  // the base

            Assert.True(wb.AutoConnect());
            Assert.Equal(0x0020, t.ConnectedPid);
            Assert.Equal(0x0020, wb.ConnectedProductId);
            Assert.Equal("ClubSport DD", wb.ProductName);
            Assert.Null(wb.LastConnectError);
        }

        [Fact]
        public void AutoConnect_InputOnly64ByteInterface_StillAdopted()
        {
            // Looser than the transport's col03 check on purpose: a 64-byte INPUT
            // qualifies, so an input-only base is adopted here and the transport
            // reports NoCol03Interface instead of the base silently vanishing.
            var wb = Make(out var t, out var bus, out _);
            bus.Devices.Add(new HidDeviceInfo(0x0E03, 8, 64, "CSL Elite"));

            Assert.True(wb.AutoConnect());
            Assert.Equal(0x0E03, t.ConnectedPid);
        }

        [Fact]
        public void PickBasePid_AllDescriptorQueriesFailed_FallsBackToFirstPid()
        {
            // -1 = descriptor query threw on a busy handle; unknown, not a mismatch.
            var devices = new List<HidDeviceInfo>
            {
                new HidDeviceInfo(0x1111, -1, -1, null),
                new HidDeviceInfo(0x2222, -1, -1, null),
            };

            Assert.Equal(0x1111, FanatecWheelbase.PickBasePid(devices));
        }

        [Fact]
        public void Connect_TransportFailure_SurfacesCategorisedReason()
        {
            var wb = Make(out var t, out var bus, out _);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "ClubSport DD"));
            t.ConnectResult = false;

            Assert.False(wb.AutoConnect());
            Assert.Contains("powered off, unplugged", wb.LastConnectError);
        }

        // ── FF 08 identity: drain → settle → commit ───────────────────────

        [Fact]
        public void Identity_CommitsOnlyAfterSettling()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            int wheelChanged = 0;
            wb.WheelChanged += _ => wheelChanged++;

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 10;
            Assert.False(wb.UpdateIdentity());   // offered, still settling
            Assert.False(wb.IdentityStable);
            Assert.False(wb.WheelDetected);      // nothing committed yet
            Assert.Equal(0, wheelChanged);

            clock.T += 250;                      // quiet past the 200 ms window
            Assert.True(wb.UpdateIdentity());    // committed

            Assert.True(wb.IdentityStable);
            Assert.True(wb.WheelDetected);
            Assert.True(wb.WheelIdentified);
            Assert.True(wb.HasIdentity);
            Assert.Equal("PSWBMW", wb.WheelCode);
            Assert.Equal(0x0C, wb.BaseType);
            Assert.False(wb.IsHub);
            Assert.Null(wb.ModuleCode);
            Assert.Equal(1, wheelChanged);
        }

        [Fact]
        public void Identity_HubWithModule_DecodesBoth()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            CommitIdentity(wb, t, clock, Ff08(0x0C, HubWire("PHUB"), module: 0x02));

            Assert.True(wb.IsHub);
            Assert.Equal("PHUB", wb.WheelCode);
            Assert.Equal("PBMR", wb.ModuleCode);
            Assert.Equal(0x02, wb.ModuleWireCode);
        }

        [Fact]
        public void Identity_UnchangedRepush_DoesNotRecommit()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            int wheelChanged = 0;
            wb.WheelChanged += _ => wheelChanged++;

            // The firmware re-pushing the same identity (reconnect flap) must not
            // re-fire WheelChanged — that's what the settler's change detection is for.
            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 10;
            Assert.False(wb.UpdateIdentity());
            clock.T += 250;
            Assert.False(wb.UpdateIdentity());
            Assert.Equal(0, wheelChanged);
            Assert.True(wb.IdentityStable);
        }

        [Fact]
        public void Identity_ReEnableCadence_KeepsPushSubscriptionAlive()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            t.Sent.Clear();

            clock.T += 9_000;
            wb.UpdateIdentity();
            Assert.DoesNotContain(t.Sent, f => f[0] == 0xFF && f[1] == 0x08 && f[2] == 0x01); // not yet

            clock.T += 1_100;                    // past the 10 s re-enable cadence
            wb.UpdateIdentity();
            Assert.Contains(t.Sent, f => f[0] == 0xFF && f[1] == 0x08 && f[2] == 0x01);       // FF 08 enable
        }

        // ── SRM converter identity ────────────────────────────────────────

        [Fact]
        public void Srm_PingsOnlyWhileUnidentified()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0005, 64, 64, "CSL Elite"));
            Assert.True(wb.AutoConnect());
            t.Sent.Clear();

            clock.T += 1_100;                    // past the 1 s ping cadence
            wb.UpdateIdentity();
            Assert.Contains(t.Sent, ContainsDeFa);   // unidentified → DE FA ping

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            t.Sent.Clear();

            clock.T += 5_000;
            wb.UpdateIdentity();
            Assert.DoesNotContain(t.Sent, ContainsDeFa);  // identified → never pings again
        }

        [Fact]
        public void Srm_CommitsWhenFf08Silent()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0005, 64, 64, "CSL Elite"));
            Assert.True(wb.AutoConnect());

            int wheelChanged = 0;
            wb.WheelChanged += _ => wheelChanged++;

            t.Srm.Enqueue(SrmReply());
            clock.T += 10;
            wb.UpdateIdentity();

            Assert.True(wb.IsSrmConverter);
            Assert.Equal("CSSWFORMV2", wb.WheelCode);
            Assert.Equal("6.12", wb.SrmKitFirmware);
            Assert.True(wb.WheelDetected);
            Assert.True(wb.HasIdentity);         // BaseType stays 0; SRM flag carries it
            Assert.Equal(0, wb.BaseType);
            Assert.True(wb.IdentityStable);      // fixed identity — always settled
            Assert.Equal(1, wheelChanged);
        }

        [Fact]
        public void Srm_ElicitedReply_MustNotOverwriteCommittedFf08Identity()
        {
            // A read-only diagnostics run sends DE FA on demand; its 0xDD reply
            // must never mutate an identity the FF 08 path already committed.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            t.Srm.Enqueue(SrmReply());
            clock.T += 10;
            wb.UpdateIdentity();

            Assert.False(wb.IsSrmConverter);
            Assert.Equal("PSWBMW", wb.WheelCode);
            Assert.Null(wb.SrmKitFirmware);
        }

        [Fact]
        public void Srm_OnceCommitted_IsFixedForTheConnection()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0005, 64, 64, "CSL Elite"));
            Assert.True(wb.AutoConnect());

            t.Srm.Enqueue(SrmReply());
            clock.T += 10;
            wb.UpdateIdentity();
            Assert.True(wb.IsSrmConverter);

            // Later FF 08 frames (impossible from a real kit, but defensive) and
            // further ticks change nothing: the converter identity is a one-shot.
            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 500;
            Assert.False(wb.UpdateIdentity());
            Assert.True(wb.IsSrmConverter);
            Assert.Equal("CSSWFORMV2", wb.WheelCode);
        }

        [Fact]
        public void Srm_NeverPingsAgain_AfterRimPull()
        {
            // Regression: pulling the wheel mid-operation made WheelDetected false
            // again, which re-armed the DE FA ping loop on a base FF 08 had
            // already identified — and since the query's report-id-0x00 frame is
            // rejected by some bases' HID driver, the transport logged a write
            // warning every second until a wheel came back. Once ANY identity has
            // committed, the connection must never ping again.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            CommitIdentity(wb, t, clock, Ff08(0x0C, 0x00));   // rim pulled
            Assert.False(wb.WheelDetected);
            Assert.True(wb.HasIdentity);                      // base still identified
            t.Sent.Clear();

            for (int i = 0; i < 10; i++)
            {
                clock.T += 1_100;                             // well past the ping cadence
                wb.UpdateIdentity();
            }

            Assert.DoesNotContain(t.Sent, ContainsDeFa);
        }

        [Fact]
        public void Srm_ElicitedReply_WithRimPulled_DoesNotConvertTheBase()
        {
            // A diagnostics run sends DE FA on demand; with the rim pulled the
            // WheelDetected guard alone no longer protects the committed base
            // identity — the 0xDD reply must still be ignored, or a read-only
            // diagnostics capture would turn a genuine base into an SRM kit.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            CommitIdentity(wb, t, clock, Ff08(0x0C, 0x00));   // rim pulled

            t.Srm.Enqueue(SrmReply());
            clock.T += 10;
            wb.UpdateIdentity();

            Assert.False(wb.IsSrmConverter);
            Assert.Equal(0x0C, wb.BaseType);                  // base identity intact
            Assert.Null(wb.SrmKitFirmware);
        }

        // ── Disconnect: the full state reset ─────────────────────────────

        [Fact]
        public void Disconnect_ResetsEveryIdentityProperty()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, HubWire("PHUB"), module: 0x02));
            Assert.NotNull(wb.LastRawReport);

            wb.Disconnect();

            // One missed field here is exactly a #37-class stale-state bug, so
            // every public identity property is pinned.
            Assert.False(wb.IsConnected);
            Assert.Equal(0, wb.ConnectedProductId);
            Assert.Null(wb.ProductName);
            Assert.False(wb.WheelDetected);
            Assert.False(wb.WheelIdentified);
            Assert.Null(wb.WheelCode);
            Assert.Equal(0, wb.WheelWireCode);
            Assert.False(wb.IsHub);
            Assert.Null(wb.ModuleCode);
            Assert.Equal(0, wb.ModuleWireCode);
            Assert.Equal(0, wb.BaseType);
            Assert.Null(wb.BaseCode);
            Assert.False(wb.IsSrmConverter);
            Assert.Null(wb.SrmKitFirmware);
            Assert.False(wb.HasIdentity);
            Assert.True(wb.IdentityStable);
            Assert.Same(WheelCapabilities.None, wb.CurrentCapabilities);
            Assert.Null(wb.LastRawReport);
            Assert.Equal("No wheel attached", wb.DisplayName);
        }

        // ── Rim swap ───────────────────────────────────────────────────────

        [Fact]
        public void RimSwap_CommitsTheNewWheel_AndFiresWheelChangedAgain()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            int wheelChanged = 0;
            wb.WheelChanged += _ => wheelChanged++;

            string other = FanatecDeviceTables.Wheels.Values.First(v => v != "PSWBMW");
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire(other)));

            Assert.Equal(other, wb.WheelCode);
            Assert.Equal(1, wheelChanged);
        }

        [Fact]
        public void RimSwapFlap_RevertingWithinTheSettleWindow_CommitsNothing()
        {
            // The firmware's transient reconnect flap: A → B → A inside the settle
            // window must ride out silently — no commit, no WheelChanged, and the
            // identity must still read as A once stable again.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            int wheelChanged = 0;
            wb.WheelChanged += _ => wheelChanged++;

            string other = FanatecDeviceTables.Wheels.Values.First(v => v != "PSWBMW");
            t.Identity.Enqueue(Ff08(0x0C, WheelWire(other)));      // flap out...
            clock.T += 50;
            wb.UpdateIdentity();
            Assert.False(wb.IdentityStable);

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));   // ...and back
            clock.T += 50;
            wb.UpdateIdentity();

            clock.T += 250;                                        // quiet again
            wb.UpdateIdentity();

            Assert.True(wb.IdentityStable);
            Assert.Equal("PSWBMW", wb.WheelCode);
            Assert.Equal(0, wheelChanged);
        }

        // ── Unrecognized hardware / diagnostics contract ──────────────────

        [Fact]
        public void UnrecognizedWire_DetectedButNotIdentified_WithReportableName()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            byte unknown = (byte)Enumerable.Range(1, 254)
                .First(i => !FanatecDeviceTables.Wheels.ContainsKey((byte)i)
                         && !FanatecDeviceTables.Hubs.ContainsKey((byte)i)
                         && i != 0xFF);
            CommitIdentity(wb, t, clock, Ff08(0x0C, unknown));

            Assert.True(wb.WheelDetected);       // something IS attached...
            Assert.False(wb.WheelIdentified);    // ...but we can't name it
            Assert.Null(wb.WheelCode);
            Assert.Equal(unknown, wb.WheelWireCode);
            Assert.Contains("Unknown (0x" + unknown.ToString("X2"), wb.DisplayName);
            Assert.Same(WheelCapabilities.None, wb.CurrentCapabilities);
        }

        [Fact]
        public void ExtInfoWire_0xFF_GetsTheDedicatedReportMarker()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            CommitIdentity(wb, t, clock, Ff08(0x0C, 0xFF));

            Assert.Contains("EXT_INFO", wb.DisplayName);
        }

        [Fact]
        public void LastRawReport_UpdatesPerReading_EvenBeforeAnyCommit()
        {
            // Diagnostics contract: a capture taken on a sitting-still
            // unrecognized wheel must reflect the live frame — retention happens
            // on every drained reading, not only on settled commits.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            Assert.Null(wb.LastRawReport);

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 10;
            wb.UpdateIdentity();                 // offered — NOT yet committed

            Assert.False(wb.WheelDetected);
            Assert.NotNull(wb.LastRawReport);
            Assert.Equal(0xFF, wb.LastRawReport[0]);
        }

        // ── Connect-time initial read ──────────────────────────────────────

        [Fact]
        public void ConnectTimeInitialRead_SeedsIdentity_FromTheEnableTriggerReply()
        {
            // ReadInitial elicits with enable+trigger and flushes stale frames
            // first, so the reply arrives via respond-on-send, as from hardware.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            t.RespondOnSend.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.True(wb.AutoConnect());       // reply consumed at connect

            clock.T += 250;                      // settle the connect-time reading
            Assert.True(wb.UpdateIdentity());
            Assert.Equal("PSWBMW", wb.WheelCode);
        }

        // ── Capability resolution hooks ────────────────────────────────────

        [Fact]
        public void ProfileOverrideResolver_RedirectsCapabilityResolution()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            string? requestedKey = null;
            wb.ProfileOverrideResolver = key => { requestedKey = key; return "PHUB"; };

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.Equal("PSWBMW", requestedKey);            // asked with the match key
            Assert.Equal("PHUB", wb.CurrentCapabilities.Profile?.Id);   // override won
        }

        [Fact]
        public void CommittedIdentity_ResolvesTheMatchingProfileCapabilities()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.Equal("PSWBMW", wb.CurrentCapabilities.Profile?.Id);
            Assert.Equal(wb.CurrentCapabilities.Name, wb.DisplayName);
        }

        // ── ITM subscription buffering ─────────────────────────────────────

        [Fact]
        public void ItmReports_BufferedDuringDrain_HandedOffOnce()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, 0x11 });
            t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, 0x22 });
            clock.T += 10;
            wb.UpdateIdentity();

            var drained = new List<byte[]>();
            wb.DrainItmReports(drained.Add);
            Assert.Equal(2, drained.Count);
            Assert.Equal(0x11, drained[0][3]);
            Assert.Equal(0x22, drained[1][3]);

            wb.DrainItmReports(drained.Add);     // buffer cleared by the hand-off
            Assert.Equal(2, drained.Count);
        }

        [Fact]
        public void ItmReports_StillDrained_OnAnSrmConverter()
        {
            // Regression (v0.6.0 field report, SRM kit + Podium Bentley GT3): once a
            // converter identity committed, UpdateIdentity short-circuited BEFORE the ITM
            // drain. The kit's FF 05 subscription pushes then sat unread in the transport
            // queue forever, so the ITM lifecycle never got its confirming push — bring-up
            // ran the whole recovery ladder and parked in Unavailable. Identity is a
            // one-shot on a converter; the ITM channel is not.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0005, 64, 64, "CSL Elite"));
            Assert.True(wb.AutoConnect());

            t.Srm.Enqueue(SrmReply());
            clock.T += 10;
            wb.UpdateIdentity();
            Assert.True(wb.IsSrmConverter);

            t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, 0x11 });
            clock.T += 10;
            Assert.False(wb.UpdateIdentity());   // no identity change — but the ITM drain still runs

            var drained = new List<byte[]>();
            wb.DrainItmReports(drained.Add);
            Assert.Single(drained);
            Assert.Equal(0x11, drained[0][3]);
        }

        [Fact]
        public void ItmReports_BufferBounded_DropsOldestWhenFull()
        {
            // Cap is 32; a stalled consumer (e.g. during the wizard) must cost the
            // OLDEST reports — only the latest subscription state matters.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            // The per-tick drain is bounded, so feed across several ticks.
            for (int i = 0; i < 40; i++)
            {
                t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, (byte)i });
                clock.T += 10;
                wb.UpdateIdentity();
            }

            var drained = new List<byte[]>();
            wb.DrainItmReports(drained.Add);

            Assert.Equal(32, drained.Count);
            Assert.Equal(39, drained[drained.Count - 1][3]);   // newest kept
            Assert.Equal(8, drained[0][3]);                    // oldest 8 dropped
        }

        [Fact]
        public void WheelChange_ClearsBufferedItmReports()
        {
            // A wheel/hub change invalidates buffered ITM subscription reports: they were
            // pushed by the PREVIOUS attachment, and feeding them to the restarted ITM
            // lifecycle could falsely confirm the new wheel's bring-up against the old page.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("GTSWX")));   // an ITM wheel

            t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, 0x11 });   // old wheel's push
            clock.T += 10;
            wb.UpdateIdentity();                                     // buffered

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("CSSWFORMV3")));   // rim swap

            var drained = new List<byte[]>();
            wb.DrainItmReports(drained.Add);
            Assert.Empty(drained);   // the stale pre-swap report was cleared
        }

        [Fact]
        public void RefreshCapabilities_DoesNotCountAsAWheelChange()
        {
            // WheelChangeCount drives the ITM lifecycle's cold-restart. A profile-store
            // re-resolution (override save, profile delete) fires WheelChanged too, but the
            // physical attachment is unchanged — bumping the count would needlessly cold-
            // restart the ITM display because the user saved an unrelated setting.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("GTSWX")));

            int afterIdentity = wb.WheelChangeCount;
            Assert.True(afterIdentity > 0);   // the identity commit counted

            wb.RefreshCapabilities();
            Assert.Equal(afterIdentity, wb.WheelChangeCount);   // the re-resolution did not

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("CSSWFORMV3")));   // a real swap
            Assert.Equal(afterIdentity + 1, wb.WheelChangeCount);
        }

        // ── Guard rails ────────────────────────────────────────────────────

        [Fact]
        public void UpdateIdentity_WhileDisconnected_IsANoOp()
        {
            var wb = Make(out var t, out var bus, out var clock);
            Assert.False(wb.UpdateIdentity());
            Assert.Empty(t.Sent);
        }

        [Fact]
        public void Disconnect_ThenReconnect_SameWheelCommitsAgain()
        {
            // The settler is reset on disconnect, so the SAME rim must re-commit
            // (and re-fire WheelChanged) on the next connection — encoders rely
            // on that event to force a full resend after firmware reset the LEDs.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            wb.Disconnect();
            Assert.True(wb.AutoConnect());

            int wheelChanged = 0;
            wb.WheelChanged += _ => wheelChanged++;
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            Assert.Equal(1, wheelChanged);
        }
    }
}
