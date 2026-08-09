// Exception: namespace FanaBridge (not FanaBridge.Core.Logging) so unqualified Log.* from both assemblies resolves via the shared root ancestor.
#pragma warning disable IDE0130 // Log must live in root FanaBridge so unqualified Log.* resolves from both Core and plugin
using FanaBridge.Core.Logging;

namespace FanaBridge
{
    /// <summary>
    /// Process-wide log seam for the core device stack. Kept static (not
    /// injected) because logging is the one cross-cutting concern threaded
    /// through the transport's reader thread, connect paths, and profile store —
    /// the sink field is volatile so a sink installed on the plugin's load path
    /// is seen by those threads.
    /// </summary>
    public static class Log
    {
        private static volatile ILogSink _sink;

        /// <summary>The active sink, or null for silent operation.</summary>
        public static ILogSink Sink
        {
            get => _sink;
            set => _sink = value;
        }

        public static void Info(string message) => _sink?.Info(message);
        public static void Warn(string message) => _sink?.Warn(message);
        public static void Error(string message) => _sink?.Error(message);
        public static void Debug(string message) => _sink?.Debug(message);
    }
}
