namespace FanaBridge.Core.Logging
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
}
