using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>Injected clock (one per session; never Thread.Sleep).</summary>
    internal sealed class ReplayClock
    {
        public long T;
        public long Now() => T;
    }

    internal sealed class FakeBus : IHidBusEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId)
            => new[] { new HidDeviceInfo(0x0020, 64, 64, "Base") };
    }

    /// <summary>
    /// One isolated engine session (v9 or v2). RISK-7 checklist: distinct transport,
    /// fresh plugin/wheelbase/instance, pm=null at DataUpdate, shared LegacyRuleWrites
    /// saved/restored by the collection fixture.
    /// </summary>
    internal sealed class ReplaySession
    {
        public WireAttemptRecorder Transport = null!;
        public ReplayClock Clock = null!;
        public FanatecWheelbase Wheelbase = null!;
        public FanatecPlugin Plugin = null!;
        public FanatecWheelDeviceInstance Instance = null!;
        public TelemetryState Telemetry { get; } = new TelemetryState();
        public int FrameIndex { get; private set; }
        public string EngineLabel { get; set; } = "";
        public JObject Settings { get; set; } = new JObject();
        public string WheelCode { get; set; } = "CSSWFORMV3";
        public byte WheelWireCode { get; set; }

        public void Frame()
        {
            Clock.T += 16;
            FrameIndex++;
            Transport.BeginFrame(FrameIndex);
            Wheelbase.UpdateIdentity();
            var data = ReplayScript.ToGameData(Telemetry);
            // pm: null so DisplayActionHub.EnsureRegistered no-ops (RISK-7).
            Instance.DataUpdate(null, ref data);
        }

        /// <summary>Re-apply the session settings bag (config-reload kept behavior).</summary>
        public void ReloadConfig()
            => Instance.SetSettings(Settings, isDefault: false);

        /// <summary>
        /// Force a wheel-change count bump via a transient different identity commit
        /// (wheel-change kept behavior).
        /// </summary>
        public void SimulateWheelChange()
        {
            // Swap to a different rim identity then back so WheelChangeCount advances twice.
            byte alt = WheelWireCode == 0x04 ? (byte)0x0E : (byte)0x04; // CSSWFORMV3 vs PSWBMW-ish
            Transport.Identity.Enqueue(ReplayHarness.IdentityReport(0x0C, alt));
            Clock.T += 250;
            Wheelbase.UpdateIdentity();
            Transport.Identity.Enqueue(ReplayHarness.IdentityReport(0x0C, WheelWireCode));
            Clock.T += 250;
            Wheelbase.UpdateIdentity();
        }

        public IReadOnlyList<WireAttempt> Attempts => Transport.Attempts;
    }

    /// <summary>
    /// Dual-engine driver: identical scripted inputs, isolated init, capture at the
    /// transport seam, ordered stream compare (seam-map §4).
    /// </summary>
    internal static class ReplayHarness
    {
        private static byte WheelWire(string code)
            => FanatecDeviceTables.Wheels.First(kv => kv.Value == code).Key;

        private static byte[] Ff08(byte baseType, byte wire) => IdentityReport(baseType, wire);

        /// <summary>FF 08 identity report used by session bring-up and wheel-change steps.</summary>
        internal static byte[] IdentityReport(byte baseType, byte wire)
        {
            var b = new byte[64];
            b[0] = 0xFF;
            b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = baseType;
            b[FanatecIdentity.OffWireCode] = wire;
            return b;
        }

        public static ReplaySession StartV9(ReplayCell cell, string v1Json)
        {
            var settings = new JObject
            {
                ["wheelType"] = cell.WheelCode,
                ["displayCustomization"] = JObject.Parse(v1Json),
            };
            return Start(cell, settings, "v9");
        }

        public static ReplaySession StartV2(ReplayCell cell, string v2Json)
        {
            var settings = new JObject
            {
                ["wheelType"] = cell.WheelCode,
                ["display"] = JObject.Parse(v2Json),
            };
            return Start(cell, settings, "v2");
        }

        private static ReplaySession Start(ReplayCell cell, JObject settings, string label)
        {
            byte wire = WheelWire(cell.WheelCode);
            var s = new ReplaySession
            {
                Transport = new WireAttemptRecorder(),
                Clock = new ReplayClock(),
                EngineLabel = label,
                Settings = settings,
                WheelCode = cell.WheelCode,
                WheelWireCode = wire,
            };
            s.Transport.BindClock(s.Clock.Now);

            s.Wheelbase = new FanatecWheelbase(s.Transport, new FakeBus(), s.Clock.Now);
            if (!s.Wheelbase.AutoConnect())
                throw new InvalidOperationException("AutoConnect failed for " + label);

            // Commit identity for the cell's wheel.
            s.Transport.Identity.Enqueue(Ff08(0x0C, wire));
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            if (!s.Wheelbase.UpdateIdentity())
                throw new InvalidOperationException("Identity commit failed for " + label
                    + " wheel=" + cell.WheelCode);

            s.Plugin = new FanatecPlugin();
            s.Plugin.InstallWheelbaseForTest(s.Wheelbase);

            var profile = WheelProfileStore.FindByWheelType(cell.WheelCode);
            if (profile == null)
                throw new InvalidOperationException("No profile for " + cell.WheelCode);

            s.Instance = new FanatecWheelDeviceInstance(new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile),
            });
            s.Instance.PluginResolver = () => s.Plugin;
            s.Instance.ItmClockForTest = s.Clock.Now;
            s.Instance.SetSettings(settings, isDefault: false);
            return s;
        }

        /// <summary>
        /// Drive both engines from an identical step list. Returns raw attempt streams.
        /// Asserts isolation: distinct transports, no shared instance refs.
        /// </summary>
        public static (IReadOnlyList<WireAttempt> v9, IReadOnlyList<WireAttempt> v2) RunPair(
            ReplayCell cell)
        {
            string v1 = ReplayFixtureFactory.LoadOrBuildV1(cell);
            string v2 = ReplayFixtureFactory.LoadOrBuildV2(cell);
            var script = ReplayScript.For(cell);

            var s9 = StartV9(cell, v1);
            var s2 = StartV2(cell, v2);

            // RISK-7 isolation asserts.
            if (ReferenceEquals(s9.Transport, s2.Transport))
                throw new InvalidOperationException("Transports must be distinct instances");
            if (ReferenceEquals(s9.Instance, s2.Instance))
                throw new InvalidOperationException("Device instances must be distinct");
            if (ReferenceEquals(s9.Plugin, s2.Plugin))
                throw new InvalidOperationException("Plugins must be distinct");

            foreach (var step in script)
            {
                step.Apply(s9);
                step.Apply(s2);
            }

            return (s9.Attempts, s2.Attempts);
        }

        public static StreamComparer.Result RunAndCompare(ReplayCell cell)
        {
            var (v9, v2) = RunPair(cell);
            return StreamComparer.Compare(v9, v2, cell.KnownDiffs);
        }
    }

    /// <summary>
    /// Saves/restores <see cref="DisplayRuleStack.LegacyRuleWrites"/> and pins
    /// <see cref="CultureInfo.InvariantCulture"/> on the test thread (adjudication MINOR).
    /// Dedicated collection so no concurrent display test races the static (RISK-7).
    /// </summary>
    [Xunit.CollectionDefinition(Name)]
    public sealed class ReplayParityCollection : Xunit.ICollectionFixture<ReplayParityFixture>
    {
        public const string Name = "E8 Replay Parity";
    }

    public sealed class ReplayParityFixture : IDisposable
    {
        private readonly bool _priorFlag;
        private readonly CultureInfo _priorCulture;
        private readonly CultureInfo _priorUiCulture;

        public ReplayParityFixture()
        {
            _priorFlag = DisplayRuleStack.LegacyRuleWrites;
            DisplayRuleStack.LegacyRuleWrites = true;

            _priorCulture = CultureInfo.CurrentCulture;
            _priorUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        public void Dispose()
        {
            DisplayRuleStack.LegacyRuleWrites = _priorFlag;
            CultureInfo.CurrentCulture = _priorCulture;
            CultureInfo.CurrentUICulture = _priorUiCulture;
        }
    }
}
