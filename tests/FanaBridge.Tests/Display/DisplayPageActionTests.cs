using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    public class DisplayPageActionTests
    {
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")!
                .MakeGenericType(typeof(object));

        private static GameData Live()
        {
            var status = (StatusDataBase)FormatterServices.GetUninitializedObject(StatusDataType);
            var data = new GameData { NewData = status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(data, new object[] { true });
            return data;
        }

        private static DisplayConfigV2 WalkDocument()
        {
            var raw = DisplayConfigV2Serializer.Load(@"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-a"",
      ""name"": ""A"",
      ""base"": { ""content"": { ""kind"": ""text"", ""text"": ""AAA"" } }
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-b"",
      ""name"": ""B"",
      ""base"": { ""content"": { ""kind"": ""text"", ""text"": ""BBB"" } }
    }
  ],
  ""priority"": {
    ""rows"": [],
    ""rest"": {
      ""inSessionPage"": { ""kind"": ""hostedPage"", ""id"": ""p-a"" },
      ""idle"": { ""kind"": ""blank"" }
    }
  },
  ""pageOrder"": [
    { ""kind"": ""hostedPage"", ""id"": ""p-a"" },
    { ""kind"": ""hostedPage"", ""id"": ""p-b"" }
  ],
  ""settings"": { ""mode"": ""legacyOnly"" }
}", _ => { });
            return DisplayConfigV2Validator.Normalize(raw, _ => { });
        }

        private static DeviceDisplayRuntime Runtime(
            DisplayConfigV2 document,
            Action<string>? log = null,
            Func<long>? manualStepClock = null)
        {
            var profile = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(profile);
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = profile,
                    Capabilities = new WheelCapabilities(profile!),
                },
                itmClock: () => null,
                log: log ?? (_ => { }),
                manualStepClock: manualStepClock);
            runtime.SetConfigV2(document);
            return runtime;
        }

        [Fact]
        public void EnsureRegistered_UsesPluginActionNames_OncePerManagerToken()
        {
            var directions = new List<int>();
            var hub = new DisplayPageActionHub(directions.Add);
            var registered = new List<string>();
            var fires = new Dictionary<string, Action>();
            var token = new object();

            hub.EnsureRegistered(token, (name, fire) =>
            {
                registered.Add(name);
                fires[name] = fire;
            });
            hub.EnsureRegistered(token, (name, fire) => registered.Add(name));

            Assert.Equal(
                new[]
                {
                    DisplayPageActionHub.NextActionName,
                    DisplayPageActionHub.PreviousActionName,
                },
                registered);

            fires[DisplayPageActionHub.NextActionName]();
            fires[DisplayPageActionHub.PreviousActionName]();
            Assert.Equal(new[] { +1, -1 }, directions);

            hub.EnsureRegistered(new object(), (name, fire) => registered.Add(name));
            Assert.Equal(4, registered.Count);
        }

        [Fact]
        public void MissingManualDocument_ActionFire_NextTick_DirectorCommandsWalkPage()
        {
            var runtime = Runtime(WalkDocument());
            var fires = new Dictionary<string, Action>();
            var actions = new DisplayPageActionHub(direction =>
            {
                runtime.EnqueueManualStep(direction);
            });
            actions.EnsureRegistered(new object(), (name, fire) => fires[name] = fire);
            var data = Live();

            runtime.TickLegacyRules(null, data, new DisplaySettings());
            Assert.Equal(
                DestinationIds.Hosted("p-a"),
                runtime.Composition!.LastSeatResult.Intent.EffectivePageDestinationId);

            fires[DisplayPageActionHub.NextActionName]();
            runtime.TickLegacyRules(null, data, new DisplaySettings());
            Assert.Equal(+1, runtime.Composition.LastSeatManualInput!.Value.WalkStep);
            Assert.Equal(
                DestinationIds.Hosted("p-b"),
                runtime.Composition.LastSeatResult.Intent.EffectivePageDestinationId);
            Assert.Equal("p-b", runtime.Composition.LastDirectorIntent.ScreenId);

            fires[DisplayPageActionHub.PreviousActionName]();
            runtime.TickLegacyRules(null, data, new DisplaySettings());
            Assert.Equal(-1, runtime.Composition.LastSeatManualInput!.Value.WalkStep);
            Assert.Equal(
                DestinationIds.Hosted("p-a"),
                runtime.Composition.LastSeatResult.Intent.EffectivePageDestinationId);
            Assert.Equal("p-a", runtime.Composition.LastDirectorIntent.ScreenId);
        }

        [Fact]
        public void UntestedIdleCapability_LogsOncePerDeviceIdentityAcrossRebuilds()
        {
            var logs = new List<string>();
            var runtime = Runtime(WalkDocument(), logs.Add);
            var data = Live();

            runtime.TickLegacyRules(null, data, new DisplaySettings());
            runtime.SetConfigV2(WalkDocument());
            runtime.TickLegacyRules(null, data, new DisplaySettings());

            Assert.Single(logs, line =>
                line.Contains("rest.idle blank capability is untested (null)"));
        }

        [Fact]
        public void SixtyFourQueuedFires_CoalesceToAtMostTheNetStep()
        {
            var runtime = Runtime(WalkDocument());

            for (int i = 0; i < 40; i++)
                Assert.True(runtime.EnqueueManualStep(+1));
            for (int i = 0; i < 24; i++)
                Assert.True(runtime.EnqueueManualStep(-1));

            Assert.Equal(DeviceDisplayRuntime.MaxPendingManualSteps, runtime.PendingManualSteps);

            var data = Live();
            runtime.TickLegacyRules(null, data, new DisplaySettings());
            Assert.Equal(+1, runtime.Composition!.LastSeatManualInput!.Value.WalkStep);
            Assert.Equal(0, runtime.PendingManualSteps);

            runtime.TickLegacyRules(null, data, new DisplaySettings());
            Assert.False(runtime.Composition.LastSeatManualInput.HasValue);
        }

        [Fact]
        public void AgedOutSteps_AreDroppedInsteadOfReplayed()
        {
            long now = 100;
            var runtime = Runtime(WalkDocument(), manualStepClock: () => now);
            Assert.True(runtime.EnqueueManualStep(+1));

            now += DeviceDisplayRuntime.MaxManualStepAgeMs + 1;
            runtime.TickLegacyRules(null, Live(), new DisplaySettings());

            Assert.Equal(0, runtime.PendingManualSteps);
            Assert.False(runtime.Composition!.LastSeatManualInput.HasValue);
        }
    }
}
