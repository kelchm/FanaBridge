namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Abstraction for a 3-digit 7-segment display output target.
    /// Implemented by <see cref="HardwareSegmentDisplay"/> for real HID output
    /// and by PreviewSegmentDisplay (Phase 8) for WPF UI previews.
    /// </summary>
    public interface ISegmentDisplay
    {
        /// <summary>Send three encoded 7-segment bytes to the display.</summary>
        void Send(byte seg0, byte seg1, byte seg2);

        /// <summary>Blank the display.</summary>
        void Clear();

        /// <summary>Send a special hardware command that bypasses the text pipeline.</summary>
        void SendCommand(DeviceCommand command);

        /// <summary>
        /// Resend the last frame if the display has been idle long enough to
        /// time out. Called every frame by the controller. No-op when the
        /// display was recently written.
        /// </summary>
        void Keepalive();
    }
}
