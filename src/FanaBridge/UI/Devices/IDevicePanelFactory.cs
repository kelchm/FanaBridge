using System;
using System.Windows.Controls;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Display;
using FanaBridge.Settings;

namespace FanaBridge.UI.Devices
{
    /// <summary>
    /// Constructs the per-device WPF settings panels.
    ///
    /// Injected when the device registry builds an instance, rather than
    /// resolved from the plugin: SimHub shows a device's settings whether or not
    /// FanaBridge is running, and routing this through the plugin singleton made
    /// the Screen and Tuning tabs disappear while it was disabled — even though
    /// nothing about editing stored settings needs the hardware.
    /// </summary>
    internal interface IDevicePanelFactory
    {
        /// <summary>A bound Screen settings panel; <paramref name="settingsChanged"/> fires on any user change.</summary>
        Control CreateScreenPanel(DisplaySettings settings, DisplayType display, byte itmDeviceId, Action settingsChanged);

        /// <summary>A bound Tuning settings panel.</summary>
        Control CreateTuningPanel(FanatecDeviceSettings settings);
    }
}
