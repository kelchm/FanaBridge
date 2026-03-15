namespace FanaBridge.Core
{
    /// <summary>
    /// Connection-check surface of the Fanatec SDK layer, used by
    /// <see cref="ConnectionMonitor"/> for heartbeat and wheel polling.
    /// </summary>
    public interface ISdkConnection
    {
        /// <summary>Whether the SDK is connected to a Fanatec wheelbase.</summary>
        bool IsConnected { get; }

        /// <summary>Disconnect from the SDK.</summary>
        void Disconnect();

        /// <summary>Poll the SDK for the current wheel identity.</summary>
        bool PollWheelIdentity();
    }
}
