using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Probe: can a device tell WPF that its hiding Enabled property changed?
    /// </summary>
    /// <remarks>
    /// Companion to EnabledHidingProbeTests: pins that hiding the event
    /// re-implements INotifyPropertyChanged (WPF's subscription lands on the
    /// derived member) and that forwarded base notifications still arrive.
    /// </remarks>
    public class EnabledNotificationProbeTests
    {
        private sealed class NotifyingDevice : DeviceInstance, INotifyPropertyChanged
        {
            public bool PretendUnavailable;

            public NotifyingDevice() => base.PropertyChanged += ForwardFromBase;

            public new bool Enabled
            {
                get => base.Enabled && !PretendUnavailable;
                set => base.Enabled = value;
            }

            /// <summary>
            /// Hides the base event. Listing the interface again re-implements
            /// it against this member, so WPF's subscription — made through the
            /// interface — lands here rather than on the base event.
            /// </summary>
            public new event PropertyChangedEventHandler? PropertyChanged;

            private void ForwardFromBase(object sender, PropertyChangedEventArgs e) =>
                PropertyChanged?.Invoke(this, e);

            public void AnnounceEnabled() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));

            public void SetCustomNameThroughTheBase(string name) => CustomName = name;

            public override DeviceState GetDeviceState() => DeviceState.Scanning;

            // Not exercised - the probe only reads Enabled.
            public override void LoadDefaultSettings() { }
            public override IEnumerable<DeviceSettingControl> GetSettingsControls() =>
                Enumerable.Empty<DeviceSettingControl>();
            public override void SetSettings(JToken settings, bool isDefault) { }
            public override JToken GetSettings(bool a, bool b) => new JObject();
            public override void End() { }
            public override void DataUpdate(
                PluginManager pluginManager, ref GameReaderCommon.GameData data) { }
            public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions() =>
                Enumerable.Empty<DynamicButtonAction>();
        }

        [Fact]
        public void ASynthesisedNotification_RefreshesALiveBinding()
        {
            // The decisive one: the pane is already bound and showing enabled,
            // then the thing that drives the device goes away.
            var read = OnStaThread(() =>
            {
                var device = new NotifyingDevice();
                var target = new ContentControl();
                BindingOperations.SetBinding(
                    target, ContentControl.IsEnabledProperty,
                    new Binding("Enabled") { Source = device });

                var before = target.IsEnabled;

                device.PretendUnavailable = true;
                device.AnnounceEnabled();

                return (before, after: target.IsEnabled);
            });

            Assert.True(read.before);
            Assert.False(read.after);
        }

        [Fact]
        public void BaseNotifications_StillReachTheBinding()
        {
            // Intercepting the subscription must not cost us everything SimHub
            // itself raises - the device tiles bind to those.
            var read = OnStaThread(() =>
            {
                var device = new NotifyingDevice();
                var target = new ContentControl();
                BindingOperations.SetBinding(
                    target, ContentControl.ContentProperty,
                    new Binding("CustomName") { Source = device });

                device.SetCustomNameThroughTheBase("renamed");
                return target.Content;
            });

            Assert.Equal("renamed", read);
        }

        [Fact]
        public void WithoutANotification_TheBindingStaysStale()
        {
            // Establishes that the notification is what does the work, rather
            // than WPF polling or re-reading for some other reason.
            var read = OnStaThread(() =>
            {
                var device = new NotifyingDevice();
                var target = new ContentControl();
                BindingOperations.SetBinding(
                    target, ContentControl.IsEnabledProperty,
                    new Binding("Enabled") { Source = device });

                device.PretendUnavailable = true;
                return target.IsEnabled;
            });

            Assert.True(read);
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
