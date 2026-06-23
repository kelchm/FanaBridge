using FanaBridge.Protocol;
using FanaBridge.Protocol.Schema;
using Xunit;

namespace FanaBridge.Tests
{
    public class TuningStateTests
    {
        // A 64-byte READ frame: FF 03 <devId> then the payload at ReadDataStart.
        private static byte[] FakeReadFrame(byte devId, byte[] payload)
        {
            var f = new byte[Wire.Col03Length];
            f[0] = Wire.Col03.ReportId;
            f[1] = Wire.Col03.TuningClass;
            f[2] = devId;
            for (int i = 0; i < payload.Length; i++)
                f[TuningPayload.ReadDataStart + i] = payload[i];
            return f;
        }

        [Fact]
        public void DecodeThenEncode_RoundTripsPayloadAcrossReadWriteShift()
        {
            // Distinct value per offset so an off-by-one read→write shift is caught.
            var payload = new byte[TuningPayload.PayloadLength];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);

            var state = TuningState.Decode(FakeReadFrame(0x02, payload), TuningPayload.ReadDataStart);
            byte[] write = state.EncodeWrite(0x02);

            Assert.Equal(Wire.Col03.ReportId, write[0]);
            Assert.Equal(Wire.Col03.TuningClass, write[1]);
            Assert.Equal(0x00, write[2]); // WRITE subcmd
            Assert.Equal(0x02, write[3]); // device id

            // Every payload byte reappears at WriteDataStart + offset (the +1 shift).
            foreach (var f in TuningPayload.Fields)
                Assert.Equal(payload[f.Offset], write[TuningPayload.WriteDataStart + f.Offset]);
        }

        [Fact]
        public void Dri_DecodesAsSigned_OthersUnsigned()
        {
            var payload = new byte[TuningPayload.PayloadLength];
            payload[7] = 0xF0; // DRI (offset 7) -> sbyte -16
            payload[2] = 0xF0; // FF  (offset 2) -> 240
            var state = TuningState.Decode(FakeReadFrame(0x02, payload), TuningPayload.ReadDataStart);
            Assert.Equal(-16, state["DRI"]);
            Assert.Equal(240, state["FF"]);
        }

        [Fact]
        public void Setter_WritesSignedValueIntoWriteFrame()
        {
            var state = TuningState.Decode(
                FakeReadFrame(0x02, new byte[TuningPayload.PayloadLength]), TuningPayload.ReadDataStart);
            state["DRI"] = -16;
            byte[] write = state.EncodeWrite(0x02);
            Assert.Equal(0xF0, write[TuningPayload.WriteDataStart + 7]);
        }
    }
}
