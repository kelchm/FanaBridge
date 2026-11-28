using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Logging;
using FanaBridge.Tests.TestDoubles;
using FanaBridge.Core.Transport;
using Xunit;

namespace FanaBridge.Tests.Core.Devices
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

        private sealed class ObservingReportStream : IReportStream
        {
            private readonly IReportStream _inner;
            private readonly Action _onTryRead;

            public ObservingReportStream(IReportStream inner, Action onTryRead)
            {
                _inner = inner;
                _onTryRead = onTryRead;
            }

            public int TryRead(byte[] buffer, int timeoutMs)
            {
                _onTryRead();
                return _inner.TryRead(buffer, timeoutMs);
            }

            public void Flush() => _inner.Flush();
            public IDisposable Tap(Action<byte[]> onFrame) => _inner.Tap(onFrame);
        }

        private sealed class FakeTransport : IConnectableTransport
        {
            public bool ConnectResult = true;
            public int ConnectedPid;
            public int DisconnectCount;
            public bool Connected;
            public Action? OnDisconnect;
            public Action? OnItmTryRead;

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

            public void Disconnect()
            {
                OnDisconnect?.Invoke();
                DisconnectCount++;
                Connected = false;
            }
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
            public IReportStream ItmReports => OnItmTryRead == null
                ? (IReportStream)Itm
                : new ObservingReportStream(Itm, OnItmTryRead);
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
            wb.WheelChanged += () => wheelChanged++;

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
            wb.WheelChanged += () => wheelChanged++;

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
            wb.WheelChanged += () => wheelChanged++;

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
            wb.WheelChanged += () => wheelChanged++;

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
            wb.WheelChanged += () => wheelChanged++;

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
        public void SetProfileOverrides_RedirectsCapabilityResolution()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            // A second profile for the same wheel, so the override has something
            // to choose that auto-resolution would not.
            using var profiles = new TempUserProfiles();
            profiles.Write("fb81-wheelbase-override.json", "PSWBMW_OVERRIDE", "PSWBMW");
            profiles.Reload();

            wb.SetProfileOverrides(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PSWBMW"] = "PSWBMW_OVERRIDE:User",
            });

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.Equal("PSWBMW_OVERRIDE", wb.CurrentCapabilities.Profile?.Id);   // override won
        }

        [Fact]
        public void SetProfileOverrides_ForADifferentWheel_IsIgnored()
        {
            // Registration ignores an override whose match differs from the
            // attachment; runtime must too, or the active profile would differ
            // from the one the device's LED editor was sized from.
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            wb.SetProfileOverrides(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PSWBMW"] = "PHUB:BuiltIn",
            });

            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.Equal("PSWBMW", wb.CurrentCapabilities.Profile?.Id);
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
            wb.WheelChanged += () => wheelChanged++;
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            Assert.Equal(1, wheelChanged);
        }

        // ── Stage 1a: atomic snapshot, leaf state lock, epochs, and purity (#137) ──

        [Fact]
        public void AutoConnect_WithInitialReading_PublishesUnstableUntilSettle()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "ClubSport DD"));
            t.RespondOnSend.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.True(wb.AutoConnect());

            // Adopt offered the initial reading to the settler, so the published
            // snapshot must already report Connected=true and IdentityStable=false
            // before the first UpdateIdentity tick.
            var afterConnect = wb.Snapshot;
            Assert.True(afterConnect.Connected);
            Assert.Equal(0x0020, afterConnect.ProductId);
            Assert.Equal("ClubSport DD", afterConnect.ProductName);
            Assert.Equal(1, afterConnect.ConnectionEpoch);
            Assert.Equal(1, afterConnect.AttachmentEpoch);
            Assert.False(afterConnect.IdentityStable);
            Assert.False(afterConnect.WheelDetected);
            Assert.False(afterConnect.HasIdentity);
            Assert.Same(WheelCapabilities.None, afterConnect.Capabilities);

            clock.T += 250;
            Assert.True(wb.UpdateIdentity());

            var committed = wb.Snapshot;
            Assert.True(committed.Connected);
            Assert.True(committed.IdentityStable);
            Assert.Equal(1, committed.ConnectionEpoch);
            Assert.Equal(2, committed.AttachmentEpoch);
            Assert.Equal(1, committed.WheelChangeCount);
            Assert.Equal("PSWBMW", committed.WheelCode);
            Assert.Equal("PSWBMW", committed.Capabilities.Profile?.Id);
        }

        [Fact]
        public void CommitIdentity_WhileSettling_KeepsPreviousIdentityAndCapabilitiesPaired()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, HubWire("PHUB"), module: 0x01));

            var initial = wb.Snapshot;
            Assert.True(initial.IdentityStable);
            Assert.Equal("PHUB", initial.WheelCode);
            Assert.Equal("PBME", initial.ModuleCode);
            Assert.Equal("PHUB_PBME", initial.Capabilities.Profile?.Id);

            // A changed reading starts settling: IdentityStable turns false while
            // the previously committed identity and capabilities stay paired in the
            // same attachment epoch.
            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 10;
            Assert.False(wb.UpdateIdentity());

            var settling = wb.Snapshot;
            Assert.False(settling.IdentityStable);
            Assert.Equal(initial.ConnectionEpoch, settling.ConnectionEpoch);
            Assert.Equal(initial.AttachmentEpoch, settling.AttachmentEpoch);
            Assert.Equal(initial.WheelChangeCount, settling.WheelChangeCount);
            Assert.Equal("PHUB", settling.WheelCode);
            Assert.Equal("PBME", settling.ModuleCode);
            Assert.Equal("PHUB_PBME", settling.Capabilities.Profile?.Id);

            // Once the settle window elapses, identity, capabilities, stability,
            // AttachmentEpoch, and WheelChangeCount publish together in one snapshot.
            clock.T += 250;
            Assert.True(wb.UpdateIdentity());

            var swapped = wb.Snapshot;
            Assert.True(swapped.IdentityStable);
            Assert.Equal(initial.ConnectionEpoch, swapped.ConnectionEpoch);
            Assert.Equal(initial.AttachmentEpoch + 1, swapped.AttachmentEpoch);
            Assert.Equal(initial.WheelChangeCount + 1, swapped.WheelChangeCount);
            Assert.Equal("PSWBMW", swapped.WheelCode);
            Assert.Null(swapped.ModuleCode);
            Assert.Equal("PSWBMW", swapped.Capabilities.Profile?.Id);
        }

        [Fact]
        public void CommitSrmIdentity_PublishesAtomicallyAndAdvancesAttachmentEpoch()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0005, 64, 64, "CSL Elite"));
            Assert.True(wb.AutoConnect());

            var before = wb.Snapshot;
            Assert.True(before.Connected);
            Assert.False(before.IsSrmConverter);
            Assert.False(before.WheelDetected);
            Assert.Same(WheelCapabilities.None, before.Capabilities);

            t.Srm.Enqueue(SrmReply());
            clock.T += 10;
            wb.UpdateIdentity();

            var after = wb.Snapshot;
            Assert.True(after.Connected);
            Assert.Equal(before.ConnectionEpoch, after.ConnectionEpoch);
            Assert.Equal(before.AttachmentEpoch + 1, after.AttachmentEpoch);
            Assert.Equal(before.WheelChangeCount + 1, after.WheelChangeCount);
            Assert.True(after.IsSrmConverter);
            Assert.True(after.IdentityStable);
            Assert.True(after.WheelDetected);
            Assert.Equal("CSSWFORMV2", after.WheelCode);
            Assert.Equal("CSSWFORMV2", after.Capabilities.Profile?.Id);
        }

        [Fact]
        public void RimSwapFlap_EndingUnchanged_BumpsAttachmentEpochWithoutWheelChanged()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            var before = wb.Snapshot;
            int wheelChanged = 0;
            wb.WheelChanged += () => wheelChanged++;

            // Flap to another wheel and back before the settle window expires.
            t.Identity.Enqueue(Ff08(0x0C, WheelWire("CSSWFORMV2")));
            clock.T += 50;
            Assert.False(wb.UpdateIdentity());
            Assert.False(wb.Snapshot.IdentityStable);
            Assert.Equal(before.AttachmentEpoch, wb.Snapshot.AttachmentEpoch);

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 50;
            Assert.False(wb.UpdateIdentity());

            clock.T += 250;
            Assert.False(wb.UpdateIdentity());

            var after = wb.Snapshot;
            Assert.True(after.IdentityStable);
            Assert.Equal(before.ConnectionEpoch, after.ConnectionEpoch);
            Assert.Equal(before.AttachmentEpoch + 1, after.AttachmentEpoch);
            Assert.Equal(before.WheelChangeCount, after.WheelChangeCount);
            Assert.Equal("PSWBMW", after.WheelCode);
            Assert.Equal(0, wheelChanged);
        }

        [Fact]
        public void ChangedIdentityReading_PublishesUnstableBeforeItmDrainInSameTick()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            Assert.True(wb.Snapshot.IdentityStable);

            var observedStableDuringItmDrain = new List<bool>();
            t.OnItmTryRead = () => observedStableDuringItmDrain.Add(wb.Snapshot.IdentityStable);

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("CSSWFORMV2")));
            t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, 0x02 });
            clock.T += 10;

            Assert.False(wb.UpdateIdentity());
            Assert.NotEmpty(observedStableDuringItmDrain);
            Assert.All(observedStableDuringItmDrain, stable => Assert.False(stable));
            Assert.False(wb.Snapshot.IdentityStable);
            Assert.Equal("PSWBMW", wb.Snapshot.WheelCode);
        }

        [Fact]
        public void UnchangedSettle_PreservesStagedItmReports()
        {
            // Until Stage 4 restarts ITM on every attachment epoch, an unchanged
            // settle does not restart the ITM lifecycle and must not drop staged
            // ITM subscription reports (Stage 4 flips both together).
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            var before = wb.Snapshot;

            // Stage an ITM report, then flap out and settle back unchanged.
            byte[] staged = new byte[] { 0xFF, 0x05, 0x01, 0x0A };
            t.Itm.Enqueue(staged);
            clock.T += 10;
            Assert.False(wb.UpdateIdentity());

            t.Identity.Enqueue(Ff08(0x0C, 0x00));
            clock.T += 10;
            Assert.False(wb.UpdateIdentity());
            Assert.False(wb.Snapshot.IdentityStable);

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 50;
            Assert.False(wb.UpdateIdentity());

            clock.T += 250;
            Assert.False(wb.UpdateIdentity());

            var after = wb.Snapshot;
            Assert.True(after.IdentityStable);
            Assert.Equal(before.AttachmentEpoch + 1, after.AttachmentEpoch);
            Assert.Equal(before.WheelChangeCount, after.WheelChangeCount);

            var drained = new List<byte[]>();
            wb.DrainItmReports(drained.Add);
            var report = Assert.Single(drained);
            Assert.Equal(staged, report);
        }

        [Fact]
        public void Disconnect_PublishesDisconnectedSnapshotBeforeClosingTransport()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "ClubSport DD"));
            Assert.True(wb.AutoConnect());
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            var before = wb.Snapshot;
            Assert.True(before.Connected);

            WheelbaseSnapshot? observedDuringTransportDisconnect = null;
            t.OnDisconnect = () => observedDuringTransportDisconnect = wb.Snapshot;

            wb.Disconnect();

            Assert.NotNull(observedDuringTransportDisconnect);
            Assert.False(observedDuringTransportDisconnect!.Connected);
            Assert.Equal(0, observedDuringTransportDisconnect.ProductId);
            Assert.Null(observedDuringTransportDisconnect.ProductName);
            Assert.False(observedDuringTransportDisconnect.WheelDetected);
            Assert.Same(WheelCapabilities.None, observedDuringTransportDisconnect.Capabilities);
            Assert.Equal(before.ConnectionEpoch + 1, observedDuringTransportDisconnect.ConnectionEpoch);
            Assert.Equal(before.AttachmentEpoch + 1, observedDuringTransportDisconnect.AttachmentEpoch);
            Assert.Equal(before.WheelChangeCount, observedDuringTransportDisconnect.WheelChangeCount);
        }

        private sealed class LockCheckingLogSink : ILogSink
        {
            private readonly Func<bool> _anyLockHeld;
            public int InfoCount;
            public int WarnCount;
            public int Violations;

            public LockCheckingLogSink(Func<bool> anyLockHeld) => _anyLockHeld = anyLockHeld;

            public void Info(string message)
            {
                Interlocked.Increment(ref InfoCount);
                if (_anyLockHeld()) Interlocked.Increment(ref Violations);
            }

            public void Warn(string message)
            {
                Interlocked.Increment(ref WarnCount);
                if (_anyLockHeld()) Interlocked.Increment(ref Violations);
            }

            public void Error(string message)
            {
                if (_anyLockHeld()) Interlocked.Increment(ref Violations);
            }

            public void Debug(string message)
            {
                if (_anyLockHeld()) Interlocked.Increment(ref Violations);
            }
        }

        [Fact]
        public void StateAndItmLocks_NeverHeldDuringLoggingOrWheelChanged()
        {
            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));

            int wheelChangedCalls = 0;
            int wheelChangedViolations = 0;
            wb.WheelChanged += () =>
            {
                wheelChangedCalls++;
                if (wb.IsStateLockHeldForTest || wb.IsItmLockHeldForTest)
                    wheelChangedViolations++;
            };

            var previousSink = Log.Sink;
            var sink = new LockCheckingLogSink(() => wb.IsStateLockHeldForTest || wb.IsItmLockHeldForTest);
            Log.Sink = sink;
            try
            {
                Assert.True(wb.AutoConnect());

                // Mismatched override forces Log.Warn on both commit and refresh.
                wb.SetProfileOverrides(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PSWBMW"] = "PHUB:BuiltIn",
                });
                CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
                wb.RefreshCapabilities();

                // Overflow the 32-entry ITM buffer to trigger the buffer-full Log.Warn.
                for (int i = 0; i < 40; i++)
                {
                    t.Itm.Enqueue(new byte[] { 0xFF, 0x05, 0x01, (byte)i });
                    clock.T += 10;
                    wb.UpdateIdentity();
                }

                wb.Disconnect();

                // Also exercise SRM commit logging and WheelChanged.
                Assert.True(wb.AutoConnect());
                t.Srm.Enqueue(SrmReply());
                clock.T += 10;
                wb.UpdateIdentity();
            }
            finally
            {
                Log.Sink = previousSink;
            }

            Assert.True(sink.InfoCount > 0);
            Assert.True(sink.WarnCount >= 3); // 2 ignored-override warnings + 1 ITM buffer-full warning
            Assert.Equal(3, wheelChangedCalls); // FF 08 commit, refresh, SRM commit
            Assert.Equal(0, sink.Violations);
            Assert.Equal(0, wheelChangedViolations);
        }

        [Fact]
        public void CommitThenRefresh_AndRefreshThenCommit_BothConvergeToLatestOverridesAndCatalog()
        {
            using var profiles = new TempUserProfiles();
            profiles.Write("fb137-stage1a-ordering.json", "PSWBMW_STAGE1A", "PSWBMW", revLeds: 5);
            profiles.Reload();

            var userOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PSWBMW"] = "PSWBMW_STAGE1A:User",
            };

            // Ordering 1: commit runs first (with auto/built-in), then override swap + refresh runs last.
            {
                var wb = Make(out var t, out var bus, out var clock);
                bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
                Assert.True(wb.AutoConnect());

                CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
                var afterCommit = wb.Snapshot;
                Assert.Equal("PSWBMW", afterCommit.Capabilities.Profile?.Id);

                wb.SetProfileOverrides(userOverride);
                wb.RefreshCapabilities();

                var afterRefresh = wb.Snapshot;
                Assert.Equal("PSWBMW_STAGE1A", afterRefresh.Capabilities.Profile?.Id);
                Assert.Equal(5, afterRefresh.Capabilities.AllLedCount);
                Assert.Equal(afterCommit.ConnectionEpoch, afterRefresh.ConnectionEpoch);
                Assert.Equal(afterCommit.AttachmentEpoch, afterRefresh.AttachmentEpoch);
                Assert.Equal(afterCommit.WheelChangeCount, afterRefresh.WheelChangeCount);
            }

            // Ordering 2: override swap + refresh runs while a swap to PSWBMW is settling, then commit runs last.
            {
                var wb = Make(out var t, out var bus, out var clock);
                bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
                Assert.True(wb.AutoConnect());
                CommitIdentity(wb, t, clock, Ff08(0x0C, HubWire("PHUB"), module: 0x01));

                t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
                clock.T += 10;
                Assert.False(wb.UpdateIdentity());
                Assert.False(wb.Snapshot.IdentityStable);

                wb.SetProfileOverrides(userOverride);
                wb.RefreshCapabilities();

                // Refresh preserved IdentityStable=false while PSWBMW was still settling.
                Assert.False(wb.Snapshot.IdentityStable);
                Assert.Equal("PHUB", wb.Snapshot.WheelCode);

                clock.T += 250;
                Assert.True(wb.UpdateIdentity());

                var afterCommit = wb.Snapshot;
                Assert.True(afterCommit.IdentityStable);
                Assert.Equal("PSWBMW", afterCommit.WheelCode);
                Assert.Equal("PSWBMW_STAGE1A", afterCommit.Capabilities.Profile?.Id);
                Assert.Equal(5, afterCommit.Capabilities.AllLedCount);
            }
        }

        [Fact]
        public void OverrideEdit_WithNoWheelAttached_IsHonouredByNextCommit()
        {
            using var profiles = new TempUserProfiles();
            profiles.Write("fb137-stage1a-nowheel.json", "PSWBMW_NOWHEEL", "PSWBMW", revLeds: 4);
            profiles.Reload();

            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            int wheelChanged = 0;
            wb.WheelChanged += () => wheelChanged++;

            // Edit overrides and refresh while no wheel is attached.
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PSWBMW"] = "PSWBMW_NOWHEEL:User",
            };
            wb.SetProfileOverrides(overrides);
            wb.RefreshCapabilities();

            // Mutating the caller's dictionary afterwards must not affect the wheelbase's copy.
            overrides["PSWBMW"] = "PSWBMW:BuiltIn";

            Assert.Equal(0, wheelChanged);
            Assert.False(wb.WheelDetected);
            Assert.Same(WheelCapabilities.None, wb.CurrentCapabilities);

            // Next wheel commit must honour the override stored while no wheel was attached.
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            Assert.Equal("PSWBMW_NOWHEEL", wb.Snapshot.Capabilities.Profile?.Id);
            Assert.Equal(4, wb.Snapshot.Capabilities.AllLedCount);
            Assert.Equal(1, wheelChanged);
        }

        [Fact]
        public void ProfileStoreReload_LeavesPreviouslyPublishedSnapshotProfileUnchanged()
        {
            using var profiles = new TempUserProfiles();
            profiles.Write("fb137-stage1a-reload.json", "PSWBMW_RELOAD", "PSWBMW", revLeds: 4, name: "BMW Reload V1");
            profiles.Reload();

            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            wb.SetProfileOverrides(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PSWBMW"] = "PSWBMW_RELOAD:User",
            });
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));

            var beforeReload = wb.Snapshot;
            Assert.Equal("BMW Reload V1", beforeReload.Capabilities.Name);
            Assert.Equal(4, beforeReload.Capabilities.AllLedCount);
            Assert.Equal("BMW Reload V1", beforeReload.Capabilities.Profile.Name);
            Assert.Equal(4, beforeReload.Capabilities.Profile.Leds.Count);

            // Rewrite the profile on disk and reload the store.
            profiles.Write("fb137-stage1a-reload.json", "PSWBMW_RELOAD", "PSWBMW", revLeds: 8, name: "BMW Reload V2");
            profiles.Reload();

            // The previously published snapshot and its frozen profile object are completely unchanged.
            Assert.Equal("BMW Reload V1", beforeReload.Capabilities.Name);
            Assert.Equal(4, beforeReload.Capabilities.AllLedCount);
            Assert.Equal("BMW Reload V1", beforeReload.Capabilities.Profile.Name);
            Assert.Equal(4, beforeReload.Capabilities.Profile.Leds.Count);
            Assert.NotSame(beforeReload.Capabilities.Profile, WheelProfileStore.GetById("PSWBMW_RELOAD"));

            // Refresh picks up the reloaded profile in a new snapshot while the old snapshot stays V1.
            wb.RefreshCapabilities();
            var afterRefresh = wb.Snapshot;
            Assert.Equal("BMW Reload V2", afterRefresh.Capabilities.Name);
            Assert.Equal(8, afterRefresh.Capabilities.AllLedCount);
            Assert.Equal("BMW Reload V1", beforeReload.Capabilities.Profile.Name);
            Assert.Equal(4, beforeReload.Capabilities.Profile.Leds.Count);
        }

        [Fact]
        public async Task ConcurrentCommitsRefreshesReloadsAndReaders_EverySnapshotIsSelfConsistentAndFinalMatchesInputs()
        {
            using var profiles = new TempUserProfiles();
            profiles.Write("fb137-stage1a-stress-a.json", "PSWBMW_STRESS_A", "PSWBMW", revLeds: 4, name: "BMW Stress A");
            profiles.Write("fb137-stage1a-stress-b.json", "PSWBMW_STRESS_B", "PSWBMW", revLeds: 7, name: "BMW Stress B");
            profiles.Write("fb137-stage1a-stress-hub.json", "PHUB_PBME_STRESS", "PHUB", moduleType: "PBME", revLeds: 6, name: "Hub Stress");
            profiles.Reload();

            var wb = Make(out var t, out var bus, out var clock);
            bus.Devices.Add(new HidDeviceInfo(0x0020, 64, 64, "Base"));
            Assert.True(wb.AutoConnect());

            var frames = new[]
            {
                Ff08(0x0C, WheelWire("PSWBMW")),
                Ff08(0x0C, HubWire("PHUB"), module: 0x01),
                Ff08(0x0C, WheelWire("CSSWFORMV2")),
            };

            var overrideSets = new[]
            {
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PSWBMW"] = "PSWBMW_STRESS_A:User",
                    ["PHUB_PBME"] = "PHUB_PBME_STRESS:User",
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PSWBMW"] = "PSWBMW_STRESS_B:User",
                },
            };

            using var start = new ManualResetEventSlim(false);
            using var updatesDone = new ManualResetEventSlim(false);
            using var done = new ManualResetEventSlim(false);
            var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
            long observedCount = 0;

            Action readerLoop = () =>
            {
                long lastConnEpoch = -1;
                long lastAttachEpoch = -1;
                int lastWheelChangeCount = -1;

                start.Wait();
                while (!done.IsSet)
                {
                    var s = wb.Snapshot;
                    Interlocked.Increment(ref observedCount);

                    if (s.ConnectionEpoch < lastConnEpoch)
                        violations.Enqueue($"ConnectionEpoch went backwards: {s.ConnectionEpoch} < {lastConnEpoch}");
                    if (s.AttachmentEpoch < lastAttachEpoch)
                        violations.Enqueue($"AttachmentEpoch went backwards: {s.AttachmentEpoch} < {lastAttachEpoch}");
                    if (s.WheelChangeCount < lastWheelChangeCount)
                        violations.Enqueue($"WheelChangeCount went backwards: {s.WheelChangeCount} < {lastWheelChangeCount}");
                    lastConnEpoch = s.ConnectionEpoch;
                    lastAttachEpoch = s.AttachmentEpoch;
                    lastWheelChangeCount = s.WheelChangeCount;

                    if (!s.Connected)
                    {
                        if (s.WheelDetected || s.HasIdentity || s.ProductId != 0 || s.ProductName != null
                            || !ReferenceEquals(s.Capabilities, WheelCapabilities.None) || !s.IdentityStable)
                        {
                            violations.Enqueue($"Disconnected snapshot had dirty state: detected={s.WheelDetected}, pid={s.ProductId}, caps={s.Capabilities.Name}");
                        }
                        continue;
                    }

                    if (s.ProductId != 0x0020 || s.ProductName != "Base")
                        violations.Enqueue($"Connected snapshot had wrong base identity: pid={s.ProductId}, name={s.ProductName}");

                    if (!s.WheelDetected)
                    {
                        if (s.WheelCode != null || s.ModuleCode != null || !ReferenceEquals(s.Capabilities, WheelCapabilities.None))
                            violations.Enqueue($"No-wheel snapshot had non-empty identity/caps: wheel={s.WheelCode}, caps={s.Capabilities.Name}");
                        continue;
                    }

                    var profile = s.Capabilities.Profile;
                    if (profile == null)
                    {
                        violations.Enqueue($"Known wheel {s.WheelCode}/{s.ModuleCode} had null profile");
                        continue;
                    }

                    if (!string.Equals(profile.Match?.WheelType, s.WheelCode, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(profile.Match?.ModuleType ?? "", s.ModuleCode ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Enqueue(
                            $"Mixed snapshot: identity={s.WheelCode}/{s.ModuleCode} paired with profile={profile.Id} ({profile.Match?.WheelType}/{profile.Match?.ModuleType})");
                    }

                    if (s.WheelCode == "PHUB" && (!s.IsHub || s.ModuleCode != "PBME"))
                        violations.Enqueue($"PHUB snapshot had mismatched hub/module: isHub={s.IsHub}, mod={s.ModuleCode}");
                    if (s.WheelCode != "PHUB" && (s.IsHub || s.ModuleCode != null))
                        violations.Enqueue($"Non-hub snapshot had hub/module: wheel={s.WheelCode}, isHub={s.IsHub}, mod={s.ModuleCode}");
                }
            };

            var reader1 = Task.Run(readerLoop);
            var reader2 = Task.Run(readerLoop);

            var updateWriter = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    for (int i = 0; i < 600; i++)
                    {
                        if (i > 0 && i % 100 == 0)
                        {
                            wb.Disconnect();
                            wb.AutoConnect();
                        }

                        t.Identity.Enqueue(frames[i % frames.Length]);
                        clock.T += 10;
                        wb.UpdateIdentity();
                        clock.T += 250;
                        wb.UpdateIdentity();
                    }
                }
                finally
                {
                    updatesDone.Set();
                }
            });

            var refreshWriter = Task.Run(() =>
            {
                start.Wait();
                int i = 0;
                while (!updatesDone.IsSet)
                {
                    wb.SetProfileOverrides(overrideSets[i++ % overrideSets.Length]);
                    wb.RefreshCapabilities();
                }
            });

            var reloadWriter = Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 20 && !updatesDone.IsSet; i++)
                {
                    WheelProfileStore.Reload();
                    Thread.Yield();
                }
            });

            start.Set();
            await Task.WhenAll(updateWriter, refreshWriter, reloadWriter);

            // Converge to explicit final inputs and verify the final snapshot matches.
            if (!wb.IsConnected)
                Assert.True(wb.AutoConnect());
            wb.SetProfileOverrides(overrideSets[2]); // PSWBMW -> PSWBMW_STRESS_B:User
            CommitIdentity(wb, t, clock, Ff08(0x0C, WheelWire("PSWBMW")));
            wb.RefreshCapabilities();

            done.Set();
            await Task.WhenAll(reader1, reader2);

            Assert.Empty(violations);
            Assert.True(Interlocked.Read(ref observedCount) > 0);

            var finalSnap = wb.Snapshot;
            Assert.True(finalSnap.Connected);
            Assert.True(finalSnap.IdentityStable);
            Assert.Equal("PSWBMW", finalSnap.WheelCode);
            Assert.Equal("PSWBMW_STRESS_B", finalSnap.Capabilities.Profile?.Id);
            Assert.Equal(7, finalSnap.Capabilities.AllLedCount);
        }
    }
}
