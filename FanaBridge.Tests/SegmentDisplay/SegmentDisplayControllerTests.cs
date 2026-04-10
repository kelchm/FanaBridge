using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.SegmentDisplay;
using FanaBridge.Shared;
using FanaBridge.Shared.Conditions;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class SegmentDisplayControllerTests
    {
        [Fact]
        public void Update_SingleGearScreen_SendsEncodedSegments()
        {
            var (controller, spy) = Create(MakeGearScreen());

            controller.Update(Props("DataCorePlugin.GameData.Gear", 3), null, false, 0);

            Assert.True(spy.SendCount > 0);
            // Gear "3" → centered " 3 " → [Blank, Digit3, Blank]
            Assert.Equal(SevenSegment.Blank, spy.LastSeg0);
            Assert.Equal(SevenSegment.Digit3, spy.LastSeg1);
            Assert.Equal(SevenSegment.Blank, spy.LastSeg2);
        }

        [Fact]
        public void Update_NoLayers_ClearsDisplay()
        {
            var (controller, spy) = Create();

            controller.Update(new StubProps(), null, false, 0);

            Assert.True(spy.ClearCount > 0);
        }

        [Fact]
        public void Update_FixedTextOverlay_SendsText()
        {
            var overlay = new SegmentDisplayLayer
            {
                Role = LayerRole.Overlay,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = "PIT" },
                Alignment = AlignmentType.Center,
                Overflow = OverflowType.Auto,
                ShowWhenIdle = true,
            };
            var screen = MakeGearScreen();
            var (controller, spy) = Create(overlay, screen);

            controller.Update(new StubProps(), null, false, 0);

            Assert.Equal("PIT", controller.CurrentText);
            Assert.Same(overlay, controller.WinningLayer);
        }

        [Fact]
        public void Update_BlinkEffect_SuppressesDuringOffPhase()
        {
            var layer = new SegmentDisplayLayer
            {
                Role = LayerRole.Overlay,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = "PIT" },
                Alignment = AlignmentType.Center,
                Overflow = OverflowType.Auto,
                Effects = new SegmentEffect[] { new BlinkEffect { OnMs = 500, OffMs = 500 } },
                ShowWhenIdle = true,
            };
            var (controller, spy) = Create(layer);

            // On phase (elapsed = 0)
            controller.Update(new StubProps(), null, false, 100);
            Assert.True(spy.SendCount > 0);
            int sendAfterOn = spy.SendCount;

            // Off phase (elapsed ~600ms since layer became active at t=100)
            spy.Reset();
            controller.Update(new StubProps(), null, false, 700);
            Assert.True(spy.ClearCount > 0);
        }

        [Fact]
        public void Update_DeviceCommandContent_CallsSendCommand()
        {
            var layer = new SegmentDisplayLayer
            {
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new DeviceCommandContent { Command = DeviceCommand.FanatecLogo },
                ShowWhenIdle = true,
            };
            var (controller, spy) = Create(layer);

            controller.Update(new StubProps(), null, false, 0);

            Assert.True(spy.CommandCount > 0);
        }

        [Fact]
        public void UpdateSettings_RebuildsAndResets()
        {
            var screen1 = MakeGearScreen();
            var (controller, spy) = Create(screen1);

            controller.NextScreen(); // move cycle index

            var newSettings = new SegmentDisplaySettings();
            var screen2 = new SegmentDisplayLayer
            {
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = "NEW" },
                ShowWhenIdle = true,
            };
            newSettings.Layers.Add(screen2);

            controller.UpdateSettings(newSettings);
            controller.Update(new StubProps(), null, false, 0);

            Assert.Same(screen2, controller.WinningLayer);
        }

        [Fact]
        public void Clear_BlanksDisplayAndResetsState()
        {
            var (controller, spy) = Create(MakeGearScreen());
            controller.Update(Props("DataCorePlugin.GameData.Gear", 3), null, false, 0);

            controller.Clear();

            Assert.True(spy.ClearCount > 0);
            Assert.Null(controller.WinningLayer);
        }

        [Fact]
        public void NextScreen_CyclesToSecondScreen()
        {
            var screen1 = new SegmentDisplayLayer
            {
                Name = "Gear",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = "GER" },
                ShowWhenIdle = true,
            };
            var screen2 = new SegmentDisplayLayer
            {
                Name = "Speed",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new FixedTextContent { Text = "SPD" },
                ShowWhenIdle = true,
            };
            var (controller, _) = Create(screen1, screen2);

            controller.Update(new StubProps(), null, false, 0);
            Assert.Equal("Gear", controller.ActiveScreenName);

            controller.NextScreen();
            controller.Update(new StubProps(), null, false, 16);
            Assert.Equal("Speed", controller.ActiveScreenName);
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static (SegmentDisplayController controller, SpyDisplay spy)
            Create(params SegmentDisplayLayer[] layers)
        {
            var settings = new SegmentDisplaySettings();
            foreach (var l in layers) settings.Layers.Add(l);
            var spy = new SpyDisplay();
            var controller = new SegmentDisplayController(spy, settings);
            return (controller, spy);
        }

        private static SegmentDisplayLayer MakeGearScreen()
        {
            return new SegmentDisplayLayer
            {
                Name = "Gear",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
                Alignment = AlignmentType.Auto,
                Overflow = OverflowType.Auto,
                ShowWhenIdle = true,
            };
        }

        private static StubProps Props(string key, object value)
        {
            var p = new StubProps();
            p.Set(key, value);
            return p;
        }

        private class StubProps : IPropertyProvider
        {
            private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
            public void Set(string name, object value) { _values[name] = value; }
            public object GetValue(string name)
            {
                object v;
                return _values.TryGetValue(name, out v) ? v : null;
            }
        }

        private class SpyDisplay : ISegmentDisplay
        {
            public int SendCount { get; private set; }
            public int ClearCount { get; private set; }
            public int CommandCount { get; private set; }
            public byte LastSeg0 { get; private set; }
            public byte LastSeg1 { get; private set; }
            public byte LastSeg2 { get; private set; }

            public void Send(byte seg0, byte seg1, byte seg2)
            {
                SendCount++;
                LastSeg0 = seg0;
                LastSeg1 = seg1;
                LastSeg2 = seg2;
            }

            public void Clear() { ClearCount++; }
            public void SendCommand(DeviceCommand command) { CommandCount++; }
            public void Keepalive() { }

            public void Reset()
            {
                SendCount = 0;
                ClearCount = 0;
                CommandCount = 0;
            }
        }
    }
}
