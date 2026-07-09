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

            public bool SendCol03(byte[] data)
            {
                Sent.Add((byte[])data.Clone());
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
