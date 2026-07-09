using System.Runtime.CompilerServices;

namespace FanaBridge
{
    /// <summary>Forwards the core's <see cref="Log"/> seam to SimHub's logger.</summary>
    internal sealed class SimHubLogSink : ILogSink
    {
        public void Info(string message) => SimHub.Logging.Current.Info(message);
        public void Warn(string message) => SimHub.Logging.Current.Warn(message);
        public void Error(string message) => SimHub.Logging.Current.Error(message);
        public void Debug(string message) => SimHub.Logging.Current.Debug(message);
    }

    internal static class CoreLogBridge
    {
        // Runs when this (plugin) assembly's module loads — before SimHub can
        // reach FanatecPlugin, FanatecDevicesRegistry, or any DeviceInstance,
        // which are the only entry points into the core. A static-ctor hook on
        // FanatecPlugin alone would miss early core work: SimHub can call
        // FanatecDevicesRegistry.GetDevices() (→ WheelProfileStore load + its
        // log lines) before the plugin type is ever touched.
        [ModuleInitializer]
        internal static void Install() => Log.Sink = new SimHubLogSink();
    }
}

// The C# 9 module-initializer feature is compiler-only; net48 just lacks the
// attribute type. Declaring it internally is the standard polyfill.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
