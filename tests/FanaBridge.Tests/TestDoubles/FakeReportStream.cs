using System;
using System.Collections.Generic;
using FanaBridge.Core.Transport;

namespace FanaBridge.Tests.TestDoubles
{
    /// <summary>
    /// Scriptable <see cref="IReportStream"/> for transport fakes: enqueue frames
    /// with <see cref="Enqueue"/>, and they come back from <see cref="TryRead"/>
    /// in order (-1 when empty — timeouts are not simulated). Records Flush calls
    /// and forwards enqueued frames to any taps, mirroring the real stream.
    /// </summary>
    internal sealed class FakeReportStream : IReportStream
    {
        private readonly Queue<byte[]> _frames = new Queue<byte[]>();
        private readonly List<Action<byte[]>> _taps = new List<Action<byte[]>>();

        public int FlushCount;
        public int ReadCount;

        /// <summary>A shared always-empty stream for fakes that never script reads.</summary>
        public static readonly FakeReportStream Empty = new FakeReportStream();

        public void Enqueue(byte[] frame)
        {
            _frames.Enqueue(frame);
            foreach (var tap in _taps.ToArray())
                tap((byte[])frame.Clone());
        }

        public int PendingCount => _frames.Count;

        public int TryRead(byte[] destination, int timeoutMs)
        {
            ReadCount++;
            if (_frames.Count == 0) return -1;
            var frame = _frames.Dequeue();
            int n = Math.Min(frame.Length, destination.Length);
            Array.Copy(frame, destination, n);
            return n;
        }

        public void Flush()
        {
            FlushCount++;
            _frames.Clear();
        }

        public IDisposable Tap(Action<byte[]> observer)
        {
            _taps.Add(observer);
            return new TapToken(this, observer);
        }

        private sealed class TapToken : IDisposable
        {
            private readonly FakeReportStream _stream;
            private readonly Action<byte[]> _observer;
            public TapToken(FakeReportStream stream, Action<byte[]> observer)
            {
                _stream = stream;
                _observer = observer;
            }
            public void Dispose() => _stream._taps.Remove(_observer);
        }
    }
}
