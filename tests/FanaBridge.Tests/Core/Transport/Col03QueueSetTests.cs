using FanaBridge.Core.Transport;
using Xunit;

namespace FanaBridge.Tests.Core.Transport
{
    public class Col03QueueSetTests
    {
        private static byte[] IdentityFrame()
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            return b;
        }

        private static byte[] ItmFrame()
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x05;
            return b;
        }

        private static byte[] SrmFrame(byte marker = 0)
        {
            var b = new byte[16];
            b[0] = 0xDD; b[1] = marker;
            return b;
        }

        private static byte[] TuningFrame()
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x03;
            return b;
        }

        [Fact]
        public void Route_DeliversEachFamilyToItsOwnQueue()
        {
            var set = new Col03QueueSet();
            set.Route(IdentityFrame(), 64);
            set.Route(ItmFrame(), 64);
            set.Route(SrmFrame(), 16);
            set.Route(TuningFrame(), 64);

            var dest = new byte[64];
            Assert.Equal(64, set.Get(Col03Family.Identity).TryRead(dest, 0));
            Assert.Equal(64, set.Get(Col03Family.Itm).TryRead(dest, 0));
            Assert.Equal(16, set.Get(Col03Family.Srm).TryRead(dest, 0));
            Assert.Equal(64, set.Get(Col03Family.Tuning).TryRead(dest, 0));

            // Exactly one frame each — cross-family pops come back empty.
            Assert.Equal(-1, set.Get(Col03Family.Identity).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Itm).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Srm).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Tuning).TryRead(dest, 0));
        }

        [Fact]
        public void Route_DropsUnclassifiedFrames()
        {
            var set = new Col03QueueSet();
            set.Route(new byte[] { 0x01, 0x80, 0x7F, 0x00 }, 4); // axis junk

            var dest = new byte[64];
            Assert.Equal(-1, set.Get(Col03Family.Identity).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Itm).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Srm).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Tuning).TryRead(dest, 0));
        }

        [Fact]
        public void Overflow_DropsOldestWithinOneFamily_OthersUntouched()
        {
            var set = new Col03QueueSet();

            // One identity frame, then flood SRM past its capacity (16).
            set.Route(IdentityFrame(), 64);
            for (byte i = 0; i < 20; i++)
                set.Route(SrmFrame(i), 16);

            var dest = new byte[64];

            // SRM kept the NEWEST 16 (markers 4..19) — oldest dropped.
            Assert.Equal(16, set.Get(Col03Family.Srm).TryRead(dest, 0));
            Assert.Equal(4, dest[1]);

            // The identity frame was not disturbed by the SRM overflow.
            Assert.Equal(64, set.Get(Col03Family.Identity).TryRead(dest, 0));
        }

        [Fact]
        public void Close_WakesEveryFamilyWithMinusOne()
        {
            var set = new Col03QueueSet();
            set.Route(IdentityFrame(), 64);
            set.Close();

            var dest = new byte[64];
            Assert.Equal(-1, set.Get(Col03Family.Identity).TryRead(dest, 0));
            Assert.Equal(-1, set.Get(Col03Family.Tuning).TryRead(dest, 250)); // no blocking after close
        }
    }
}
