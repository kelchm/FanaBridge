using FanaBridge.Core.Devices.Identity;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Identity
{
    public class IdentitySettlerTests
    {
        private const int Settle = 200;
        private const byte HubWire = 0x0C;
        private const byte NoWire  = 0x00;
        private const byte BmwWire = 0x0F;
        private const byte PBMR    = 0x02;
        private const byte NoMod   = 0x00;

        // Commit an initial identity at t=0 and return a settler positioned in steady state.
        private static IdentitySettler Seeded(byte wire, byte module)
        {
            var s = new IdentitySettler(Settle);
            s.Offer(wire, module, 0);
            Assert.True(s.Tick(Settle, out _, out _)); // first commit is a change
            Assert.True(s.IsStable);
            return s;
        }

        [Fact]
        public void InitialReading_CommitsAfterSettleWindow()
        {
            var s = new IdentitySettler(Settle);
            s.Offer(HubWire, PBMR, 0);

            Assert.False(s.IsStable);
            Assert.False(s.Tick(199, out _, out _)); // still within the window
            Assert.True(s.Tick(200, out var w, out var m)); // committed
            Assert.Equal(HubWire, w);
            Assert.Equal(PBMR, m);
            Assert.True(s.IsStable);
        }

        [Fact]
        public void SteadyState_NeverSettlesOrCommits()
        {
            var s = Seeded(HubWire, PBMR);
            for (long t = 200; t < 2000; t += 50)
            {
                s.Offer(HubWire, PBMR, t);            // same reading every tick
                Assert.True(s.IsStable);
                Assert.False(s.Tick(t, out _, out _));
            }
        }

        [Fact]
        public void CleanChange_CommitsOneWindowLater()
        {
            var s = Seeded(HubWire, PBMR);

            s.Offer(HubWire, NoMod, 1000);            // module removed
            Assert.False(s.IsStable);                 // suppressed while settling
            Assert.False(s.Tick(1100, out _, out _)); // < deadline
            Assert.True(s.Tick(1200, out var w, out var m));
            Assert.Equal(HubWire, w);
            Assert.Equal(NoMod, m);
        }

        [Fact]
        public void Flap_RidesOut_AndCommitsWhatItSettlesTo()
        {
            var s = Seeded(HubWire, NoMod); // hub present, no module yet

            // A reconnect storm: wire flaps 0x0C <-> 0x00 every 15 ms for ~2 s,
            // ending settled on the hub with the module now present.
            long t = 1000;
            for (; t < 3000; t += 15)
                s.Offer((t / 15) % 2 == 0 ? NoWire : HubWire, NoMod, t);
            s.Offer(HubWire, PBMR, t); // final settled reading

            // Never commits mid-storm (deadline keeps moving), and stays unstable.
            Assert.False(s.IsStable);
            Assert.False(s.Tick(t + 199, out _, out _));

            // Commits the settled value one window after the last push.
            Assert.True(s.Tick(t + 200, out var w, out var m));
            Assert.Equal(HubWire, w);
            Assert.Equal(PBMR, m);
            Assert.True(s.IsStable);
        }

        [Fact]
        public void SettlesBackToCommitted_NoChangeEvent()
        {
            var s = Seeded(HubWire, PBMR);

            s.Offer(NoWire, NoMod, 1000);  // a blip away...
            s.Offer(HubWire, PBMR, 1030);  // ...then back to the committed value
            Assert.False(s.Tick(1100, out _, out _)); // settling
            // Window expires having settled back to what was already committed: no event.
            Assert.False(s.Tick(1230, out _, out _));
            Assert.True(s.IsStable);
        }

        [Fact]
        public void WheelSwap_CommitsNewWheel()
        {
            var s = Seeded(HubWire, NoMod);
            s.Offer(BmwWire, NoMod, 500);
            Assert.True(s.Tick(700, out var w, out _));
            Assert.Equal(BmwWire, w);
        }

        [Fact]
        public void Reset_ClearsCommittedAndSettling()
        {
            var s = Seeded(HubWire, PBMR);
            s.Offer(NoWire, NoMod, 1000); // mid-settle
            s.Reset();

            Assert.True(s.IsStable);
            // After reset the same reading must re-earn its settle window from scratch.
            s.Offer(HubWire, PBMR, 2000);
            Assert.False(s.Tick(2199, out _, out _));
            Assert.True(s.Tick(2200, out _, out _));
        }
    }
}
