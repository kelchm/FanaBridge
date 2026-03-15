using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FanaBridge.Diagnostics
{
    /// <summary>
    /// Direction of a captured HID packet relative to the host.
    /// </summary>
    public enum PacketDirection
    {
        /// <summary>Host → Device (write / OUT report).</summary>
        TX,
        /// <summary>Device → Host (read / IN report).</summary>
        RX
    }

    /// <summary>
    /// The HID collection interface a packet was sent/received on.
    /// </summary>
    public enum HidInterface
    {
        /// <summary>8-byte display interface.</summary>
        Col01,
        /// <summary>64-byte LED / config interface.</summary>
        Col03
    }

    /// <summary>
    /// Manages a single USB capture session, writing timestamped HID packets
    /// to a human-readable text file.
    ///
    /// Thread-safe: multiple callers may write packets concurrently.
    /// </summary>
    public sealed class CaptureSession : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly long _startTicks;
        private readonly object _writeLock = new object();
        private bool _disposed;

        /// <summary>Full path to the capture file.</summary>
        public string FilePath { get; }

        /// <summary>
        /// Starts a new capture session.
        /// </summary>
        /// <param name="filePath">Path to write the capture file.</param>
        /// <param name="deviceName">Device name for the file header (informational).</param>
        public CaptureSession(string filePath, string deviceName)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            _writer = new StreamWriter(filePath, false, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _startTicks = DateTime.UtcNow.Ticks;

            _writer.WriteLine("# FanaBridge USB Capture v1");
            _writer.WriteLine("# Started: " + DateTime.UtcNow.ToString("o"));
            _writer.WriteLine("# Device: " + (deviceName ?? "Unknown"));
            _writer.WriteLine("#");
            _writer.WriteLine("# RelativeMs  Dir  Interface  Length  Data");
            _writer.WriteLine("#" + new string('-', 79));
        }

        /// <summary>
        /// Records a packet.
        /// </summary>
        public void WritePacket(PacketDirection direction, HidInterface iface,
                                byte[] data, int offset, int length)
        {
            if (_disposed) return;
            if (data == null || length <= 0) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            double relativeMs = (nowTicks - _startTicks) / (double)TimeSpan.TicksPerMillisecond;

            string dir = direction == PacketDirection.TX ? "TX" : "RX";
            string ifaceName = iface == HidInterface.Col01 ? "COL01" : "COL03";
            string hex = FormatHex(data, offset, length);

            string line = string.Format(CultureInfo.InvariantCulture,
                "{0,12:F3}  {1}  {2}  {3,3}  {4}",
                relativeMs, dir, ifaceName, length, hex);

            lock (_writeLock)
            {
                if (!_disposed)
                    _writer.WriteLine(line);
            }
        }

        private static string FormatHex(byte[] data, int offset, int length)
        {
            var sb = new StringBuilder(length * 3);
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("X2"));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            lock (_writeLock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _writer.Dispose(); } catch { }
            }
        }
    }
}
