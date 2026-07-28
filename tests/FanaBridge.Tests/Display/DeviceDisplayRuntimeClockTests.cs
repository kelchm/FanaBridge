using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Rules;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Pre-E8 G1: the runtime's late-bound clock must reach
    /// <see cref="DisplayRuleStack"/> so dwell/keepalive are deterministic under test.
    /// Production still passes null → stack DefaultClock (byte-identical).
    /// </summary>
    public class DeviceDisplayRuntimeClockTests : IDisposable
    {
        private readonly bool _priorFlag;

        public DeviceDisplayRuntimeClockTests()
        {
            _priorFlag = DisplayRuleStack.LegacyRuleWrites;
            DisplayRuleStack.LegacyRuleWrites = true;
        }

        public void Dispose() => DisplayRuleStack.LegacyRuleWrites = _priorFlag;

        private sealed class Clock
        {
            public long T;
            public long Now() => T;
        }

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public List<byte[]> SentCol01Reports { get; } = new List<byte[]>();

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01Reports.Add(copy);
                return true;
            }

            public bool SendCol03(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public int Col03MaxInputReportLength => 64;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")!
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Live(int isInPit = 1)
        {
            var s = NewStatus();
            Set(s, "IsInPitLane", isInPit);
            Set(s, "Gear", "1");
            var d = new GameData { NewData = (StatusDataBase)s };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { true });
            return d;
        }

        private const string LogoSpecialConfig =
            "{ \"schemaVersion\": 1, \"segmentDisplay\": { "
            + "\"rules\": [ { \"id\": \"s1\", "
            + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
            + "\"show\": { \"kind\": \"special\", \"command\": \"logo\" }, "
            + "\"hold\": { \"kind\": \"whileActive\" } } ] } }";

        private static byte[] SpecialFrame(byte pattern)
            => new byte[] { 0x01, 0xF8, 0x09, 0x01, SpecialCommands.Subcommand, pattern, 0x00, 0x00 };

        /// <summary>
        /// Injected clock reaches the stack built by <see cref="DeviceDisplayRuntime.TickLegacyRules"/>:
        /// a held special command re-sends only when the manual clock crosses KeepaliveMs.
        /// </summary>
        [Fact]
        public void InjectedClock_ThroughRuntime_DrivesSpecialKeepaliveBoundary()
        {
            var clock = new Clock();
            Func<long> clockFn = clock.Now;

            var profile = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(profile);
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = profile,
                    Capabilities = new WheelCapabilities(profile!),
                },
                // Same late-bound shape as FanatecWheelDeviceInstance (ItmClockForTest).
                itmClock: () => clockFn,
                log: _ => { });

            var world = DisplayConfigSerializer.Load(LogoSpecialConfig, _ => { });
            runtime.SetConfig(world);

            var transport = new RecordingTransport();
            var settings = new DisplaySettings { DisplayMode = "Gear" };
            var driver = new LegacyDisplayDriver(new DisplayEncoder(transport), settings);
            runtime.SetLegacySegmentWriter((a, b, c) => driver.TryShowSegments(a, b, c));
            runtime.SetSpecialScreenHooks(
                p => driver.ShowSpecialScreen(p),
                () =>
                {
                    driver.ArmExitBlank();
                    driver.InvalidateSegmentGates();
                });

            clock.T = 0;
            runtime.TickLegacyRules(null, Live(isInPit: 1), settings);
            Assert.Single(transport.SentCol01Reports);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), transport.SentCol01Reports[0]);

            // Just shy of keepalive — still one send (wall clock would race without injection).
            clock.T = SpecialCommands.KeepaliveMs - 1;
            runtime.TickLegacyRules(null, Live(isInPit: 1), settings);
            Assert.Single(transport.SentCol01Reports);

            // Cross the boundary on the injected clock → re-send.
            clock.T = SpecialCommands.KeepaliveMs;
            runtime.TickLegacyRules(null, Live(isInPit: 1), settings);
            Assert.Equal(2, transport.SentCol01Reports.Count);
            Assert.Equal(SpecialFrame(SpecialCommands.PatternLogo), transport.SentCol01Reports[1]);
        }
    }
}
