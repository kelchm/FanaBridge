using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.Core.Transport
{
    public class HidReportQueueTests
    {
        private static byte[] Report(params byte[] bytes) => bytes;

        // ── Empty / timeout-0 contract (the per-frame drain path) ─────────

        [Fact]
        public void TryRead_EmptyWithZeroTimeout_ReturnsMinusOneImmediately()
        {
            var queue = new HidReportQueue(16);
            var dest = new byte[64];

            var sw = Stopwatch.StartNew();
            int n = queue.TryRead(dest, 0);
            sw.Stop();

            Assert.Equal(-1, n);
            // The whole point of the queue: an idle poll must not block (and
            // must not throw). Generous bound to tolerate CI scheduling.
            Assert.True(sw.ElapsedMilliseconds < 50,
                $"idle TryRead took {sw.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void TryRead_EmptyWithNegativeTimeout_ReturnsMinusOne()
        {
            var queue = new HidReportQueue(16);
            Assert.Equal(-1, queue.TryRead(new byte[64], -5));
        }

        // ── Basic dequeue semantics ───────────────────────────────────────

        [Fact]
        public void EnqueueThenTryRead_ReturnsLengthAndBytes()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(0xFF, 0x08, 0x01, 0x02), 4);

            var dest = new byte[64];
            int n = queue.TryRead(dest, 0);

            Assert.Equal(4, n);
            Assert.Equal(new byte[] { 0xFF, 0x08, 0x01, 0x02 }, new ArraySegment<byte>(dest, 0, 4));
        }

        [Fact]
        public void Enqueue_TakesPrivateCopy()
        {
            var queue = new HidReportQueue(16);
            var source = Report(0xAA, 0xBB);
            queue.Enqueue(source, 2);
            source[0] = 0x00; // reader thread reuses its buffer — must not alias

            var dest = new byte[64];
            queue.TryRead(dest, 0);
            Assert.Equal(0xAA, dest[0]);
        }

        [Fact]
        public void TryRead_PreservesFifoOrder()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(1), 1);
            queue.Enqueue(Report(2), 1);
            queue.Enqueue(Report(3), 1);

            var dest = new byte[64];
            queue.TryRead(dest, 0); Assert.Equal(1, dest[0]);
            queue.TryRead(dest, 0); Assert.Equal(2, dest[0]);
            queue.TryRead(dest, 0); Assert.Equal(3, dest[0]);
            Assert.Equal(-1, queue.TryRead(dest, 0));
        }

        [Fact]
        public void Enqueue_LengthBeyondReport_ClampsToReportLength()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(7, 8), 10);

            var dest = new byte[64];
            Assert.Equal(2, queue.TryRead(dest, 0));
        }

        [Fact]
        public void Enqueue_ZeroLengthOrNull_Ignored()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(1, 2, 3), 0);
            queue.Enqueue(null, 3);

            Assert.Equal(-1, queue.TryRead(new byte[64], 0));
        }

        [Fact]
        public void TryRead_SmallDestination_TruncatesToDestination()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(1, 2, 3, 4), 4);

            var dest = new byte[2];
            int n = queue.TryRead(dest, 0);

            Assert.Equal(2, n);
            Assert.Equal(new byte[] { 1, 2 }, dest);
        }

        // ── Capacity: drop-oldest ─────────────────────────────────────────

        [Fact]
        public void Enqueue_BeyondCapacity_DropsOldest()
        {
            var queue = new HidReportQueue(2);
            queue.Enqueue(Report(1), 1);
            queue.Enqueue(Report(2), 1);
            queue.Enqueue(Report(3), 1); // evicts report 1

            var dest = new byte[64];
            queue.TryRead(dest, 0); Assert.Equal(2, dest[0]);
            queue.TryRead(dest, 0); Assert.Equal(3, dest[0]);
            Assert.Equal(-1, queue.TryRead(dest, 0));
        }

        // ── Blocking wait (elicited command→response reads) ───────────────

        [Fact]
        public async Task TryRead_BlockedReader_WokenByEnqueue()
        {
            var queue = new HidReportQueue(16);
            var dest = new byte[64];

            var reader = Task.Run(() => queue.TryRead(dest, 5000));
            // Let the reader park in its wait before producing.
            await Task.Delay(50);
            queue.Enqueue(Report(0xDD, 1, 2, 3, 4, 5), 6);

            var winner = await Task.WhenAny(reader, Task.Delay(1000));
            Assert.True(winner == reader, "reader was not woken by Enqueue");
            Assert.Equal(6, await reader);
            Assert.Equal(0xDD, dest[0]);
        }

        [Fact]
        public void TryRead_EmptyWithShortTimeout_TimesOutWithMinusOne()
        {
            var queue = new HidReportQueue(16);

            var sw = Stopwatch.StartNew();
            int n = queue.TryRead(new byte[64], 50);
            sw.Stop();

            Assert.Equal(-1, n);
            Assert.True(sw.ElapsedMilliseconds >= 40,
                $"timed out after only {sw.ElapsedMilliseconds} ms");
        }

        // ── Close (Disconnect path) ───────────────────────────────────────

        [Fact]
        public async Task Close_WakesBlockedReaderWithMinusOne()
        {
            var queue = new HidReportQueue(16);

            var reader = Task.Run(() => queue.TryRead(new byte[64], 5000));
            await Task.Delay(50);
            queue.Close();

            var winner = await Task.WhenAny(reader, Task.Delay(1000));
            Assert.True(winner == reader, "reader was not woken by Close");
            Assert.Equal(-1, await reader);
        }

        [Fact]
        public void Close_DropsQueuedReportsAndRefusesNewOnes()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(1), 1);
            queue.Close();
            queue.Enqueue(Report(2), 1);

            Assert.Equal(-1, queue.TryRead(new byte[64], 0));
            // A closed queue must not block even with a timeout.
            Assert.Equal(-1, queue.TryRead(new byte[64], 250));
        }

        // ── Flush ─────────────────────────────────────────────────────────

        [Fact]
        public void Flush_DropsPendingWithoutClosing()
        {
            var queue = new HidReportQueue(16);
            queue.Enqueue(Report(1), 1);
            queue.Flush();

            Assert.Equal(-1, queue.TryRead(new byte[64], 0));

            // Still open: new frames flow.
            queue.Enqueue(Report(2), 1);
            var dest = new byte[64];
            Assert.Equal(1, queue.TryRead(dest, 0));
            Assert.Equal(2, dest[0]);
        }

        // ── Taps (non-consuming observers) ────────────────────────────────

        [Fact]
        public void Tap_ObserverGetsCopy_ConsumerStillGetsFrame()
        {
            var queue = new HidReportQueue(16);
            byte[]? observed = null;
            using (queue.Tap(f => observed = f))
            {
                queue.Enqueue(Report(0xAB, 0xCD), 2);
            }

            Assert.NotNull(observed);
            Assert.Equal(new byte[] { 0xAB, 0xCD }, observed);

            // The tap did NOT consume: the owner still reads the frame.
            var dest = new byte[64];
            Assert.Equal(2, queue.TryRead(dest, 0));
            Assert.Equal(0xAB, dest[0]);

            // The observer's copy is private — mutating it can't corrupt the
            // frame the consumer received.
            observed[0] = 0x00;
            Assert.Equal(0xAB, dest[0]);
        }

        [Fact]
        public void Tap_Disposed_StopsObserving()
        {
            var queue = new HidReportQueue(16);
            int seen = 0;
            var token = queue.Tap(_ => seen++);
            queue.Enqueue(Report(1), 1);
            token.Dispose();
            queue.Enqueue(Report(2), 1);

            Assert.Equal(1, seen);
        }

        [Fact]
        public void Tap_ThrowingObserver_DoesNotBreakEnqueue()
        {
            var queue = new HidReportQueue(16);
            using (queue.Tap(_ => throw new InvalidOperationException("observer bug")))
            {
                queue.Enqueue(Report(7), 1);
            }

            var dest = new byte[64];
            Assert.Equal(1, queue.TryRead(dest, 0));
            Assert.Equal(7, dest[0]);
        }
    }
}
