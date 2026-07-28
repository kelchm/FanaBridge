using System;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>Transport channel for a recorded write attempt (seam-map §4.3).</summary>
    internal enum Chan
    {
        Col01 = 0,
        Col03 = 1,
    }

    /// <summary>
    /// One attempted <see cref="FanaBridge.Transport.IDeviceTransport"/> write,
    /// recorded at SendCol01/SendCol03 entry (including declined sends).
    /// Equality for the diff excludes <see cref="TickMs"/> (diagnostic only).
    /// </summary>
    internal readonly struct WireAttempt : IEquatable<WireAttempt>
    {
        public WireAttempt(
            int frameIndex,
            int seqInFrame,
            long tickMs,
            Chan channel,
            byte[] payload,
            bool accepted)
        {
            FrameIndex = frameIndex;
            SeqInFrame = seqInFrame;
            TickMs = tickMs;
            Channel = channel;
            Payload = payload ?? Array.Empty<byte>();
            Accepted = accepted;
        }

        public int FrameIndex { get; }
        public int SeqInFrame { get; }
        public long TickMs { get; }
        public Chan Channel { get; }
        public byte[] Payload { get; }
        public bool Accepted { get; }

        public bool Equals(WireAttempt other)
            => FrameIndex == other.FrameIndex
            && SeqInFrame == other.SeqInFrame
            && Channel == other.Channel
            && Accepted == other.Accepted
            && PayloadBytesEqual(Payload, other.Payload);

        public override bool Equals(object? obj)
            => obj is WireAttempt other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = FrameIndex;
                h = (h * 397) ^ SeqInFrame;
                h = (h * 397) ^ (int)Channel;
                h = (h * 397) ^ (Accepted ? 1 : 0);
                h = (h * 397) ^ Payload.Length;
                for (int i = 0; i < Payload.Length && i < 8; i++)
                    h = (h * 397) ^ Payload[i];
                return h;
            }
        }

        public string ToHex()
            => BitConverter.ToString(Payload);

        public static bool PayloadBytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A declared legal divergence between v9 and v2 streams (seam-map §4.6).
    /// Null expected arrays mean "engine emitted nothing at this index".
    /// </summary>
    internal sealed class KnownDiff
    {
        public KnownDiff(
            string name,
            int frameIndex,
            Chan channel,
            byte[]? expectedV9,
            byte[]? expectedV2)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            FrameIndex = frameIndex;
            Channel = channel;
            ExpectedV9 = expectedV9;
            ExpectedV2 = expectedV2;
        }

        public string Name { get; }
        public int FrameIndex { get; }
        public Chan Channel { get; }
        public byte[]? ExpectedV9 { get; }
        public byte[]? ExpectedV2 { get; }
    }
}
