using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using SimHub.Plugins.Devices;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Probe: can a device present one Enabled value to SimHub's WPF bindings
    /// and another to SimHub's own code?
    /// </summary>
    /// <remarks>
    /// SimHub greys out a device's settings pane by binding the hosting
    /// control's IsEnabled to the device's Enabled property, and it also
    /// persists that same property to the device's file. Reporting false while
    /// the plugin is off would grey the pane, but would also write false into
    /// the user's settings and leave the device genuinely switched off next
    /// launch.
    ///
    /// SimHub reads the property through a DeviceInstance-typed reference,
    /// which binds at compile time to the base member, while WPF resolves it
    /// on the runtime type. If that difference holds, a hiding property can
    /// answer the two callers differently. These pin whether it does — if any
    /// of them fail, the approach is not available.
    /// </remarks>
    public class EnabledHidingProbeTests
    {
        private sealed class HidingDevice : DeviceInstance
        {
            public bool PretendUnavailable;

            /// <summary>Hides, rather than overrides, the base property.</summary>
            public new bool Enabled
            {
                get => base.Enabled && !PretendUnavailable;
                set => base.Enabled = value;
            }

            public override DeviceState GetDeviceState() => DeviceState.Scanning;

            // Not exercised - the probe only reads Enabled.
            public override void LoadDefaultSettings() { }
            public override System.Collections.Generic.IEnumerable<DeviceSettingControl>
                GetSettingsControls() => System.Linq.Enumerable.Empty<DeviceSettingControl>();
            public override void SetSettings(Newtonsoft.Json.Linq.JToken settings, bool isDefault) { }
            public override Newtonsoft.Json.Linq.JToken GetSettings(bool a, bool b) =>
                new Newtonsoft.Json.Linq.JObject();
            public override void End() { }
            public override void DataUpdate(
                SimHub.Plugins.PluginManager pluginManager, ref GameReaderCommon.GameData data) { }
            public override System.Collections.Generic.IEnumerable<SimHub.Plugins.DynamicButtonAction>
                GetDynamicButtonActions() =>
                System.Linq.Enumerable.Empty<SimHub.Plugins.DynamicButtonAction>();
        }

        [Fact]
        public void SimHubsOwnCode_SeesTheRealValue()
        {
            // SimHub persists via a DeviceInstance-typed reference, so it must
            // keep seeing the user's actual preference.
            var device = new HidingDevice { PretendUnavailable = true };
            DeviceInstance asSimHubSeesIt = device;

            Assert.True(asSimHubSeesIt.Enabled);   // what gets written to disk
            Assert.False(device.Enabled);          // what the UI should see
        }

        [Fact]
        public void TypeDescriptor_ResolvesTheDerivedProperty()
        {
            // WPF resolves binding paths through TypeDescriptor, so this is what
            // decides which property the pane's IsEnabled actually reads.
            var device = new HidingDevice { PretendUnavailable = true };

            var property = TypeDescriptor.GetProperties(device)["Enabled"];

            Assert.NotNull(property);
            Assert.Equal(typeof(HidingDevice), property.ComponentType);
            Assert.Equal(false, property.GetValue(device));
        }

        [Fact]
        public void AWpfBinding_ReadsTheDerivedValue()
        {
            // The decisive one: a real binding, as SimHub's settings host makes it.
            var read = OnStaThread(() =>
            {
                var device = new HidingDevice { PretendUnavailable = true };
                var target = new ContentControl();
                BindingOperations.SetBinding(
                    target, ContentControl.IsEnabledProperty,
                    new Binding("Enabled") { Source = device });
                return target.IsEnabled;
            });

            Assert.False(read);
        }

        private static T OnStaThread<T>(Func<T> body)
        {
            T result = default!;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { result = body(); }
                catch (Exception ex)
                { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
            return result;
        }
    }
}
