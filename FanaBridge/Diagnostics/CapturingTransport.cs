using System;
using FanaBridge.Transport;

namespace FanaBridge.Diagnostics
{
    /// <summary>
    /// Transparent decorator around <see cref="IDeviceTransport"/> that captures
    /// all HID traffic to a <see cref="CaptureSession"/> when one is active.
    ///
    /// Protocol encoders see this as a normal <see cref="IDeviceTransport"/>;
    /// no changes are required to any existing encoder or adapter code.
    ///
    /// When no capture session is set, the overhead is a single null check per call.
    /// </summary>
    public sealed class CapturingTransport : IDeviceTransport
    {
        private readonly IDeviceTransport _inner;
        private volatile CaptureSession _session;

        /// <summary>The currently active capture session, or null.</summary>
        public CaptureSession ActiveSession => _session;

        /// <summary>Whether a capture is currently in progress.</summary>
        public bool IsCapturing => _session != null;

        public CapturingTransport(IDeviceTransport inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>
        /// Starts capturing to the given session.
        /// Any previously active session is disposed first.
        /// </summary>
        public void StartCapture(CaptureSession session)
        {
            var old = _session;
            _session = session ?? throw new ArgumentNullException(nameof(session));
            old?.Dispose();
        }

        /// <summary>
        /// Stops the active capture and disposes the session.
        /// </summary>
        public CaptureSession StopCapture()
        {
            var session = _session;
            _session = null;
            session?.Dispose();
            return session;
        }

        // ── IDeviceTransport passthrough with capture ────────────────────

        public bool IsConnected => _inner.IsConnected;

        public int Col03MaxInputReportLength => _inner.Col03MaxInputReportLength;

        public bool SendCol03(byte[] data)
        {
            _session?.WritePacket(PacketDirection.TX, HidInterface.Col03,
                                  data, 0, data?.Length ?? 0);
            return _inner.SendCol03(data);
        }

        public int ReadCol03(byte[] buffer, int timeoutMs)
        {
            int bytesRead = _inner.ReadCol03(buffer, timeoutMs);

            if (bytesRead > 0)
            {
                _session?.WritePacket(PacketDirection.RX, HidInterface.Col03,
                                      buffer, 0, bytesRead);
            }

            return bytesRead;
        }

        public bool SendCol01(byte[] data)
        {
            _session?.WritePacket(PacketDirection.TX, HidInterface.Col01,
                                  data, 0, data?.Length ?? 0);
            return _inner.SendCol01(data);
        }

        public IDisposable BeginBatch()
        {
            return _inner.BeginBatch();
        }
    }
}
