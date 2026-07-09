using System;
using System.Collections.Generic;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Pins the col03 reader loop's concurrency invariants, which previously
    /// existed only as comments: transient errors retry before faulting, an
    /// intentional teardown never faults, and a stale session's reader can't
    /// poison the current one. The loop runs synchronously on the test thread
    /// against a scripted stream source.
    /// </summary>
    public class FanatecTransportReaderTests
    {
        // Scripted ICol03Source: each step either returns a frame, throws, or
        // performs a side effect first (e.g. flagging teardown mid-loop).
        private sealed class ScriptedSource : FanatecTransport.ICol03Source
        {
            private readonly Queue<Func<byte[], int>> _steps = new Queue<Func<byte[], int>>();
            public int ReadTimeout { set { } }

            public ScriptedSource Frame(byte[] frame)
            {
                _steps.Enqueue(buf => { Array.Copy(frame, buf, frame.Length); return frame.Length; });
                return this;
            }

            public ScriptedSource Error(Action? beforeThrow = null)
            {
                _steps.Enqueue(_ =>
                {
                    beforeThrow?.Invoke();
                    throw new System.IO.IOException("scripted read error");
                });
                return this;
            }

            public ScriptedSource Timeout()
            {
                _steps.Enqueue(_ => throw new TimeoutException("scripted idle timeout"));
                return this;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                // Script exhausted: treat as a persistent device failure so the
                // loop always terminates (tests never rely on this tail).
                if (_steps.Count == 0) throw new System.IO.IOException("script exhausted");
                return _steps.Dequeue()(buffer);
            }
        }

        private static byte[] IdentityFrame()
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            return b;
        }

        private static (FanatecTransport t, Col03QueueSet q) NewSession()
        {
            var t = new FanatecTransport();
            var q = new Col03QueueSet();
            t.BeginReaderSessionForTest(q);
            return (t, q);
        }

        [Fact]
        public void TransientErrors_UnderRetryMax_Recover_WithoutFaulting()
        {
            var (t, q) = NewSession();
            var source = new ScriptedSource()
                .Frame(IdentityFrame())
                .Error().Error().Error()                       // 3 < COL03_READ_RETRY_MAX
                .Frame(IdentityFrame())                        // recovered
                .Error(t.SignalStoppingForTest);               // clean exit tail

            // Loop exit closes the queues (clearing them), so routing is observed
            // through a non-consuming tap, as a live consumer would see it.
            var identityFrames = new List<byte[]>();
            using (q.Get(Col03Family.Identity).Tap(identityFrames.Add))
                t.Col03ReadLoop(source, q, 64);

            // Both frames made it through the hiccup, and the reader is healthy.
            Assert.Equal(2, identityFrames.Count);
            Assert.False(t.ReaderFaultedForTest);
        }

        [Fact]
        public void PersistentErrors_FaultTheReader_SoIsConnectedReportsFalse()
        {
            var (t, q) = NewSession();
            var source = new ScriptedSource();
            for (int i = 0; i < FanatecTransport.COL03_READ_RETRY_MAX; i++)
                source.Error();

            t.Col03ReadLoop(source, q, 64);

            // The fault flag is what drives IsConnected=false and, from there,
            // the ConnectionMonitor's reconnect — input dead + writes alive is
            // exactly the state this exists to escape.
            Assert.True(t.ReaderFaultedForTest);
        }

        [Fact]
        public void IntentionalTeardown_NeverFaults()
        {
            var (t, q) = NewSession();
            // Disconnect sets the stopping flag BEFORE disposing the stream; the
            // dispose is what wakes the parked read as an exception.
            var source = new ScriptedSource().Error(t.SignalStoppingForTest);

            t.Col03ReadLoop(source, q, 64);

            Assert.False(t.ReaderFaultedForTest);
        }

        [Fact]
        public void StaleSessionReader_CannotPoisonTheCurrentSession()
        {
            var (t, staleQueues) = NewSession();

            // A new session replaces the queue set (as a reconnect does) while the
            // old reader is still unwinding with persistent errors.
            var currentQueues = new Col03QueueSet();
            t.BeginReaderSessionForTest(currentQueues);

            var source = new ScriptedSource();
            for (int i = 0; i < FanatecTransport.COL03_READ_RETRY_MAX; i++)
                source.Error();

            t.Col03ReadLoop(source, staleQueues, 64);   // the STALE session's loop

            // The stale reader must exit without flagging the fresh session dead.
            Assert.False(t.ReaderFaultedForTest);
        }

        [Fact]
        public void LoopExit_ClosesItsQueues_WakingBlockedConsumers()
        {
            var (t, q) = NewSession();
            var source = new ScriptedSource().Error(t.SignalStoppingForTest);

            t.Col03ReadLoop(source, q, 64);

            // A closed queue returns -1 immediately — this is what wakes a
            // blocked elicit during teardown instead of deadlocking it.
            var dest = new byte[64];
            Assert.Equal(-1, q.Get(Col03Family.Tuning).TryRead(dest, 5_000));
        }

        [Fact]
        public void IdleTimeouts_ParkAgain_WithoutFaultingOrCountingAsErrors()
        {
            // A timeout is the reader's normal idle state (~24 days at the
            // configured window) — it must neither fault the reader nor spend
            // the transient-error retry budget.
            var (t, q) = NewSession();
            var source = new ScriptedSource()
                .Timeout().Timeout().Timeout().Timeout().Timeout().Timeout()
                .Frame(IdentityFrame())
                .Error(t.SignalStoppingForTest);

            var identityFrames = new List<byte[]>();
            using (q.Get(Col03Family.Identity).Tap(identityFrames.Add))
                t.Col03ReadLoop(source, q, 64);

            Assert.Single(identityFrames);       // still reading after 6 timeouts
            Assert.False(t.ReaderFaultedForTest);
        }

        [Fact]
        public void Frames_RouteByWireSignature_NotArrivalOrder()
        {
            var (t, q) = NewSession();
            var itm = new byte[64]; itm[0] = 0xFF; itm[1] = 0x05;
            var source = new ScriptedSource()
                .Frame(itm)
                .Frame(IdentityFrame())
                .Error(t.SignalStoppingForTest);

            var identityFrames = new List<byte[]>();
            var itmFrames = new List<byte[]>();
            using (q.Get(Col03Family.Identity).Tap(identityFrames.Add))
            using (q.Get(Col03Family.Itm).Tap(itmFrames.Add))
                t.Col03ReadLoop(source, q, 64);

            Assert.Single(identityFrames);
            Assert.Equal(0x08, identityFrames[0][1]);
            Assert.Single(itmFrames);
            Assert.Equal(0x05, itmFrames[0][1]);
        }
    }
}
