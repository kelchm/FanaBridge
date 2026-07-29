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
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The Display tab's seam: the <see cref="IDisplayPanelHost"/> members the device
    /// instance implements, and the ONE <see cref="DisplayPanelSnapshot"/> envelope
    /// they publish — composition gating (recompose only when a part changed) and the
    /// teardown edges enumerated on the envelope's doc comment, one test per edge.
    /// </summary>
    public class DisplayPanelHostTests
    {
        // ── Harness (mirrors DisplayCustomizationWiringTests) ─────────────

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

        private static byte HubWire(string code) =>
            FanatecDeviceTables.Hubs.First(kv => kv.Value == code).Key;

        private static byte[] Ff08(byte baseType, byte wire)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = baseType;
            b[FanatecIdentity.OffWireCode] = wire;
            return b;
        }

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        private sealed class Session
        {
            public RecordingConnectableTransport Transport = null!;
            public Clock Clock = null!;
            public FanatecWheelbase Wheelbase = null!;
            public FanatecPlugin Plugin = null!;
            public FanatecWheelDeviceInstance Instance = null!;

            public IDisplayPanelHost Host => Instance;
            public IDisplayPropertyCatalog PropertyCatalog => Instance;
            public IMappedRoleCatalog RoleCatalog => Instance;

            public void Frame(GameData d)
            {
                Clock.T += 16;
                Wheelbase.UpdateIdentity();          // drains pushed ITM reports
                var frame = d;
                Instance.DataUpdate(null, ref frame);
            }
        }

        private static Session StartSession(JObject settings)
            => StartSession(settings, WheelWire("CSSWFORMV3"), "CSSWFORMV3");

        // Starts a session on an arbitrary attachment: the wire byte drives the
        // wheelbase identity, and the profile wheel-type builds the registration caps
        // (DeviceConfig.Capabilities) — so a session can register as any wheel/hub the
        // resolved-caps tests need (e.g. a display-less PHUB base).
        private static Session StartSession(JObject settings, byte wheelWire, string profileWheelType)
        {
            var s = new Session
            {
                Transport = new RecordingConnectableTransport(),
                Clock = new Clock(),
            };
            s.Wheelbase = new FanatecWheelbase(s.Transport, new FakeBus(), s.Clock.Now);
            Assert.True(s.Wheelbase.AutoConnect());

            // Commit the requested identity (default: CSSWFORMV3 — an ITM wheel, display device 3).
            s.Transport.Identity.Enqueue(Ff08(0x0C, wheelWire));
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            Assert.True(s.Wheelbase.UpdateIdentity());

            s.Plugin = new FanatecPlugin();
            s.Plugin.InstallWheelbaseForTest(s.Wheelbase);

            var profile = WheelProfileStore.FindByWheelType(profileWheelType);
            Assert.NotNull(profile);
            s.Instance = new FanatecWheelDeviceInstance(new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile!),
            });
            s.Instance.PluginResolver = () => s.Plugin;
            s.Instance.ItmClockForTest = s.Clock.Now;
            if (settings != null)
                s.Instance.SetSettings(settings, isDefault: false);
            return s;
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        // Page 1 (Lap Info) subscriptions for display device 3.
        private static byte[] LapInfoPush => HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");

        private static JObject RuleDocument() =>
            JObject.Parse(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } } ] } }");

        // Runs a session to push-confirmed sync on page 1 (Lap Info).
        private static Session SyncedSession(JObject settings, GameData running)
        {
            var s = StartSession(settings);
            s.Frame(running);                        // bring-up: gate-on, enable, PageSet
            s.Transport.Itm.Enqueue(LapInfoPush);    // firmware answers with page-1 subs
            s.Frame(running);                        // adopted; accumulate window opens
            s.Clock.T += 80;
            s.Frame(running);                        // judged → Synced; first values
            s.Clock.T += 300;                        // values-snapshot throttle window
            s.Frame(running);
            return s;
        }

        // ── Composition ───────────────────────────────────────────────────

        [Fact]
        public void Envelope_PublishesStatusAndValues_WithoutAnyRuleConfig()
        {
            // §9b: no document keys bake a v2 document (rest floor); composition owns
            // the engine (Rules null, ComposedResolution set). Status + values publish.
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject { ["wheelType"] = "CSSWFORMV3" }, running);

            var envelope = s.Host.Snapshot;
            Assert.NotNull(envelope);
            Assert.NotNull(envelope!.ItmStatus);
            Assert.NotNull(envelope.Values);
            Assert.Null(envelope.Rules);             // v2 composition, not v1 rules
            Assert.NotNull(envelope.ComposedResolution);
            Assert.True(s.Instance.ItmDisplayForTest!.HasExternalPagePolicy);

            // The parts ARE the producers' snapshots — composed, never copied.
            Assert.Same(s.Instance.DisplayValuesSnapshot, envelope.Values);
        }

        [Fact]
        public void Envelope_CarriesComposedResolution_WhileV2IsActive()
        {
            // E8b: rules envelope part is always null; v2 publishes ComposedResolution.
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
            }, running);

            var envelope = s.Host.Snapshot;
            Assert.NotNull(envelope);
            Assert.Null(envelope!.Rules);
            Assert.NotNull(envelope.ComposedResolution);
        }

        [Fact]
        public void Envelope_IsNotRecomposed_WhileNoPartChanges()
        {
            // v1 empty document: rule/values/status parts stabilize so the envelope
            // identity can be reused on idle frames (v2 composition rebuilds its
            // resolution record each tick — identity reuse is a v1-path pin).
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            }, running);

            var first = s.Host.Snapshot;
            Assert.NotNull(first);
            s.Frame(running);                        // nothing edges: same values, same
            s.Frame(running);                        // state, same status
            Assert.Same(first, s.Host.Snapshot);     // zero-allocation idle frames
        }

        [Fact]
        public void DeviceStatusRow_ReadsThroughTheEnvelope()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject { ["wheelType"] = "CSSWFORMV3" }, running);

            // The Device Status row (FanatecPlugin.ItmStatus → ItmStatusDescription)
            // and the Display tab must be reading the same published line.
            Assert.NotNull(s.Instance.ItmStatusDescription);
            Assert.Equal(s.Host.Snapshot!.ItmStatus, s.Instance.ItmStatusDescription);
        }

        // ── Teardown edges (one per edge on the envelope's doc comment) ───

        [Fact]
        public void Disconnect_ClearsTheEnvelope()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = RuleDocument(),
            }, running);
            Assert.NotNull(s.Host.Snapshot);

            s.Transport.Connected = false;           // base gone → Scanning
            s.Frame(running);

            Assert.Null(s.Host.Snapshot);
            Assert.Null(s.Instance.ItmStatusDescription);
        }

        [Fact]
        public void GenerationRebind_ReplacesEveryPart()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
            }, running);
            var before = s.Host.Snapshot;
            Assert.NotNull(before);

            // The plugin is replaced in-process (issue #37): the drivers rebuild
            // against the new generation the same frame, so by frame end the envelope
            // must carry ONLY new-generation parts — never the disposed session's.
            var pluginB = new FanatecPlugin();
            pluginB.InstallWheelbaseForTest(s.Wheelbase);
            s.Instance.PluginResolver = () => pluginB;
            s.Frame(running);

            var after = s.Host.Snapshot;
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.NotNull(after!.ItmStatus);        // every part REPLACED, not dropped:
            Assert.Null(after.Rules);                // E8b: no v9 rules part
            Assert.NotNull(after.ComposedResolution);
            Assert.NotNull(after.Values);            // for a part that silently vanished
            Assert.NotSame(before!.Values, after.Values);
            // The rebuilt driver starts cold: the fresh twin has observed this frame's
            // bring-up PageSet (so it truthfully shows the newly-selected page) but no
            // values have been painted yet — placeholders, not the disposed session's
            // synced screen.
            Assert.True(after.Values!.ShowingPlaceholders);
        }

        [Fact]
        public void ItmDisplayIdChange_RebuildsStatusAndValues()
        {
            var running = Data(NewStatus());
            // Use no document keys so §9b bakes a v2 doc (live composition) for the session.
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
            }, running);
            var before = s.Host.Snapshot;
            Assert.NotNull(before);
            Assert.Null(before!.Rules); // E8b: no v9 rules envelope
            Assert.NotNull(before.Values!.Page);     // synced — the old driver shows a page
            Assert.NotNull(s.Instance.CompositionForTest);

            // A profile override retargets the ITM display id (device 3 → 4) while the
            // display type stays ITM: the driver is hot-swapped in place.
            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");
            Assert.NotNull(bentley);
            s.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);

            var after = s.Host.Snapshot;
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            // Status and values follow the rebuilt driver from the same frame…
            Assert.NotNull(after!.ItmStatus);
            Assert.NotEqual(before.ItmStatus, after.ItmStatus);  // not the old controller's line
            Assert.NotNull(after.Values);
            Assert.NotSame(before.Values, after.Values);
            // The rebuilt driver starts cold: the fresh twin shows the newly-selected
            // page (from this frame's bring-up PageSet) with placeholders — not the old
            // driver's synced values.
            Assert.True(after.Values!.ShowingPlaceholders);
            Assert.Null(after.Rules);
            Assert.NotNull(s.Instance.CompositionForTest);
        }

        [Fact]
        public void DisplayTypeSwitchAwayFromItm_ClearsTheEnvelope()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = RuleDocument(),
            }, running);
            Assert.NotNull(s.Host.Snapshot);

            // A profile override retargets the device to a basic display: the ITM
            // driver (status + values) goes. A migrated legacy world may keep a Rules
            // part via the basic-path stack (TickLegacyRules).
            var basic = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(basic);
            s.Wheelbase.ProfileOverrideResolver = _ => basic!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);

            Assert.Null(s.Instance.ItmStatusDescription);
            Assert.Null(s.Instance.ItmDisplayForTest);
            var envelope = s.Host.Snapshot;
            // ITM parts cleared; Rules may remain for the legacy world on basic.
            if (envelope != null)
            {
                Assert.Null(envelope.ItmStatus);
                Assert.Null(envelope.Values);
            }
        }

        [Fact]
        public void CustomizationRemoved_ClearsComposition_KeepsStatusAndValues()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
            }, running);
            Assert.NotNull(s.Host.Snapshot!.ComposedResolution);

            // Mode off drops composition; ITM status/values remain while the driver lives.
            s.Instance.SetSettings(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = JObject.Parse(@"{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""priority"": { ""rows"": [], ""rest"": { ""inSessionPage"": { ""kind"": ""blank"" }, ""idle"": { ""kind"": ""blank"" } } },
  ""settings"": { ""mode"": ""off"" }
}"),
            }, isDefault: false);
            s.Frame(running);

            var envelope = s.Host.Snapshot;
            Assert.NotNull(envelope);
            Assert.Null(envelope!.ComposedResolution);
            Assert.Null(envelope.Rules);
            Assert.NotNull(envelope.ItmStatus);
            Assert.NotNull(envelope.Values);
        }

        [Fact]
        public void LegacyControl_DropsItmPagePolicy()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
            }, running);
            Assert.True(s.Instance.ItmDisplayForTest!.HasExternalPagePolicy);

            // DisplayControl Legacy releases ITM page policy (ItmActive false).
            s.Instance.SetSettings(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["itmEnabled"] = true,
            }, isDefault: false);
            s.Frame(running);

            var envelope = s.Host.Snapshot;
            Assert.NotNull(envelope);                // the driver still reports status
            Assert.False(((IDisplayPanelHost)s.Instance).DisplaySettings.ItmActive);
            Assert.False(s.Instance.ItmDisplayForTest!.HasExternalPagePolicy);
        }

        [Fact]
        public void End_DropsTheValuesPart()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject { ["wheelType"] = "CSSWFORMV3" }, running);
            Assert.NotNull(s.Host.Snapshot!.Values);

            s.Instance.End();                        // driver stops; no more frames

            var envelope = s.Host.Snapshot;
            Assert.NotNull(envelope);
            Assert.Null(envelope!.Values);           // a stopped session shows no values
        }

        // ── DisplayControl Off + migration self-heal ──────────────────────

        private static void SetStatusProp(object status, string property, object value)
            => status.GetType().GetProperty(property)!.GetSetMethod(true)!
                .Invoke(status, new[] { value });

        private static GameData GearRunning(string gear = "3")
        {
            var status = NewStatus();
            SetStatusProp(status, "Gear", gear);
            return Data(status);
        }

        [Fact]
        public void BasicPath_OffControl_DoesNotPaintCol01()
        {
            // Basic 7-seg must honor DisplayControl=Off the same way the ITM branch does:
            // blank once, then hands-off — never keep driving gear/speed under Off.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayControl"] = DisplaySettings.ControlOff,
                ["displayMode"] = "Gear",
                ["itmEnabled"] = false,
            }, WheelWire("PSWBMW"), "PSWBMW");

            Assert.Equal(DisplayType.Basic, s.Host.DisplayType);
            Assert.False(s.Host.DisplaySettings.LegacyPageActive);

            s.Frame(GearRunning("3"));
            s.Frame(GearRunning("4"));
            s.Frame(GearRunning("5"));

            // At most one blank (Clear) is allowed; no gear-digit content after Off.
            Assert.True(s.Transport.SentCol01.Count <= 1,
                "Off must not keep painting col01 on the basic path");
            foreach (var report in s.Transport.SentCol01)
            {
                // ClearDisplay writes blank segments (positions 5-7).
                Assert.Equal(SevenSegment.Blank, report[5]);
                Assert.Equal(SevenSegment.Blank, report[6]);
                Assert.Equal(SevenSegment.Blank, report[7]);
            }
        }

        [Fact]
        public void BasicPath_LegacyControl_PaintsGearOnCol01()
        {
            // No live v2 composition → classic mode driver paints displayMode on col01.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["displayMode"] = "Gear",
                ["itmEnabled"] = false,
            }, WheelWire("PSWBMW"), "PSWBMW");

            s.Frame(GearRunning("4"));

            Assert.NotEmpty(s.Transport.SentCol01);
            var last = s.Transport.SentCol01[s.Transport.SentCol01.Count - 1];
            Assert.Equal(SevenSegment.Digit4, last[6]);
        }

        [Fact]
        public void MigratedSettings_PromoteToItm_WhenCapsBecomeItmCapable()
        {
            // Pre-tristate blob loaded while non-ITM → Legacy in memory; profile override
            // to an ITM wheel re-Reads (displayControl still absent) and promotes to Itm.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayMode"] = "Gear",
                ["itmEnabled"] = true,
            }, WheelWire("PSWBMW"), "PSWBMW");

            Assert.Equal(DisplaySettings.ControlLegacy, s.Host.DisplaySettings.DisplayControl);
            Assert.False(s.Host.DisplaySettings.ItmActive);

            var itm = WheelProfileStore.FindByWheelType("CSSWFORMV3");
            Assert.NotNull(itm);
            s.Wheelbase.ProfileOverrideResolver = _ => itm!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(GearRunning("2"));

            Assert.Equal(DisplayType.Itm, s.Host.DisplayType);
            Assert.Equal(DisplaySettings.ControlItm, s.Host.DisplaySettings.DisplayControl);
            Assert.True(s.Host.DisplaySettings.ItmActive);
            // Resolve-on-read: still not rewritten until the next Write.
            Assert.Null(((JObject)s.Instance.GetSettings(false, false))["displayControl"]);
        }

        // ── Interface round-trip ──────────────────────────────────────────

        [Fact]
        public void Host_TypedMembers_RoundTrip()
        {
            // E8b: SetSettings with a v1 key stores nothing; UI ApplyDisplayConfig still
            // populates GetDisplayConfig for the v1 views until E9-exit.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            });
            var host = s.Host;

            Assert.Equal(DisplayType.Itm, host.DisplayType);
            Assert.Equal(3, host.ItmDeviceId);       // CSSWFORMV3 = display device 3
            Assert.NotNull(host.DisplaySettings);

            Assert.Null(host.GetDisplayConfig());
            host.ApplyDisplayConfig(DisplayConfigSerializer.Load(
                RuleDocument().ToString(), _ => { }));
            Assert.NotNull(host.GetDisplayConfig());
            Assert.NotEmpty(host.GetDisplayConfig()!.Itm.Rules);
            Assert.NotNull(((JObject)s.Instance.GetSettings(false, false))["displayCustomization"]);

            // Settings path: the panel mutates DisplaySettings and notifies; the
            // instance syncs the change into its persisted settings.
            host.DisplaySettings.ItmDefaultPage = 5;
            host.NotifySettingsChanged();
            Assert.Equal((byte?)5, (byte?)((JObject)s.Instance.GetSettings(false, false))["itmDefaultPage"]);
        }

        [Fact]
        public void HostCaps_ReportTheResolvedOverride_NotFrozenRegistration()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject { ["wheelType"] = "CSSWFORMV3" }, running);

            // Registration caps: CSSWFORMV3 is an ITM wheel on display device 3.
            Assert.Equal(DisplayType.Itm, s.Host.DisplayType);
            Assert.Equal(3, s.Host.ItmDeviceId);

            // A profile override retargets the ITM display id (device 3 → 4). The driver
            // runtime already follows ResolveCapsFor; the panel host must report the SAME
            // override-resolved id so the Display tab can't populate the wrong page table.
            // Pre-fix the host returned the frozen registration id (3).
            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");
            Assert.NotNull(bentley);
            s.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);

            Assert.Equal(4, s.Host.ItmDeviceId);                 // resolved override, not 3
            Assert.Equal(DisplayType.Itm, s.Host.DisplayType);   // same resolved source
        }

        [Fact]
        public void ShouldOfferDisplayTab_FollowsResolvedCaps_InBothDirections()
        {
            var running = Data(NewStatus());

            // OFF direction: an ITM wheel (registration ITM) overridden onto a display-
            // less profile must STOP offering the Display tab. The tab-creation gate now
            // reads the resolved caps; pre-fix it read the frozen registration caps
            // (still ITM here) and kept offering the tab.
            var s = SyncedSession(new JObject { ["wheelType"] = "CSSWFORMV3" }, running);
            Assert.Equal(DisplayType.Itm, s.Host.DisplayType);
            Assert.True(s.Instance.ShouldOfferDisplayTab);

            var hub = WheelProfileStore.FindByWheelType("PHUB");           // display: none
            Assert.NotNull(hub);
            Assert.Equal(DisplayType.None, new WheelCapabilities(hub!).Display);
            s.Wheelbase.ProfileOverrideResolver = _ => hub!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);

            Assert.Equal(DisplayType.None, s.Host.DisplayType);   // resolved caps lost the display…
            Assert.False(s.Instance.ShouldOfferDisplayTab);       // …so the tab is no longer offered

            // ON direction: a display-less base (registration None) overridden onto an ITM
            // profile must START offering the tab. Registration caps are None here, so
            // ONLY the resolved-caps gate can turn the tab on — the pre-fix registration
            // gate would leave it off.
            var h = StartSession(new JObject { ["wheelType"] = "PHUB" }, HubWire("PHUB"), "PHUB");
            Assert.Equal(DisplayType.None, h.Host.DisplayType);
            Assert.False(h.Instance.ShouldOfferDisplayTab);

            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");    // display: itm (device 4)
            Assert.NotNull(bentley);
            h.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            h.Wheelbase.RefreshCapabilities();

            Assert.Equal(DisplayType.Itm, h.Host.DisplayType);    // resolved caps gained a display…
            Assert.True(h.Instance.ShouldOfferDisplayTab);        // …so the tab is now offered
        }

        [Fact]
        public void Host_PickerSurfaces_DegradeToEmpty_WithNoPluginManager()
        {
            // Init is never called in this harness, so PluginManager is null — the
            // on-demand picker surfaces must return empties, never throw.
            var s = StartSession(new JObject { ["wheelType"] = "CSSWFORMV3" });

            Assert.Empty(s.PropertyCatalog.GetAllPropertyNames());
            var roles = s.RoleCatalog.GetMappedRoles();
            Assert.Equal(MappedRolesSource.None, roles.Source);
            Assert.Empty(roles.Roles);
        }
    }
}
