using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Device-instance wiring for display customization: the per-device settings key
    /// (parse on SetSettings, snapshot serialization on GetSettings, before-Init
    /// tolerance), <see cref="DisplayCustomizationConfig.IsEmpty"/>, and the two wire
    /// gates over one scripted ITM session driven through a real
    /// FanatecWheelDeviceInstance — a golden gate (the session's absolute col03 frame
    /// sequence, pinned byte for byte, so no code path can add/drop/reorder a frame
    /// unnoticed) and a byte-parity gate (no displayCustomization key vs an explicitly
    /// empty document must emit identical sequences, constructing no piece of the
    /// rules runtime in either case).
    /// </summary>
    public class DisplayCustomizationWiringTests
    {
        // ── Instance/plugin harness (see FanatecWheelDeviceInstanceTests) ─

        private sealed class RecordingConnectableTransport : IConnectableTransport
        {
            public bool Connected;
            public FakeReportStream Identity { get; } = new FakeReportStream();
            public FakeReportStream Itm { get; } = new FakeReportStream();
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public bool Connect(int productId) { Connected = true; return true; }
            public void Disconnect() => Connected = false;
            public void Dispose() => Disconnect();
            public bool IsConnected => Connected;
            public bool IsDevicePresent => Connected;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                FanatecTransport.TransportConnectStatus.Connected;

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return true;
            }

            public List<byte[]> SentCol01 { get; } = new List<byte[]>();

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01.Add(copy);
                return true;
            }
            public IReportStream IdentityReports => Identity;
            public IReportStream ItmReports => Itm;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col03MaxInputReportLength => 64;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class FakeBus : IHidBusEnumerator
        {
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) =>
                new[] { new HidDeviceInfo(0x0020, 64, 64, "Base") };
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private static byte WheelWire(string code) =>
            FanatecDeviceTables.Wheels.First(kv => kv.Value == code).Key;

        private static byte[] Ff08(byte baseType, byte wire)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = baseType;
            b[FanatecIdentity.OffWireCode] = wire;
            return b;
        }

        private static FanatecWheelDeviceInstance InstanceFor(string wheelCode)
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            var config = new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile!),
            };
            return new FanatecWheelDeviceInstance(config);
        }

        // ── GameData (see ItmTelemetryTests) ─────────────────────────────
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);
        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        // ── Persistence ──────────────────────────────────────────────────

        private static JObject RuleDocument(string conditionKind = "greaterThan") =>
            JObject.Parse(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"" + conditionKind + "\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } } ] } }");

        [Fact]
        public void ApplyDisplayConfig_ParsesAndGetSettingsRoundTripsIt()
        {
            // E8b: SetSettings ignores the v1 key; the UI write path (ApplyDisplayConfig)
            // is the remaining store for the v1 bag until E9-exit.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;   // before plugin Init — must still work

            inst.ApplyDisplayConfig(DisplayConfigSerializer.Load(
                RuleDocument().ToString(), _ => { }));

            var saved = inst.GetSettings(false, false) as JObject;
            Assert.NotNull(saved);
            var doc = saved!["displayCustomization"] as JObject;
            Assert.NotNull(doc);

            // The snapshot re-serializes the parsed config — same content.
            var reloaded = DisplayConfigSerializer.Load(doc!.ToString(), _ => { });
            var rule = Assert.Single(reloaded.Itm.Rules);
            Assert.Equal("r1", rule.Id);
            Assert.Equal(ConditionKind.GreaterThan, rule.When.Kind);
            Assert.False(reloaded.IsEmpty);
        }

        [Fact]
        public void ApplyDisplayConfig_PreservesEnumText_AFutureVersionWrote()
        {
            // The piece-1 EnumText contract: a condition kind only a future build knows
            // survives ApplyDisplayConfig → GetSettings verbatim.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.ApplyDisplayConfig(DisplayConfigSerializer.Load(
                RuleDocument(conditionKind: "sparkles").ToString(), _ => { }));

            var saved = (JObject)inst.GetSettings(false, false);
            string doc = saved["displayCustomization"]!.ToString();
            Assert.Contains("sparkles", doc);
            Assert.Contains("r1", doc);
        }

        [Fact]
        public void GetSettings_WithoutConfig_OmitsTheKey()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
        }

        [Fact]
        public void LoadDefaultSettings_LeavesNoKeyStateUnbaked()
        {
            // §9b: LoadDefaultSettings never bakes — no-key state waits for first live
            // DataUpdate. Pre-connection nothing drives the display, so deferral is free.
            var inst = InstanceFor("CSSWFORMV3");
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject { ["displayCustomization"] = RuleDocument() }, false);
            inst.LoadDefaultSettings();

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
            Assert.Null(saved["display"]);
            Assert.Null(inst.DisplayRuntimeForTest.CurrentConfigV2);
            Assert.Null(inst.DisplayRuntimeForTest.CurrentConfig);
            Assert.False(inst.HasLiveResolvedDisplayCaps);
        }

        [Fact]
        public void SetSettings_WithoutDocumentKeys_LeavesUnbaked()
        {
            // A settings payload with no display / displayCustomization keys leaves the
            // bake pending — SetSettings must never bake from registration fallback.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject { ["displayCustomization"] = RuleDocument() }, false);
            inst.SetSettings(new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["displayMode"] = "Speed",
                ["itmDefaultPage"] = 2,
            }, false);

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
            Assert.Null(saved["display"]);
            Assert.Null(inst.DisplayRuntimeForTest.CurrentConfigV2);
            Assert.False(inst.HasLiveResolvedDisplayCaps);
        }

        [Fact]
        public void PreEpicBake_FirstLiveDataUpdate_BakesFromPreEpicSettings()
        {
            // §9b: bake fires on first live-resolved DataUpdate (connected wheel match).
            // CSSWFORMV3 live + defaults → marker, mode on, rest lapInfo, Gear hosted.
            var s = StartSession(new JObject { ["wheelType"] = "CSSWFORMV3" });
            Assert.Null(s.Instance.DisplayRuntimeForTest.CurrentConfigV2);
            Assert.True(s.Instance.HasLiveResolvedDisplayCaps);

            s.Frame(Data(NewStatus()));

            var saved = (JObject)s.Instance.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
            var doc = saved["display"] as JObject;
            Assert.NotNull(doc);
            var reloaded = DisplayConfigV2Serializer.Load(doc!.ToString(), _ => { });
            Assert.True(PreEpicSettingsMigrator.HasMarker(reloaded));
            Assert.Equal(SettingsMode.On, reloaded.Settings.Mode);
            Assert.Equal(PageRefKind.ItmPage, reloaded.Priority.Rest.InSessionPage.Kind);
            Assert.Equal("lapInfo", reloaded.Priority.Rest.InSessionPage.CatalogPageId);
            var hosted = Assert.Single(reloaded.Pages);
            Assert.Equal(PageEntryKind.HostedPage, hosted.Kind);
            Assert.Equal(ContentKind.Gear, hosted.Base.Content.Kind);
            Assert.Equal("Gear", hosted.Name);
        }

        [Fact]
        public void PreEpicBake_RegistrationItm_LiveBasic_BakesBasicContent()
        {
            // Registration profile is ITM (CSSWFORMV3) but live resolve is Basic via
            // override — bake must wait for live resolve and stamp segment-only content
            // (hosted rest, no ITM catalog ref). Registration-fallback bake would lock
            // wrong ITM rest then never refresh.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayMode"] = "Speed",
                ["displayControl"] = DisplaySettings.ControlLegacy,
            });
            Assert.Null(s.Instance.DisplayRuntimeForTest.CurrentConfigV2);
            // Registration profile is ITM; without the live override ResolveCapsFor
            // would still report Itm for this descriptor.
            var reg = WheelProfileStore.FindByWheelType("CSSWFORMV3");
            Assert.NotNull(reg);
            Assert.Equal(DisplayType.Itm, new WheelCapabilities(reg!).Display);

            var basic = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(basic);
            Assert.Equal(DisplayType.Basic, new WheelCapabilities(basic!).Display);
            s.Wheelbase.ProfileOverrideResolver = _ => basic!.Id;
            s.Wheelbase.RefreshCapabilities();

            Assert.Equal(DisplayType.Basic, ((IDisplayPanelHost)s.Instance).DisplayType);
            Assert.True(s.Instance.HasLiveResolvedDisplayCaps);

            s.Frame(Data(NewStatus()));

            var v2 = s.Instance.DisplayRuntimeForTest.CurrentConfigV2;
            Assert.NotNull(v2);
            Assert.True(PreEpicSettingsMigrator.HasMarker(v2));
            Assert.Equal(SettingsMode.LegacyOnly, v2!.Settings.Mode);
            Assert.Equal(PageRefKind.HostedPage, v2.Priority.Rest.InSessionPage.Kind);
            var hosted = Assert.Single(v2.Pages);
            Assert.Equal(ContentKind.Speed, hosted.Base.Content.Kind);
            Assert.Equal(hosted.Id, v2.Priority.Rest.InSessionPage.Id);
            Assert.DoesNotContain(v2.Pages, p => p.Kind == PageEntryKind.ItmPage);
        }

        [Fact]
        public void PreEpicBake_MarkedDocument_NotRebakedOnReconnectOrCapsFlip()
        {
            // A marked document is USER-OWNED from the moment it is written — user edits
            // survive reconnection and caps flips. No marker refresh / open-migration window.
            var editedMarked = new JObject
            {
                ["schemaVersion"] = 2,
                ["settings"] = new JObject { ["mode"] = "on" },
                ["profileId"] = "user-edited-keep",
                [PreEpicSettingsMigrator.MarkerKey] = PreEpicSettingsMigrator.MarkerValue,
                ["priority"] = new JObject
                {
                    ["rest"] = new JObject
                    {
                        ["inSessionPage"] = new JObject
                        {
                            ["kind"] = "itmPage",
                            ["catalogPageId"] = "tyreTemps",
                        },
                    },
                },
            };
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = editedMarked,
            });

            Assert.Equal("user-edited-keep",
                s.Instance.DisplayRuntimeForTest.CurrentConfigV2!.ProfileId);
            Assert.Equal("tyreTemps",
                s.Instance.DisplayRuntimeForTest.CurrentConfigV2.Priority.Rest.InSessionPage
                    .CatalogPageId);

            s.Frame(Data(NewStatus()));

            // Caps flip Basic → ITM must not rebake over the marked document.
            var basic = WheelProfileStore.FindByWheelType("PSWBMW");
            s.Wheelbase.ProfileOverrideResolver = _ => basic!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(Data(NewStatus()));
            s.Wheelbase.ProfileOverrideResolver = _ => null;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(Data(NewStatus()));

            var saved = (JObject)s.Instance.GetSettings(false, false);
            var doc = saved["display"] as JObject;
            Assert.NotNull(doc);
            Assert.Equal("user-edited-keep", (string)doc!["profileId"]);
            Assert.Equal("tyreTemps",
                (string)doc["priority"]!["rest"]!["inSessionPage"]!["catalogPageId"]);
            Assert.Equal(PreEpicSettingsMigrator.MarkerValue,
                (string)doc[PreEpicSettingsMigrator.MarkerKey]);
            Assert.Equal("user-edited-keep",
                s.Instance.DisplayRuntimeForTest.CurrentConfigV2!.ProfileId);
        }

        [Fact]
        public void SetSettings_ExistingV2_NeverOverwrittenByPreEpicBake()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var authored = new JObject
            {
                ["schemaVersion"] = 2,
                ["settings"] = new JObject { ["mode"] = "off" },
                ["profileId"] = "authored-keep",
            };
            inst.SetSettings(new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayControl"] = DisplaySettings.ControlItm,
                ["itmDefaultPage"] = 5,
                ["display"] = authored,
            }, false);

            var saved = (JObject)inst.GetSettings(false, false);
            var doc = saved["display"] as JObject;
            Assert.NotNull(doc);
            Assert.Equal("authored-keep", (string)doc!["profileId"]);
            Assert.Equal("off", (string)doc["settings"]!["mode"]);
            // Authored docs are not re-stamped with the bake marker.
            Assert.Null(doc[PreEpicSettingsMigrator.MarkerKey]);
        }

        [Fact]
        public void SetSettings_V1Document_DoesNotTriggerPreEpicBake()
        {
            // E8b: v1 key present = no engine, no bake (must not run §9b).
            // Raw bag rides through GetSettings inertly until E9-exit; log-once on notice.
            var hierarchy = (Hierarchy)LogManager.GetRepository();
            var appender = new MemoryAppender();
            hierarchy.Root.RemoveAllAppenders();
            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.Info;
            hierarchy.Configured = true;
            SimHub.Logging.Reset();

            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var bag = RuleDocument();
            var settings = new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayControl"] = DisplaySettings.ControlItm,
                ["itmDefaultPage"] = 5,
                ["displayCustomization"] = bag,
            };

            inst.SetSettings(settings, false);
            inst.SetSettings(settings, false); // second pass: notice must stay one-shot

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["display"]);
            Assert.True(JToken.DeepEquals(bag, saved["displayCustomization"])); // inert passthrough
            Assert.Null(inst.DisplayRuntimeForTest.CurrentConfigV2);
            Assert.Null(inst.DisplayRuntimeForTest.CurrentConfig); // no engine ran

            const string notice = "ignoring displayCustomization (v1)";
            int noticeCount = appender.GetEvents()
                .Count(e => e.RenderedMessage != null && e.RenderedMessage.IndexOf(notice, StringComparison.Ordinal) >= 0);
            Assert.Equal(1, noticeCount);
        }

        // ── IsEmpty (the parity gate's switch) ───────────────────────────

        [Fact]
        public void IsEmpty_FreshAndParsedEmptyDocuments()
        {
            Assert.True(new DisplayCustomizationConfig().IsEmpty);
            Assert.True(DisplayConfigSerializer.Load("{}", _ => { }).IsEmpty);
            Assert.True(DisplayConfigSerializer.Load(null, _ => { }).IsEmpty);
            // Null members (hand-built, not normalized) still count as empty.
            Assert.True(new DisplayCustomizationConfig
            {
                Itm = null,
                Legacy = null,
                FieldMappings = null,
            }.IsEmpty);
        }

        [Fact]
        public void IsEmpty_FalseForEachKindOfContent()
        {
            Assert.False(DisplayConfigSerializer.Load(RuleDocument().ToString(), _ => { }).IsEmpty);
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"itm\": { \"basePage\": \"tyreTemps\" } }", _ => { }).IsEmpty);
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"segmentDisplay\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ] } }",
                _ => { }).IsEmpty);
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"fieldMappings\": { \"5\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" } } } }",
                _ => { }).IsEmpty);
            // A legacy rule set with rules only.
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"segmentDisplay\": { \"screens\": [ { \"id\": \"s\", \"text\": \"P1\" } ], \"rules\": [ "
                + "{ \"id\": \"l1\", \"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"segmentScreen\", \"screenId\": \"s\" } } ] } }",
                _ => { }).IsEmpty);
        }

        // ── Scripted-session harness ─────────────────────────────────────

        // A connected CSSWFORMV3 (an ITM wheel, display device 3) instance over a
        // recording transport, with EVERYTHING — wheelbase identity, the ITM driver,
        // its lifecycle — on ONE injected clock, so scripted sessions are fully
        // deterministic (no real sleeps: a scheduler stall can never add or drop a
        // frame, which the byte-parity gate depends on).
        private sealed class Session
        {
            public RecordingConnectableTransport Transport = null!;
            public Clock Clock = null!;
            public FanatecWheelbase Wheelbase = null!;
            public FanatecPlugin Plugin = null!;
            public FanatecWheelDeviceInstance Instance = null!;

            public void Frame(GameData d)
            {
                Clock.T += 16;
                Wheelbase.UpdateIdentity();          // drains pushed ITM reports
                var frame = d;
                Instance.DataUpdate(null, ref frame);
            }
        }

        private static Session StartSession(JObject settings)
        {
            var s = new Session
            {
                Transport = new RecordingConnectableTransport(),
                Clock = new Clock(),
            };
            s.Wheelbase = new FanatecWheelbase(s.Transport, new FakeBus(), s.Clock.Now);
            Assert.True(s.Wheelbase.AutoConnect());

            // Commit the CSSWFORMV3 identity (an ITM wheel, display device 3).
            s.Transport.Identity.Enqueue(Ff08(0x0C, WheelWire("CSSWFORMV3")));
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            Assert.True(s.Wheelbase.UpdateIdentity());

            s.Plugin = new FanatecPlugin();
            s.Plugin.InstallWheelbaseForTest(s.Wheelbase);

            s.Instance = InstanceFor("CSSWFORMV3");
            s.Instance.PluginResolver = () => s.Plugin;
            s.Instance.ItmClockForTest = s.Clock.Now;
            if (settings != null)
                s.Instance.SetSettings(settings, isDefault: false);
            return s;
        }

        // ── The byte-parity gate ─────────────────────────────────────────

        // Runs the scripted ITM session (bring-up, push-confirmed sync, values, a
        // wheel-side page change, game exit) against a real device instance and
        // returns every col03 frame the transport saw. Clock advances are sized
        // comfortably past the lifecycle's windows (50 ms accumulate/quiet).
        private static List<byte[]> RunScriptedSession(
            JObject settings, out FanatecWheelDeviceInstance inst)
        {
            var session = StartSession(settings);
            inst = session.Instance;

            var s = NewStatus();
            Set(s, "SpeedLocal", 142.0);
            Set(s, "Gear", "4");
            Set(s, "CurrentLap", 3);
            Set(s, "TotalLaps", 12);
            Set(s, "Position", 2);
            Set(s, "OpponentsCount", 16);

            // Page 1 (Lap Info) and page 5 (Tyre Temps) subscription pushes for
            // display device 3, byte-identical to the ItmDisplayDriverTests fixtures.
            byte[] lapInfoPush = HexToBytes(
                "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");
            byte[] tyrePush = HexToBytes(
                "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");

            var running = Data(s);
            session.Frame(running);                  // bring-up: gate-on, enable, PageSet

            session.Transport.Itm.Enqueue(lapInfoPush);  // firmware answers with page 1 subs
            session.Frame(running);                  // adopted; accumulate window opens
            session.Clock.T += 80;
            session.Frame(running);                  // judged → Synced; first value paint
            session.Clock.T += 30;
            session.Frame(running);                  // tight second tap + ParamDefs
            session.Clock.T += 60;
            session.Frame(running);                  // ParamDefs double-tap

            session.Transport.Itm.Enqueue(tyrePush); // wheel button → page 5
            session.Frame(running);                  // adopted; re-judged
            session.Clock.T += 80;
            session.Frame(running);                  // Synced on page 5; repaint
            session.Clock.T += 30;
            session.Frame(running);                  // tap + defs

            session.Frame(Data(s, gameRunning: false));  // game exit → DisplayReset
            session.Frame(Data(s, gameRunning: false));  // idle — nothing further

            return session.Transport.Sent;
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static string[] AsHex(List<byte[]> frames)
            => frames.Select(f => BitConverter.ToString(f)).ToArray();

        // The scripted session's complete col03 output, byte for byte: gate-on identity
        // reads, DisplayReset, ITM-mode-on, enable, PageSet(1); page-1 values + tight
        // second tap + ParamDefs double-tap (lap/position totals); the wheel-button
        // page-5 repaint (values ×2 + temp-unit ParamDefs); the game-exit DisplayReset.
        // Regenerate by dumping AsHex(RunScriptedSession(...)) after any DELIBERATE
        // wire-behavior change — an unreviewed diff here is a regression.
        private static readonly string[] GoldenFrames =
        {
            "FF-08-01-FF-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-08-02-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-05-01-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-02-01-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-02-02-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-04-03-01-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-01-03-00-01-00-02-8E-00-03-01-04-00-01-04-03-02-F9-01-01-03-03-03-F5-01-01-02-03-04-FD-01-04-00-00-00-00-03-05-FE-01-04-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-01-03-00-01-00-02-8E-00-03-01-04-00-01-04-03-02-F9-01-01-03-03-03-F5-01-01-02-03-04-FD-01-04-00-00-00-00-03-05-FE-01-04-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-03-03-82-00-00-03-2F-31-32-03-83-00-00-03-2F-31-36-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-03-03-82-00-00-03-2F-31-32-03-83-00-00-03-2F-31-36-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-01-03-00-01-00-02-8E-00-03-01-04-00-01-04-03-02-2A-00-01-00-03-03-30-00-01-00-03-04-2D-00-01-00-03-05-33-00-01-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-01-03-00-01-00-02-8E-00-03-01-04-00-01-04-03-02-2A-00-01-00-03-03-30-00-01-00-03-04-2D-00-01-00-03-05-33-00-01-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-03-03-82-00-00-01-43-03-83-00-00-01-43-03-84-00-00-01-43-03-85-00-00-01-43-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            "FF-05-05-01-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
        };

        [Fact]
        public void ScriptedSession_MatchesTheGoldenFrameSequence()
        {
            // The arm-vs-arm parity gate below cannot see a change that affects both
            // of its arms identically — the snapshot/observer path runs in BOTH, so a
            // stray send from (say) the values-snapshot compose would keep parity while
            // changing the wire. This golden pins the absolute frame sequence of the
            // same scripted session: any added, dropped, reordered, or altered frame
            // fails here even when parity holds.
            var frames = RunScriptedSession(
                new JObject { ["wheelType"] = "CSSWFORMV3" }, out _);
            Assert.Equal(GoldenFrames, AsHex(frames));
        }

        [Fact]
        public void MigratedDefault_Col03FramePathIsByteIdentical_ToPreFeature()
        {
            // Phase 9a v1 path: a migrated-default user (Gear world, no ITM
            // rules/fieldMappings) must produce col03 sequences byte-identical to the
            // pre-feature build. §9b owns the no-document-key path now, so these arms
            // pin the v1 key (empty displayCustomization) to keep LegacyModeMigration.
            // (a) empty displayCustomization → migration synthesizes Gear.
            var baseline = RunScriptedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            }, out var instA);

            // (b) another empty document → same synthesis.
            var withEmptyDoc = RunScriptedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            }, out var instB);

            // (c) empty fieldMappings only → synthesis grafts Gear, still no ITM rules.
            var withEmptyMappings = RunScriptedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject
                {
                    ["fieldMappings"] = new JObject(),
                },
            }, out var instC);

            // The script really exercised the ITM path (not two empty recordings).
            Assert.Contains(baseline, f => f[1] == 0x05 && f[2] == 0x04);   // PageSet
            Assert.Contains(baseline, f => f[1] == 0x05 && f[2] == 0x01);   // ValueUpdate
            Assert.Contains(baseline, f => f[1] == 0x05 && f[2] == 0x05);   // DisplayReset

            // Byte parity: identical frame sequences, frame for frame.
            Assert.Equal(AsHex(baseline), AsHex(withEmptyDoc));
            Assert.Equal(AsHex(baseline), AsHex(withEmptyMappings));
            // Absolute golden still holds for the migrated-default arm.
            Assert.Equal(GoldenFrames, AsHex(baseline));
            // E8b: v1 key builds no engine; ITM page policy is NOT taken over.
            Assert.False(instA.ItmDisplayForTest!.HasExternalPagePolicy);
            Assert.False(instB.ItmDisplayForTest!.HasExternalPagePolicy);
            Assert.False(instC.ItmDisplayForTest!.HasExternalPagePolicy);
        }

        [Fact]
        public void MigratedLegacyOnly_ItmActive_HasExternalPagePolicyFalse()
        {
            // Page-policy pin (v1 path): migrated Gear-world-only + ItmActive must leave
            // built-in page policy (live default-page semantics) untouched.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            });
            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            Assert.True(((IDisplayPanelHost)s.Instance).DisplaySettings.ItmActive);
            // E8b: v1-only key builds no engine.
            Assert.Null(s.Instance.CompositionForTest);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.False(driver!.HasExternalPagePolicy);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, driver.Lifecycle.DefaultPage);
            Assert.True(driver.Lifecycle.GameStartPageRevert);
        }

        // FieldMappingOverride / BeginFrame ordering tests deleted with the v9 stack
        // (v1 fieldMappings no longer drive the mapper).

        // ── Page-policy handoff (v2 composition owns the base page) ──────

        [Fact]
        public void NonEmptyConfig_DriverPagePolicyFollowsComposition()
        {
            // Live v2 document with ITM rest owns page policy: lifecycle default page
            // follows rest, game-start revert suppressed.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = JObject.Parse(@"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-pit"",
      ""name"": ""Pit"",
      ""base"": { ""content"": { ""kind"": ""text"", ""text"": ""PIT"" } }
    }
  ],
  ""priority"": {
    ""rows"": [ { ""kind"": ""manual"" } ],
    ""rest"": {
      ""inSessionPage"": { ""kind"": ""itmPage"", ""catalogPageId"": ""fuelErsDrs"" },
      ""idle"": { ""kind"": ""blank"" }
    }
  },
  ""settings"": { ""mode"": ""on"" }
}"),
            });

            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            byte fuelWire = 0;
            foreach (var p in ItmDeviceCatalog.PagesFor(3))
                if (p.Page == ItmPage.FuelErsDrs)
                    fuelWire = p.Number;
            Assert.NotEqual(0, fuelWire);

            Assert.NotNull(s.Instance.CompositionForTest);
            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.True(driver!.HasExternalPagePolicy);
            Assert.Equal(fuelWire, driver.Lifecycle.DefaultPage);
            Assert.False(driver.Lifecycle.GameStartPageRevert);
        }

        [Fact]
        public void EmptyConfig_DriverPagePolicyKeepsTheSettings()
        {
            // v1 empty document → Gear world only (no ITM destinations): stock ITM page
            // policy — the setting is the default page and game-start revert stays on.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            });
            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.False(driver!.HasExternalPagePolicy);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, driver.Lifecycle.DefaultPage);
            Assert.True(driver.Lifecycle.GameStartPageRevert);
        }

        [Fact]
        public void PreEpicBake_NoDocumentKeys_TakesItmPagePolicyFromRest()
        {
            // §9b: no display / displayCustomization keys → bake v2 with rest = lapInfo;
            // composition takes external page policy at the rest floor wire page.
            var s = StartSession(new JObject { ["wheelType"] = "CSSWFORMV3" });
            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            Assert.NotNull(s.Instance.DisplayRuntimeForTest.CurrentConfigV2);
            Assert.True(PreEpicSettingsMigrator.HasMarker(
                s.Instance.DisplayRuntimeForTest.CurrentConfigV2));
            Assert.NotNull(s.Instance.CompositionForTest);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.True(driver!.HasExternalPagePolicy);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, driver.Lifecycle.DefaultPage);
            Assert.False(driver.Lifecycle.GameStartPageRevert);
        }

        // ── ApplyDisplayConfig (the UI write path) ───────────────────────

        [Fact]
        public void ApplyDisplayConfig_NormalizesThroughTheLoadPath_AndPublishes()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            // A UI-built document with defects only the validator fixes: a rule with
            // no id and an unrenderable legacy screen. Applying must behave exactly
            // like loading the same document from settings.
            var config = new DisplayCustomizationConfig();
            config.Itm.Rules.Add(new DisplayRule
            {
                When = new RuleCondition
                {
                    Kind = ConditionKind.GreaterThan,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Speed },
                    Value = 100,
                },
                Show = new RuleTarget { Kind = TargetKind.Page, Page = ItmPage.TyreTemps },
                Hold = new HoldSpec { Kind = HoldKind.WhileActive },
            });
            config.Legacy.Screens.Add(new LegacyScreen { Id = "bad", Text = "TOOLONG" });

            inst.ApplyDisplayConfig(config);

            var saved = (JObject)inst.GetSettings(false, false);
            var doc = saved["displayCustomization"];
            Assert.NotNull(doc);
            var published = DisplayConfigSerializer.Load(doc!.ToString(), _ => { });
            var rule = Assert.Single(published.Itm.Rules);
            Assert.False(string.IsNullOrEmpty(rule.Id));   // the validator assigned one
            Assert.Empty(published.Legacy.Screens);        // unrenderable screen dropped
        }

        [Fact]
        public void ApplyDisplayConfig_EmptyOrNull_PublishesNull()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            // UI write path still stores the v1 bag for GetSettings / GetDisplayConfig
            // (runtime engine does not consume it after E8b).
            inst.ApplyDisplayConfig(
                DisplayConfigSerializer.Load(RuleDocument().ToString(), _ => { }));
            Assert.NotNull(((JObject)inst.GetSettings(false, false))["displayCustomization"]);

            // Removing the last customization publishes null — the empty-config parity
            // fast path (and the omitted settings key) come back.
            inst.ApplyDisplayConfig(new DisplayCustomizationConfig());
            Assert.Null(((JObject)inst.GetSettings(false, false))["displayCustomization"]);

            inst.ApplyDisplayConfig(
                DisplayConfigSerializer.Load(RuleDocument().ToString(), _ => { }));
            inst.ApplyDisplayConfig(null!);
            Assert.Null(((JObject)inst.GetSettings(false, false))["displayCustomization"]);
        }

        [Fact]
        public void ApplyDisplayConfig_StoresForUi_NoRuntimeEngine()
        {
            // E8b: ApplyDisplayConfig still normalizes + publishes the v1 bag for the
            // UI / GetSettings, but builds no runtime engine.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            });
            var running = Data(NewStatus());
            s.Frame(running);
            Assert.Null(s.Instance.CompositionForTest);
            Assert.Null(((IDisplayPanelHost)s.Instance).GetDisplayConfig());

            s.Instance.ApplyDisplayConfig(
                DisplayConfigSerializer.Load(RuleDocument().ToString(), _ => { }));
            s.Frame(running);
            Assert.Null(s.Instance.CompositionForTest);
            Assert.Null(s.Instance.DisplayRuleSnapshot);
            Assert.NotNull(((IDisplayPanelHost)s.Instance).GetDisplayConfig());
            Assert.NotEmpty(((IDisplayPanelHost)s.Instance).GetDisplayConfig()!.Itm.Rules);

            s.Instance.ApplyDisplayConfig(new DisplayCustomizationConfig());
            s.Frame(running);
            Assert.Null(((IDisplayPanelHost)s.Instance).GetDisplayConfig());
            Assert.Null(s.Instance.DisplayRuleSnapshot);
        }

        [Fact]
        public void EmptiedWorldLive_BlanksCol01Once_ThenSilence()
        {
            // Classic mode paints Gear; switching to None blanks once then silence.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["displayMode"] = "Gear",
                ["displayCustomization"] = new JObject(), // no engine, no bake
            });
            var status = NewStatus();
            Set(status, "Gear", "3");
            var running = Data(status);

            s.Frame(running);
            Assert.NotEmpty(s.Transport.SentCol01);   // mode driver painted Gear

            s.Instance.SetSettings(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["displayMode"] = "None",
                ["displayCustomization"] = new JObject(),
            }, isDefault: false);
            int before = s.Transport.SentCol01.Count;
            s.Frame(running);
            Assert.Equal(before + 1, s.Transport.SentCol01.Count);   // blank-once
            s.Frame(running);
            s.Frame(running);
            Assert.Equal(before + 1, s.Transport.SentCol01.Count);   // then silence
        }

        [Fact]
        public void ExitBlankedPage_ThenWorldEmptied_NoRedundantBlank()
        {
            // A page already blanked by the game-exit handoff must NOT be blanked again
            // when the world empties at idle — idle col01 silence (the firmware may be
            // using the display, e.g. its tuning menu). The driver's exit-blank latch is
            // the single source of truth for "page holds our content".
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            });
            var status = NewStatus();
            Set(status, "Gear", "3");

            s.Frame(Data(status));                          // migrated Gear world paints
            Assert.NotEmpty(s.Transport.SentCol01);
            s.Frame(Data(status, gameRunning: false));      // game exit → blank-once
            int afterExit = s.Transport.SentCol01.Count;

            s.Instance.ApplyDisplayConfig(new DisplayCustomizationConfig());
            s.Frame(Data(status, gameRunning: false));      // world emptied at idle
            s.Frame(Data(status, gameRunning: false));
            Assert.Equal(afterExit, s.Transport.SentCol01.Count);   // silence — no re-blank
        }

        [Fact]
        public void FrozenNone_EmptyWorld_NeverWritesCol01()
        {
            // Fresh session with a frozen "None" mode: nothing is synthesized, the page
            // was never painted, and col01 must stay untouched across live frames.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayMode"] = "None",
            });
            var status = NewStatus();
            Set(status, "Gear", "3");
            var running = Data(status);

            s.Frame(running);
            s.Frame(running);
            Assert.Empty(s.Transport.SentCol01);
        }

        // ── GetSettingsControls (the Display tab) ────────────────────────

        private sealed class FakePanelFactory : IDevicePanelFactory
        {
            public IDisplayPanelHost? LastHost;
            public IDisplayPropertyCatalog? LastPropertyCatalog;
            public IMappedRoleCatalog? LastRoleCatalog;
            public IDisplayPickerStore? LastPickerStore;

            public System.Windows.Controls.Control CreateDisplayPanel(
                IDisplayPanelHost host,
                IDisplayPropertyCatalog propertyCatalog,
                IMappedRoleCatalog roleCatalog,
                IDisplayPickerStore pickerStore)
            {
                LastHost = host;
                LastPropertyCatalog = propertyCatalog;
                LastRoleCatalog = roleCatalog;
                LastPickerStore = pickerStore;
                return null!;   // no WPF control off the UI thread; the tab only stores it
            }

            public System.Windows.Controls.Control CreateTuningPanel(JObject customSettings) => null!;
        }

        // A display-only wheel (no LEDs, no encoders) so GetSettingsControls yields
        // exactly the display tab and never touches the LED module's WPF surface.
        private static FanatecWheelDeviceInstance BareDisplayInstance(
            string display, out FakePanelFactory panels)
        {
            var profile = new WheelProfile
            {
                Id = "TEST_" + display,
                Name = "Test " + display + " wheel",
                Display = display,
            };
            var inst = new FanatecWheelDeviceInstance(new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile),
            });
            var factory = new FakePanelFactory();
            var plugin = new FanatecPlugin { PanelFactory = factory };
            inst.PluginResolver = () => plugin;
            panels = factory;
            return inst;
        }

        [Theory]
        [InlineData("Itm", DisplayType.Itm)]
        [InlineData("Basic", DisplayType.Basic)]
        public void GetSettingsControls_YieldsTheDisplayTab_ForDisplayWheels(
            string display, DisplayType expectedType)
        {
            var inst = BareDisplayInstance(display, out var panels);

            var tabs = inst.GetSettingsControls().ToList();

            var tab = Assert.Single(tabs);
            Assert.Equal("Display", tab.Title);   // the old tab said "Screen"
            Assert.NotNull(panels.LastHost);
            var host = panels.LastHost!;
            Assert.Same(inst, host);              // the instance IS the panel's host
            // …and it is also the two on-demand editor catalogs threaded alongside.
            Assert.Same(inst, panels.LastPropertyCatalog);
            Assert.Same(inst, panels.LastRoleCatalog);
            // Plugin-wide picker store (favorites/recents) is threaded from the plugin.
            Assert.NotNull(panels.LastPickerStore);
            Assert.Equal(expectedType, host.DisplayType);
            Assert.NotNull(host.DisplaySettings);

            // The members are live windows into the instance: no config yet …
            Assert.Null(host.GetDisplayConfig());
            Assert.Null(host.Snapshot);

            // … and ApplyDisplayConfig routes through the instance's normalize-and-publish.
            var config = DisplayConfigSerializer.Load(RuleDocument().ToString(), _ => { });
            host.ApplyDisplayConfig(config);
            Assert.NotNull(host.GetDisplayConfig());
            Assert.NotNull(((JObject)inst.GetSettings(false, false))["displayCustomization"]);
        }

        [Fact]
        public void GetSettingsControls_NoDisplay_NoDisplayTab()
        {
            var inst = BareDisplayInstance("None", out var panels);
            Assert.Empty(inst.GetSettingsControls());
            Assert.Null(panels.LastHost);
        }

        // ── Display-values snapshot (the live-mirror feed) ───────────────

        [Fact]
        public void ValuesSnapshot_PublishedWithoutAnyRuleConfig_AndClearedWithTheDriver()
        {
            // The values snapshot exists for every ITM user. §9b bakes a v2 document
            // (rest = lapInfo) when no document keys are present; composition owns the
            // engine (no v1 rule snapshot) and takes external page policy.
            var session = StartSession(new JObject { ["wheelType"] = "CSSWFORMV3" });

            var s = NewStatus();
            Set(s, "SpeedLocal", 268.0);
            Set(s, "Gear", "6");
            Set(s, "CurrentLap", 15);
            Set(s, "TotalLaps", 73);
            var running = Data(s);

            session.Frame(running);                  // bring-up
            session.Transport.Itm.Enqueue(HexToBytes(
                "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a"));
            session.Frame(running);                  // push adopted
            session.Clock.T += 80;
            session.Frame(running);                  // Synced; first values
            session.Clock.T += 300;                  // snapshot throttle window
            session.Frame(running);

            var snap = session.Instance.DisplayValuesSnapshot;
            Assert.NotNull(snap);
            Assert.Equal(ItmPage.LapInfo, snap!.Page);
            Assert.Equal("15 /73", Assert.Single(snap.LeftTop!.Fields).Value);
            Assert.Equal("268", snap.SpeedText);
            Assert.Null(session.Instance.DisplayRuleSnapshot); // v2 composition, not v1 rules
            Assert.NotNull(session.Instance.DisplayRuntimeForTest.CurrentConfigV2);
            Assert.True(session.Instance.ItmDisplayForTest!.HasExternalPagePolicy);

            // Switching the display type away from ITM tears the driver down — the
            // values snapshot must go with it (same edge as the ITM status snapshot).
            var basic = WheelProfileStore.FindByWheelType("PSWBMW");
            session.Wheelbase.ProfileOverrideResolver = _ => basic!.Id;
            session.Wheelbase.RefreshCapabilities();
            session.Frame(running);
            Assert.Null(session.Instance.DisplayValuesSnapshot);
        }

        // ── Display-type teardown ────────────────────────────────────────

        [Fact]
        public void DisplayTypeSwitchAwayFromItm_ClearsItmSession()
        {
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = new JObject
                {
                    ["schemaVersion"] = 2,
                    ["settings"] = new JObject { ["mode"] = "on" },
                    ["pages"] = new JArray
                    {
                        new JObject
                        {
                            ["id"] = "rest",
                            ["kind"] = "hosted",
                            ["catalogPageId"] = "lapInfo",
                        },
                    },
                    ["rest"] = "rest",
                },
            });
            var running = Data(NewStatus());
            s.Frame(running);
            Assert.NotNull(s.Instance.CompositionForTest);
            Assert.NotNull(s.Instance.ItmDisplayForTest);

            // A user profile override switching the device to a basic display takes
            // effect on the frame path with no restart — the ITM driver goes.
            var basic = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(basic);
            Assert.NotEqual(DisplayType.Itm, new WheelCapabilities(basic!).Display);
            s.Wheelbase.ProfileOverrideResolver = _ => basic!.Id;
            s.Wheelbase.RefreshCapabilities();

            s.Frame(running);
            Assert.Null(s.Instance.ItmDisplayForTest);
            Assert.Null(s.Instance.DisplayValuesSnapshot);
        }
    }
}
