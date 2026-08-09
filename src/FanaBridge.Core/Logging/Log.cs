// Exception: namespace FanaBridge (not FanaBridge.Core.Logging) so unqualified Log.* from both assemblies resolves via the shared root ancestor.
#pragma warning disable IDE0130 // Log must live in root FanaBridge so unqualified Log.* resolves from both Core and plugin
namespace FanaBridge
{
    /// <summary>
    /// Log output contract for the SimHub-free core. The plugin shell installs a
    /// sink that forwards to SimHub's logger; standalone hosts (unit tests) leave
    /// it unset and core logging is a silent no-op.
    /// </summary>
    public interface ILogSink
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Debug(string message);
    }

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
