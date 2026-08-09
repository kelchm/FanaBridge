using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Leds;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;

namespace FanaBridge.Tests.TestDoubles
{
    /// <summary>
    /// An LED module host whose outcomes the test drives.
    /// </summary>
    /// <remarks>
    /// SimHub's real module reaches into the running host as soon as settings
    /// are applied, so the success and failure branches of the persistence
    /// rules can only be told apart behind this seam.
    ///
    /// It mimics the two behaviours of the real module that persistence has to
    /// cope with: it echoes back the module-owned roots it was given, and it
    /// reports every channel key — using null for channels it has no driver
    /// for, which is what used to delete stored data for those channels.
    /// </remarks>
    internal sealed class FakeLedModuleHost : IFanatecLedModuleHost
    {
        private static readonly string[] ChannelRoots =
            { "leds", "buttons", "encoders", "matrix", "raw" };

        private readonly HashSet<string> _drivenChannels;
        private JObject _applied = new JObject();

        /// <summary>Channels this module has drivers for; the rest project as null.</summary>
        public FakeLedModuleHost(params string[] drivenChannels)
        {
            _drivenChannels = new HashSet<string>(
                drivenChannels.Length > 0 ? drivenChannels : new[] { "leds", "buttons", "raw" },
                StringComparer.OrdinalIgnoreCase);
        }

        public bool AcceptSettings { get; set; } = true;
        public bool ThrowOnCapture { get; set; }
        public bool ThrowOnDefaults { get; set; }
        /// <summary>Whether LoadDefaults mutates before it throws.</summary>
        public bool MutateBeforeThrowingOnDefaults { get; set; }

        public int ApplyCount { get; private set; }
        public int DefaultsCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int DisplayCount { get; private set; }
        public int StopDrivingCount { get; private set; }
        public JObject LastApplied => _applied;

        /// <summary>The LEDs tab, when a test needs to see it offered.</summary>
        public Control? EditControlForTest { get; set; }
        public Control EditControl => EditControlForTest!;

        public bool Apply(JObject source, bool isDefault)
        {
            ApplyCount++;
            if (!AcceptSettings)
                return false;

            _applied = (JObject)source.DeepClone();
            return true;
        }

        public JObject Capture(bool forTemplate, bool forDefaultSettings)
        {
            if (ThrowOnCapture)
                throw new InvalidOperationException("capture failed");

            var result = new JObject
            {
                ["ledModuleSettings"] = _applied["ledModuleSettings"]?.DeepClone()
                                        ?? new JObject { ["Brightness"] = 100.0 },
            };

            foreach (var channel in ChannelRoots)
            {
                result[channel] = _drivenChannels.Contains(channel)
                    ? _applied[channel]?.DeepClone() ?? new JObject()
                    : JValue.CreateNull();
            }

            return result;
        }

        public void LoadDefaults()
        {
            DefaultsCount++;
            if (ThrowOnDefaults)
            {
                // Mimic a reset that got part way before failing, leaving the
                // module holding neither the old settings nor the defaults.
                if (MutateBeforeThrowingOnDefaults)
                    _applied = new JObject();
                throw new InvalidOperationException("defaults failed");
            }

            _applied = new JObject();
        }

        /// <summary>Optional hooks so a test can observe or block these calls.</summary>
        public Action? OnDisplay { get; set; }
        public Action? OnStopDriving { get; set; }

        public void Display()
        {
            DisplayCount++;
            OnDisplay?.Invoke();
        }

        /// <summary>
        /// The real host never lets StopDriving throw, but nothing enforces
        /// that on an implementation — so the device's disconnect edge is
        /// tested against one that does.
        /// </summary>
        public bool ThrowOnStopDriving { get; set; }

        public void StopDriving()
        {
            StopDrivingCount++;
            OnStopDriving?.Invoke();
            if (ThrowOnStopDriving)
                throw new InvalidOperationException("stop driving failed");
        }

        /// <summary>Last values pushed, or null if the device never said.</summary>
        public bool? CanDrive { get; private set; }
        public bool? ReportedConnected { get; private set; }

        public void SetStatus(bool canDrive, bool connected)
        {
            CanDrive = canDrive;
            ReportedConnected = connected;
        }
        public void HotSwapIfNeeded(WheelCapabilities currentCaps) { }

        public IEnumerable<DynamicButtonAction> GetDynamicActions() =>
            Enumerable.Empty<DynamicButtonAction>();


        public void Dispose() => DisposeCount++;
    }
}
